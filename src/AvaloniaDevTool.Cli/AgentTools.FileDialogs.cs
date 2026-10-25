using System.ComponentModel;
using System.Runtime.CompilerServices;
using AvaloniaDevTool.NativeAutomation;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class AgentTools
{
    private readonly ConditionalWeakTable<NativeAutomationSession, NativeFileDialogController> _dialogs = new();

    private Task<T> WithDialogsAsync<T>(CancellationToken token, Func<NativeFileDialogController, CancellationToken, Task<T>> action) =>
        WithNativeAsync(token, (native, cancellation) => action(_dialogs.GetValue(native, static owner => new(owner)), cancellation));

    [McpServerTool(Name = "file-dialogs", ReadOnly = true, Destructive = false), Description("Lists real Windows common file/folder pickers belonging to the attached application. Native dialogs remain accessible while a managed input call is waiting on a modal window.")]
    public Task<NativeFileDialogInfo[]> FileDialogs(CancellationToken cancellationToken = default) =>
        WithDialogsAsync(cancellationToken, (dialogs, token) => dialogs.GetDialogsAsync(token));

    [McpServerTool(Name = "file-dialog-inspect", ReadOnly = true, Destructive = false), Description("Reads the current picker, file type filter, filename field and any owned confirmation prompts. Dialog closure does not verify application file I/O.")]
    public Task<NativeFileDialogInfo> InspectFileDialog(string dialogId, CancellationToken cancellationToken = default) =>
        WithDialogsAsync(cancellationToken, (dialogs, token) => dialogs.InspectAsync(dialogId, token));

    [McpServerTool(Name = "file-dialog-set-paths", ReadOnly = false, Destructive = true), Description("Enters and reads back absolute paths in a real native picker without accepting it. kind is OpenFile, OpenFiles, SaveFile or Folder. Call accept separately and verify the application's loaded/saved result.")]
    public Task<NativeFileDialogInfo> SetFileDialogPaths(string dialogId, string[] paths, string kind = "OpenFile", CancellationToken cancellationToken = default)
    {
        if (!Enum.GetNames<NativeFileDialogKind>().Contains(kind, StringComparer.OrdinalIgnoreCase) ||
            !Enum.TryParse<NativeFileDialogKind>(kind, true, out var parsed) || parsed == NativeFileDialogKind.Unknown)
            throw new McpException("invalid_argument: kind must be OpenFile, OpenFiles, SaveFile or Folder.");
        return WithDialogsAsync(cancellationToken, (dialogs, token) => dialogs.SetPathsAsync(dialogId, paths, parsed, token));
    }

    [McpServerTool(Name = "file-dialog-set-filter", ReadOnly = false, Destructive = true), Description("Selects an exact displayed file type name or a one-based filter index in a real native picker. The two selection arguments are mutually exclusive.")]
    public Task<NativeFileDialogInfo> SetFileDialogFilter(string dialogId, string? filterName = null, int? filterIndex = null,
        CancellationToken cancellationToken = default) =>
        WithDialogsAsync(cancellationToken, (dialogs, token) => dialogs.SetFilterAsync(dialogId, filterName, filterIndex, token));

    [McpServerTool(Name = "file-dialog-accept", ReadOnly = false, Destructive = true), Description("Presses the native picker's Open/Save/Select Folder action. An overwrite prompt remains pending for a separate explicit response. A closed picker does not prove the application completed its read/write.")]
    public Task<NativeFileDialogInfo> AcceptFileDialog(string dialogId, CancellationToken cancellationToken = default) =>
        WithDialogsAsync(cancellationToken, (dialogs, token) => dialogs.AcceptAsync(dialogId, token));

    [McpServerTool(Name = "file-dialog-cancel", ReadOnly = false, Destructive = true), Description("Cancels the specified real native picker. Verify the application preserved its document state when cancellation completes.")]
    public Task<NativeFileDialogInfo> CancelFileDialog(string dialogId, CancellationToken cancellationToken = default) =>
        WithDialogsAsync(cancellationToken, (dialogs, token) => dialogs.CancelAsync(dialogId, token));

    [McpServerTool(Name = "file-dialog-overwrite", ReadOnly = false, Destructive = true), Description("Answers a verified overwrite prompt by its freshly inspected id. overwrite=true replaces the previously selected existing save target; false refuses replacement. Other confirmation dialogs are rejected.")]
    public Task<NativeFileDialogInfo> OverwriteFileDialog(string dialogId, string promptId, bool overwrite, CancellationToken cancellationToken = default) =>
        WithDialogsAsync(cancellationToken, (dialogs, token) => dialogs.RespondToOverwriteAsync(dialogId, promptId, overwrite, token));

    [McpServerTool(Name = "file-dialog-wait", ReadOnly = true, Destructive = false), Description("Waits for a native picker to open, or for the specified dialog to close. Cancellation and timeout are explicit failures; application I/O must be checked independently.")]
    public Task<NativeFileDialogInfo> WaitForFileDialog(string? dialogId = null, int timeoutMs = 10000, CancellationToken cancellationToken = default) =>
        WithDialogsAsync(cancellationToken, (dialogs, token) => dialogId is null ? dialogs.WaitForDialogAsync(timeoutMs, token) : dialogs.WaitForClosedAsync(dialogId, timeoutMs, token));
}
