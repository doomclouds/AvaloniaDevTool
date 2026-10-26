using System.Diagnostics;
using System.Reflection;
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
public sealed class NativePickerProfileTests
{
    private const string TextProperty = "Avalonia.Controls.TextBlock:Text";

    private static readonly JsonSerializerOptions WireOptions = new(ProtocolJson.Options)
        { Converters = { new JsonStringEnumConverter() } };

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public static bool IsInteractiveWindows => OperatingSystem.IsWindows() && Environment.UserInteractive;

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task A_real_picker_with_a_matched_but_truncated_profile_refuses_inspection_and_path_mutation()
    {
        await using var fixture = await SampleProcess.StartAsync();
        var originalPath = Path.Combine(fixture.DirectoryPath, "original filename 中文.txt");
        var replacementPath = Path.Combine(fixture.DirectoryPath, "rejected replacement.txt");
        await File.WriteAllTextAsync(originalPath, "original document", new UTF8Encoding(false), Token);
        await File.WriteAllTextAsync(replacementPath, "replacement document", new UTF8Encoding(false), Token);
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet", Name = "Native picker profile stdio tests",
                Arguments = [Metadata("PreviewCliPath"), "mcp", "--pipe", fixture.PipeName],
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            }),
            cancellationToken: Token);
        Read<ItemSelectionResult>(await Call(client,
            "ui-select-item",
            ("nodeId", await FindManaged(client, "MainTabs")),
            ("index", 1)));
        var beforeDocument = await DocumentState.ReadAsync(client);
        var click = Call(client, "ui-click", ("nodeId", await FindManaged(client, "OpenSingleButton")));
        var mcpPicker = Read<NativeFileDialogInfo>(await Call(client, "file-dialog-wait", ("timeoutMs", 10000)));
        Assert.Equal(fixture.ProcessId, mcpPicker.Window.ProcessId);

        var workerPath = Path.Combine(Path.GetDirectoryName(Metadata("PreviewCliPath"))!,
            "native-worker",
            "AvaloniaDevTool.NativeAutomation.Worker.dll");
        Assert.True(File.Exists(workerPath), workerPath);
        await using var native = await NativeAutomationSession.AttachAsync(fixture.ProcessId,
            fixture.StartTimeUtc,
            new NativeAutomationOptions { WorkerPath = workerPath },
            Token);
        var window = Assert.Single(await native.GetWindowsAsync(Token), item => item.Handle == mcpPicker.Window.Handle);
        Assert.Equal(fixture.ProcessId, window.ProcessId);
        var completeController = new NativeFileDialogController(native);
        var baseline = await completeController.InspectAsync(window.NodeId, Token);
        Assert.Equal("windows-common-item-dialog-v1", baseline.ProviderProfile);
        Assert.Equal("Open", baseline.State);
        baseline = await completeController.SetPathsAsync(window.NodeId,
            [originalPath],
            NativeFileDialogKind.OpenFile,
            Token);
        Assert.Equal(originalPath, baseline.PathInputValue);

        var observations = new List<BudgetObservation>();
        NativeFileDialogOptions? limitedOptions = null;
        for (var depth = 0; depth <= 32; depth++)
        {
            var tree = await native.GetTreeAsync(new NativeTreeQuery
                    { RootNodeId = window.NodeId, MaxDepth = depth, MaxNodes = 5000 },
                Token);
            var observation = ObserveProfile(depth, tree);
            observations.Add(observation);
            if (observation.ProfileMatched && tree.Truncated)
            {
                limitedOptions = new NativeFileDialogOptions { MaxDepth = depth, MaxNodes = 5000 };
                break;
            }
        }

        Evidence("real-picker-depth-search", new { fixture.ProcessId, window.Handle, observations });
        Assert.True(limitedOptions is not null,
            "No real tree budget retained the complete filename/accept/cancel/shell profile while truncating another subtree. " +
            JsonSerializer.Serialize(observations));

        var limitedController = new NativeFileDialogController(native, limitedOptions);
        var inspection =
            await Assert.ThrowsAsync<NativeAutomationException>(() =>
                limitedController.InspectAsync(window.NodeId, Token));
        Assert.Equal("file_dialog_tree_truncated", inspection.Code);
        var afterInspection = await completeController.InspectAsync(window.NodeId, Token);
        Assert.Equal(baseline.Window.Handle, afterInspection.Window.Handle);
        Assert.Equal(baseline.PathInputValue, afterInspection.PathInputValue);
        Assert.Equal("Open", afterInspection.State);
        var mutation = await Assert.ThrowsAsync<NativeAutomationException>(() => limitedController.SetPathsAsync(
            window.NodeId,
            [replacementPath],
            NativeFileDialogKind.OpenFile,
            Token));
        Assert.Equal("file_dialog_tree_truncated", mutation.Code);
        var afterMutation = await completeController.InspectAsync(window.NodeId, Token);
        Assert.Equal(baseline.Window.Handle, afterMutation.Window.Handle);
        Assert.Equal(baseline.PathInputValue, afterMutation.PathInputValue);
        Assert.Equal("Open", afterMutation.State);
        Assert.False(File.Exists(fixture.ResultPath),
            "The application completed while the picker should still be open.");
        Evidence("truncated-profile-rejection",
            new
            {
                budget = limitedOptions, inspectError = inspection.Code, mutationError = mutation.Code,
                originalFilename = baseline.PathInputValue, retainedFilename = afterMutation.PathInputValue,
                originalHandle = baseline.Window.Handle, retainedHandle = afterMutation.Window.Handle
            });

        var permitted =
            await completeController.SetPathsAsync(window.NodeId,
                [replacementPath],
                NativeFileDialogKind.OpenFile,
                Token);
        Assert.Equal(replacementPath, permitted.PathInputValue);
        Assert.Equal(baseline.Window.Handle, permitted.Window.Handle);
        await completeController.CancelAsync(window.NodeId, Token);
        var closed = await completeController.WaitForClosedAsync(window.NodeId, cancellationToken: Token);
        Assert.Equal("CancelledDialogClosed", closed.State);
        Assert.False(closed.ApplicationIoVerified);
        Read<InputResult>(await click);
        var result = await fixture.WaitResultAsync();
        Assert.Equal("openSingle", result.Action);
        Assert.True(result.Cancelled);
        Assert.Empty(result.SelectedPaths);
        Assert.Null(result.Error);
        Assert.Equal(beforeDocument, await DocumentState.ReadAsync(client));
        Assert.Equal(beforeDocument.Content, result.Content);
        Assert.Equal(beforeDocument.Path == "(untitled)" ? null : beforeDocument.Path, result.DocumentPath);
        Assert.Equal("original document", await File.ReadAllTextAsync(originalPath, Encoding.UTF8, Token));
        Assert.Equal("replacement document", await File.ReadAllTextAsync(replacementPath, Encoding.UTF8, Token));
        Evidence("default-budget-cancel",
            new
            {
                permitted.ProviderProfile, permitted.PathInputValue, closed.State, result.Cancelled,
                result.SelectedPaths
            });
    }

    private static BudgetObservation ObserveProfile(int depth, NativeTreeResult tree)
    {
        var edits = tree.Nodes.Where(node => node.ControlType == "Edit" && node.AutomationId is "1148" or "1152")
            .ToArray();
        var accept =
            tree.Nodes.Count(node => node.AutomationId == "1" && node.ControlType is "Button" or "SplitButton");
        var cancel =
            tree.Nodes.Count(node => node.AutomationId == "2" && node.ControlType is "Button" or "SplitButton");
        var shell = tree.Nodes.Any(node => node.ClassName == "DUIViewWndClassName");
        return new(depth,
            tree.Nodes.Length,
            tree.Truncated,
            edits.Select(node => node.AutomationId).ToArray(),
            accept,
            cancel,
            shell,
            edits.Length == 1 && accept == 1 && cancel == 1 && shell);
    }

    private sealed record BudgetObservation(
        int Depth,
        int Nodes,
        bool Truncated,
        string[] FilenameIds,
        int AcceptButtons,
        int CancelButtons,
        bool HasShell,
        bool ProfileMatched);

    private static void Evidence(string evidence, object actual) => TestContext.Current.TestOutputHelper?.WriteLine(
        JsonSerializer.Serialize(new { evidence, actual }, WireOptions));

    private static string Metadata(string key) => typeof(NativePickerProfileTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(item => item.Key == key).Value!;

    private static async Task<CallToolResult> Call(McpClient client,
        string name,
        params (string Name, object? Value)[] values) =>
        await client.CallToolAsync(name,
                values.ToDictionary(item => item.Name, item => item.Value),
                cancellationToken: Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(120), Token);

    private static async Task<long> FindManaged(McpClient client, string name) => Assert.Single(
        Read<TreeSearchResult>(await Call(client, "search", ("query", name))).Matches,
        node => node.Name == name).Id;

    private static async Task<string?> ReadText(McpClient client, string name) => Assert.Single(Read<PropertyInfoDto[]>(
        await Call(client,
            "props",
            ("nodeId", await FindManaged(client, name)),
            ("propertyNames", new[] { TextProperty })))).Value;

    private static T Read<T>(CallToolResult result)
    {
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.False(result.IsError == true, text);
        var root = result.StructuredContent is { } content
            ? JsonNode.Parse(JsonSerializer.Serialize(content))
            : JsonNode.Parse(text);
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        return root!.Deserialize<T>(WireOptions)!;
    }

    private sealed record DocumentState(string? Content, string? Path, string? SelectedFiles, string? Folder)
    {
        public static async Task<DocumentState> ReadAsync(McpClient client) => new(
            await ReadText(client, "DocumentEditor"),
            await ReadText(client, "DocumentPath"),
            await ReadText(client, "SelectedFiles"),
            await ReadText(client, "FolderPath"));
    }

    private sealed record SampleResult(
        string Action,
        bool Cancelled,
        string[] SelectedPaths,
        string? DocumentPath,
        string Content,
        string? Error);

    private sealed class SampleProcess : IAsyncDisposable
    {
        private const string Prefix = "avdt-picker-profile-";
        private readonly Process _process;
        private readonly Task<string> _output;
        private readonly Task<string> _errors;

        private SampleProcess(Process process, string directory, string pipe)
        {
            _process = process;
            DirectoryPath = directory;
            PipeName = pipe;
            StartTimeUtc = process.StartTime.ToUniversalTime();
            _output = process.StandardOutput.ReadToEndAsync();
            _errors = process.StandardError.ReadToEndAsync();
        }

        public string DirectoryPath { get; }
        public string PipeName { get; }
        public string ResultPath => Path.Combine(DirectoryPath, "result.json");
        public int ProcessId => _process.Id;
        public DateTimeOffset StartTimeUtc { get; }

        public static async Task<SampleProcess> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var pipe = DiagnosticsProtocol.PipePrefix + "picker-profile-" + Guid.NewGuid().ToString("N");
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
                var timer = Stopwatch.StartNew();
                while (!File.Exists(ready) && timer.Elapsed < TimeSpan.FromSeconds(20))
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

        public async Task<SampleResult> WaitResultAsync()
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(ResultPath) && timer.Elapsed < TimeSpan.FromSeconds(15))
                await Task.Delay(50, Token);
            Assert.True(File.Exists(ResultPath), "The application did not record the real picker cancellation.");
            return JsonSerializer.Deserialize<SampleResult>(
                await File.ReadAllTextAsync(ResultPath, Encoding.UTF8, Token),
                WireOptions)!;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                if (new DateTimeOffset(_process.StartTime.ToUniversalTime()) != StartTimeUtc)
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
            if (!resolved.StartsWith(temp + Prefix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(resolved) + Path.DirectorySeparatorChar,
                    temp,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Temporary cleanup escaped its owned directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
