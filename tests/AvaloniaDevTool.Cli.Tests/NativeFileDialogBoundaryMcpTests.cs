using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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
public sealed class NativeFileDialogBoundaryMcpTests
{
    private const string TextProperty = "Avalonia.Controls.TextBlock:Text";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private static readonly JsonSerializerOptions WireOptions = new(ProtocolJson.Options)
        { Converters = { new JsonStringEnumConverter() } };

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public static bool IsInteractiveWindows => OperatingSystem.IsWindows() && Environment.UserInteractive;

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Missing_picker_times_out_and_external_filename_changes_cannot_be_accepted()
    {
        await using var fixture = await SampleProcess.StartAsync();
        await using var client = await CreateClientAsync(fixture.PipeName);
        await SelectFilesTab(client);
        var before = await DocumentState.ReadAsync(client);
        Assert.Empty(Read<NativeFileDialogInfo[]>(await Call(client, "file-dialogs")));
        var watch = Stopwatch.StartNew();
        AssertError(await Call(client, "file-dialog-wait", ("timeoutMs", 200)), "file_dialog_timeout");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Empty(Read<NativeFileDialogInfo[]>(await Call(client, "file-dialogs")));
        Assert.Equal(before, await DocumentState.ReadAsync(client));
        Assert.False(fixture.HasResult);

        var requested = Path.Combine(fixture.DirectoryPath, "intended 中文.txt");
        var replacement = Path.Combine(fixture.DirectoryPath, "external change.txt");
        await File.WriteAllTextAsync(requested, "Intended document\n", Utf8, Token);
        await File.WriteAllTextAsync(replacement, "External replacement\n", Utf8, Token);
        var picker = await OpenPicker(client, fixture, "OpenSingleButton");
        var selected = await SetPaths(client, picker.Info.DialogId, "OpenFile", requested);
        var changed = Read<NativeActionResult>(await Call(client,
            "native-action",
            ("nodeId", selected.PathInputNodeId),
            ("action", "SetValue"),
            ("value", replacement)));
        Assert.NotEqual("failed", changed.Status);
        var externallyChanged =
            Read<NativeFileDialogInfo>(await Call(client, "file-dialog-inspect", ("dialogId", picker.Info.DialogId)));
        Assert.Equal(replacement, externallyChanged.PathInputValue);
        AssertError(await Call(client, "file-dialog-accept", ("dialogId", picker.Info.DialogId)),
            "file_dialog_selection_changed");
        var retained =
            Read<NativeFileDialogInfo>(await Call(client, "file-dialog-inspect", ("dialogId", picker.Info.DialogId)));
        Assert.Equal("Open", retained.State);
        Assert.Equal(replacement, retained.PathInputValue);
        Assert.Equal([requested], retained.RequestedPaths);
        Assert.Contains(Read<NativeFileDialogInfo[]>(await Call(client, "file-dialogs")),
            item => item.DialogId == picker.Info.DialogId);
        Assert.False(fixture.HasResult);
        await CancelAndClose(client, picker);
        var cancelled = await fixture.WaitResultAsync(0, "openSingle");
        Assert.True(cancelled.Cancelled);
        Assert.Empty(cancelled.SelectedPaths);
        Assert.Equal(before, await DocumentState.ReadAsync(client));
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Stale_overwrite_prompt_cannot_answer_a_replacement_prompt()
    {
        await using var fixture = await SampleProcess.StartAsync();
        var target = Path.Combine(fixture.DirectoryPath, "existing overwrite 中文.txt");
        var original = Utf8.GetBytes("Existing content must survive stale prompt handling.\n");
        await File.WriteAllBytesAsync(target, original, Token);
        await using var client = await CreateClientAsync(fixture.PipeName);
        await SelectFilesTab(client);
        var before = await DocumentState.ReadAsync(client);
        var picker = await OpenPicker(client, fixture, "SaveAsButton");
        await SetPaths(client, picker.Info.DialogId, "SaveFile", target);
        Read<NativeFileDialogInfo>(await Call(client, "file-dialog-accept", ("dialogId", picker.Info.DialogId)));
        var first = await WaitOverwritePrompt(client, picker.Info.DialogId, fixture.ProcessId);
        Read<NativeFileDialogInfo>(await Call(client,
            "file-dialog-overwrite",
            ("dialogId", picker.Info.DialogId),
            ("promptId", first.Window.NodeId),
            ("overwrite", false)));
        await WaitNoPrompts(client, picker.Info.DialogId);

        await SetPaths(client, picker.Info.DialogId, "SaveFile", target);
        Read<NativeFileDialogInfo>(await Call(client, "file-dialog-accept", ("dialogId", picker.Info.DialogId)));
        var second = await WaitOverwritePrompt(client, picker.Info.DialogId, fixture.ProcessId);
        Assert.NotEqual(first.Window.NodeId, second.Window.NodeId);
        var promptBefore = Read<NativeTreeResult>(await Call(client,
            "native-tree",
            ("rootNodeId", second.Window.NodeId),
            ("maxDepth", 6),
            ("maxNodes", 150)));
        Assert.False(promptBefore.Truncated);
        AssertError(await Call(client,
                "file-dialog-overwrite",
                ("dialogId", picker.Info.DialogId),
                ("promptId", first.Window.NodeId),
                ("overwrite", true)),
            "stale_file_dialog_prompt");
        var after = Read<NativeFileDialogInfo>(await Call(client,
            "file-dialog-inspect",
            ("dialogId", picker.Info.DialogId)));
        Assert.Equal("ConfirmationRequired", after.State);
        var retained = Assert.Single(after.Prompts);
        Assert.Equal(second.Window.NodeId, retained.Window.NodeId);
        Assert.Equal(second.Message, retained.Message);
        Assert.Equal(second.YesNodeId, retained.YesNodeId);
        Assert.Equal(second.NoNodeId, retained.NoNodeId);
        Assert.Equal(original, await File.ReadAllBytesAsync(target, Token));
        Assert.False(fixture.HasResult);
        Read<NativeFileDialogInfo>(await Call(client,
            "file-dialog-overwrite",
            ("dialogId", picker.Info.DialogId),
            ("promptId", retained.Window.NodeId),
            ("overwrite", false)));
        await WaitNoPrompts(client, picker.Info.DialogId);
        await CancelAndClose(client, picker);
        var result = await fixture.WaitResultAsync(0, "saveAs");
        Assert.True(result.Cancelled);
        Assert.Equal(before, await DocumentState.ReadAsync(client));
        Assert.Equal(original, await File.ReadAllBytesAsync(target, Token));
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Default_extension_is_verified_by_real_saved_path_bytes_and_reopened_content()
    {
        await using var fixture = await SampleProcess.StartAsync();
        await using var client = await CreateClientAsync(fixture.PipeName);
        await SelectFilesTab(client);
        const string content = "Default extension output 中文 😀\n";
        var editor = await FindManaged(client, "DocumentEditor");
        Read<TextSelectionResult>(await Call(client,
            "ui-text-selection",
            ("nodeId", editor),
            ("replacementText", content)));
        var extensionless = Path.Combine(fixture.DirectoryPath, "默认 扩展名");
        var expected = extensionless + ".txt";
        var picker = await OpenPicker(client, fixture, "SaveAsButton");
        var filterResponse = await Call(client,
            "file-dialog-set-filter",
            ("dialogId", picker.Info.DialogId),
            ("filterIndex", 1));
        if (filterResponse.IsError == true)
        {
            // Persist the original failure before any probe can invalidate or time out.
            await fixture.WriteEvidenceAsync("save-filter-failure.json",
                new
                    { response = Text(filterResponse), filterResponse, picker = picker.Info });
            await fixture.CaptureToolEvidenceAsync("save-filter-dialog.json",
                () =>
                    Call(client, "file-dialog-inspect", ("dialogId", picker.Info.DialogId)));
            await fixture.CaptureToolEvidenceAsync("save-filter-picker-tree.json",
                () =>
                    Call(client,
                        "native-tree",
                        ("rootNodeId", picker.Info.DialogId),
                        ("maxDepth", 20),
                        ("maxNodes", 5000)));
            await fixture.CaptureToolEvidenceAsync("save-filter-combo-tree.json",
                () =>
                    Call(client,
                        "native-tree",
                        ("rootNodeId", picker.Info.FilterNodeId),
                        ("maxDepth", 20),
                        ("maxNodes", 500)));
        }

        var plain = Read<NativeFileDialogInfo>(filterResponse);
        Assert.False(string.IsNullOrWhiteSpace(plain.FilterValue));
        await SetPaths(client, picker.Info.DialogId, "SaveFile", extensionless);
        await AcceptAndClose(client, picker);
        var saved = await fixture.WaitResultAsync(0, "saveAs");
        Assert.False(saved.Cancelled);
        Assert.Equal([expected], saved.SelectedPaths);
        await VerifyDocument(client, saved, content, expected);
        Assert.False(File.Exists(extensionless));
        Assert.Equal(Utf8.GetBytes(content), await File.ReadAllBytesAsync(expected, Token));
        var reopen = await OpenPicker(client, fixture, "OpenSingleButton");
        await SetPaths(client, reopen.Info.DialogId, "OpenFile", expected);
        await AcceptAndClose(client, reopen);
        var opened = await fixture.WaitResultAsync(saved.Sequence, "openSingle");
        await VerifyDocument(client, opened, content, expected);
    }

    [Theory(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    [InlineData("read", false)]
    [InlineData("write", false)]
    [InlineData("read", true)]
    [InlineData("write", true)]
    public async Task Real_file_io_failures_preserve_document_and_report_error(string operation, bool denyAccess)
    {
        await using var fixture = await SampleProcess.StartAsync();
        var baseline = Path.Combine(fixture.DirectoryPath, "baseline document.txt");
        var target = Path.Combine(fixture.DirectoryPath, "blocked target 中文.txt");
        const string baselineContent = "Previously opened real UTF-8 document 😀\n";
        var original = Utf8.GetBytes("Original target bytes that must remain unchanged.\n");
        await File.WriteAllTextAsync(baseline, baselineContent, Utf8, Token);
        await File.WriteAllBytesAsync(target, original, Token);
        await using var client = await CreateClientAsync(fixture.PipeName);
        await SelectFilesTab(client);
        var initialPicker = await OpenPicker(client, fixture, "OpenSingleButton");
        await SetPaths(client, initialPicker.Info.DialogId, "OpenFile", baseline);
        await AcceptAndClose(client, initialPicker);
        var opened = await fixture.WaitResultAsync(0, "openSingle");
        await VerifyDocument(client, opened, baselineContent, baseline);
        if (operation == "write")
            Read<TextSelectionResult>(await Call(client,
                "ui-text-selection",
                ("nodeId", await FindManaged(client, "DocumentEditor")),
                ("replacementText", "Unsaved replacement text 中文 😀\n")));
        var before = await DocumentState.ReadAsync(client);

        await fixture.ArmIoGateAsync();
        var picker = await OpenPicker(client, fixture, operation == "read" ? "OpenSingleButton" : "SaveAsButton");
        await SetPaths(client, picker.Info.DialogId, operation == "read" ? "OpenFile" : "SaveFile", target);
        Read<NativeFileDialogInfo>(await Call(client, "file-dialog-accept", ("dialogId", picker.Info.DialogId)));
        if (operation == "write")
        {
            var prompt = await WaitOverwritePrompt(client, picker.Info.DialogId, fixture.ProcessId);
            Read<NativeFileDialogInfo>(await Call(client,
                "file-dialog-overwrite",
                ("dialogId", picker.Info.DialogId),
                ("promptId", prompt.Window.NodeId),
                ("overwrite", true)));
        }

        await WaitClosed(client, picker.Info.DialogId);
        var gate = await fixture.WaitIoGateAsync();
        Assert.Equal(operation, gate.Operation);
        Assert.Equal(target, gate.Path);
        SampleResult failed;
        // Apply the real obstruction only after the genuine picker has returned.
        using (denyAccess
                   ? DenyFileDataAccess(target, operation)
                   : new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await fixture.ReleaseIoGateAsync();
            Read<InputResult>(await picker.Click);
            failed = await fixture.WaitResultAsync(opened.Sequence,
                operation == "read" ? "openSingle" : "saveAs",
                expectError: true);
            Assert.Contains(denyAccess ? "UnauthorizedAccessException" : "IOException", failed.Error);
            Assert.StartsWith("Error:", failed.Status);
            Assert.False(failed.Cancelled);
            Assert.Equal([target], failed.SelectedPaths);
            Assert.Equal(before, await DocumentState.ReadAsync(client));
            await VerifyDocument(client, failed, before.Content!, baseline);
            Assert.Equal(failed.Status, await ReadText(client, await FindManaged(client, "FileStatus")));
            Assert.Empty(Read<NativeFileDialogInfo[]>(await Call(client, "file-dialogs")));
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(target, Token));
        Assert.Equal(Utf8.GetBytes(baselineContent), await File.ReadAllBytesAsync(baseline, Token));

        // The application must remain usable after the genuine I/O failure.
        var recovery = await OpenPicker(client, fixture, "OpenSingleButton");
        await SetPaths(client, recovery.Info.DialogId, "OpenFile", baseline);
        await AcceptAndClose(client, recovery);
        var recovered = await fixture.WaitResultAsync(failed.Sequence, "openSingle");
        await VerifyDocument(client, recovered, baselineContent, baseline);
    }

    private static IDisposable DenyFileDataAccess(string path, string operation)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var resolved = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(resolved)!;
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(parent), temporary, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(parent).StartsWith("avdt-picker-boundary-", StringComparison.Ordinal))
            throw new InvalidOperationException("ACL changes are restricted to the owned temporary fixture files.");
        var file = new FileInfo(path);
        var original = file.GetAccessControl(AccessControlSections.Access);
        var originalDacl = original.GetSecurityDescriptorBinaryForm();
        var recoveryFile = path + ".test-original-dacl.bin";
        File.WriteAllBytes(recoveryFile, originalDacl);
        var restricted = new FileSecurity();
        restricted.SetSecurityDescriptorBinaryForm(originalDacl, AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("The test process has no Windows SID.");
        restricted.AddAccessRule(new FileSystemAccessRule(sid,
            operation == "read" ? FileSystemRights.ReadData : FileSystemRights.WriteData,
            AccessControlType.Deny));
        file.SetAccessControl(restricted);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
            {
                using var stream = File.Open(path,
                    FileMode.Open,
                    operation == "read" ? FileAccess.Read : FileAccess.Write,
                    FileShare.ReadWrite);
            });
            return new RestoreFileAccess(() =>
            {
                RestoreFileDacl(path, originalDacl);
                File.Delete(recoveryFile);
            });
        }
        catch
        {
            RestoreFileDacl(path, originalDacl);
            File.Delete(recoveryFile);
            throw;
        }
    }

    private static void RestoreFileDacl(string path, byte[] originalDacl)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        // A freshly loaded FileSecurity marks only the DACL as modified for Persist.
        var restored = new FileSecurity();
        restored.SetSecurityDescriptorBinaryForm(originalDacl, AccessControlSections.Access);
        var file = new FileInfo(path);
        file.SetAccessControl(restored);
        Assert.Equal(restored.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
            file.GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    private sealed class RestoreFileAccess(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private static Task<McpClient> CreateClientAsync(string pipe) => McpClient.CreateAsync(new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = "dotnet", Name = "Native picker boundary stdio tests",
                Arguments = [Metadata("PreviewCliPath"), "mcp", "--pipe", pipe],
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            }),
        cancellationToken: Token);

    private static string Metadata(string key) => typeof(NativeFileDialogBoundaryMcpTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == key).Value!;

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

    private static async Task<string?> ReadText(McpClient client, long id) => Assert.Single(
            Read<PropertyInfoDto[]>(await Call(client,
                "props",
                ("nodeId", id),
                ("propertyNames", new[] { TextProperty }))))
        .Value;

    private static async Task SelectFilesTab(McpClient client) => Read<ItemSelectionResult>(await Call(client,
        "ui-select-item",
        ("nodeId", await FindManaged(client, "MainTabs")),
        ("index", 1)));

    private sealed record Picker(NativeFileDialogInfo Info, Task<CallToolResult> Click);

    private static async Task<Picker> OpenPicker(McpClient client, SampleProcess fixture, string button)
    {
        var click = Call(client, "ui-click", ("nodeId", await FindManaged(client, button)));
        var info = Read<NativeFileDialogInfo>(await Call(client, "file-dialog-wait", ("timeoutMs", 10000)));
        Assert.Equal(fixture.ProcessId, info.Window.ProcessId);
        Assert.False(info.ApplicationIoVerified);
        return new(info, click);
    }

    private static async Task<NativeFileDialogInfo> SetPaths(McpClient client,
        string dialogId,
        string kind,
        params string[] paths)
    {
        var info = Read<NativeFileDialogInfo>(await Call(client,
            "file-dialog-set-paths",
            ("dialogId", dialogId),
            ("paths", paths),
            ("kind", kind)));
        Assert.Equal(paths, info.RequestedPaths);
        Assert.False(info.ApplicationIoVerified);
        return info;
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
        var info = Read<NativeFileDialogInfo>(await Call(client,
            "file-dialog-wait",
            ("dialogId", dialogId),
            ("timeoutMs", 10000)));
        Assert.EndsWith("Closed", info.State);
        Assert.False(info.ApplicationIoVerified);
        Assert.Empty(info.Prompts);
        return info;
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

        throw new TimeoutException("The overwrite refusal did not return to the original picker.");
    }

    private static async Task VerifyDocument(McpClient client, SampleResult result, string content, string? path)
    {
        Assert.Equal(content, result.Content);
        Assert.Equal(path, result.DocumentPath);
        Assert.Equal(Utf8.GetByteCount(content), result.ByteLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(content))), result.Sha256, ignoreCase: true);
        Assert.Equal(content, await ReadText(client, await FindManaged(client, "DocumentEditor")));
        Assert.Equal(path ?? "(untitled)", await ReadText(client, await FindManaged(client, "DocumentPath")));
    }

    private static void AssertError(CallToolResult result, string code)
    {
        Assert.True(result.IsError, Text(result));
        Assert.Contains(code, Text(result));
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

    private sealed record IoGateEntry(string Operation, string Path);

    private sealed class SampleProcess : IAsyncDisposable
    {
        private const string Prefix = "avdt-picker-boundary-";
        private readonly Process _process;
        private readonly DateTime _startedUtc;
        private readonly Task<string> _output;
        private readonly Task<string> _errors;
        private readonly string _resultPath;
        private string GateDirectory => Path.Combine(DirectoryPath, "io-gate");

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
        public bool HasResult => File.Exists(_resultPath);

        public static async Task<SampleProcess> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var pipe = DiagnosticsProtocol.PipePrefix + "picker-boundary-" + Guid.NewGuid().ToString("N");
            var ready = Path.Combine(directory, "ready.txt");
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[]
                     {
                         Metadata("AutomationSamplePath"), "--pipe", pipe, "--test-root", directory,
                         "--ready-file", ready, "--result-file", Path.Combine(directory, "result.json"), "--io-gate",
                         Path.Combine(directory, "io-gate")
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

        public async Task ArmIoGateAsync()
        {
            Directory.CreateDirectory(GateDirectory);
            foreach (var name in new[] { "release", "entered.json", "arm" })
                File.Delete(Path.Combine(GateDirectory, name));
            await File.WriteAllTextAsync(Path.Combine(GateDirectory, "arm"), "armed", Utf8, Token);
        }

        public async Task<IoGateEntry> WaitIoGateAsync()
        {
            var entered = Path.Combine(GateDirectory, "entered.json");
            var watch = Stopwatch.StartNew();
            while (!File.Exists(entered) && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                Assert.False(_process.HasExited, "The sample exited before reaching real file I/O.");
                await Task.Delay(25, Token);
            }

            Assert.True(File.Exists(entered), "The real picker did not reach the test-only I/O gate.");
            return JsonSerializer.Deserialize<IoGateEntry>(await File.ReadAllTextAsync(entered, Encoding.UTF8, Token),
                WireOptions)!;
        }

        public Task ReleaseIoGateAsync() =>
            File.WriteAllTextAsync(Path.Combine(GateDirectory, "release"), "released", Utf8, Token);

        public async Task WriteEvidenceAsync<T>(string filename, T value)
        {
            var destination = Path.Combine(DirectoryPath, filename);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, WireOptions), Utf8, Token);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        public async Task CaptureToolEvidenceAsync(string filename, Func<Task<CallToolResult>> inspect)
        {
            object evidence;
            try
            {
                var response = await inspect();
                evidence = new { response = Text(response), toolResponse = response };
            }
            catch (Exception error)
            {
                evidence = new { error = error.ToString() };
            }

            await WriteEvidenceAsync(filename, evidence);
        }

        public async Task<SampleResult> WaitResultAsync(long previousSequence, string action, bool expectError = false)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (File.Exists(_resultPath))
                {
                    var result =
                        JsonSerializer.Deserialize<SampleResult>(
                            await File.ReadAllTextAsync(_resultPath, Encoding.UTF8, Token),
                            WireOptions)!;
                    if (result.Sequence > previousSequence)
                    {
                        await WriteEvidenceAsync($"result-{result.Sequence}.json", result);
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
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) +
                            Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temporary + Prefix, StringComparison.OrdinalIgnoreCase) ||
                Path.GetDirectoryName(resolved) + Path.DirectorySeparatorChar != temporary)
                throw new InvalidOperationException("Temporary cleanup escaped its owned directory.");
            foreach (var recovery in Directory.EnumerateFiles(resolved, "*.test-original-dacl.bin"))
            {
                RestoreFileDacl(recovery[..^".test-original-dacl.bin".Length], await File.ReadAllBytesAsync(recovery));
                File.Delete(recovery);
            }

            var evidence = Path.GetFullPath(Path.Combine(Metadata("PreviewRepositoryPath"),
                "artifacts",
                "verification",
                "file-boundaries",
                "fixtures",
                Path.GetFileName(resolved)));
            Directory.CreateDirectory(evidence);
            foreach (var source in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(evidence, Path.GetRelativePath(resolved, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }

            await File.WriteAllTextAsync(Path.Combine(evidence, "stdout.txt"), await _output, Utf8);
            await File.WriteAllTextAsync(Path.Combine(evidence, "stderr.txt"), await _errors, Utf8);
            Directory.Delete(resolved, recursive: true);
        }
    }
}
