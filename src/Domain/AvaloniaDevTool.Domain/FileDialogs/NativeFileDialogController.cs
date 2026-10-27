using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace AvaloniaDevTool.NativeAutomation;

/// <summary>
/// Operates real Windows common item dialogs through the scoped native backend.
/// No StorageProvider replacement, global keyboard input, or title matching is used.
/// </summary>
public sealed class NativeFileDialogController(INativeAutomationTarget session, NativeFileDialogOptions? options = null)
{
    private readonly NativeFileDialogOptions _options = options ?? new();
    private readonly ConcurrentDictionary<string, DialogState> _dialogs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public async Task<NativeFileDialogInfo[]> GetDialogsAsync(CancellationToken cancellationToken = default)
    {
        var windows = await session.GetWindowsAsync(cancellationToken);
        var result = new List<NativeFileDialogInfo>();
        foreach (var window in windows.Where(IsCommonDialog))
        {
            try
            {
                var info = await ReadDialogAsync(window, windows, false, cancellationToken);
                if (info is not null)
                    result.Add(info);
            }
            catch (NativeAutomationException exception) when (exception.Code is "stale_node" or "out_of_scope")
            {
                // A disappearing candidate is not actionable. A later discovery
                // obtains a new identity rather than rebinding this window ID.
            }
        }

        return [.. result];
    }

