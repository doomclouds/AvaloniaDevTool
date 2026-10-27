using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvaloniaDevTool.Protocol;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Agent;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class OfficialConnectionTests
{
    private static string CliPath => typeof(OfficialConnectionTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == "PreviewCliPath").Value!;

    [Fact]
    public async Task Current_catalog_lists_all_capabilities_and_validates_arguments_before_binding()
    {
        await using var client = await CreateClient();
        using var raw = await RawToolsList();
        var tools = raw.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(HttpMcpIntegrationTests.ExecutionToolNames.Append("select-machine").Order(StringComparer.Ordinal),
            tools.Select(tool => tool.GetProperty("name").GetString()).Order(StringComparer.Ordinal));

        foreach (var invalid in new[]
                 {
                     ("input", new Dictionary<string, object?> { ["nodeId"] = 1, ["inputEvent"] = 0 }),
                     ("props", new Dictionary<string, object?> { ["nodeId"] = "1" }),
                     ("set-prop", new Dictionary<string, object?> { ["nodeId"] = 1, ["propertyName"] = "Text" }),
                     ("tree", new Dictionary<string, object?> { ["treeKind"] = "NotATree" })
                 })
        {
            var result = await Call(client, invalid.Item1, invalid.Item2);
            Assert.True(result.IsError);
            Assert.Contains("invalid_argument", Text(result));
        }
    }

    [Fact]
    public async Task Discovery_and_id_switching_use_two_real_processes_and_reject_previous_node_handles()
    {
        await using var alpha = await ExternalTarget.Start("Alpha");
        await using var beta = await ExternalTarget.Start("Beta");
        await using var client = await CreateClient();
        ApplicationAttachmentResult listed = null!;
        await Until(async () =>
        {
            listed = Read<ApplicationAttachmentResult>(await Call(client,
                "attach-to-app",
                new Dictionary<string, object?>()));
            return listed.Applications.Any(app => app.PipeName == alpha.Pipe) &&
                   listed.Applications.Any(app => app.PipeName == beta.Pipe);
        });
        Assert.False(listed.Attached);
        var first = listed.Applications.Single(app => app.PipeName == alpha.Pipe);
        var second = listed.Applications.Single(app => app.PipeName == beta.Pipe);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(alpha.Process.Id, first.Id);
        Assert.NotEqual(beta.Process.Id, second.Id);
        var attached = Read<ApplicationAttachmentResult>(await Call(client,
            "attach-to-app",
            new Dictionary<string, object?> { ["id"] = first.Id }));
        Assert.True(attached.Attached);
        Assert.Equal(alpha.Process.Id, attached.Session!.ProcessId);
        var oldNode = Assert.Single(Read<TreeSearchResult>(await Call(client,
            "search",
            new Dictionary<string, object?> { ["query"] = "SharedTarget" })).Matches);
        Assert.Equal("Alpha",
            Read<PropertyInfoDto[]>(await Call(client,
                    "props",
                    new Dictionary<string, object?> { ["nodeId"] = oldNode.Id, ["propertyNames"] = new[] { "Text" } }))
                .Single().Value);
        var switched = Read<ApplicationAttachmentResult>(await Call(client,
            "attach-to-app",
            new Dictionary<string, object?> { ["id"] = second.Id }));
        Assert.Equal(beta.Process.Id, switched.Session!.ProcessId);
        var newNode = Assert.Single(Read<TreeSearchResult>(await Call(client,
            "search",
            new Dictionary<string, object?> { ["query"] = "SharedTarget" })).Matches);
        Assert.NotEqual(oldNode.Id, newNode.Id);
        var stale = await Call(client,
            "set-prop",
            new Dictionary<string, object?>
                { ["nodeId"] = oldNode.Id, ["propertyName"] = "Text", ["value"] = "Wrong target" });
        Assert.True(stale.IsError);
        Assert.Contains("stale_node", Text(stale));
        Assert.Equal("Beta",
            Read<PropertyInfoDto[]>(await Call(client,
                    "props",
                    new Dictionary<string, object?> { ["nodeId"] = newNode.Id, ["propertyNames"] = new[] { "Text" } }))
                .Single().Value);
        Assert.NotEqual(true, (await Call(client, "detach", new Dictionary<string, object?>())).IsError);
        Assert.NotEqual(true, (await Call(client, "detach", new Dictionary<string, object?>())).IsError);
        Assert.False(alpha.Process.HasExited);
        Assert.False(beta.Process.HasExited);
        var detached = await Call(client, "props", new Dictionary<string, object?> { ["nodeId"] = newNode.Id });
        Assert.True(detached.IsError);
        Assert.Contains("not_connected", Text(detached));
    }

    [Fact]
    public async Task Incompatible_wire_version_reports_a_diagnostic_instead_of_an_attached_session()
    {
        var pipe = DiagnosticsProtocol.PipePrefix + "official-version-" + Guid.NewGuid().ToString("N");
        await using var stream = new NamedPipeServerStream(pipe,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var accepted = stream.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await using var server = new RpcPeer(stream,
            (_, _, _) =>
                Task.FromResult<object?>(new SessionInfo("wrong",
                    "Wrong bridge",
                    Environment.ProcessId,
                    "12.1.4",
                    99)));
        await using var client = await CreateClient();
        var apps = Read<ApplicationAttachmentResult>(
            await Call(client, "attach-to-app", new Dictionary<string, object?>()));
        var id = apps.Applications.Single(app => app.PipeName == pipe).Id;
        var pending = Call(client, "attach-to-app", new Dictionary<string, object?> { ["id"] = id });
        await accepted;
        server.Start();
        var result = await pending;
        Assert.True(result.IsError);
        Assert.Contains("unsupported_version", Text(result));
        Assert.Contains("99", Text(result));
    }

    [AvaloniaFact]
    public async Task A_running_window_without_agent_startup_is_not_advertised_and_reports_setup_guidance()
    {
        var pipe = DiagnosticsProtocol.PipePrefix + "unstarted-" + Guid.NewGuid().ToString("N");
        var window = new Window { Content = new TextBlock { Name = "UnstartedTarget", Text = "SDK startup probe" } };
        window.Show();
        try
        {
            await using var client = await CreateClient(pipe);
            var absent = Read<ApplicationAttachmentResult>(await Call(client, "attach-to-app", new()));
            Assert.DoesNotContain(absent.Applications, app => app.PipeName == pipe);
            Assert.Contains("DiagnosticsAgent.Attach", absent.Guidance);
            var failed = await Call(client, "props", new() { ["nodeId"] = 0 });
            Assert.True(failed.IsError);
            Assert.Contains("connection_timeout", Text(failed));
            Assert.Contains("DiagnosticsAgent.Attach", Text(failed));
            await using var agent = DiagnosticsAgent.Attach(Avalonia.Application.Current!,
                new() { PipeName = pipe, TopLevelProvider = () => [window] });
            await Until(async () => Read<ApplicationAttachmentResult>(await Call(client, "attach-to-app", new()))
                .Applications.Any(app => app.PipeName == pipe));
            var matches = Read<TreeSearchResult>(await Call(client, "search", new() { ["query"] = "UnstartedTarget" }));
            Assert.Single(matches.Matches);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public async Task An_independent_application_without_the_bridge_package_reports_the_preparation_requirement()
    {
        var fixture = typeof(OfficialConnectionTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(item => item.Key == "PreviewFixturePath").Value!;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(fixture);
        start.ArgumentList.Add("--without-diagnostics");
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            using var ready = JsonDocument.Parse(line!);
            Assert.True(ready.RootElement.GetProperty("ready").GetBoolean());
            Assert.False(ready.RootElement.GetProperty("agentReferenced").GetBoolean());
            Assert.Equal(process.Id, ready.RootElement.GetProperty("processId").GetInt32());
            var pipe = DiagnosticsProtocol.PipePrefix + process.Id;
            await using var client = await CreateClient(pipe);
            var absent = Read<ApplicationAttachmentResult>(await Call(client, "attach-to-app", new()));
            Assert.DoesNotContain(absent.Applications, app => app.PipeName == pipe);
            Assert.Contains("AvaloniaDevTool.Agent", absent.Guidance);
            var result = await Call(client, "props", new() { ["nodeId"] = 0 });
            Assert.True(result.IsError);
            Assert.Contains("connection_timeout", Text(result));
            Assert.Contains("AvaloniaDevTool.Agent", Text(result));
            Assert.False(process.HasExited);
        }
        finally
        {
            process.StandardInput.Close();
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            await errors;
        }

        Assert.Equal(0, process.ExitCode);
    }

    private static Task<CallToolResult> Call(McpClient client, string name, Dictionary<string, object?> args) =>
        client.CallToolAsync(name, args, cancellationToken: TestContext.Current.CancellationToken).AsTask();

    private static async Task<JsonDocument> RawToolsList()
    {
        var token = TestContext.Current.CancellationToken;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { CliPath, "mcp" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(token);

        async Task<JsonDocument> Response(int id)
        {
            while (await process.StandardOutput.ReadLineAsync(token).AsTask()
                       .WaitAsync(TimeSpan.FromSeconds(10), token) is { } line)
            {
                var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out var value) && value.TryGetInt32(out var returned) &&
                    returned == id)
                    return document;
                document.Dispose();
            }

            throw new InvalidOperationException("The MCP server exited before a response: " + await errors);
        }

        try
        {
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"wire-contract-test\",\"version\":\"1\"}}}");
            await process.StandardInput.FlushAsync(token);
            using var initialized = await Response(1);
            Assert.False(initialized.RootElement.TryGetProperty("error", out _), initialized.RootElement.GetRawText());
            Assert.Equal(AvaloniaDevTool.Cli.Catalog.ExecutionProtocol.Version,
                initialized.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}");
            await process.StandardInput.FlushAsync(token);
            return await Response(2);
        }
        finally
        {
            process.StandardInput.Close();
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            await errors;
        }
    }

    private static Task<McpClient> CreateClient(string? pipe = null) => McpClient.CreateAsync(new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = "dotnet",
                Arguments = pipe is null
                    ? [CliPath, "mcp"]
                    : [CliPath, "mcp", "--pipe", pipe],
                ShutdownTimeout = TimeSpan.FromSeconds(5), Name = "Official connection tests"
            }),
        cancellationToken: TestContext.Current.CancellationToken);

    private static string Text(CallToolResult value) =>
        string.Join("\n", value.Content.OfType<TextContentBlock>().Select(item => item.Text));

    private static T Read<T>(CallToolResult value)
    {
        Assert.NotEqual(true, value.IsError);
        var root = value.StructuredContent is { } structured
            ? JsonNode.Parse(JsonSerializer.Serialize(structured))
            : JsonNode.Parse(Text(value));
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var result))
            root = result;
        return root!.Deserialize<T>(ProtocolJson.Options)!;
    }

    private static async Task Until(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await predicate())
            await Task.Delay(40, timeout.Token);
    }

    private sealed class ExternalTarget : IAsyncDisposable
    {
        public Process Process { get; }
        public string Pipe { get; }
        private readonly string _directory;
        private readonly NamedPipeServerStream _control;
        private readonly Task<string> _output, _errors;

        private ExternalTarget(Process process, string pipe, string directory, NamedPipeServerStream control)
        {
            Process = process;
            Pipe = pipe;
            _directory = directory;
            _control = control;
            _output = process.StandardOutput.ReadToEndAsync();
            _errors = process.StandardError.ReadToEndAsync();
        }

        public static async Task<ExternalTarget> Start(string name)
        {
            var directory = Path.Combine(Path.GetTempPath(), "avdt-official-two-apps", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, name + ".axaml");
            File.WriteAllText(source,
                $"<UserControl xmlns=\"https://github.com/avaloniaui\" Width=\"240\" Height=\"120\"><TextBlock Name=\"SharedTarget\" Text=\"{name}\" /></UserControl>",
                Encoding.UTF8);
            var pipe = DiagnosticsProtocol.PipePrefix + "official-app-" + Guid.NewGuid().ToString("N");
            var controlName = "official-owner-" + Guid.NewGuid().ToString("N");
            var control = new NamedPipeServerStream(controlName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            var connected = control.WaitForConnectionAsync();
            var host = Path.Combine(Path.GetDirectoryName(CliPath)!, "previewer", "AvaloniaDevTool.Previewer.dll");
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var arg in new[]
                     {
                         host, "--xaml", source, "--pipe", pipe, "--headless", "--no-watch", "--control-pipe",
                         controlName
                     })
                start.ArgumentList.Add(arg);
            var target = new ExternalTarget(Process.Start(start)!, pipe, directory, control);
            try
            {
                await connected.WaitAsync(TimeSpan.FromSeconds(15));
                using var reader = new StreamReader(control, Encoding.UTF8, leaveOpen: true);
                var ready = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(ready is not null && ready.Contains("true", StringComparison.Ordinal), ready);
                return target;
            }
            catch
            {
                await target.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _control.DisposeAsync();
            try
            {
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                if (!Process.HasExited)
                    Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync();
            }

            await Task.WhenAll(_output, _errors);
            Process.Dispose();
            if (!Path.GetFullPath(_directory)
                    .StartsWith(
                        Path.Combine(Path.GetTempPath(), "avdt-official-two-apps") + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Temporary cleanup escaped its root.");
            Directory.Delete(_directory, recursive: true);
        }
    }
}
