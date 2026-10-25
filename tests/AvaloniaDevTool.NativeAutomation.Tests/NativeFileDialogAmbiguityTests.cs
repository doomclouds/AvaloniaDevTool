using System.Diagnostics;
using System.Text;
using AvaloniaDevTool.NativeAutomation;
using Xunit;

namespace AvaloniaDevTool.NativeAutomation.Tests;

public sealed partial class NativeAutomationIntegrationTests
{
    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "Two real Windows common item dialogs require an interactive desktop.")]
    public async Task Two_real_pickers_in_one_process_require_an_explicit_dialog_id_and_keep_mutations_isolated()
    {
        var directory = Path.Combine(Path.GetTempPath(), "avdt-multi-picker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var target = Path.Combine(directory, "selected-first.txt");
            await File.WriteAllTextAsync(target, "first picker target", new UTF8Encoding(false), TestContext.Current.CancellationToken);
            using var fixture = await StartMultiPickerFixtureAsync(directory);
            await using var native = await AttachAsync(fixture);
            var controller = new NativeFileDialogController(native);
            var original = await WaitForTwoPickersAsync(controller);
            Assert.All(original, dialog =>
            {
                Assert.Equal(fixture.Id, dialog.Window.ProcessId);
                Assert.Equal("windows-common-item-dialog-v1", dialog.ProviderProfile);
                Assert.Equal("#32770", dialog.Window.ClassName);
                Assert.True(dialog.Window.IsOwned);
                Assert.True(dialog.Window.IsModal);
                Assert.True(dialog.Window.IsEnabled);
            });
            Assert.NotEqual(original[0].DialogId, original[1].DialogId);
            Assert.NotEqual(original[0].Window.Handle, original[1].Window.Handle);
            Assert.NotEqual(original[0].Window.OwnerHandle, original[1].Window.OwnerHandle);
            var first = Assert.Single(original, dialog => dialog.PathInputValue == "first-untouched.txt");
            var second = Assert.Single(original, dialog => dialog.PathInputValue == "second-untouched.txt");

            var ambiguous = await Assert.ThrowsAsync<NativeAutomationException>(() =>
                controller.WaitForDialogAsync(5_000, TestContext.Current.CancellationToken));

            Assert.Equal("ambiguous_file_dialog", ambiguous.Code);
            foreach (var before in original)
            {
                var after = await controller.InspectAsync(before.DialogId, TestContext.Current.CancellationToken);
                Assert.Equal("Open", after.State);
                Assert.Equal(before.PathInputValue, after.PathInputValue);
                Assert.Null(after.LastAction);
                Assert.Empty(after.RequestedPaths);
            }

            var changed = await controller.SetPathsAsync(first.DialogId, [target], NativeFileDialogKind.OpenFile,
                TestContext.Current.CancellationToken);

            Assert.Equal(first.DialogId, changed.DialogId);
            Assert.Equal(target, changed.PathInputValue);
            Assert.Equal([target], changed.RequestedPaths);
            await AssertOtherPickerUnchangedAsync(controller, second);
            await controller.CancelAsync(first.DialogId, TestContext.Current.CancellationToken);
            Assert.Equal("CancelledDialogClosed", (await controller.WaitForClosedAsync(first.DialogId, 5_000,
                TestContext.Current.CancellationToken)).State);
            await AssertOtherPickerUnchangedAsync(controller, second);
            var remaining = await controller.WaitForDialogAsync(5_000, TestContext.Current.CancellationToken);
            Assert.Equal(second.DialogId, remaining.DialogId);
            Assert.Equal(second.Window.Handle, remaining.Window.Handle);
            Assert.Equal(second.PathInputValue, remaining.PathInputValue);
            await controller.CancelAsync(second.DialogId, TestContext.Current.CancellationToken);
            Assert.Equal("CancelledDialogClosed", (await controller.WaitForClosedAsync(second.DialogId, 5_000,
                TestContext.Current.CancellationToken)).State);
            Assert.Equal("first picker target", await File.ReadAllTextAsync(target, Encoding.UTF8, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task AssertOtherPickerUnchangedAsync(NativeFileDialogController controller, NativeFileDialogInfo before)
    {
        var after = await controller.InspectAsync(before.DialogId, TestContext.Current.CancellationToken);
        Assert.Equal("Open", after.State);
        Assert.Equal(before.Window.Handle, after.Window.Handle);
        Assert.Equal(before.Window.OwnerHandle, after.Window.OwnerHandle);
        Assert.Equal(before.PathInputValue, after.PathInputValue);
        Assert.Equal(before.FilterValue, after.FilterValue);
        Assert.Null(after.LastAction);
        Assert.Empty(after.RequestedPaths);
        Assert.Empty(after.Prompts);
    }

    private static async Task<NativeFileDialogInfo[]> WaitForTwoPickersAsync(NativeFileDialogController controller)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            var dialogs = await controller.GetDialogsAsync(TestContext.Current.CancellationToken);
            if (dialogs.Length == 2) return dialogs;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Two supported common item dialogs did not appear in the same fixture process.");
    }

    private static async Task<Process> StartMultiPickerFixtureAsync(string directory)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(ArtifactPath("AvaloniaDevTool.NativeAutomation.Fixture.dll"));
        start.ArgumentList.Add("--native-multi-picker");
        start.ArgumentList.Add(directory);
        var process = new OwnedProcess(start);
        process.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var handshake = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (handshake is null)
                throw new InvalidOperationException(await process.StandardError.ReadToEndAsync(timeout.Token));
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}