    public async Task<NativeFileDialogInfo> InspectAsync(string dialogId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dialogId);
        for (var attempt = 0;; attempt++)
        {
            var windows = await session.GetWindowsAsync(cancellationToken);
            var window = windows.SingleOrDefault(item => item.NodeId == dialogId);
            if (window is null)
            {
                if (!_dialogs.TryGetValue(dialogId, out var previous))
                    throw Error("stale_file_dialog",
                        "The dialog ID is unknown or stale. Discover the currently open dialogs again.");
                return previous.Info with
                {
                    State = previous.Intent switch
                    {
                        "accept" or "overwrite" => "AcceptedDialogClosed",
                        "cancel" => "CancelledDialogClosed",
                        _ => "Closed"
                    },
                    Prompts = [],
                    LastAction = previous.Action,
                    ApplicationIoVerified = false,
                    Note = "The original dialog is closed. Verify the application result and file I/O independently."
                };
            }

            try
            {
                return await ReadDialogAsync(window, windows, true, cancellationToken)
                       ?? throw Error("unsupported_file_dialog",
                           "The window is not a supported Windows common item dialog.");
            }
            catch (NativeAutomationException exception) when (attempt < 2 &&
                                                              exception.Code is "stale_node" or "out_of_scope"
                                                                  or "file_dialog_tree_truncated")
            {
                // Editing filenames can rebuild child controls. Retry only the read
                // against the same dialog ID, including transient incomplete walks;
                // mutations and replacement IDs are never retried. Persistent tree
                // budget limits still fail without any action.
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    public async Task<NativeFileDialogInfo> SetPathsAsync(string dialogId,
        IReadOnlyList<string> paths,
        NativeFileDialogKind kind = NativeFileDialogKind.OpenFile,
        CancellationToken cancellationToken = default)
    {
        var normalized = await ValidatePathsAsync(paths, kind, cancellationToken);
        var overwriteExpected = kind == NativeFileDialogKind.SaveFile &&
                                await IsExistingFileAsync(normalized[0], cancellationToken);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var info = await RequireOpenAsync(dialogId, cancellationToken);
            var value = normalized.Length == 1
                ? normalized[0]
                : string.Join(" ", normalized.Select(path => $"\"{path}\""));
            var action = await PerformAsync(info.PathInputNodeId!, "SetValue", value, cancellationToken);
            var readback = await session.GetElementAsync(info.PathInputNodeId!, cancellationToken);
            if (!string.Equals(readback.Value, value, StringComparison.Ordinal))
                throw Error("file_dialog_readback_mismatch",
                    "The filename field did not retain the exact requested value. The dialog was not accepted.");
            var updated = info with
            {
                RequestedKind = kind,
                RequestedPaths = normalized,
                PathInputValue = value,
                LastAction = action,
                Note = "Paths were entered and read back. No Open, Save, or Select Folder action has been performed."
            };
            _dialogs[dialogId] = new(updated, value, null, action, overwriteExpected);
            return updated;
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>Selects an exact displayed file type, or a one-based item index.</summary>
    public async Task<NativeFileDialogInfo> SetFilterAsync(string dialogId,
        string? filterName = null,
        int? filterIndex = null,
        CancellationToken cancellationToken = default)
    {
        if ((filterName is null) == (filterIndex is null) || filterIndex is < 1 || filterName?.Length == 0)
            throw Error("invalid_filter", "Specify either an exact filter name or a positive one-based filter index.");
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var info = await RequireOpenAsync(dialogId, cancellationToken);
            if (info.FilterNodeId is null)
                throw Error("unsupported_file_filter", "The verified dialog profile has no file type combo box.");
            var combo = await session.GetElementAsync(info.FilterNodeId, cancellationToken);
            NativeActionResult? expansion = null;
            if (combo.SupportedPatterns.Contains("ExpandCollapse", StringComparer.OrdinalIgnoreCase))
                expansion = await PerformAsync(combo.NodeId, "Expand", null, cancellationToken);
            var options = await ReadFilterOptionsAsync(combo.NodeId, expansion, cancellationToken);
            NativeElementInfo? selected;
            if (filterIndex is { } index)
                selected = index <= options.Length ? options[index - 1] : null;
            else
            {
                var matches = options.Where(item => string.Equals(item.Name, filterName, StringComparison.Ordinal))
                    .ToArray();
                if (matches.Length > 1)
                    throw Error("ambiguous_file_filter",
                        "More than one filter has the requested name. Use a one-based index.");
                selected = matches.SingleOrDefault();
            }

            if (selected is null)
                throw Error("file_filter_not_found",
                    "The requested file type is not exposed in this dialog's filter list.");
            await PerformAsync(selected.NodeId, "Select", null, cancellationToken);
            var updated = await InspectAsync(dialogId, cancellationToken);
            if (!string.Equals(updated.FilterValue, selected.Name, StringComparison.Ordinal))
            {
                var item = await session.GetElementAsync(selected.NodeId, cancellationToken);
                if (item.IsSelected != true)
                    throw Error("file_filter_readback_mismatch", "The filter selection could not be verified.");
            }

            return updated with
            {
                Note =
                "The file type selection was verified. Windows may apply its default extension; verify the final application path after saving."
            };
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<NativeElementInfo[]> ReadFilterOptionsAsync(string comboId,
        NativeActionResult? expansion,
        CancellationToken cancellationToken)
    {
        var visibilityWait = Stopwatch.StartNew();
        var usedDropDown = false;
        while (true)
        {
            if (expansion is { Status: "pending", OperationId: { } operationId })
            {
                expansion = await session.GetActionStatusAsync(operationId, cancellationToken);
                if (expansion.Status == "failed")
                    throw Error(expansion.ErrorCode ?? "file_filter_expand_failed",
                        expansion.ErrorMessage ?? "The filter could not be expanded.");
            }

            // Provider requests retain their own bounded timeout. A visibility
            // poll must not cancel a live RPC and destroy the shared worker/IDs.
            var tree = await session.GetTreeAsync(new NativeTreeQuery
                {
                    RootNodeId = comboId, MaxDepth = 6, MaxNodes = 200
                },
                cancellationToken);
            if (tree.Truncated)
                throw Error("file_filter_truncated", "The filter list was truncated; no filter was selected.");
            var choices = tree.Nodes.Where(item => item.ControlType == "ListItem").ToArray();
            if (choices.Length > 0)
                return choices;
            if (visibilityWait.Elapsed >= TimeSpan.FromSeconds(3))
            {
                // Some common-dialog providers acknowledge Expand while the combo
                // remains collapsed. Invoke its own verified button once, never toggle
                // an expanded or still-pending control and never guess by its title.
                var combo = tree.Nodes.SingleOrDefault(item => item.NodeId == comboId);
                var buttons = tree.Nodes.Where(item => item.ParentNodeId == comboId &&
                                                       item.ControlType == "Button" &&
                                                       item.AutomationId == "DropDown" &&
                                                       item.SupportedPatterns.Contains("Invoke",
                                                           StringComparer.OrdinalIgnoreCase)).ToArray();
                if (!usedDropDown && expansion is { Status: "completed" } &&
                    combo?.ExpandCollapseState == "Collapsed" && buttons.Length == 1)
                {
                    expansion = await PerformAsync(buttons[0].NodeId, "Invoke", null, cancellationToken);
                    usedDropDown = true;
                    visibilityWait.Restart();
                    continue;
                }

                throw Error("file_filter_timeout",
                    "The expanded file type choices did not appear before the deadline. No filter was selected.");
            }

            // Expansion is issued once; subsequent iterations observe only.
            await Task.Delay(40, cancellationToken);
        }
    }

    public async Task<NativeFileDialogInfo> AcceptAsync(string dialogId, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var info = await RequireOpenAsync(dialogId, cancellationToken);
            var previous = _dialogs[dialogId];
            if (previous.PathText is not null &&
                !string.Equals(info.PathInputValue, previous.PathText, StringComparison.Ordinal))
                throw Error("file_dialog_selection_changed",
                    "The filename field changed after SetPaths. Inspect and set the intended paths again before accepting.");
            var action = await PerformAsync(info.AcceptNodeId!, "Invoke", null, cancellationToken);
            _dialogs[dialogId] = previous with { Intent = "accept", Action = action };
            return await InspectAfterActionAsync(dialogId, cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<NativeFileDialogInfo> CancelAsync(string dialogId, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var info = await RequireOpenAsync(dialogId, cancellationToken);
            var action = await PerformAsync(info.CancelNodeId!, "Invoke", null, cancellationToken);
            _dialogs[dialogId] = _dialogs[dialogId] with { Intent = "cancel", Action = action };
            return await InspectAfterActionAsync(dialogId, cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Answers only an inspected Yes/No prompt owned by this picker after submitting
    /// an existing SaveFile target. The prompt ID prevents acting on a replacement.
    /// </summary>
    public async Task<NativeFileDialogInfo> RespondToOverwriteAsync(string dialogId,
        string promptId,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var info = await InspectAsync(dialogId, cancellationToken);
            var prompt = info.Prompts.SingleOrDefault(item => item.Window.NodeId == promptId)
                         ?? throw Error("stale_file_dialog_prompt",
                             "The specified prompt is no longer owned by this dialog. Inspect the dialog again.");
            if (!prompt.ExpectedOverwrite || prompt.YesNodeId is null || prompt.NoNodeId is null)
                throw Error("unsupported_file_dialog_prompt",
                    "This is not a verified Yes/No prompt after an existing SaveFile target. Inspect its native tree before responding.");
            var action = await PerformAsync(overwrite ? prompt.YesNodeId : prompt.NoNodeId,
                "Invoke",
                null,
                cancellationToken);
            _dialogs[dialogId] = _dialogs[dialogId] with { Intent = overwrite ? "overwrite" : null, Action = action };
            return await InspectAfterActionAsync(dialogId, cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<NativeFileDialogInfo> WaitForDialogAsync(int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeoutMs);
        var wait = Stopwatch.StartNew();
        ExceptionDispatchInfo? lastReadFailure = null;
        do
        {
            // Polling deadlines must not cancel a live provider RPC and invalidate
            // the shared session. Each RPC retains its own bounded timeout.
            try
            {
                var dialogs = await GetDialogsAsync(cancellationToken);
                lastReadFailure = null;
                if (dialogs.Length > 1)
                    throw Error("ambiguous_file_dialog",
                        "Multiple supported file dialogs belong to the target. Select a dialog ID explicitly.");
                if (dialogs.Length == 1)
                    return dialogs[0];
            }
            catch (NativeAutomationException error) when (error.Code is "file_dialog_tree_truncated" or "stale_node")
            {
                // Discovery may see a picker while its provider is rebuilding the
                // tree. Only this wait retries the read; an incomplete result is
                // never interpreted as absence, a verified identity, or an action.
                lastReadFailure = ExceptionDispatchInfo.Capture(error);
            }

            var remaining = timeoutMs - wait.Elapsed.TotalMilliseconds;
            if (remaining <= 0)
                break;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, remaining)), cancellationToken);
        } while (wait.Elapsed.TotalMilliseconds < timeoutMs);

        lastReadFailure?.Throw();
        throw Error("file_dialog_timeout", "No supported file dialog appeared before the deadline.");
    }

    /// <summary>Returns on closure or a modal prompt; it never answers prompts automatically.</summary>
    public async Task<NativeFileDialogInfo> WaitForClosedAsync(string dialogId,
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeoutMs);
        var wait = Stopwatch.StartNew();
        do
        {
            var info = await InspectAsync(dialogId, cancellationToken);
            if (IsClosed(info) || info.Prompts.Length > 0 || info.State == "ActionFailed")
                return info;
            var remaining = timeoutMs - wait.Elapsed.TotalMilliseconds;
            if (remaining <= 0)
                break;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, remaining)), cancellationToken);
        } while (wait.Elapsed.TotalMilliseconds < timeoutMs);

        throw Error("file_dialog_timeout",
            "The original file dialog remained open. Inspect it for validation errors or selection changes.");
    }

    private async Task<NativeFileDialogInfo> RequireOpenAsync(string dialogId, CancellationToken cancellationToken)
    {
        var info = await InspectAsync(dialogId, cancellationToken);
        if (IsClosed(info))
            throw Error("stale_file_dialog",
                "The original file dialog has closed. Discover its replacement rather than reusing this ID.");
        if (info.Prompts.Length != 0)
            throw Error("file_dialog_prompt_active",
                "The picker owns an active modal prompt. Inspect and answer that prompt before changing the picker.");
        if (info.State == "ActionPending")
            throw Error("file_dialog_action_pending",
                "A native action is still pending. Wait or inspect its operation before changing the picker.");
        return info;
    }

    private async Task<NativeFileDialogInfo> InspectAfterActionAsync(string dialogId,
        CancellationToken cancellationToken)
    {
        // A modal Invoke may still be pending; this delay is bounded and does not
        // imply that the application has completed the corresponding I/O.
        await Task.Delay(100, cancellationToken);
        return await InspectAsync(dialogId, cancellationToken);
    }

    private async Task<NativeActionResult> PerformAsync(string nodeId,
        string action,
        string? value,
        CancellationToken cancellationToken)
    {
        var result =
            await session.ExecuteAsync(new NativeActionRequest { NodeId = nodeId, Action = action, Value = value },
                cancellationToken);
        if (result.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
            throw Error(result.ErrorCode ?? "file_dialog_action_failed",
                result.ErrorMessage ?? "The native dialog action failed.");
        return result;
    }

    private async Task<NativeFileDialogInfo?> ReadDialogAsync(NativeWindowInfo window,
        NativeWindowInfo[] windows,
        bool requireProfile,
        CancellationToken cancellationToken)
    {
        if (!IsCommonDialog(window))
            return null;
        if (_options.MaxDepth is < 0 or > 32 || _options.MaxNodes is < 1 or > 5000)
            throw Error("invalid_file_dialog_budget",
                "File dialog budgets must use MaxDepth 0..32 and MaxNodes 1..5000.");
        var tree = await session.GetTreeAsync(new NativeTreeQuery
            {
                RootNodeId = window.NodeId,
                MaxDepth = _options.MaxDepth,
                MaxNodes = _options.MaxNodes
            },
            cancellationToken);
        if (tree.Truncated)
            throw Error("file_dialog_tree_truncated",
                "The native picker tree is incomplete. Its control identities cannot be verified safely; no action was performed.");
        var byId = tree.Nodes.ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        var nodes = tree.Nodes.Where(item => IsInsideRootDialog(item, window.NodeId, byId)).ToArray();
        var edits = nodes.Where(item => item.ControlType == "Edit" &&
                                        (item.AutomationId is "1148" or "1152" || HasAncestor(item,
                                            byId,
                                            ancestor => ancestor.AutomationId == "FileNameControlHost")))
            .ToArray();
        var accept = UniqueButton(nodes, "1");
        var cancel = UniqueButton(nodes, "2");
        if (edits.Length != 1 || accept is null || cancel is null ||
            !nodes.Any(item => item.ClassName == "DUIViewWndClassName"))
        {
            if (requireProfile)
                throw Error("unsupported_file_dialog",
                    "The picker does not match the verified filename/edit and confirm/cancel control structure. No action was performed.");
            return null;
        }

        var filterMatches = nodes.Where(item => item.ControlType == "ComboBox" &&
                                                item.AutomationId is "FileTypeControlHost" or "FilesOfTypeControlHost"
                                                    or "1136").ToArray();
        var filter = filterMatches.Length == 1 ? filterMatches[0] : null;
        _dialogs.TryGetValue(window.NodeId, out var previous);
        var captured = previous;
        if (previous?.Action is { Status: "pending", OperationId: { } operationId })
        {
            var status = await session.GetActionStatusAsync(operationId, cancellationToken);
            previous = previous with { Action = status };
        }

        var prompts = new List<NativeFileDialogPrompt>();
        foreach (var promptWindow in windows.Where(item => item.NodeId != window.NodeId && IsCommonDialog(item) &&
                                                           IsOwnedBy(item, window.Handle, windows)))
        {
            var promptTree = await session.GetTreeAsync(new NativeTreeQuery
                {
                    RootNodeId = promptWindow.NodeId,
                    MaxDepth = 6,
                    MaxNodes = 150
                },
                cancellationToken);
            var yes = promptTree.Truncated ? null : UniqueButton(promptTree.Nodes, "6");
            var no = promptTree.Truncated ? null : UniqueButton(promptTree.Nodes, "7");
            prompts.Add(new NativeFileDialogPrompt
            {
                Window = promptWindow,
                Message = string.Join(Environment.NewLine,
                    promptTree.Nodes.Where(item => item.ControlType == "Text" &&
                                                   !string.IsNullOrWhiteSpace(item.Name)).Select(item => item.Name)
                        .Distinct(StringComparer.Ordinal)),
                YesNodeId = yes?.NodeId,
                NoNodeId = no?.NodeId,
                ExpectedOverwrite = previous is { OverwriteExpected: true, Intent: "accept" or "overwrite" } &&
                                    yes is not null && no is not null
            });
        }

        var info = new NativeFileDialogInfo
        {
            DialogId = window.NodeId,
            Window = window,
            ProviderProfile = edits[0].AutomationId == "1152"
                ? "windows-common-folder-dialog-v1"
                : "windows-common-item-dialog-v1",
            RequestedKind = previous?.Info.RequestedKind ?? NativeFileDialogKind.Unknown,
            State = prompts.Count > 0
                ? "ConfirmationRequired"
                : previous?.Action?.Status.ToLowerInvariant() switch
                {
                    "pending" => "ActionPending",
                    "failed" => "ActionFailed",
                    _ => "Open"
                },
            PathInputNodeId = edits[0].NodeId,
            PathInputValue = edits[0].Value,
            AcceptNodeId = accept.NodeId,
            CancelNodeId = cancel.NodeId,
            FilterNodeId = filter?.NodeId,
            FilterValue = filter?.Value ?? nodes.FirstOrDefault(item =>
                item.IsSelected == true && item.ControlType == "ListItem" &&
                filter is not null && HasAncestor(item, byId, ancestor => ancestor.NodeId == filter.NodeId))?.Name,
            FilterOptions = filter is null
                ? []
                : nodes.Where(item => item.ControlType == "ListItem" &&
                                      HasAncestor(item, byId, ancestor => ancestor.NodeId == filter.NodeId))
                    .Select(item => item.Name).ToArray(),
            RequestedPaths = previous?.Info.RequestedPaths ?? [],
            Prompts = [.. prompts],
            LastAction = previous?.Action,
            ApplicationIoVerified = false,
            Note =
                "Dialog state only. Requested kind is caller intent, not a localized-title inference; application I/O requires independent verification."
        };
        if (captured is null)
            _dialogs.TryAdd(window.NodeId, new(info, null, null, null, false));
        else
            _dialogs.TryUpdate(window.NodeId, previous! with { Info = info }, captured);
        return info;
    }

    private static bool IsCommonDialog(NativeWindowInfo window) => window.IsVisible && window.ClassName == "#32770";

    private static bool IsInsideRootDialog(NativeElementInfo node,
        string rootId,
        Dictionary<string, NativeElementInfo> byId) =>
        !HasAncestor(node, byId, ancestor => ancestor.NodeId != rootId && ancestor.ClassName == "#32770");

    private static bool HasAncestor(NativeElementInfo node,
        Dictionary<string, NativeElementInfo> byId,
        Func<NativeElementInfo, bool> predicate)
    {
        var parentId = node.ParentNodeId;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (parentId is not null && seen.Add(parentId) && byId.TryGetValue(parentId, out var parent))
        {
            if (predicate(parent))
                return true;
            parentId = parent.ParentNodeId;
        }

        return false;
    }

    private static NativeElementInfo? UniqueButton(IEnumerable<NativeElementInfo> nodes, string automationId)
    {
        var matches = nodes.Where(item =>
            (item.AutomationId == automationId || automationId is "6" or "7" &&
                item.AutomationId == "CommandButton_" + automationId && item.ClassName == "CCPushButton") &&
            item.ControlType is "Button" or "SplitButton").ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool IsOwnedBy(NativeWindowInfo window, long ownerHandle, NativeWindowInfo[] windows)
    {
        var handle = window.OwnerHandle;
        var visited = new HashSet<long>();
        while (handle != 0 && visited.Add(handle))
        {
            if (handle == ownerHandle)
                return true;
            handle = windows.FirstOrDefault(item => item.Handle == handle)?.OwnerHandle ?? 0;
        }

        return false;
    }

    private static string[] ValidatePaths(IReadOnlyList<string> paths, NativeFileDialogKind kind)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (kind == NativeFileDialogKind.Unknown || !Enum.IsDefined(kind))
            throw Error("invalid_file_dialog_kind", "Choose OpenFile, OpenFiles, SaveFile, or Folder explicitly.");
        if (paths.Count == 0 || (kind != NativeFileDialogKind.OpenFiles && paths.Count != 1))
            throw Error("invalid_file_dialog_paths", "Provide exactly one path, or one or more paths for OpenFiles.");
        if (!OperatingSystem.IsWindows())
            throw Error("platform_not_supported", "Native Windows file dialog paths require Windows.");
        var normalized = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                path.IndexOfAny(['\0', '"', '*', '?', '\r', '\n', '<', '>', '|']) >= 0 ||
                path.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
                path.StartsWith("\\\\.\\", StringComparison.Ordinal))
                throw Error("invalid_file_dialog_path",
                    "Use an absolute drive or UNC filesystem path without quotes, wildcards, device prefixes, or control characters.");
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                                  or PathTooLongException)
            {
                throw Error("invalid_file_dialog_path", exception.Message);
            }

            if (fullPath.AsSpan(2).Contains(':') || fullPath.Any(char.IsControl))
                throw Error("invalid_file_dialog_path",
                    "Alternate data streams and control characters are not supported picker paths.");
            foreach (var segment in path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
                         .Skip(path.StartsWith("\\\\", StringComparison.Ordinal) ? 2 : 1))
            {
                if (segment is "." or "..")
                    continue;
                var stem = segment.Split('.')[0];
                if (segment.EndsWith(' ') || segment.EndsWith('.') ||
                    stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                    stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                    stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                    stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                    stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                         stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                    stem[3] is >= '1' and <= '9')
                    throw Error("invalid_file_dialog_path",
                        "Reserved device names and path segments ending in a dot or space are not supported picker paths.");
            }

            switch (kind)
            {
                case NativeFileDialogKind.OpenFile:
                case NativeFileDialogKind.OpenFiles:
                    if (!File.Exists(fullPath))
                        throw Error("file_dialog_file_not_found", "An OpenFile path must name an existing file.");
                    break;
                case NativeFileDialogKind.Folder:
                    if (!Directory.Exists(fullPath))
                        throw Error("file_dialog_folder_not_found", "A Folder path must name an existing directory.");
                    break;
                case NativeFileDialogKind.SaveFile:
                    if (string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)) || Directory.Exists(fullPath) ||
                        !Directory.Exists(Path.GetDirectoryName(fullPath)))
                        throw Error("file_dialog_save_parent_missing",
                            "A SaveFile path must have a filename and an existing parent directory.");
                    break;
            }

