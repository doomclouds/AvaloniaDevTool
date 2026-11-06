using System.Reflection;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Cli.Gateway;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class GatewayMcpIntegrationTests
{
    private static string CliPath => Environment.GetEnvironmentVariable("AVDT_TEST_CLI_EXECUTABLE") ??
                                     typeof(GatewayMcpIntegrationTests).Assembly
                                         .GetCustomAttributes<AssemblyMetadataAttribute>()
                                         .Single(item => item.Key == "PreviewCliPath").Value!;

    [AvaloniaFact]
    public async Task One_stdio_catalog_switches_local_http_and_back_with_scoped_nodes_and_intact_images()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        var localPixels = new Border { Name = "GatewayLocalPixels", Width = 60, Height = 30, Background = Brushes.Red };
        var remotePixels = new Border
            { Name = "GatewayRemotePixels", Width = 80, Height = 40, Background = Brushes.Blue };
        var localWindow = new Window { Width = 160, Height = 100, Content = localPixels };
        var remoteWindow = new Window { Width = 160, Height = 100, Content = remotePixels };
        localWindow.Show();
        remoteWindow.Show();
        try
        {
            using (var frame = localWindow.CaptureRenderedFrame())
                Assert.NotNull(frame);
            using (var frame = remoteWindow.CaptureRenderedFrame())
                Assert.NotNull(frame);
            await using var local = Attach(localWindow);
            await using var remote = Attach(remoteWindow);
            await using var host = await HttpMcpIntegrationTests.HttpHost.StartAsync(remote.PipeName, token);
            await using var gateway = await ConnectAsync(local.PipeName, token, host.Endpoint.Port);
            var originalCatalog = await gateway.ListToolsAsync(cancellationToken: token);
            Assert.Equal(HttpMcpIntegrationTests.ExecutionToolNames.Length + 1, originalCatalog.Count);
            Assert.Single(originalCatalog, tool => tool.Name == "select-machine");
            var localId = await FindAsync(gateway, localPixels.Name!, token);
            VerifyImage(await Call(gateway, "ui-screenshot", token, ("nodeId", localId)),
                localId,
                60,
                30,
                SKColors.Red);

            var missingPreview =
                Path.Combine(Path.GetTempPath(), "avdt-missing-preview-" + Guid.NewGuid().ToString("N"));
            var failedPreview = await Call(gateway,
                "attach-to-file",
                token,
                ("xamlFilePath", missingPreview + ".axaml"),
                ("xamlProjectPath", missingPreview + ".csproj"));
            Assert.True(failedPreview.IsError == true);
            Success(await Call(gateway, "props", token, ("nodeId", localId)));
            Assert.Equal(localId, await FindAsync(gateway, localPixels.Name!, token));

            var selected = await Call(gateway, "select-machine", token, ("ip", "127.0.0.1"));
            Success(selected);
            Assert.Equal("remote", Read<MachineSelection>(selected).Mode);
            Assert.Equal("127.0.0.1", Read<MachineSelection>(selected).Machine);
            Assert.False(local.IsConnected);
            await AttachAsync(gateway, remote.PipeName, token);
            var stale = await Call(gateway, "ui-click", token, ("nodeId", localId));
            Error(stale, "stale_node");
            var remoteId = await FindAsync(gateway, remotePixels.Name!, token);
            Assert.NotEqual(localId, remoteId);
            VerifyImage(await Call(gateway, "ui-screenshot", token, ("nodeId", remoteId)),
                remoteId,
                80,
                40,
                SKColors.Blue);
            var inspected = Read<RuntimeObjectSnapshot>(await Call(gateway, "object", token, ("objectId", remoteId)));
            Assert.Equal(remoteId, inspected.ObjectId);
            var layout = Read<LayoutSnapshot>(await Call(gateway, "layout", token, ("nodeId", remoteId)));
            Assert.Equal(remoteId, layout.RootObjectId);
            using var meter = new Meter("Gateway.Remote.Diagnostics." + Guid.NewGuid().ToString("N"));
            var counter = meter.CreateCounter<int>("operations");
            Success(await Call(gateway, "metrics-start", token, ("meterNames", new[] { meter.Name })));
            counter.Add(7);
            var metrics = Read<MetricsSnapshotDto>(await Call(gateway, "metrics", token));
            var series = Assert.Single(metrics.Series);
            Assert.Equal(series.Id, Assert.Single(metrics.Samples).SeriesId);
            Success(await Call(gateway, "metrics-clear", token, ("seriesId", series.Id)));
            Assert.Empty(Read<MetricsSnapshotDto>(await Call(gateway, "metrics", token)).Samples);
            Success(await Call(gateway, "metrics-stop", token));

            var failed = await Call(gateway, "select-machine", token, ("ip", "127.0.0.2"));
            Error(failed, "machine_connection_failed");
            Success(await Call(gateway, "props", token, ("nodeId", remoteId)));
            Assert.Equal(originalCatalog.Select(tool => (tool.Name, tool.ProtocolTool.InputSchema.GetRawText())),
                (await gateway.ListToolsAsync(cancellationToken: token))
                .Select(tool => (tool.Name, tool.ProtocolTool.InputSchema.GetRawText())));

            Assert.Equal("local", Read<MachineSelection>(await Call(gateway, "select-machine", token)).Mode);
            Assert.False(remote.IsConnected);
            await AttachAsync(gateway, local.PipeName, token);
            Error(await Call(gateway, "ui-screenshot", token, ("nodeId", remoteId)), "stale_node");
            var freshLocal = await FindAsync(gateway, localPixels.Name!, token);
            Assert.NotEqual(localId, freshLocal);
            VerifyImage(await Call(gateway, "ui-screenshot", token, ("nodeId", freshLocal)),
                freshLocal,
                60,
                30,
                SKColors.Red);
        }
        finally
        {
            localWindow.Close();
            remoteWindow.Close();
        }
    }

    [AvaloniaFact]
    public async Task Machine_switch_refuses_an_active_wait_and_remote_loss_never_falls_back_or_replays_input()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        var localClicks = 0;
        var remoteClicks = 0;
        var localButton = new Button { Name = "GatewayNoFallback", Content = "Local", Width = 120, Height = 40 };
        var remoteButton = new Button { Name = "GatewayNoReplay", Content = "Remote", Width = 120, Height = 40 };
        localButton.Click += (_, _) => localClicks++;
        remoteButton.Click += (_, _) => remoteClicks++;
        var localWindow = new Window { Width = 200, Height = 120, Content = localButton };
        var remoteWindow = new Window { Width = 200, Height = 120, Content = remoteButton };
        localWindow.Show();
        remoteWindow.Show();
        try
        {
            await using var local = Attach(localWindow);
            await using var remote = Attach(remoteWindow);
            await using var host = await HttpMcpIntegrationTests.HttpHost.StartAsync(remote.PipeName, token);
            await using var gateway = await ConnectAsync(local.PipeName, token, host.Endpoint.Port);
            _ = await FindAsync(gateway, localButton.Name!, token);
            var waiting = Call(gateway, "ui-wait", token, ("query", "NeverAppearsInGatewayTest"), ("timeoutMs", 2000));
            CallToolResult? busy = null;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(50, token);
                busy = await Call(gateway, "select-machine", token);
                if (busy.IsError == true)
                    break;
            }

            Assert.NotNull(busy);
            Error(busy, "machine_busy");
            Error(await waiting, "ui_wait_timeout");
            Success(await Call(gateway, "select-machine", token, ("ip", "127.0.0.1")));
            await AttachAsync(gateway, remote.PipeName, token);
            var remoteId = await FindAsync(gateway, remoteButton.Name!, token);
            host.Process.Kill(entireProcessTree: true);
            await host.Process.WaitForExitAsync(token);
            Error(await Call(gateway, "ui-click", token, ("nodeId", remoteId)), "execution_unknown");
            Error(await Call(gateway, "tree", token), "machine_disconnected");
            Assert.Equal(0, localClicks);
            Assert.Equal(0, remoteClicks);
            Assert.False(local.IsConnected);
        }
        finally
        {
            localWindow.Close();
            remoteWindow.Close();
        }
    }

    private static DiagnosticsAgent Attach(Window window) => DiagnosticsAgent.Attach(Application.Current!,
        new DiagnosticsAgentOptions
        {
            ApplicationName = "Gateway regression",
            PipeName = "avalonia-devtool-gateway-" + Guid.NewGuid().ToString("N"),
            TopLevelProvider = () => [window]
        });

    private static Task<McpClient> ConnectAsync(string pipe, CancellationToken token, int executorPort) =>
        McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = CliPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? CliPath : "dotnet",
                Arguments = CliPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? ["mcp", "--pipe", pipe]
                    : [CliPath, "mcp", "--pipe", pipe],
                Name = "Gateway regression",
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["AVDT_EXECUTOR_PORT"] = executorPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            }),
            cancellationToken: token);

    private static async Task AttachAsync(McpClient client, string pipe, CancellationToken token)
    {
        var discovered = Read<ApplicationAttachmentResult>(await Call(client, "attach-to-app", token));
        var target = Assert.Single(discovered.Applications, application => application.PipeName == pipe);
        Success(await Call(client, "attach-to-app", token, ("id", target.Id)));
    }

    private static async Task<long> FindAsync(McpClient client, string name, CancellationToken token) =>
        Assert.Single(Read<TreeSearchResult>(await Call(client, "search", token, ("query", name))).Matches).Id;

    private static async Task<CallToolResult> Call(McpClient client,
        string name,
        CancellationToken token,
        params (string Name, object? Value)[] args) => await client.CallToolAsync(name,
        args.ToDictionary(argument => argument.Name, argument => argument.Value),
        cancellationToken: token);

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static void Success(CallToolResult result) => Assert.False(result.IsError == true, Text(result));

    private static void Error(CallToolResult result, string code)
    {
        Assert.True(result.IsError == true, Text(result));
        Assert.Contains(code, Text(result));
    }

    private static T Read<T>(CallToolResult result)
    {
        Success(result);
        return JsonSerializer.Deserialize<T>(Assert.Single(result.Content.OfType<TextContentBlock>()).Text,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static void VerifyImage(CallToolResult result, long nodeId, int width, int height, SKColor color)
    {
        Success(result);
        var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        using var bitmap = SKBitmap.Decode(image.DecodedData.ToArray());
        Assert.NotNull(bitmap);
        Assert.Equal(width, bitmap.Width);
        Assert.Equal(height, bitmap.Height);
        Assert.Equal(color, bitmap.GetPixel(width / 2, height / 2));
        using var metadata = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        Assert.Equal(nodeId, metadata.RootElement.GetProperty("NodeId").GetInt64());
        Assert.Equal("control-local-dip", metadata.RootElement.GetProperty("CoordinateSpace").GetString());
    }
}
