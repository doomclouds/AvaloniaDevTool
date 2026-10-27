using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AvaloniaDevTool.NativeAutomation;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

[Collection(NativeDesktopMcpCollection.Name)]
public sealed partial class NativeFileDialogMcpTests
{
    private const string TextProperty = "Avalonia.Controls.TextBlock:Text";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private static readonly JsonSerializerOptions WireOptions = new(ProtocolJson.Options)
        { Converters = { new JsonStringEnumConverter() } };

    public static bool IsInteractiveWindows => OperatingSystem.IsWindows() && Environment.UserInteractive;

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Default_stdio_tools_drive_real_native_input_and_file_dialogs_and_verify_application_io()
    {
        await using var fixture = await SampleProcess.StartAsync();
        var firstPath = Path.Combine(fixture.DirectoryPath, "中文 文档 一.txt");
        var secondPath = Path.Combine(fixture.DirectoryPath, "第二个 spaced file.txt");
        var folderPath = Path.Combine(fixture.DirectoryPath, "选定 文件夹");
        var savedPath = Path.Combine(fixture.DirectoryPath, "保存 新文件.txt");
        var overwritePath = Path.Combine(fixture.DirectoryPath, "existing long 中文.txt");
        const string firstContent = "第一行 中文 UTF-8 😀\nsecond line with spaces\n";
        const string secondContent = "另一个文件的真实内容\nSecond document\n";
        const string savedContent = "保存 新 UTF-8 😀\nVerified through a real native picker.\n";
        const string shortContent = "短 😀\n";
        await File.WriteAllTextAsync(firstPath, firstContent, Utf8, Token);
        await File.WriteAllTextAsync(secondPath, secondContent, Utf8, Token);
        await File.WriteAllTextAsync(overwritePath, new string('L', 8192) + "旧文件尾部", Utf8, Token);
        Directory.CreateDirectory(folderPath);
        var originalOverwriteBytes = await File.ReadAllBytesAsync(overwritePath, Token);
        var originalOverwriteHash = SHA256.HashData(originalOverwriteBytes);
        await using var client = await CreateClientAsync(fixture.PipeName);

        var editorId = await FindManaged(client, "InputTextBox");
        var windows = Read<NativeWindowInfo[]>(await Call(client, "native-windows"));
        Assert.All(windows, window => Assert.Equal(fixture.ProcessId, window.ProcessId));
        var main = Assert.Single(windows, window => window.Title == "AvaloniaDevTool automation acceptance");
        var nativeEditor = await FindNative(client, main.NodeId, "InputTextBox");
        const string nativeText = "原生 Unicode 输入 😀 with spaces";
        Assert.True(Read<NativeInputResult>(await Call(client,
            "native-input",
            ("nodeId", nativeEditor.NodeId),
            ("kind", "KeyChord"),
            ("keys", new[] { "A" }),
            ("modifiers", new[] { "Ctrl" }))).Injected);
        var injected = Read<NativeInputResult>(await Call(client,
            "native-input",
            ("nodeId", nativeEditor.NodeId),
            ("kind", "Text"),
            ("text", nativeText)));
        Assert.True(injected.Injected);
        Assert.False(injected.ApplicationEffectVerified);
        await WaitText(client, "InputTextBox", nativeText);
        Assert.Equal(nativeText, await ReadText(client, editorId));
        var commit = await FindNative(client, main.NodeId, "CommitInputButton");
        Read<NativeInputResult>(await Call(client, "native-input", ("nodeId", commit.NodeId), ("kind", "Click")));
        await WaitText(client, "InputStatus", "Committed: " + nativeText);
        var counter = await FindNative(client, main.NodeId, "CounterButton");
        Read<NativeInputResult>(await Call(client, "native-input", ("nodeId", counter.NodeId), ("kind", "Click")));
        await WaitText(client, "InteractionStatus", "Clicks=1");

        Read<ItemSelectionResult>(await Call(client,
            "ui-select-item",
            ("nodeId", await FindManaged(client, "MainTabs")),
            ("index", 1)));
        var documentId = await FindManaged(client, "DocumentEditor");
        long sequence = 0;

        var single = await OpenPicker(client, fixture.ProcessId, "OpenSingleButton", "Automation sample - Open single");
        var plain = Read<NativeFileDialogInfo>(await Call(client,
            "file-dialog-set-filter",
            ("dialogId", single.Info.DialogId),
            ("filterIndex", 1)));
        var all = Read<NativeFileDialogInfo>(await Call(client,
            "file-dialog-set-filter",
            ("dialogId", single.Info.DialogId),
            ("filterIndex", 2)));
        Assert.False(string.IsNullOrWhiteSpace(plain.FilterValue));
        Assert.False(string.IsNullOrWhiteSpace(all.FilterValue));
        Assert.NotEqual(plain.FilterValue, all.FilterValue);
        var restoredFilter = Read<NativeFileDialogInfo>(await Call(client,
            "file-dialog-set-filter",
            ("dialogId", single.Info.DialogId),
            ("filterName", plain.FilterValue)));
        Assert.Equal(plain.FilterValue, restoredFilter.FilterValue);
        await SetPaths(client, single.Info.DialogId, "OpenFile", firstPath);
        await AcceptAndClose(client, single);
        var opened = await fixture.WaitResultAsync(sequence, "openSingle");
        sequence = opened.Sequence;
        Assert.False(opened.Cancelled);
        Assert.Equal([firstPath], opened.SelectedPaths);
        await VerifyDocument(client, documentId, opened, firstContent, firstPath);

        var beforeCancel = await DocumentState.ReadAsync(client);
        var cancel = await OpenPicker(client, fixture.ProcessId, "OpenSingleButton", "Automation sample - Open single");
        Assert.NotEqual(single.Info.DialogId, cancel.Info.DialogId);
        var beforeStale =
            Read<NativeFileDialogInfo>(await Call(client, "file-dialog-inspect", ("dialogId", cancel.Info.DialogId)));
        foreach (var (path, code) in new[]
                 {
                     ("relative.txt", "invalid_file_dialog_path"), (@"\\?\C:\device.txt", "invalid_file_dialog_path"),
                     (firstPath + ":stream", "invalid_file_dialog_path"),
                     (Path.Combine(fixture.DirectoryPath, "*.txt"), "invalid_file_dialog_path"),
                     (Path.Combine(fixture.DirectoryPath, "absent.txt"), "file_dialog_file_not_found")
                 })
        {
            var invalid = await Call(client,
                "file-dialog-set-paths",
                ("dialogId", cancel.Info.DialogId),
                ("paths", new[] { path }),
                ("kind", "OpenFile"));
            Assert.True(invalid.IsError);
            Assert.Contains(code, Text(invalid));
        }

        var stale = await Call(client,
            "file-dialog-set-paths",
            ("dialogId", single.Info.DialogId),
            ("paths", new[] { secondPath }),
            ("kind", "OpenFile"));
        Assert.True(stale.IsError);
        Assert.Contains("stale_file_dialog", Text(stale));
        var afterStale =
            Read<NativeFileDialogInfo>(await Call(client, "file-dialog-inspect", ("dialogId", cancel.Info.DialogId)));
        Assert.Equal(beforeStale.DialogId, afterStale.DialogId);
        Assert.Equal(beforeStale.PathInputValue, afterStale.PathInputValue);
        await CancelAndClose(client, cancel);
        var cancelled = await fixture.WaitResultAsync(sequence, "openSingle");
        sequence = cancelled.Sequence;
        Assert.True(cancelled.Cancelled);
        Assert.Empty(cancelled.SelectedPaths);
        Assert.Equal(beforeCancel, await DocumentState.ReadAsync(client));
        await VerifyDocument(client, documentId, cancelled, firstContent, firstPath);

        var multiple = await OpenPicker(client,
            fixture.ProcessId,
            "OpenMultipleButton",
            "Automation sample - Open multiple");
        await SetPaths(client, multiple.Info.DialogId, "OpenFiles", firstPath, secondPath);
        await AcceptAndClose(client, multiple);
        var combined = await fixture.WaitResultAsync(sequence, "openMultiple");
        sequence = combined.Sequence;
        Assert.Equal(new[] { firstPath, secondPath }.Order(), combined.SelectedPaths.Order());
        var contentByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { [firstPath] = firstContent, [secondPath] = secondContent };
        var combinedContent = string.Join("\n--- next file ---\n",
            combined.SelectedPaths.Select(path => contentByPath[path]));
        await VerifyDocument(client, documentId, combined, combinedContent, null);
        Assert.Equal(string.Join("\n", combined.SelectedPaths),
            await ReadText(client, await FindManaged(client, "SelectedFiles")));

        var folder = await OpenPicker(client, fixture.ProcessId, "OpenFolderButton", "Automation sample - Open folder");
        await SetPaths(client, folder.Info.DialogId, "Folder", folderPath);
        await AcceptAndClose(client, folder);
        var selectedFolder = await fixture.WaitResultAsync(sequence, "openFolder");
        sequence = selectedFolder.Sequence;
        Assert.Equal(Path.TrimEndingDirectorySeparator(folderPath),
            Path.TrimEndingDirectorySeparator(Assert.Single(selectedFolder.SelectedPaths)));
        Assert.Equal(Path.TrimEndingDirectorySeparator(folderPath),
            Path.TrimEndingDirectorySeparator(await ReadText(client, await FindManaged(client, "FolderPath")) ?? ""));
        await VerifyDocument(client, documentId, selectedFolder, combinedContent, null);

        foreach (var (button, title, action) in new[]
                 {
                     ("OpenMultipleButton", "Automation sample - Open multiple", "openMultiple"),
                     ("OpenFolderButton", "Automation sample - Open folder", "openFolder"),
                     ("SaveAsButton", "Automation sample - Save UTF-8 document", "saveAs")
                 })
        {
            var before = await DocumentState.ReadAsync(client);
            var picker = await OpenPicker(client, fixture.ProcessId, button, title);
            await CancelAndClose(client, picker);
            var cancellation = await fixture.WaitResultAsync(sequence, action);
            sequence = cancellation.Sequence;
            Assert.True(cancellation.Cancelled);
            Assert.Empty(cancellation.SelectedPaths);
            Assert.Equal(before, await DocumentState.ReadAsync(client));
            Assert.False(File.Exists(savedPath));
            Assert.Equal(originalOverwriteHash, SHA256.HashData(await File.ReadAllBytesAsync(overwritePath, Token)));
        }

        var invalidUtf8Path = Path.Combine(fixture.DirectoryPath, "非法 UTF8.txt");
        await File.WriteAllBytesAsync(invalidUtf8Path, [0xFF, 0xFE, 0xFF], Token);
        var beforeInvalidUtf8 = await DocumentState.ReadAsync(client);
        var invalidUtf8 =
            await OpenPicker(client, fixture.ProcessId, "OpenSingleButton", "Automation sample - Open single");
        await SetPaths(client, invalidUtf8.Info.DialogId, "OpenFile", invalidUtf8Path);
        await AcceptAndClose(client, invalidUtf8);
        var failedRead = await fixture.WaitResultAsync(sequence, "openSingle", expectError: true);
        sequence = failedRead.Sequence;
        Assert.Contains("DecoderFallbackException", failedRead.Error);
        Assert.StartsWith("Error:", failedRead.Status);
        Assert.Equal(beforeInvalidUtf8, await DocumentState.ReadAsync(client));
        await VerifyDocument(client, documentId, failedRead, combinedContent, null);

        Read<TextSelectionResult>(await Call(client,
            "ui-text-selection",
            ("nodeId", documentId),
            ("replacementText", savedContent)));
        Assert.Equal(savedContent, await ReadText(client, documentId));
        var saveNew = await OpenPicker(client,
            fixture.ProcessId,
            "SaveAsButton",
            "Automation sample - Save UTF-8 document");
        await SetPaths(client, saveNew.Info.DialogId, "SaveFile", savedPath);
        await AcceptAndClose(client, saveNew);
        var saved = await fixture.WaitResultAsync(sequence, "saveAs");
        sequence = saved.Sequence;
        await VerifyDocument(client, documentId, saved, savedContent, savedPath);
        await VerifyDisk(savedPath, savedContent, saved.Sha256);
        var reopenNew =
            await OpenPicker(client, fixture.ProcessId, "OpenSingleButton", "Automation sample - Open single");
        await SetPaths(client, reopenNew.Info.DialogId, "OpenFile", savedPath);
        await AcceptAndClose(client, reopenNew);
        var reopened = await fixture.WaitResultAsync(sequence, "openSingle");
        sequence = reopened.Sequence;
        await VerifyDocument(client, documentId, reopened, savedContent, savedPath);

        Read<TextSelectionResult>(await Call(client,
            "ui-text-selection",
            ("nodeId", documentId),
            ("replacementText", shortContent)));
        var beforeRefusal = await DocumentState.ReadAsync(client);
        var refuse = await OpenPicker(client,
            fixture.ProcessId,
            "SaveAsButton",
            "Automation sample - Save UTF-8 document");
        await SetPaths(client, refuse.Info.DialogId, "SaveFile", overwritePath);
        Read<NativeFileDialogInfo>(await Call(client, "file-dialog-accept", ("dialogId", refuse.Info.DialogId)));
        var noPrompt = await WaitOverwritePrompt(client, refuse.Info.DialogId, fixture.ProcessId);
        Read<NativeFileDialogInfo>(await Call(client,
            "file-dialog-overwrite",
            ("dialogId", refuse.Info.DialogId),
            ("promptId", noPrompt.Window.NodeId),
            ("overwrite", false)));
        await WaitNoPrompts(client, refuse.Info.DialogId);
        await CancelAndClose(client, refuse);
        var refused = await fixture.WaitResultAsync(sequence, "saveAs");
        sequence = refused.Sequence;
        Assert.True(refused.Cancelled);
        Assert.Equal(beforeRefusal, await DocumentState.ReadAsync(client));
        await VerifyDocument(client, documentId, refused, shortContent, savedPath);
        Assert.Equal(originalOverwriteHash, SHA256.HashData(await File.ReadAllBytesAsync(overwritePath, Token)));

        var overwrite = await OpenPicker(client,
            fixture.ProcessId,
            "SaveAsButton",
            "Automation sample - Save UTF-8 document");
        await SetPaths(client, overwrite.Info.DialogId, "SaveFile", overwritePath);
        Read<NativeFileDialogInfo>(await Call(client, "file-dialog-accept", ("dialogId", overwrite.Info.DialogId)));
        var yesPrompt = await WaitOverwritePrompt(client, overwrite.Info.DialogId, fixture.ProcessId);
        Read<NativeFileDialogInfo>(await Call(client,
            "file-dialog-overwrite",
            ("dialogId", overwrite.Info.DialogId),
            ("promptId", yesPrompt.Window.NodeId),
            ("overwrite", true)));
        await WaitClosed(client, overwrite.Info.DialogId);
        Read<InputResult>(await overwrite.Click);
        var overwritten = await fixture.WaitResultAsync(sequence, "saveAs");
        sequence = overwritten.Sequence;
        Assert.False(overwritten.Cancelled);
        await VerifyDocument(client, documentId, overwritten, shortContent, overwritePath);
        await VerifyDisk(overwritePath, shortContent, overwritten.Sha256);
        Assert.True(new FileInfo(overwritePath).Length < originalOverwriteBytes.Length);
        var reopenOverwrite =
            await OpenPicker(client, fixture.ProcessId, "OpenSingleButton", "Automation sample - Open single");
        await SetPaths(client, reopenOverwrite.Info.DialogId, "OpenFile", overwritePath);
        await AcceptAndClose(client, reopenOverwrite);
        var reopenedOverwrite = await fixture.WaitResultAsync(sequence, "openSingle");
        await VerifyDocument(client, documentId, reopenedOverwrite, shortContent, overwritePath);

        Read<ItemSelectionResult>(await Call(client,
            "ui-select-item",
            ("nodeId", await FindManaged(client, "MainTabs")),
            ("index", 0)));
        var modalButtonId = await FindManaged(client, "OpenDialogButton");
        Read<ElementActionResult>(await Call(client, "action", ("nodeId", modalButtonId), ("action", "BringIntoView")));
        await Task.Delay(100, Token);
        Read<InputResult>(await Call(client, "ui-click", ("nodeId", modalButtonId)));
        var modal = await WaitWindow(client, fixture.ProcessId, "Automation modal dialog");
        Assert.Equal(main.Handle, modal.OwnerHandle);
        Assert.True(modal.IsOwned);
        Assert.True(modal.IsModal);
        Assert.False(Assert.Single(Read<NativeWindowInfo[]>(await Call(client, "native-windows")),
            window => window.Handle == main.Handle).IsEnabled);
        Read<ControlActionResult>(await Call(client,
            "ui-control-action",
            ("nodeId", await FindManaged(client, "DialogCancelButton")),
            ("action", "Invoke")));
        await WaitText(client, "DialogStatus", "Dialog cancelled");
        Assert.DoesNotContain(Read<NativeWindowInfo[]>(await Call(client, "native-windows")),
            window => window.Handle == modal.Handle);

        var closed =
            Read<NativeActionResult>(await Call(client, "native-action", ("nodeId", main.NodeId), ("action", "Close")));
        Assert.DoesNotContain(closed.Status, new[] { "failed" });
        await fixture.WaitForExitAsync();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Native_double_click_preserves_button_business_and_double_tap_gesture_at_stationary_cursor()
    {
        await using var fixture = await SampleProcess.StartAsync();
        await using var client = await CreateClientAsync(fixture.PipeName);
        var counterId = await FindManaged(client, "CounterButton");
        var main = Assert.Single(Read<NativeWindowInfo[]>(await Call(client, "native-windows")),
            window => window.Title == "AvaloniaDevTool automation acceptance");
        var target = await FindNative(client, main.NodeId, "CounterButton");
        var bounds = Assert.IsType<NativeBounds>(target.Bounds);
        var stationarySamples = 0;
        var cursorEvidence = new List<string>();
        for (var cycle = 0; cycle < 10; cycle++)
        {
            Read<InputResult>(await Call(client, "ui-click", ("nodeId", counterId), ("clickCount", 2)));
            await WaitText(client, "InteractionStatus", $"Clicks={cycle * 4 + 2}");
            bounds = Assert.IsType<NativeBounds>(
                Read<NativeElementInfo>(await Call(client, "native-inspect", ("nodeId", target.NodeId))).Bounds);
            Assert.True(ReadPhysicalCursor(out var cursor));
            cursorEvidence.Add($"cycle={cycle},cursor=({cursor.X},{cursor.Y}),bounds={bounds}");
            if (Math.Abs(cursor.X - Math.Round(bounds.X + bounds.Width / 2)) <= 1 &&
                Math.Abs(cursor.Y - Math.Round(bounds.Y + bounds.Height / 2)) <= 1)
                stationarySamples++;
            var injected = Read<NativeInputResult>(await Call(client,
                "native-input",
                ("nodeId", target.NodeId),
                ("kind", "DoubleClick")));
            await WaitText(client,
                "InteractionStatus",
                $"Clicks={cycle * 4 + 4}",
                $"Cycle={cycle}; cursor=({cursor.X},{cursor.Y}); bounds={bounds}; injection={injected.Note}");
        }

        Assert.True(stationarySamples >= 9,
            $"Expected at least nine stationary-cursor samples; observed {stationarySamples}. {string.Join(" | ", cursorEvidence)}");
        var trace = await ReadCounterTrace(client);
        Assert.Contains("\"Kind\":\"down\"", trace);
        Assert.Contains("\"Kind\":\"up\"", trace);
        Assert.Contains("\"Clicks\":40", trace);
        var doubleTap = await FindNative(client, main.NodeId, "DoubleTapTarget");
        Read<NativeInputResult>(await Call(client,
            "native-input",
            ("nodeId", doubleTap.NodeId),
            ("kind", "DoubleClick")));
        await WaitText(client, "DoubleTapStatus", "DoubleTaps=1");
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Default_stdio_tools_drive_real_secondary_windows_popups_menus_wheel_and_drag_business_state()
    {
        await using var fixture = await SampleProcess.StartAsync();
        await using var client = await CreateClientAsync(fixture.PipeName);
        var counterId = await FindManaged(client, "CounterButton");
        var main = Assert.Single(Read<NativeWindowInfo[]>(await Call(client, "native-windows")),
            window => window.Title == "AvaloniaDevTool automation acceptance");
        Read<InputResult>(await Call(client, "ui-click", ("nodeId", counterId), ("clickCount", 2)));
        await WaitText(client, "InteractionStatus", "Clicks=2");
        var nativeCounter = await FindNative(client, main.NodeId, "CounterButton");
        Assert.True(ReadPhysicalCursor(out var cursorBeforeDoubleClick));
        Read<NativeInputResult>(await Call(client,
            "native-input",
            ("nodeId", nativeCounter.NodeId),
            ("kind", "DoubleClick")));
        await WaitText(client,
            "InteractionStatus",
            "Clicks=4",
            $"Cursor=({cursorBeforeDoubleClick.X},{cursorBeforeDoubleClick.Y}); target bounds={nativeCounter.Bounds}");
        Read<ElementActionResult>(await Call(client, "action", ("nodeId", counterId), ("action", "Disable")));
        var disabledClick = await Call(client, "ui-click", ("nodeId", counterId));
        Assert.True(disabledClick.IsError);
        Assert.Contains("disabled_target", Text(disabledClick));
        Read<ElementActionResult>(await Call(client, "action", ("nodeId", counterId), ("action", "Enable")));
        var invalidPoint = await Call(client, "ui-click", ("nodeId", counterId), ("x", -1));
        Assert.True(invalidPoint.IsError);
        Assert.Contains("invalid_coordinates", Text(invalidPoint));
        await WaitText(client, "InteractionStatus", "Clicks=4");
        var doubleTapTarget = await FindNative(client, main.NodeId, "DoubleTapTarget");
        Read<NativeInputResult>(await Call(client,
            "native-input",
            ("nodeId", doubleTapTarget.NodeId),
            ("kind", "DoubleClick")));
        await WaitText(client, "DoubleTapStatus", "DoubleTaps=1");
        Read<InputResult>(await Call(client,
            "ui-key-chord",
            ("nodeId", await FindManaged(client, "InputTextBox")),
            ("keys", new[] { "Enter" })));
        await WaitText(client, "InputStatus", "Enter: initial text");
        Read<ItemSelectionResult>(await Call(client,
            "ui-select-item",
            ("nodeId", await FindManaged(client, "ChoiceComboBox")),
            ("index", 1)));
        Read<ItemSelectionResult>(await Call(client,
            "ui-select-item",
            ("nodeId", await FindManaged(client, "ChoiceListBox")),
            ("index", 1)));
        await WaitText(client, "SelectionStatus", "Combo=Beta; List=Two; Tab=Controls");
        Read<ControlActionResult>(await Call(client,
            "ui-control-action",
            ("nodeId", await FindManaged(client, "OptionCheckBox")),
            ("action", "Toggle")));
        await WaitText(client, "InteractionStatus", "Option=True");

        await BringIntoView(client, "OpenSecondaryButton");
        Read<InputResult>(await Call(client, "ui-click", ("nodeId", await FindManaged(client, "OpenSecondaryButton"))));
        var secondary = await WaitWindow(client, fixture.ProcessId, "Automation secondary window");
        Assert.Equal(main.Handle, secondary.OwnerHandle);
        Assert.True(secondary.IsOwned);
        var secondaryInput = await FindManaged(client, "SecondaryInput");
        Read<TextSelectionResult>(await Call(client,
            "ui-text-selection",
            ("nodeId", secondaryInput),
            ("replacementText", "第二窗口 😀")));
        Read<InputResult>(
            await Call(client, "ui-click", ("nodeId", await FindManaged(client, "SecondaryActionButton"))));
        await WaitText(client, "SecondaryStatus", "Applied 1: 第二窗口 😀");
        await WaitText(client, "WindowStatus", "Applied 1: 第二窗口 😀");
        var nativeSecondary = await FindNative(client, secondary.NodeId, "SecondaryInput");
        Assert.Equal("SecondaryInput",
            Read<NativeElementInfo>(await Call(client, "native-inspect", ("nodeId", nativeSecondary.NodeId)))
                .AutomationId);
        var crossRoot = await Call(client,
            "native-input",
            ("nodeId", nativeSecondary.NodeId),
            ("kind", "Drag"),
            ("targetNodeId", main.NodeId));
        Assert.True(crossRoot.IsError);
        Assert.True(Text(crossRoot).Contains("out_of_scope", StringComparison.Ordinal), Text(crossRoot));
        Read<InputResult>(await Call(client,
            "ui-click",
            ("nodeId", await FindManaged(client, "CloseSecondaryButton"))));
        await WaitText(client, "WindowStatus", "Secondary closed");
        Assert.DoesNotContain(Read<NativeWindowInfo[]>(await Call(client, "native-windows")),
            window => window.Handle == secondary.Handle);
        var staleNative = await Call(client, "native-inspect", ("nodeId", nativeSecondary.NodeId));
        Assert.True(staleNative.IsError);
        Assert.True(Text(staleNative).Contains("stale_node", StringComparison.Ordinal) ||
                    Text(staleNative).Contains("out_of_scope", StringComparison.Ordinal),
            Text(staleNative));

        await BringIntoView(client, "OpenDialogButton");
        Read<InputResult>(await Call(client, "ui-click", ("nodeId", await FindManaged(client, "OpenDialogButton"))));
        var modal = await WaitWindow(client, fixture.ProcessId, "Automation modal dialog");
        Assert.Equal(main.Handle, modal.OwnerHandle);
        Assert.False(Assert.Single(Read<NativeWindowInfo[]>(await Call(client, "native-windows")),
            window => window.Handle == main.Handle).IsEnabled);
        var beforeBlockedOwner = await ReadText(client, await FindManaged(client, "InteractionStatus"));
        var blockedOwner = await Call(client, "ui-click", ("nodeId", await FindManaged(client, "CounterButton")));
        Assert.True(blockedOwner.IsError);
        Assert.Contains("disabled_window", Text(blockedOwner));
        Assert.Equal(beforeBlockedOwner, await ReadText(client, await FindManaged(client, "InteractionStatus")));
        Read<TextSelectionResult>(await Call(client,
            "ui-text-selection",
            ("nodeId", await FindManaged(client, "DialogInput")),
            ("replacementText", "模态确认 😀")));
        Read<InputResult>(await Call(client, "ui-click", ("nodeId", await FindManaged(client, "DialogAcceptButton"))));
        await WaitText(client, "DialogStatus", "Dialog accepted: 模态确认 😀");
        Assert.True(Assert.Single(Read<NativeWindowInfo[]>(await Call(client, "native-windows")),
            window => window.Handle == main.Handle).IsEnabled);

        await BringIntoView(client, "OpenPopupButton");
        Read<InputResult>(await Call(client, "ui-click", ("nodeId", await FindManaged(client, "OpenPopupButton"))));
        await WaitText(client, "PopupStatus", "Popup open");
        Assert.Contains(Read<ControlTreeSnapshot>(await Call(client, "tree", ("treeKind", "Visual"), ("depth", -1)))
                .Entries,
            entry => entry.Node.Name == "PopupActionButton");
        Read<InputResult>(await Call(client, "ui-click", ("nodeId", await FindManaged(client, "PopupActionButton"))));
        await WaitText(client, "PopupStatus", "Popup action invoked");
        Assert.DoesNotContain(
            Read<ControlTreeSnapshot>(await Call(client, "tree", ("treeKind", "Visual"), ("depth", -1))).Entries,
            entry => entry.Node.Name == "PopupActionButton");

        await BringIntoView(client, "OpenFlyoutButton");
        Read<InputResult>(await Call(client, "ui-click", ("nodeId", await FindManaged(client, "OpenFlyoutButton"))));
        await WaitText(client, "FlyoutStatus", "Flyout open");
        var flyoutAction = await FindManaged(client, "FlyoutActionButton");
        Assert.Contains(Read<ControlTreeSnapshot>(await Call(client, "tree", ("treeKind", "Visual"), ("depth", -1)))
                .Entries,
            entry => entry.Node.Name == "FlyoutActionButton");
        Read<InputResult>(await Call(client, "ui-click", ("nodeId", flyoutAction)));
        await WaitText(client, "FlyoutStatus", "Flyout action invoked");
        Read<InputResult>(await Call(client, "ui-key-chord", ("nodeId", flyoutAction), ("keys", new[] { "Escape" })));
        Assert.DoesNotContain(
            Read<ControlTreeSnapshot>(await Call(client, "tree", ("treeKind", "Visual"), ("depth", -1))).Entries,
            entry => entry.Node.Name == "FlyoutActionButton");

        await BringIntoView(client, "ContextMenuTarget");
        var contextTarget = await FindNative(client, main.NodeId, "ContextTargetText");
        Read<NativeInputResult>(await Call(client,
            "native-input",
            ("nodeId", contextTarget.NodeId),
            ("kind", "RightClick")));
        await WaitExists(client, "ContextMenuActionItem");
        Read<InputResult>(
            await Call(client, "ui-click", ("nodeId", await FindManaged(client, "ContextMenuActionItem"))));
        await WaitText(client, "MenuStatus", "Context menu action invoked");

        Read<ControlActionResult>(await Call(client,
            "ui-control-action",
            ("nodeId", await FindManaged(client, "ActionsMenu")),
            ("action", "Expand")));
        Read<InputResult>(await Call(client, "ui-click", ("nodeId", await FindManaged(client, "MenuActionItem"))));
        await WaitText(client, "MenuStatus", "Menu action invoked");
        Read<ControlActionResult>(await Call(client,
            "ui-control-action",
            ("nodeId", await FindManaged(client, "ActionsMenu")),
            ("action", "Expand")));
        Read<ControlActionResult>(await Call(client,
            "ui-control-action",
            ("nodeId", await FindManaged(client, "NestedMenu")),
            ("action", "Expand")));
        Read<InputResult>(await Call(client,
            "ui-click",
            ("nodeId", await FindManaged(client, "NestedMenuActionItem"))));
        await WaitText(client, "MenuStatus", "Nested menu action invoked");

        Read<ItemSelectionResult>(await Call(client,
            "ui-select-item",
            ("nodeId", await FindManaged(client, "MainTabs")),
            ("index", 2)));
        await BringIntoView(client, "LongScrollViewer");
        var beforeScroll = await ReadText(client, await FindManaged(client, "ScrollStatus"));
        var nativeScroll = await FindNative(client, main.NodeId, "LongScrollViewer");
        Read<NativeInputResult>(await Call(client,
            "native-input",
            ("nodeId", nativeScroll.NodeId),
            ("kind", "Wheel"),
            ("wheelDelta", -12000)));
        await WaitChangedText(client, "ScrollStatus", beforeScroll);
        var afterScroll = await ReadText(client, await FindManaged(client, "ScrollStatus"));
        Assert.StartsWith("Offset=", afterScroll);
        Assert.DoesNotContain("Offset=0;", afterScroll);
        var lastRow = await FindNative(client, main.NodeId, "ScrollItem60");
        Read<NativeInputResult>(await Call(client, "native-input", ("nodeId", lastRow.NodeId), ("kind", "Click")));
        await WaitText(client, "ScrollStatus", "Clicked row=60");

        await BringIntoView(client, "DragCanvas");
        var dragSource = await FindNative(client, main.NodeId, "DragHandleText");
        var dragTarget = await FindNative(client, main.NodeId, "DropTargetText");
        Read<NativeInputResult>(await Call(client, "native-input", ("nodeId", dragSource.NodeId), ("kind", "Move")));
        Read<NativeInputResult>(await Call(client,
            "native-input",
            ("nodeId", dragSource.NodeId),
            ("kind", "Drag"),
            ("targetNodeId", dragTarget.NodeId),
            ("dragSteps", 24)));
        var dropped = Read<UiWaitResult>(await Call(client,
            "ui-wait",
            ("query", "DragStatus"),
            ("condition", "Contains"),
            ("propertyName", TextProperty),
            ("expectedValue", "Dropped=True"),
            ("timeoutMs", 10000)));
        Assert.Contains("Dropped=True", dropped.PropertyValue);
        Read<InputResult>(await Call(client, "ui-click", ("nodeId", await FindManaged(client, "ResetDragButton"))));
        await WaitText(client, "DragStatus", "Drag reset");
        Read<InputResult>(await Call(client,
            "ui-drag",
            ("sourceNodeId", await FindManaged(client, "DragHandle")),
            ("targetNodeId", await FindManaged(client, "DropTarget"))));
        Assert.Contains("Dropped=True",
            Read<UiWaitResult>(await Call(client,
                "ui-wait",
                ("query", "DragStatus"),
                ("condition", "Contains"),
                ("expectedValue", "Dropped=True"),
                ("propertyName", TextProperty))).PropertyValue);
        Read<NativeActionResult>(await Call(client, "native-action", ("nodeId", main.NodeId), ("action", "Close")));
        await fixture.WaitForExitAsync();
    }

    private static async Task BringIntoView(McpClient client, string name)
    {
        Read<ElementActionResult>(await Call(client,
            "action",
            ("nodeId", await FindManaged(client, name)),
            ("action", "BringIntoView")));
        await Task.Delay(100, Token);
    }

    private static async Task WaitExists(McpClient client, string name) =>
        Assert.True(Read<UiWaitResult>(await Call(client, "ui-wait", ("query", name), ("timeoutMs", 10000))).Matched);

    private static async Task WaitChangedText(McpClient client, string name, string? previous)
    {
        var watch = Stopwatch.StartNew();
        var id = await FindManaged(client, name);
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (await ReadText(client, id) != previous)
                return;
            await Task.Delay(50, Token);
        }

        throw new TimeoutException($"The business state '{name}' did not change after native input.");
    }

    private static string Metadata(string key) => typeof(NativeFileDialogMcpTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == key).Value!;

    private static Task<McpClient> CreateClientAsync(string pipe) => McpClient.CreateAsync(new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = "dotnet", Name = "Native picker end-to-end stdio tests",
                Arguments = [Metadata("PreviewCliPath"), "mcp", "--pipe", pipe],
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            }),
        cancellationToken: Token);

    private static async Task<CallToolResult> Call(McpClient client,
        string name,
        params (string Name, object? Value)[] values) =>
        await client.CallToolAsync(name,
                values.ToDictionary(item => item.Name, item => item.Value),
                cancellationToken: Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(20), Token);

    private static async Task<long> FindManaged(McpClient client, string name) => Assert.Single(
        Read<TreeSearchResult>(await Call(client, "search", ("query", name))).Matches,
        node => node.Name == name).Id;

    private static async Task<NativeElementInfo> FindNative(McpClient client, string root, string automationId)
    {
        var result = Read<NativeFindResult>(await Call(client,
            "native-find",
            ("rootNodeId", root),
            ("automationId", automationId)));
        Assert.False(result.Truncated);
        return Assert.Single(result.Matches);
    }

    private static async Task<string?> ReadText(McpClient client, long id) => Assert.Single(
            Read<PropertyInfoDto[]>(await Call(client,
                "props",
                ("nodeId", id),
                ("propertyNames", new[] { TextProperty }))))
        .Value;

    private static async Task WaitText(McpClient client, string name, string value, string? context = null)
    {
        var response = await Call(client,
            "ui-wait",
            ("query", name),
            ("condition", "Equals"),
            ("propertyName", TextProperty),
            ("expectedValue", value),
            ("timeoutMs", 10000),
            ("pollIntervalMs", 50));
        await AssertWaitTextResponse(response,
            name,
            value,
            context,
            async () => await ReadText(client, await FindManaged(client, name)),
            name == "InteractionStatus" ? async () => await ReadCounterTrace(client) : null,
            name == "InteractionStatus" ? async () => await ReadSettledInputState(client) : null);
    }

    private static async Task AssertWaitTextResponse(CallToolResult response,
        string name,
        string expected,
        string? context,
        Func<Task<string?>> readCurrent,
        Func<Task<string?>>? readTrace,
        Func<Task<string?>>? readInputState = null)
    {
        // ui-wait reports a timeout as an MCP error rather than Matched=false.
        // Snapshot the still-live fixture before any assertion disposes it.
        var result = response.IsError == true ? null : Read<UiWaitResult>(response);
        if (result?.Matched == true && result.PropertyValue == expected)
            return;

        static async Task<string?> Capture(Func<Task<string?>> read)
        {
            try
            {
                return await read();
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return $"snapshot_failed: {exception.GetType().Name}: {exception.Message}";
            }
        }

        var observed = await Capture(readCurrent);
        var trace = readTrace is null ? null : await Capture(readTrace);
        var inputState = readInputState is null ? null : await Capture(readInputState);
        var error = response.IsError == true ? Text(response) : null;
        var directory = Path.Combine(Path.GetTempPath(), "avdt-native-input-failures");
        var evidencePath = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
        string? evidenceError = null;
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(evidencePath,
                JsonSerializer.Serialize(new
                        { time = DateTimeOffset.UtcNow, name, expected, observed, context, error, trace, inputState },
                    new JsonSerializerOptions { WriteIndented = true }),
                Utf8,
                Token);
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            evidenceError = exception.Message;
        }

        Assert.Fail($"Expected {name}='{expected}', last observed='{observed}'. {context} ui-wait error={error}; " +
                    $"Pointer trace={trace}; settled input={inputState}; evidence={evidencePath}; evidence write error={evidenceError}");
    }

    [Fact]
    public async Task Ui_wait_error_preserves_current_state_and_pointer_trace_before_asserting()
    {
        var currentRead = false;
        var traceRead = false;
        var response = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "ui_wait_timeout: requested text was not observed" }]
        };
        var failure = await Record.ExceptionAsync(() => AssertWaitTextResponse(response,
            "InteractionStatus",
            "Clicks=4",
            "unit response, not live UI; cycle=0",
            () =>
            {
                currentRead = true;
                return Task.FromResult<string?>("Clicks=3");
            },
            () =>
            {
                traceRead = true;
                return Task.FromResult<string?>("CaptureLost; IsPressed=false");
            }));
        Assert.True(currentRead);
        Assert.True(traceRead);
        Assert.NotNull(failure);
        Assert.Contains("last observed='Clicks=3'", failure.Message);
        Assert.Contains("CaptureLost; IsPressed=false", failure.Message);
        Assert.Contains("ui_wait_timeout", failure.Message);
        Assert.Contains("evidence=", failure.Message);
    }

    private static async Task<string> ReadCounterTrace(McpClient client) => Assert.IsType<string>(
        await ReadText(client, await FindManaged(client, "CounterPointerTrace")));

    private static async Task<string> ReadSettledInputState(McpClient client)
    {
        object hardware;
        var previous = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            GetPhysicalCursorPos(out var cursor);
            var foreground = GetForegroundWindow();
            var root = GetAncestor(foreground, 2);
            GetWindowThreadProcessId(foreground, out var processId);
            var underCursor = WindowFromPoint(cursor);
            GetWindowThreadProcessId(underCursor, out var pointProcessId);
            hardware = new
            {
                cursor.X, cursor.Y, foreground = foreground.ToInt64(), root = root.ToInt64(), processId,
                underCursor = underCursor.ToInt64(), pointRoot = GetAncestor(underCursor, 2).ToInt64(), pointProcessId
            };
        }
        finally
        {
            if (previous != 0)
                SetThreadDpiAwarenessContext(previous);
        }

        var windows = Read<NativeWindowInfo[]>(await Call(client, "native-windows"));
        return JsonSerializer.Serialize(new { hardware, windows });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalCursorPos(out CursorPoint point);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(CursorPoint point);

    private static bool ReadPhysicalCursor(out CursorPoint point)
    {
        var previous = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            return GetPhysicalCursorPos(out point);
        }
        finally
        {
            if (previous != 0)
                SetThreadDpiAwarenessContext(previous);
        }
    }

    private static async Task<NativeWindowInfo> WaitWindow(McpClient client, int processId, string title)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            var windows = Read<NativeWindowInfo[]>(await Call(client, "native-windows"));
            Assert.All(windows, window => Assert.Equal(processId, window.ProcessId));
            if (windows.SingleOrDefault(window => window.Title == title) is { } found)
                return found;
            await Task.Delay(50, Token);
        }

        throw new TimeoutException($"The owned window '{title}' was not observed.");
    }

    private sealed record Picker(NativeFileDialogInfo Info, Task<CallToolResult> Click);

    private static async Task<Picker> OpenPicker(McpClient client, int processId, string button, string title)
    {
        var id = await FindManaged(client, button);
        // The native call must complete while the managed click may still hold its own call gate.
        var click = Call(client, "ui-click", ("nodeId", id));
        var info = Read<NativeFileDialogInfo>(await Call(client, "file-dialog-wait", ("timeoutMs", 10000)));
        Assert.Equal(processId, info.Window.ProcessId);
        Assert.Equal(title, info.Window.Title);
        Assert.False(info.ApplicationIoVerified);
        Assert.Contains(Read<NativeFileDialogInfo[]>(await Call(client, "file-dialogs")),
            dialog => dialog.DialogId == info.DialogId);
        return new(info, click);
    }

    private static async Task SetPaths(McpClient client, string dialogId, string kind, params string[] paths)
    {
        var info = Read<NativeFileDialogInfo>(await Call(client,
            "file-dialog-set-paths",
            ("dialogId", dialogId),
            ("paths", paths),
            ("kind", kind)));
        Assert.Equal(paths, info.RequestedPaths);
        Assert.False(info.ApplicationIoVerified);
    }

    private static async Task AcceptAndClose(McpClient client, Picker picker)
    {
        Read<NativeFileDialogInfo>(await Call(client, "file-dialog-accept", ("dialogId", picker.Info.DialogId)));
        await WaitClosed(client, picker.Info.DialogId);
        Read<InputResult>(await picker.Click);
    }

    private static async Task CancelAndClose(McpClient client, Picker picker)
    {
        Read<NativeFileDialogInfo>(await Call(client, "file-dialog-cancel", ("dialogId", picker.Info.DialogId)));
        var closed = await WaitClosed(client, picker.Info.DialogId);
        Assert.Equal("CancelledDialogClosed", closed.State);
        Read<InputResult>(await picker.Click);
    }

    private static async Task<NativeFileDialogInfo> WaitClosed(McpClient client, string dialogId)
    {
        var closed =
            Read<NativeFileDialogInfo>(await Call(client,
                "file-dialog-wait",
                ("dialogId", dialogId),
                ("timeoutMs", 10000)));
        Assert.EndsWith("Closed", closed.State);
        Assert.False(closed.ApplicationIoVerified);
        Assert.Empty(closed.Prompts);
        return closed;
    }

    private static async Task<NativeFileDialogPrompt> WaitOverwritePrompt(McpClient client,
        string dialogId,
        int processId)
    {
        var info = Read<NativeFileDialogInfo>(await Call(client,
            "file-dialog-wait",
            ("dialogId", dialogId),
            ("timeoutMs", 10000)));
        var prompt = Assert.Single(info.Prompts);
        Assert.True(prompt.ExpectedOverwrite);
        Assert.Equal(processId, prompt.Window.ProcessId);
        Assert.NotNull(prompt.YesNodeId);
        Assert.NotNull(prompt.NoNodeId);
        return prompt;
    }

    private static async Task WaitNoPrompts(McpClient client, string dialogId)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            var info = Read<NativeFileDialogInfo>(await Call(client, "file-dialog-inspect", ("dialogId", dialogId)));
            if (info.Prompts.Length == 0 && info.Window.IsEnabled)
                return;
            await Task.Delay(50, Token);
        }

        throw new TimeoutException("The rejected overwrite prompt did not return to the original picker.");
    }

    private static async Task VerifyDocument(McpClient client,
        long documentId,
        SampleResult result,
        string content,
        string? path)
    {
        Assert.Equal(content, result.Content);
        Assert.Equal(path, result.DocumentPath);
        Assert.Equal(Utf8.GetByteCount(content), result.ByteLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(content))), result.Sha256, ignoreCase: true);
        Assert.Equal(content, await ReadText(client, documentId));
        if (path is not null)
            Assert.Equal(path, await ReadText(client, await FindManaged(client, "DocumentPath")));
    }

    private static async Task VerifyDisk(string path, string content, string sampleHash)
    {
        var bytes = await File.ReadAllBytesAsync(path, Token);
        Assert.Equal(Utf8.GetBytes(content), bytes);
        Assert.Equal(content, Utf8.GetString(bytes));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), sampleHash, ignoreCase: true);
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static T Read<T>(CallToolResult result)
    {
        Assert.False(result.IsError == true, Text(result));
        var root = result.StructuredContent is { } content
            ? JsonNode.Parse(JsonSerializer.Serialize(content))
            : JsonNode.Parse(Text(result));
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        return root!.Deserialize<T>(WireOptions)!;
    }

    private sealed record DocumentState(string? Content, string? Path, string? SelectedFiles, string? Folder)
    {
        public static async Task<DocumentState> ReadAsync(McpClient client) => new(
            await ReadText(client, await FindManaged(client, "DocumentEditor")),
            await ReadText(client, await FindManaged(client, "DocumentPath")),
            await ReadText(client, await FindManaged(client, "SelectedFiles")),
            await ReadText(client, await FindManaged(client, "FolderPath")));
    }

    private sealed record SampleResult(
        long Sequence,
        string Action,
        bool Cancelled,
        string[] SelectedPaths,
        string? DocumentPath,
        string Content,
        int ByteLength,
        string Sha256,
        string? Status,
        string? Error);

    private sealed class SampleProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly DateTime _startedUtc;
        private readonly Task<string> _output;
        private readonly Task<string> _errors;
        private readonly string _resultPath;

        private SampleProcess(Process process, string directory, string pipe)
        {
            _process = process;
            _startedUtc = process.StartTime.ToUniversalTime();
            DirectoryPath = directory;
            PipeName = pipe;
            _resultPath = Path.Combine(directory, "result.json");
            _output = process.StandardOutput.ReadToEndAsync();
            _errors = process.StandardError.ReadToEndAsync();
        }

        public string DirectoryPath { get; }
        public string PipeName { get; }
        public int ProcessId => _process.Id;

        public static async Task<SampleProcess> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "avdt-native-mcp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var pipe = DiagnosticsProtocol.PipePrefix + "native-mcp-" + Guid.NewGuid().ToString("N");
            var ready = Path.Combine(directory, "ready.txt");
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[]
                     {
                         Metadata("AutomationSamplePath"), "--pipe", pipe, "--test-root", directory,
                         "--ready-file", ready, "--result-file", Path.Combine(directory, "result.json")
                     })
                start.ArgumentList.Add(argument);
            var owned = new SampleProcess(Process.Start(start)!, directory, pipe);
            try
            {
                var watch = Stopwatch.StartNew();
                while (!File.Exists(ready) && watch.Elapsed < TimeSpan.FromSeconds(20))
                {
                    Assert.False(owned._process.HasExited, "The owned sample exited before readiness.");
                    await Task.Delay(50, Token);
                }

                Assert.True(File.Exists(ready), "The owned sample did not report readiness.");
                var text = await File.ReadAllTextAsync(ready, Encoding.UTF8, Token);
                Assert.Contains($"pid={owned.ProcessId}\n", text);
                Assert.Contains($"pipe={pipe}\n", text);
                return owned;
            }
            catch
            {
                await owned.DisposeAsync();
                throw;
            }
        }

        public async Task<SampleResult> WaitResultAsync(long previousSequence, string action, bool expectError = false)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (File.Exists(_resultPath))
                {
                    var json = await File.ReadAllTextAsync(_resultPath, Encoding.UTF8, Token);
                    var result = JsonSerializer.Deserialize<SampleResult>(json, WireOptions)!;
                    if (result.Sequence > previousSequence)
                    {
                        Assert.Equal(previousSequence + 1, result.Sequence);
                        Assert.Equal(action, result.Action);
                        if (expectError)
                            Assert.NotNull(result.Error);
                        else
                            Assert.Null(result.Error);
                        return result;
                    }
                }

                await Task.Delay(50, Token);
            }

            throw new TimeoutException($"The application did not complete {action} after sequence {previousSequence}.");
        }

        public async Task WaitForExitAsync()
        {
            await _process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(0, _process.ExitCode);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                if (_process.StartTime.ToUniversalTime() != _startedUtc)
                    throw new InvalidOperationException(
                        "The owned sample process identity changed; refusing to terminate it.");
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None);
            }

            await Task.WhenAll(_output, _errors);
            _process.Dispose();
            var resolved = Path.GetFullPath(DirectoryPath);
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) +
                       Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp + "avdt-native-mcp-", StringComparison.OrdinalIgnoreCase) ||
                Path.GetDirectoryName(resolved) + Path.DirectorySeparatorChar != temp)
                throw new InvalidOperationException("Temporary cleanup escaped its owned directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}

[CollectionDefinition(NativeDesktopMcpCollection.Name, DisableParallelization = true)]
public sealed class NativeDesktopMcpCollection
{
    public const string Name = "Native desktop MCP";
}