            normalized.Add(fullPath);
        }

        return [.. normalized];
    }

    /// <summary>Performs bounded filesystem preflight without accessing or changing any UI.</summary>
    public static async Task<string[]> ValidatePathsAsync(IReadOnlyList<string> paths,
        NativeFileDialogKind kind,
        CancellationToken cancellationToken = default)
    {
        // A disconnected UNC share may block a filesystem probe. No UI mutation
        // starts until validation completes; abandoning a probe cannot click a dialog.
        ArgumentNullException.ThrowIfNull(paths);
        var captured = paths.ToArray();
        try
        {
            return await Task.Run(() => ValidatePaths(captured, kind), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw Error("file_dialog_path_timeout",
                "Filesystem path validation timed out. The dialog was not changed.");
        }
    }

    private static async Task<bool> IsExistingFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => File.Exists(path), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw Error("file_dialog_path_timeout", "Overwrite preflight timed out. The dialog was not changed.");
        }
    }

    private static bool IsClosed(NativeFileDialogInfo info) => info.State.EndsWith("Closed", StringComparison.Ordinal);

    private static void ValidateTimeout(int timeoutMs)
    {
        if (timeoutMs is < 1 or > 120_000)
            throw Error("invalid_file_dialog_timeout", "Timeout must be between 1 and 120000 milliseconds.");
    }

    private static NativeAutomationException Error(string code, string message) => new(code, message);

    private sealed record DialogState(
        NativeFileDialogInfo Info,
        string? PathText,
        string? Intent,
        NativeActionResult? Action,
        bool OverwriteExpected);
}
