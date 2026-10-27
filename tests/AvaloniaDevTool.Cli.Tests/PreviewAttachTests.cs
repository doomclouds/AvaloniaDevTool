using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class PreviewAttachTests
{
    private static string CliPath => Metadata("PreviewCliPath");

    [Fact]
    public async Task
        Stdio_attach_previews_built_classful_project_resources_custom_controls_input_pixels_and_live_saves()
    {
        using var files = new FixtureFiles();
        await files.BuildAsync();
        await using var client = await CreateClientAsync();
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, tool => tool.Name == "attach-to-file");
        var attachment = Read<PreviewAttachment>(await CallAsync(client, "attach-to-file", Arguments(files)));
        var processId = attachment.Session.ProcessId;
        Assert.NotEqual(Environment.ProcessId, processId);
        try
        {
            var tree = Read<ControlTreeSnapshot>(await CallAsync(client,
                "tree",
                new Dictionary<string, object?> { ["depth"] = -1, ["treeKind"] = "Logical" }));
            Assert.Contains(tree.Entries, entry => entry.Node.Name == "SavedText");
            Assert.Equal("Project App resource", (await SearchAsync(client, "ProjectResourceText")).Preview);
            Assert.Equal("Built custom control", (await SearchAsync(client, "CustomControlText")).Preview);
            Assert.Equal("Referenced assembly control", (await SearchAsync(client, "ReferencedControlText")).Preview);
            var editable = await SearchAsync(client, "EditableText");
            var input = Read<InputResult>(await CallAsync(client,
                "input",
                new Dictionary<string, object?>
                    { ["nodeId"] = editable.Id, ["inputEvent"] = "Text", ["arg"] = " appended" }));
            Assert.True(input.Delivered);
            var properties = Read<PropertyInfoDto[]>(await CallAsync(client,
                "props",
                new Dictionary<string, object?> { ["nodeId"] = editable.Id }));
            Assert.Contains(properties,
                property => property.Name == "Text" &&
                            property.Value?.Contains("appended", StringComparison.Ordinal) == true);
            var screenshot = await CallAsync(client,
                "screenshot",
                new Dictionary<string, object?> { ["nodeId"] = editable.Id });
            Assert.NotEqual(true, screenshot.IsError);
            var image = Assert.Single(screenshot.Content.OfType<ImageContentBlock>());
            using var bitmap = SKBitmap.Decode(image.DecodedData.ToArray());
            Assert.NotNull(bitmap);
            Assert.True(bitmap.Width > 10 && bitmap.Height > 10);
            Assert.True(bitmap.Pixels.Distinct().Take(5).Count() >= 5,
                "Skia must render actual text pixels, not an empty headless bitmap.");
            var original = await SearchAsync(client, "SavedText");
            files.Write(files.Initial.Replace("First source", "Second source", StringComparison.Ordinal));
            var second = await WaitForNodeAsync(client, "SavedText", node => node?.Preview == "Second source");
            Assert.NotEqual(original.Id, second.Id);
            files.Write("<UserControl");
            await WaitForNodeAsync(client,
                "PreviewErrorText",
                node => node?.Preview?.Contains("xaml_load_failed", StringComparison.Ordinal) == true);
            Assert.Equal(second.Id, (await SearchAsync(client, "SavedText")).Id);
            files.Write(files.Initial.Replace("First source", "Recovered source", StringComparison.Ordinal));
            await WaitForNodeAsync(client, "SavedText", node => node?.Preview == "Recovered source");
            Assert.NotEqual(true, (await CallAsync(client, "detach", new Dictionary<string, object?>())).IsError);
            await WaitForExitAsync(processId);
            var disconnected = await CallAsync(client, "tree", new Dictionary<string, object?>());
            Assert.True(disconnected.IsError);
        }
        finally
        {
            await CallAsync(client, "detach", new Dictionary<string, object?>());
        }
    }

    [Fact]
    public async Task Window_root_uses_the_real_designer_and_mcp_shutdown_releases_its_process()
    {
        using var files = new FixtureFiles(window: true);
        await files.BuildAsync();
        var client = await CreateClientAsync();
        int pid;
        try
        {
            pid = Read<PreviewAttachment>(await CallAsync(client, "attach-to-file", Arguments(files))).Session
                .ProcessId;
            Assert.Equal("Project App resource", (await SearchAsync(client, "WindowResourceText")).Preview);
            Assert.Contains("第 1 次", (await SearchAsync(client, "PreviewStatusText")).Preview);
            files.Write(files.Initial.Replace("<Window ",
                "<Window Title=\"Window source saved\" ",
                StringComparison.Ordinal));
            await WaitForNodeAsync(client,
                "PreviewStatusText",
                node => node?.Preview?.Contains("第 2 次", StringComparison.Ordinal) == true);
            var roots = Read<ControlTreeSnapshot>(await CallAsync(client,
                "tree",
                new Dictionary<string, object?> { ["depth"] = 1 }));
            Assert.True(roots.Entries.Count >= 2);
            Assert.Contains(roots.Entries, entry => entry.Node.Preview == "Window source saved");
        }
        finally
        {
            await client.DisposeAsync();
        }

        await WaitForExitAsync(pid);
    }

    [Fact]
    public async Task Missing_build_and_absent_class_fail_clearly_and_leave_no_attachment()
    {
        using var files = new FixtureFiles();
        await using var client = await CreateClientAsync();
        var missing = Arguments(files);
        var missingResult = await CallAsync(client, "attach-to-file", missing);
        Assert.True(missingResult.IsError);
        Assert.Contains("project_not_built", Text(missingResult));
        await files.BuildAsync();
        files.Write(files.Initial.Replace("AvaloniaDevTool.PreviewFixture.FixtureView",
            "AvaloniaDevTool.PreviewFixture.AbsentClass",
            StringComparison.Ordinal));
        var invalid = await CallAsync(client, "attach-to-file", Arguments(files));
        Assert.True(invalid.IsError);
        Assert.Contains("class_not_built", Text(invalid));
        Assert.True((await CallAsync(client, "tree", new Dictionary<string, object?>())).IsError);
    }

    [Fact]
    public async Task Style_and_resource_dictionary_use_the_framework_design_preview_conversion()
    {
        using var files = new FixtureFiles();
        await files.BuildAsync();
        files.Write("""
            <Style xmlns="https://github.com/avaloniaui" Selector="Button">
              <Design.PreviewWith><Button Name="StylePreview" Content="Style source preview" /></Design.PreviewWith>
              <Setter Property="Background" Value="#D32F2F" />
            </Style>
            """);
        await using var client = await CreateClientAsync();
        Read<PreviewAttachment>(await CallAsync(client, "attach-to-file", Arguments(files)));
        var button = await SearchAsync(client, "StylePreview");
        var properties = Read<PropertyInfoDto[]>(await CallAsync(client,
            "props",
            new Dictionary<string, object?> { ["nodeId"] = button.Id }));
        Assert.Contains(properties,
            property => property.Name == "Background" &&
                        property.Value?.Contains("d32f2f", StringComparison.OrdinalIgnoreCase) == true);
        files.Write("""
            <ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Design.PreviewWith><TextBlock Name="DictionaryPreview" Text="{DynamicResource Greeting}" /></Design.PreviewWith>
              <x:String x:Key="Greeting">Dictionary source preview</x:String>
            </ResourceDictionary>
            """);
        await WaitForNodeAsync(client, "DictionaryPreview", node => node?.Preview == "Dictionary source preview");
    }

    [Fact]
    public async Task Referenced_library_controls_share_the_selected_projects_App_resources()
    {
        using var files = new FixtureFiles(referenced: true);
        await files.BuildAsync();
        await using var client = await CreateClientAsync();
        var arguments = Arguments(files);
        var attachment = Read<PreviewAttachment>(await CallAsync(client, "attach-to-file", arguments));
        Assert.Equal(files.AssemblyPath, attachment.AssemblyPath);
        Assert.Equal("Project App resource", (await SearchAsync(client, "LibraryAppResource")).Preview);
    }

    [Fact]
    public async Task Startup_timeout_and_cancellation_kill_the_owned_blocked_child()
    {
        using var files = new FixtureFiles();
        await files.BuildAsync();
        var pidPath = Path.Combine(files.Root, "child.pid");
        await using var client = await CreateClientAsync(new Dictionary<string, string?>
            { ["AVDT_OFFICIAL_PREVIEW_BLOCK"] = pidPath });
        var arguments = Arguments(files);
        var timeout = await CallAsync(client, "attach-to-file", arguments);
        Assert.True(timeout.IsError);
        Assert.Contains("preview_timeout", Text(timeout));
        var pid = int.Parse(
            (await File.ReadAllTextAsync(pidPath, Encoding.UTF8, TestContext.Current.CancellationToken)).Trim('\uFEFF',
                ' ',
                '\r',
                '\n'));
        await WaitForExitAsync(pid);
        File.Delete(pidPath);
        var requestId = new RequestId("cancel-owned-preview");
        var pending = client.SendRequestAsync(new JsonRpcRequest
            {
                Id = requestId, Method = "tools/call",
                Params = JsonSerializer.SerializeToNode(new { name = "attach-to-file", arguments })
            },
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => File.Exists(pidPath));
        var cancelledPid =
            int.Parse(
                (await File.ReadAllTextAsync(pidPath, Encoding.UTF8, TestContext.Current.CancellationToken)).Trim(
                    '\uFEFF',
                    ' ',
                    '\r',
                    '\n'));
        await client.SendMessageAsync(new JsonRpcNotification
            {
                Method = "notifications/cancelled",
                Params = JsonSerializer.SerializeToNode(new CancelledNotificationParams { RequestId = requestId })
            },
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await WaitForExitAsync(cancelledPid);
        Assert.True((await CallAsync(client, "tree", new Dictionary<string, object?>())).IsError);
    }

    [Fact]
    public async Task Attaching_to_an_external_app_never_gives_the_session_permission_to_kill_it()
    {
        using var files = new FixtureFiles();
        await files.BuildAsync();
        var pipe = "avalonia-devtool-external-preview-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[]
                 {
                     Path.Combine(Path.GetDirectoryName(CliPath)!, "previewer", "AvaloniaDevTool.Previewer.dll"),
                     "--xaml", files.SourcePath, "--assembly", files.AssemblyPath, "--project", files.ProjectPath,
                     "--pipe",
                     pipe, "--headless"
                 })
            start.ArgumentList.Add(argument);
        using var external = Process.Start(start)!;
        var output = external.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = external.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var client = await CreateClientAsync();
            var attach = await AttachToAppAsync(client, pipe);
            Assert.NotEqual(true, attach.IsError);
            Assert.Equal("Project App resource", (await SearchAsync(client, "ProjectResourceText")).Preview);
            await CallAsync(client, "detach", new Dictionary<string, object?>());
            Assert.False(external.HasExited);
            await client.DisposeAsync();
            Assert.False(external.HasExited);
        }
        finally
        {
            if (!external.HasExited)
                external.Kill(entireProcessTree: true);
            await external.WaitForExitAsync(TestContext.Current.CancellationToken);
            await Task.WhenAll(output, error);
        }
    }

    [Fact]
    public async Task Abrupt_owner_exit_releases_a_child_even_while_the_target_builder_is_blocked()
    {
        using var files = new FixtureFiles();
        await files.BuildAsync();
        var pidPath = Path.Combine(files.Root, "orphan.pid");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(CliPath);
        start.ArgumentList.Add("mcp");
        start.Environment["AVDT_OFFICIAL_PREVIEW_BLOCK"] = pidPath;
        start.Environment["AVDT_PREVIEW_HEADLESS"] = "true";
        using var owner = Process.Start(start)!;
        var error = owner.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await owner.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-11-25", capabilities = new { },
                    clientInfo = new { name = "owner-loss-test", version = "1" }
                }
            }));
            var initialized =
                JsonNode.Parse((await owner.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken))!);
            Assert.NotNull(initialized?["result"]);
            await owner.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var arguments = Arguments(files);
            await owner.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "attach-to-file", arguments }
            }));
            await WaitUntilAsync(() => File.Exists(pidPath));
            var child = int.Parse(
                (await File.ReadAllTextAsync(pidPath, Encoding.UTF8, TestContext.Current.CancellationToken)).Trim(
                    '\uFEFF',
                    ' ',
                    '\r',
                    '\n'));
            owner.Kill();
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken);
            await WaitForExitAsync(child);
        }
        finally
        {
            if (!owner.HasExited)
                owner.Kill(entireProcessTree: true);
            await owner.WaitForExitAsync(TestContext.Current.CancellationToken);
            await error;
        }
    }

    private static async Task<CallToolResult> CallAsync(McpClient client,
        string tool,
        Dictionary<string, object?> arguments) => await client.CallToolAsync(tool,
        arguments,
        cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<CallToolResult> CallCancellableAsync(McpClient client,
        string tool,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken) =>
        await client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken);

    private static Dictionary<string, object?> Arguments(FixtureFiles files) => new()
    {
        ["xamlFilePath"] = files.SourcePath, ["xamlProjectPath"] = files.ProjectPath
    };

    private static Task<McpClient> CreateClientAsync(Dictionary<string, string?>? environment = null)
    {
        environment ??= new();
        environment["AVDT_PREVIEW_HEADLESS"] = "true";
        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet", Name = "owned file preview", Arguments = [CliPath, "mcp"],
            EnvironmentVariables = environment, ShutdownTimeout = TimeSpan.FromSeconds(5)
        }));
    }

    private static async Task<CallToolResult> AttachToAppAsync(McpClient client, string pipe)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        while (true)
        {
            var applications = Read<ApplicationAttachmentResult>(await CallCancellableAsync(client,
                "attach-to-app",
                new(),
                timeout.Token));
            var target = applications.Applications.SingleOrDefault(application => application.PipeName == pipe);
            if (target is not null)
                return await CallCancellableAsync(client, "attach-to-app", new() { ["id"] = target.Id }, timeout.Token);
            await Task.Delay(40, timeout.Token);
        }
    }

    private static async Task<TreeNodeInfo> SearchAsync(McpClient client, string name)
    {
        var search = Read<TreeSearchResult>(await CallAsync(client,
            "search",
            new Dictionary<string, object?> { ["query"] = name, ["treeKind"] = "Logical" }));
        return Assert.Single(search.Matches, node => node.Name == name);
    }

    private static async Task<TreeNodeInfo> WaitForNodeAsync(McpClient client,
        string name,
        Func<TreeNodeInfo?, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (true)
        {
            var result = Read<TreeSearchResult>(await CallCancellableAsync(client,
                "search",
                new Dictionary<string, object?> { ["query"] = name, ["treeKind"] = "Logical" },
                timeout.Token));
            var node = result.Matches.FirstOrDefault(node => node.Name == name);
            if (predicate(node))
                return node!;
            await Task.Delay(40, timeout.Token);
        }
    }

    private static async Task WaitForExitAsync(int pid) => await WaitUntilAsync(() =>
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    });

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!predicate())
            await Task.Delay(40, timeout.Token);
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(item => item.Text));

    private static T Read<T>(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var root = result.StructuredContent is { } structured
            ? JsonNode.Parse(JsonSerializer.Serialize(structured))
            : JsonNode.Parse(Text(result));
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        return root!.Deserialize<T>(ProtocolJson.Options)!;
    }

    private static string Metadata(string key) => Path.GetFullPath(typeof(PreviewAttachTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == key).Value!);

    private sealed class FixtureFiles : IDisposable
    {
        private readonly OfficialPreviewTests.ProjectFiles _project = new();
        public string Root => _project.Root;
        public string SourcePath { get; }
        public string ProjectPath => _project.Project;
        public string AssemblyPath => _project.Output;
        public string Initial { get; }

        public FixtureFiles(bool window = false, bool referenced = false)
        {
            SourcePath = window ? Path.Combine(Root, "FixtureWindow.axaml") : _project.Source;
            Initial = File.ReadAllText(SourcePath, Encoding.UTF8);
            if (referenced)
                Initial = Initial.Replace("<referenced:ReferencedBadge />",
                    "<referenced:ReferencedBadge /><referenced:ReferencedView />",
                    StringComparison.Ordinal);
            Write(Initial);
        }

        public Task BuildAsync() => _project.BuildAsync();
        public void Write(string text) => File.WriteAllText(SourcePath, text, new UTF8Encoding(false));
        public void Dispose() => _project.Dispose();
    }
}
