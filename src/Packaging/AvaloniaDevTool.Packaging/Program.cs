using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace AvaloniaDevTool.Packaging;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            await using var source = assembly.GetManifestResourceStream("AvaloniaDevTool.Packaging.bundle.json")
                                     ?? throw new InvalidDataException(
                                         "This bootstrap has no embedded bundle. Publish it with Build-Bundle.ps1.");
            var manifest = await JsonSerializer.DeserializeAsync<BundleManifest>(source, BundleManifest.JsonOptions)
                           ?? throw new InvalidDataException("The bundle manifest is empty.");
            manifest.Validate();
            if (args is ["--bundle-info"])
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        manifest.SchemaVersion, manifest.ApplicationVersion, manifest.RuntimeIdentifier,
                        manifest.EntryPoint, manifest.PayloadSha256, FileCount = manifest.Files.Length,
                        UncompressedBytes = manifest.Files.Sum(file => file.Length)
                    },
                    BundleManifest.JsonOptions));
                return 0;
            }

            var cache = Environment.GetEnvironmentVariable("AVDT_BUNDLE_CACHE") ??
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "AvaloniaDevTool",
                            "bundles");
            var store = new BundleStore(manifest,
                () =>
                    assembly.GetManifestResourceStream("AvaloniaDevTool.Packaging.bundle.zip")
                    ?? throw new InvalidDataException("The embedded bundle payload is missing."));
            var directory = await store.EnsureExtractedAsync(cache);
            if (args is ["--bundle-verify"])
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        Verified = true, RuntimeRoot = directory, manifest.RuntimeIdentifier,
                        manifest.PayloadSha256, FileCount = manifest.Files.Length
                    },
                    BundleManifest.JsonOptions));
                return 0;
            }

            var start = new ProcessStartInfo(Path.Combine(directory, manifest.EntryPoint))
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory
            };
            start.Environment["AVDT_RUNTIME_ROOT"] = directory;
            foreach (var argument in args)
                start.ArgumentList.Add(argument);
            using var child = Process.Start(start) ??
                              throw new IOException("The embedded console could not be started.");
            try
            {
                using var lifetime = new ChildProcessLifetime(child);
                using var stopping = new CancellationTokenSource();
                ConsoleCancelEventHandler onCancel = (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    stopping.Cancel();
                };
                Console.CancelKeyPress += onCancel;
                try
                {
                    try
                    {
                        await child.WaitForExitAsync(stopping.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Both console processes receive Ctrl+C. Keep the bootstrap alive while the
                        // console closes its MCP session, preview process and native worker.
                        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        try
                        {
                            await child.WaitForExitAsync(grace.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            child.Kill(entireProcessTree: true);
                            await child.WaitForExitAsync();
                        }
                    }

                    return child.ExitCode;
                }
                finally
                {
                    Console.CancelKeyPress -= onCancel;
                }
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                                          or JsonException or Win32Exception or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"bundle_start_failed: {error.Message}");
            return 1;
        }
    }
}
