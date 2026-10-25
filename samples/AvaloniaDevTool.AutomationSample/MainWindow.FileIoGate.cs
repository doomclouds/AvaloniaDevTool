using System.Text;
using System.Text.Json;

namespace AvaloniaDevTool.AutomationSample;

public sealed partial class MainWindow
{
    private static async Task WaitForFileIoGateAsync(string operation, string path)
    {
        if (Program.IoGateDirectory is not { } directory) return;
        var armed = Path.Combine(directory, "arm");
        if (!File.Exists(armed)) return;

        // This opt-in test gate runs after the real picker returns and before real I/O.
        // It only coordinates a race; it never supplies a file or manufactures an error.
        Directory.CreateDirectory(directory);
        File.Delete(armed);
        var entered = Path.Combine(directory, "entered.json");
        var temporary = Path.Combine(directory, "entered.tmp");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new { operation, path }), new UTF8Encoding(false));
        File.Move(temporary, entered, overwrite: true);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (!File.Exists(Path.Combine(directory, "release")))
                await Task.Delay(25, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The test-only file I/O gate was not released within 30 seconds.");
        }
    }
}
