using AvaloniaDevTool.NativeAutomation;
using Xunit;

namespace AvaloniaDevTool.NativeAutomation.Tests;

public sealed class NativeFileDialogValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "avdt-picker-paths-" + Guid.NewGuid().ToString("N"));

    public NativeFileDialogValidationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ExistingUnicodeFilesAndNewSavePathArePreservedExactly()
    {
        if (!OperatingSystem.IsWindows()) return;
        var first = Path.Combine(_root, "first 中文 file.txt");
        var second = Path.Combine(_root, "second.txt");
        await File.WriteAllTextAsync(first, "first", System.Text.Encoding.UTF8, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "second", System.Text.Encoding.UTF8, TestContext.Current.CancellationToken);

        Assert.Equal([first], await NativeFileDialogController.ValidatePathsAsync([first], NativeFileDialogKind.OpenFile, TestContext.Current.CancellationToken));
        Assert.Equal([first, second], await NativeFileDialogController.ValidatePathsAsync([first, second], NativeFileDialogKind.OpenFiles, TestContext.Current.CancellationToken));
        Assert.Equal([_root], await NativeFileDialogController.ValidatePathsAsync([_root], NativeFileDialogKind.Folder, TestContext.Current.CancellationToken));
        var save = Path.Combine(_root, "new 中文.md");
        Assert.Equal([save], await NativeFileDialogController.ValidatePathsAsync([save], NativeFileDialogKind.SaveFile, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(save));
    }

    [Theory]
    [InlineData("relative.txt")]
    [InlineData("https://example.invalid/document.txt")]
    [InlineData("shell:Downloads")]
    [InlineData("C:\\test\\*.txt")]
    [InlineData("C:\\test\\\"name.txt")]
    [InlineData("C:\\test\\name.txt:stream")]
    [InlineData("\\\\?\\C:\\test\\name.txt")]
    [InlineData("\\\\.\\NUL")]
    [InlineData("C:\\test\\CON.txt")]
    [InlineData("C:\\test\\LPT9.log")]
    [InlineData("C:\\test\\name.\\file.txt")]
    [InlineData("C:\\test\\name \\file.txt")]
    [InlineData("C:\\test\\line\nname.txt")]
    public async Task UnsupportedPathSyntaxIsRejectedBeforeAnyUiMutation(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var error = await Assert.ThrowsAsync<NativeAutomationException>(() =>
            NativeFileDialogController.ValidatePathsAsync([path], NativeFileDialogKind.SaveFile, TestContext.Current.CancellationToken));
        Assert.Equal("invalid_file_dialog_path", error.Code);
    }

    [Fact]
    public async Task WrongPathKindsAndMissingParentsFailPreflight()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(_root, "file.txt");
        await File.WriteAllTextAsync(file, "content", System.Text.Encoding.UTF8, TestContext.Current.CancellationToken);
        await Expect("file_dialog_file_not_found", [_root], NativeFileDialogKind.OpenFile);
        await Expect("file_dialog_folder_not_found", [file], NativeFileDialogKind.Folder);
        await Expect("file_dialog_save_parent_missing", [Path.Combine(_root, "missing", "save.txt")], NativeFileDialogKind.SaveFile);
        await Expect("invalid_file_dialog_paths", [], NativeFileDialogKind.OpenFile);
        await Expect("invalid_file_dialog_paths", [file, file], NativeFileDialogKind.SaveFile);
        await Expect("invalid_file_dialog_kind", [file], NativeFileDialogKind.Unknown);
    }

    [Fact]
    public async Task CancelledPreflightDoesNotCreateAFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_root, "cancelled.txt");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeFileDialogController.ValidatePathsAsync([path], NativeFileDialogKind.SaveFile, cancellation.Token));
        Assert.False(File.Exists(path));
    }

    private static async Task Expect(string code, string[] paths, NativeFileDialogKind kind)
    {
        var error = await Assert.ThrowsAsync<NativeAutomationException>(() => NativeFileDialogController.ValidatePathsAsync(paths, kind, TestContext.Current.CancellationToken));
        Assert.Equal(code, error.Code);
    }

    public void Dispose()
    {
        var fullPath = Path.GetFullPath(_root);
        var tempPath = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(fullPath), tempPath, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullPath).StartsWith("avdt-picker-paths-", StringComparison.Ordinal))
            throw new InvalidOperationException("The test cleanup path left its owned temporary directory.");
        Directory.Delete(fullPath, recursive: true);
    }
}
