using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace AvaloniaDevTool.AutomationSample;

public sealed partial class MainWindow
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly TextBlock _fileStatus = Label("FileStatus", "Ready");
    private readonly TextBlock _documentPath = Label("DocumentPath", "(untitled)");
    private readonly TextBlock _selectedFiles = Label("SelectedFiles", "(none)");
    private readonly TextBlock _folderPath = Label("FolderPath", "(none)");
    private readonly TextBox _editor = Identify(new TextBox
    {
        Text = "Initial UTF-8 document: 你好，Avalonia!\nEdit this text before saving.\n",
        AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        Height = 260, HorizontalAlignment = HorizontalAlignment.Stretch
    }, "DocumentEditor");
    private readonly List<Button> _fileButtons = [];
    private bool _fileOperation;
    private string? _currentDocumentPath;
    private string[] _operationPaths = [];
    private bool _operationCancelled;
    private long _operationSequence;

    private Control BuildFilesPage()
    {
        var buttons = Row(
            FileButton("OpenSingleButton", "Open file", "openSingle", OpenSingleAsync),
            FileButton("OpenMultipleButton", "Open multiple files", "openMultiple", OpenMultipleAsync),
            FileButton("OpenFolderButton", "Select folder", "openFolder", OpenFolderAsync),
            FileButton("SaveAsButton", "Save as UTF-8", "saveAs", SaveAsAsync));
        var content = new StackPanel
        {
            Margin = new Thickness(8, 12), Spacing = 10,
            Children =
            {
                Label("FilesInstructions", "Real OS pickers. Open reads UTF-8 into the editor; Save writes and truncates the chosen file. Cancelling keeps the document unchanged."),
                buttons, _fileStatus,
                Label("DocumentPathLabel", "Current document:"), _documentPath,
                _editor,
                Label("SelectedFilesLabel", "Opened file paths:"), _selectedFiles,
                Label("FolderPathLabel", "Selected folder:"), _folderPath,
                ActionButton("ResetDocumentButton", "Reset document", () =>
                {
                    if (_fileOperation) return;
                    _editor.Text = "Initial UTF-8 document: 你好，Avalonia!\nEdit this text before saving.\n";
                    _currentDocumentPath = null;
                    _documentPath.Text = "(untitled)";
                    _selectedFiles.Text = "(none)";
                    _fileStatus.Text = "Document reset";
                })
            }
        };
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private Button FileButton(string id, string text, string operation, Func<Task> action)
    {
        var button = ActionButton(id, text, async () => await RunFileOperationAsync(operation, action));
        _fileButtons.Add(button);
        return button;
    }

    private async Task RunFileOperationAsync(string operation, Func<Task> action)
    {
        if (_fileOperation) return;
        _fileOperation = true;
        _operationPaths = [];
        _operationCancelled = false;
        string? operationError = null;
        foreach (var button in _fileButtons) button.IsEnabled = false;
        _editor.IsReadOnly = true;
        try
        {
            await action();
        }
        catch (Exception error)
        {
            operationError = $"{error.GetType().Name}: {error.Message}";
            _fileStatus.Text = $"Error: {operationError}";
        }
        finally
        {
            try
            {
                await WriteOperationResultAsync(operation, operationError);
            }
            catch (Exception error)
            {
                _fileStatus.Text += $"; Result file error: {error.GetType().Name}: {error.Message}";
            }
            _editor.IsReadOnly = false;
            foreach (var button in _fileButtons) button.IsEnabled = true;
            _fileOperation = false;
        }
    }

    private async Task OpenSingleAsync()
    {
        if (!StorageProvider.CanOpen) throw new NotSupportedException("The real platform storage provider cannot open files.");
        _fileStatus.Text = "Waiting: Open single";
        using var start = await GetStartFolderAsync();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Automation sample - Open single", AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = [FilePickerFileTypes.TextPlain, FilePickerFileTypes.All]
        });
        if (files.Count == 0) { _operationCancelled = true; _fileStatus.Text = "Cancelled: Open single"; return; }
        try
        {
            _operationPaths = files.Select(GetPath).ToArray();
            var text = await ReadUtf8Async(files[0]);
            var path = GetPath(files[0]);
            _editor.Text = text;
            _currentDocumentPath = path;
            _documentPath.Text = path;
            _selectedFiles.Text = path;
            _fileStatus.Text = $"Opened: 1 file; Characters={text.Length}; Path={path}";
        }
        finally
        {
            foreach (var file in files) file.Dispose();
        }
    }

    private async Task OpenMultipleAsync()
    {
        if (!StorageProvider.CanOpen) throw new NotSupportedException("The real platform storage provider cannot open files.");
        _fileStatus.Text = "Waiting: Open multiple";
        using var start = await GetStartFolderAsync();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Automation sample - Open multiple", AllowMultiple = true,
            SuggestedStartLocation = start,
            FileTypeFilter = [FilePickerFileTypes.TextPlain, FilePickerFileTypes.All]
        });
        if (files.Count == 0) { _operationCancelled = true; _fileStatus.Text = "Cancelled: Open multiple"; return; }
        try
        {
            _operationPaths = files.Select(GetPath).ToArray();
            // Apply the document only after every selected file has been read successfully.
            var contents = new List<string>();
            foreach (var file in files) contents.Add(await ReadUtf8Async(file));
            var text = string.Join("\n--- next file ---\n", contents);
            _editor.Text = text;
            _currentDocumentPath = files.Count == 1 ? GetPath(files[0]) : null;
            _documentPath.Text = _currentDocumentPath ?? $"(combined {files.Count} files)";
            _selectedFiles.Text = string.Join("\n", files.Select(GetPath));
            _fileStatus.Text = $"Opened: {files.Count} files; Characters={text.Length}";
        }
        finally
        {
            foreach (var file in files) file.Dispose();
        }
    }

    private async Task OpenFolderAsync()
    {
        if (!StorageProvider.CanPickFolder) throw new NotSupportedException("The real platform storage provider cannot select folders.");
        _fileStatus.Text = "Waiting: Open folder";
        using var start = await GetStartFolderAsync();
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Automation sample - Open folder", AllowMultiple = false,
            SuggestedStartLocation = start
        });
        if (folders.Count == 0) { _operationCancelled = true; _fileStatus.Text = "Cancelled: Open folder"; return; }
        try
        {
            _operationPaths = folders.Select(GetPath).ToArray();
            _folderPath.Text = GetPath(folders[0]);
            _fileStatus.Text = $"Selected folder: {_folderPath.Text}";
        }
        finally
        {
            foreach (var folder in folders) folder.Dispose();
        }
    }

    private async Task SaveAsAsync()
    {
        if (!StorageProvider.CanSave) throw new NotSupportedException("The real platform storage provider cannot save files.");
        var content = _editor.Text ?? "";
        _fileStatus.Text = "Waiting: Save as";
        using var start = await GetStartFolderAsync();
        using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Automation sample - Save UTF-8 document", ShowOverwritePrompt = true,
            SuggestedStartLocation = start,
            SuggestedFileName = _currentDocumentPath is null ? "saved-document.txt" : Path.GetFileName(_currentDocumentPath),
            DefaultExtension = "txt", FileTypeChoices = [FilePickerFileTypes.TextPlain, FilePickerFileTypes.All]
        });
        if (file is null) { _operationCancelled = true; _fileStatus.Text = "Cancelled: Save as"; return; }
        _operationPaths = [GetPath(file)];
        await WaitForFileIoGateAsync("write", GetPath(file));

        // OpenWriteAsync does not guarantee truncation. Explicitly set the resulting length.
        await using (var stream = await file.OpenWriteAsync())
        {
            if (!stream.CanSeek)
                throw new NotSupportedException("The selected file cannot be truncated safely because its stream is not seekable.");
            var bytes = Utf8.GetBytes(content);
            stream.Position = 0;
            await stream.WriteAsync(bytes);
            stream.SetLength(bytes.Length);
            await stream.FlushAsync();
        }
        var savedPath = GetPath(file);
        _currentDocumentPath = savedPath;
        _documentPath.Text = savedPath;
        _fileStatus.Text = $"Saved: Characters={content.Length}; Bytes={Utf8.GetByteCount(content)}; Path={savedPath}";
    }

    private async Task<IStorageFolder?> GetStartFolderAsync() => Program.TestRoot is { } root
        ? await StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(root)))
        : null;

    private static string GetPath(IStorageItem item) => item.TryGetLocalPath() ?? item.Path.ToString();

    private static async Task<string> ReadUtf8Async(IStorageFile file)
    {
        await WaitForFileIoGateAsync("read", GetPath(file));
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false);
        var text = await reader.ReadToEndAsync();
        return text.StartsWith('\uFEFF') ? text[1..] : text;
    }

    private async Task WriteOperationResultAsync(string action, string? error)
    {
        if (Program.ResultFile is not { } resultFile) return;
        var content = _editor.Text ?? "";
        var bytes = Utf8.GetBytes(content);
        var result = new
        {
            sequence = ++_operationSequence,
            action,
            cancelled = _operationCancelled,
            selectedPaths = _operationPaths,
            documentPath = _currentDocumentPath,
            content,
            byteLength = bytes.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            status = _fileStatus.Text,
            error,
            timestampUtc = DateTimeOffset.UtcNow
        };
        var parent = Path.GetDirectoryName(resultFile)!;
        Directory.CreateDirectory(parent);
        var temporaryFile = Path.Combine(parent, $".{Path.GetFileName(resultFile)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryFile, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), Utf8);
            File.Move(temporaryFile, resultFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
        }
    }
}
