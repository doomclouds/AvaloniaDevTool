using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class Phase7McpTests
{
    [AvaloniaFact]
    public async Task Stdio_metrics_configuration_frames_and_clear_and_structured_brush_edit_use_actual_agent_services()
    {
        using var meter = new Meter("Phase7.Mcp." + Guid.NewGuid().ToString("N"));
        var histogram = meter.CreateHistogram<int>("duration", "ms");
        var counter = meter.CreateCounter<int>("count");
        var original = new LinearGradientBrush
        {
            StartPoint = RelativePoint.TopLeft, EndPoint = RelativePoint.Parse("100%, 0%"),
            GradientStops = [new GradientStop(Colors.Red, 0), new GradientStop(Colors.Blue, 1)]
        };
        var edited = new Border { Name = "Phase7Brush", Background = original, Width = 80, Height = 40 };
        var shared = new Border { Background = original, Width = 80, Height = 40 };
        var target = new Window { Content = new StackPanel { Children = { edited, shared } } };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                    { PipeName = "phase7-mcp-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var client = await CreateClientAsync();
            var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            var metricsStart = Assert.Single(tools, tool => tool.Name == "metrics-start");
            foreach (var parameter in new[]
                     {
                         "pollIntervalMs", "notificationIntervalMs", "historyWindowMs", "frameIntervalMs",
                         "aggregateFrames"
                     })
                Assert.Contains(parameter, metricsStart.JsonSchema.ToString());
            Assert.Contains(tools, tool => tool.Name == "metrics-clear");
            Assert.Contains(tools, tool => tool.Name == "set-brush");
            Read<SessionInfo>(await client.CallToolAsync("attach-to-app",
                new Dictionary<string, object?> { ["pipeName"] = agent.PipeName }));
            var started = Read<MetricsStatusDto>(await client.CallToolAsync("metrics-start",
                new Dictionary<string, object?>
                {
                    ["meterNames"] = new[] { meter.Name }, ["capacity"] = 128, ["pollIntervalMs"] = 250,
                    ["notificationIntervalMs"] = 100, ["historyWindowMs"] = 30000, ["frameIntervalMs"] = 10000,
                    ["aggregateFrames"] = true
                }));
            Assert.True(started.IsRunning);
            Assert.Equal(128, started.Capacity);
            Assert.Equal(250, started.PollIntervalMs);
            Assert.Equal(100, started.NotificationIntervalMs);
            Assert.Equal(30000, started.HistoryWindowMs);
            Assert.Equal(10000, started.FrameIntervalMs);
            Assert.True(started.AggregateFrames);
            foreach (var value in new[] { 1, 2, 3, 4, 100 })
                histogram.Record(value);
            counter.Add(9);
            var snapshot =
                Read<MetricsSnapshotDto>(await client.CallToolAsync("metrics", new Dictionary<string, object?>()));
            Assert.Equal(6, snapshot.Samples.Count);
            var histogramInstrument = snapshot.Instruments.Single(item => item.Name == "duration");
            var histogramSeries = snapshot.Series.Single(item => item.InstrumentId == histogramInstrument.Id);
            Assert.Equal("Observation", histogramInstrument.ValueSemantics);
            Assert.Equal(3, histogramSeries.WindowP50);
            Assert.Equal(100, histogramSeries.WindowP90);
            Assert.Equal(100, histogramSeries.WindowP95);
            var frame = Assert.Single(snapshot.Frames!, item => item.SeriesId == histogramSeries.Id);
            Assert.Equal(5, frame.Count);
            Assert.Equal(22, frame.Value);
            Assert.Equal(100, frame.P90);
            Assert.Equal("observation_mean", frame.Aggregation);
            var cleared = Read<MetricsStatusDto>(await client.CallToolAsync("metrics-clear",
                new Dictionary<string, object?>
                    { ["seriesId"] = histogramSeries.Id }));
            Assert.True(cleared.IsRunning);
            Assert.Equal(started.CaptureId, cleared.CaptureId);
            Assert.True(cleared.Revision > snapshot.Status.Revision);
            snapshot = Read<MetricsSnapshotDto>(
                await client.CallToolAsync("metrics", new Dictionary<string, object?>()));
            Assert.Equal(9, Assert.Single(snapshot.Samples).Value);
            Assert.Equal(0, snapshot.Series.Single(item => item.Id == histogramSeries.Id).WindowCount);
            histogram.Record(50);
            snapshot = Read<MetricsSnapshotDto>(
                await client.CallToolAsync("metrics", new Dictionary<string, object?>()));
            Assert.Equal(1, snapshot.Series.Single(item => item.Id == histogramSeries.Id).WindowCount);
            Assert.Contains(snapshot.Samples,
                item => item.SeriesId == histogramSeries.Id && item.Value == 50 && item.Sequence > 6);

            var nodeId = Assert.Single(Read<TreeSearchResult>(await client.CallToolAsync("search",
                new Dictionary<string, object?>
                    { ["query"] = edited.Name, ["kind"] = "Logical" })).Matches).Id;

            async Task<PropertyInfoDto> Background() => Assert.Single(Read<PropertyInfoDto[]>(
                await client.CallToolAsync("props",
                    new Dictionary<string, object?>
                        { ["nodeId"] = nodeId, ["propertyNames"] = new[] { "Background" } })));

            var property = await Background();
            Assert.Equal(BrushKind.Linear, property.Presentation!.Brush!.Kind);
            var draft = new BrushDto(BrushKind.Linear,
                Opacity: .7,
                Stops: [new("Green", 0), new("Purple", 1)],
                SpreadMethod: "Reflect",
                StartPoint: "0%, 50%",
                EndPoint: "100%, 50%");
            Read<OperationResult>(await client.CallToolAsync("set-brush",
                new Dictionary<string, object?>
                    { ["nodeId"] = nodeId, ["propertyName"] = property.Id, ["brush"] = draft }));
            var applied = Assert.IsType<LinearGradientBrush>(edited.Background);
            Assert.NotSame(original, applied);
            Assert.Equal(.7, applied.Opacity);
            Assert.Equal(Colors.Green, applied.GradientStops[0].Color);
            Assert.Equal(Colors.Purple, applied.GradientStops[1].Color);
            Assert.Equal(RelativePoint.Parse(draft.StartPoint), applied.StartPoint);
            Assert.Equal(RelativePoint.Parse(draft.EndPoint), applied.EndPoint);
            var readBack = (await Background()).Presentation!.Brush!;
            Assert.Equal(BrushKind.Linear, readBack.Kind);
            Assert.Equal(.7, readBack.Opacity);
            Assert.Equal("Reflect", readBack.SpreadMethod);
            Assert.Equal(Colors.Green.ToString(), readBack.Stops![0].Color);
            Assert.Equal(Colors.Purple.ToString(), readBack.Stops[1].Color);
            Assert.Same(original, shared.Background);
            Assert.Equal(Colors.Red, original.GradientStops[0].Color);
            Assert.Equal(Colors.Blue, original.GradientStops[1].Color);
            Assert.Equal(RelativePoint.TopLeft, original.StartPoint);
            Assert.Equal(RelativePoint.Parse("100%, 0%"), original.EndPoint);
            Assert.NotEqual(true,
                (await client.CallToolAsync("set-prop",
                    new Dictionary<string, object?>
                        { ["nodeId"] = nodeId, ["propertyName"] = property.Id, ["value"] = null })).IsError);
            Assert.Same(original, edited.Background);
            Assert.Equal(Colors.Red.ToString(), (await Background()).Presentation!.Brush!.Stops![0].Color);
            var allCleared =
                Read<MetricsStatusDto>(await client.CallToolAsync("metrics-clear", new Dictionary<string, object?>()));
            Assert.True(allCleared.IsRunning);
            Assert.Empty(
                Read<MetricsSnapshotDto>(await client.CallToolAsync("metrics", new Dictionary<string, object?>()))
                    .Samples);
            Assert.False(
                Read<MetricsStatusDto>(await client.CallToolAsync("metrics-stop", new Dictionary<string, object?>()))
                    .IsRunning);
        }
        finally
        {
            target.Close();
        }
    }

    private static Task<McpClient> CreateClientAsync()
    {
        var assembly = typeof(Phase7McpTests).Assembly.Location;
        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet", Name = "Phase 7 metrics and brush MCP integration",
            Arguments =
            [
                "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"), "--depsfile",
                Path.ChangeExtension(assembly, ".deps.json"), typeof(Program).Assembly.Location, "mcp", "--profile",
                "extended"
            ],
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        }));
    }

    private static T Read<T>(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(item => item.Text));
        var root = result.StructuredContent is { } structured
            ? JsonNode.Parse(JsonSerializer.Serialize(structured))
            : JsonNode.Parse(text);
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        return root!.Deserialize<T>(ProtocolJson.Options)!;
    }
}
