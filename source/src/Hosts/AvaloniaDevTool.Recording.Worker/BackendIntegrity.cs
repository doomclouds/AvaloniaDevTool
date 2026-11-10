using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Recording.Worker;

internal static class BackendIntegrity
{
    internal static void Verify()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ScreenRecorderLib.dll");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "ScreenRecorderLib.manifest.json")));
        var expected = manifest.RootElement.GetProperty("sha256").GetString();
        using var input = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(input));
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("recording_backend_integrity_failed: The recording backend SHA-256 does not match its manifest.");
    }
}
