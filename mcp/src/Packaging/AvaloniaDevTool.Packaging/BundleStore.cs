using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Packaging;

internal sealed class BundleStore
{
    internal const string ManifestFileName = ".avdt-bundle.json";
    private readonly BundleManifest _manifest;
    private readonly Func<Stream> _openPayload;

    internal BundleStore(BundleManifest manifest, Func<Stream> openPayload)
    {
        manifest.Validate();
        _manifest = manifest;
        _openPayload = openPayload;
    }

    internal async Task<string> EnsureExtractedAsync(string cacheRoot, CancellationToken cancellationToken = default)
    {
        var parent = Path.GetFullPath(Path.Combine(cacheRoot, _manifest.RuntimeIdentifier));
        Directory.CreateDirectory(parent);
        RejectReparsePoint(parent);
        var destination = Path.Combine(parent, _manifest.PayloadSha256.ToLowerInvariant());
        await using var extractionLock = await AcquireLockAsync(destination + ".lock", cancellationToken);
        if (await IsValidAsync(destination, cancellationToken))
            return destination;
        ResetRuntimeDirectory(destination, parent);
        await ExtractAsync(destination, cancellationToken);
        // The exclusive payload lock protects extraction and validation across processes.
        // The marker is the final commit; an interrupted extraction is never executable.
        await File.WriteAllTextAsync(Path.Combine(destination, ManifestFileName),
            JsonSerializer.Serialize(_manifest, BundleManifest.JsonOptions),
            cancellationToken);
        return destination;
    }

    private async Task<bool> IsValidAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            return false;
        try
        {
            RejectReparsePoint(directory);
            var entries = EnumerateFilesWithoutLinks(directory)
                .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (entries.Count != _manifest.Files.Length + 1 || !entries.Remove(ManifestFileName))
                return false;
            var cached = await File.ReadAllTextAsync(Path.Combine(directory, ManifestFileName), cancellationToken);
            if (cached != JsonSerializer.Serialize(_manifest, BundleManifest.JsonOptions))
                return false;
            foreach (var file in _manifest.Files)
            {
                if (!entries.Remove(file.Path))
                    return false;
                var path = ResolveEntry(directory, file.Path);
                if (new FileInfo(path).Length != file.Length)
                    return false;
                await using var source = File.OpenRead(path);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken));
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return entries.Count == 0;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private async Task ExtractAsync(string destination, CancellationToken cancellationToken)
    {
        await using var payload = _openPayload();
        if (!payload.CanSeek)
            throw new InvalidDataException("The embedded bundle must be seekable.");
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(payload, cancellationToken));
        if (!actualHash.Equals(_manifest.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The embedded bundle failed its SHA-256 check.");
        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
        var expected = _manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BundleManifest.ValidateRelativePath(entry.FullName);
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                !expected.Remove(entry.FullName, out var file) || entry.Length != file.Length)
                throw new InvalidDataException($"Unexpected bundle entry '{entry.FullName}'.");
            var path = ResolveEntry(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var source = entry.Open();
            await using var output = new FileStream(path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                long length = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    length = checked(length + read);
                    if (length > file.Length)
                        throw new InvalidDataException($"Bundle entry '{entry.FullName}' exceeds its declared length.");
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                if (length != file.Length ||
                    !Convert.ToHexString(hash.GetHashAndReset())
                        .Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Bundle entry '{entry.FullName}' failed its integrity check.");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        if (expected.Count != 0)
            throw new InvalidDataException("The embedded bundle is missing manifest entries.");
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
            {
                await Task.Delay(100, timeout.Token);
            }
        }
    }

    private static string ResolveEntry(string directory, string entry)
    {
        var path = Path.GetFullPath(Path.Combine(directory, entry.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A bundle entry escapes its extraction directory.");
        return path;
    }

    private static IEnumerable<string> EnumerateFilesWithoutLinks(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectReparsePoint(entry);
            if (Directory.Exists(entry))
            {
                foreach (var file in EnumerateFilesWithoutLinks(entry))
                    yield return file;
            }
            else
                yield return entry;
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The bundle cache contains a symbolic link or reparse point.");
    }

    private static void ResetRuntimeDirectory(string directory, string parent)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!fullPath.StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove a path outside the bundle cache.");
        Directory.CreateDirectory(fullPath);
        RejectReparsePoint(fullPath);
        File.Delete(Path.Combine(fullPath, ManifestFileName));
        foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath))
        {
            if (Directory.Exists(entry))
                DeleteTreeWithoutFollowingLinks(entry);
            else
                File.Delete(entry);
        }
    }

    private static void DeleteTreeWithoutFollowingLinks(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(directory);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (Directory.Exists(entry))
                DeleteTreeWithoutFollowingLinks(entry);
            else
                File.Delete(entry);
        }

        Directory.Delete(directory);
    }
}
