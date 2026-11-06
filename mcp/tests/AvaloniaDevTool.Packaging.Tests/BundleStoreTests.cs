using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AvaloniaDevTool.Packaging;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AvaloniaDevTool.Packaging.Tests;

public sealed class BundleStoreTests : IDisposable
{
    private readonly string _cache =
        Path.Combine(Path.GetTempPath(), "avdt-bundle-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Concurrent_extractors_publish_one_complete_verified_runtime()
    {
        var (manifest, payload) = CreateBundle(("console.exe", "console"), ("previewer/preview.exe", "preview"));
        var directories = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new BundleStore(manifest, () => new MemoryStream(payload)).EnsureExtractedAsync(_cache)));
        Assert.Single(directories.Distinct());
        Assert.Equal("console", await File.ReadAllTextAsync(Path.Combine(directories[0], "console.exe")));
        Assert.Equal("preview", await File.ReadAllTextAsync(Path.Combine(directories[0], "previewer", "preview.exe")));
        Assert.True(File.Exists(Path.Combine(directories[0], BundleStore.ManifestFileName)));
    }

    [Fact]
    public async Task Modified_or_extra_cached_files_are_replaced_before_execution()
    {
        var (manifest, payload) = CreateBundle(("console.exe", "original"));
        var store = new BundleStore(manifest, () => new MemoryStream(payload));
        var directory = await store.EnsureExtractedAsync(_cache);
        await File.WriteAllTextAsync(Path.Combine(directory, "console.exe"), "modified");
        await File.WriteAllTextAsync(Path.Combine(directory, "injected.dll"), "extra");
        Assert.Equal(directory, await store.EnsureExtractedAsync(_cache));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(directory, "console.exe")));
        Assert.False(File.Exists(Path.Combine(directory, "injected.dll")));
    }

    [Fact]
    public async Task A_temporarily_unreadable_committed_runtime_is_not_deleted_or_reextracted()
    {
        var (manifest, payload) = CreateBundle(("console.exe", "original"));
        var openedPayloads = 0;
        var store = new BundleStore(manifest,
            () =>
            {
                Interlocked.Increment(ref openedPayloads);
                return new MemoryStream(payload);
            });
        var directory = await store.EnsureExtractedAsync(_cache);
        var executable = Path.Combine(directory, "console.exe");
        await using (var held = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.EnsureExtractedAsync(_cache));
            Assert.True(File.Exists(Path.Combine(directory, BundleStore.ManifestFileName)));
            Assert.Equal(1, openedPayloads);
        }

        Assert.Equal(directory, await store.EnsureExtractedAsync(_cache));
        Assert.Equal("original", await File.ReadAllTextAsync(executable));
        Assert.Equal(1, openedPayloads);
    }

    [Fact]
    public async Task Corrupted_embedded_payload_does_not_publish_a_runtime()
    {
        var (manifest, payload) = CreateBundle(("console.exe", "console"));
        payload[^1] ^= 0xFF;
        var store = new BundleStore(manifest, () => new MemoryStream(payload));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnsureExtractedAsync(_cache));
        Assert.False(File.Exists(Path.Combine(RuntimeDirectory(manifest), BundleStore.ManifestFileName)));
    }

    [Fact]
    public async Task Archive_content_must_match_each_manifest_hash()
    {
        var (manifest, payload) = CreateBundle(("console.exe", "console"));
        manifest = manifest with { Files = [manifest.Files[0] with { Sha256 = new string('0', 64) }] };
        var store = new BundleStore(manifest, () => new MemoryStream(payload));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnsureExtractedAsync(_cache));
        Assert.False(File.Exists(Path.Combine(RuntimeDirectory(manifest), BundleStore.ManifestFileName)));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("nested/../../escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("/escape.exe")]
    [InlineData("nested\\escape.exe")]
    [InlineData("console.exe:stream")]
    public void Unsafe_manifest_paths_are_rejected(string path)
    {
        var (manifest, payload) = CreateBundle(("console.exe", "console"));
        manifest = manifest with { Files = [new(path, 1, manifest.Files[0].Sha256)] };
        Assert.Throws<InvalidDataException>(() => new BundleStore(manifest, () => new MemoryStream(payload)));
    }

    [Fact]
    public async Task Unlisted_archive_entries_are_rejected()
    {
        var (manifest, payload) = CreateBundle(("console.exe", "console"), ("extra.dll", "extra"));
        manifest = manifest with { Files = [manifest.Files[0]] };
        var store = new BundleStore(manifest, () => new MemoryStream(payload));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.EnsureExtractedAsync(_cache));
    }

    [Fact]
    public async Task Interrupted_extraction_without_a_commit_marker_is_repaired_under_the_payload_lock()
    {
        var (manifest, payload) = CreateBundle(("console.exe", "console"));
        var directory = RuntimeDirectory(manifest);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "partial.exe"), "incomplete");
        await new BundleStore(manifest, () => new MemoryStream(payload)).EnsureExtractedAsync(_cache);
        Assert.False(File.Exists(Path.Combine(directory, "partial.exe")));
        Assert.Equal("console", await File.ReadAllTextAsync(Path.Combine(directory, "console.exe")));
        Assert.True(File.Exists(Path.Combine(directory, BundleStore.ManifestFileName)));
    }

    [Fact]
    public async Task Extraction_commits_while_the_runtime_directory_is_held_without_delete_sharing()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (manifest, payload) = CreateBundle(("console.exe", "console"));
        var directory = RuntimeDirectory(manifest);
        Directory.CreateDirectory(directory);
        using var directoryLock = CreateFileW(directory, 0x80000000, 1, 0, 3, 0x02000000, 0);
        Assert.False(directoryLock.IsInvalid);
        await File.WriteAllTextAsync(Path.Combine(directory, "partial.exe"), "incomplete");
        Assert.Equal(directory,
            await new BundleStore(manifest, () => new MemoryStream(payload))
                .EnsureExtractedAsync(_cache).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("console", await File.ReadAllTextAsync(Path.Combine(directory, "console.exe")));
        Assert.True(File.Exists(Path.Combine(directory, BundleStore.ManifestFileName)));
    }

    private string RuntimeDirectory(BundleManifest manifest) =>
        Path.Combine(_cache, manifest.RuntimeIdentifier, manifest.PayloadSha256.ToLowerInvariant());

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path,
        uint access,
        uint sharing,
        nint security,
        uint creation,
        uint attributes,
        nint template);

    private static (BundleManifest Manifest, byte[] Payload) CreateBundle(params (string Path, string Text)[] files)
    {
        using var payload = new MemoryStream();
        var manifestFiles = new List<BundleFile>();
        using (var archive = new ZipArchive(payload, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                manifestFiles.Add(new(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
                using var target = archive.CreateEntry(path).Open();
                target.Write(bytes);
            }
        }

        var data = payload.ToArray();
        return (new(1,
            "test",
            "win-x64",
            "console.exe",
            Convert.ToHexString(SHA256.HashData(data)),
            manifestFiles.ToArray()), data);
    }

    public void Dispose()
    {
        if (Directory.Exists(_cache))
            Directory.Delete(_cache, recursive: true);
    }
}
