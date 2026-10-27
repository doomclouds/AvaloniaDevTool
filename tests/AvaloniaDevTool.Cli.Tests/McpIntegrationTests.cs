using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class McpIntegrationTests
{
    [Fact]
    public async Task Stdio_server_initializes_advertises_tools_and_explains_missing_attachment()
    {
        await using var client = await CreateClientAsync();
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        foreach (var expected in new[]
                 {
                     "tree", "props", "set-prop", "styles", "ancestors", "search", "screenshot", "pseudo-class",
                     "resources", "assets", "activity", "profiler-start", "profiler-stop", "profiler-clear", "profiler"
                 })
            Assert.Contains(tools, tool => tool.Name == expected);
        var failure = await client.CallToolAsync("props",
            new Dictionary<string, object?> { ["nodeId"] = 1 },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(failure.IsError);
        Assert.Contains("not_connected", Text(failure));
    }

    [AvaloniaFact]
    public async Task Stdio_tools_inspect_edit_capture_and_detach_a_real_agent()
    {
        var model = new MessageModel { Text = "Original binding" };
        var text = new TextBlock { Name = "McpTarget", Width = 180, Height = 40, DataContext = model };
        text.Bind(TextBlock.TextProperty, new Binding(nameof(MessageModel.Text)) { Source = model });
        var window = new Window { Width = 400, Height = 200, Content = text };
        window.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                {
                    PipeName = "avalonia-devtool-mcp-test-" + Guid.NewGuid().ToString("N"),
                    ApplicationName = "MCP integration target",
                    TopLevelProvider = () => [window]
                });
            await using var client = await CreateClientAsync();
            var attach = await McpTestTarget.AttachAsync(client, agent.PipeName);
            Assert.NotEqual(true, attach.IsError);
            var merged = Read<ControlTreeSnapshot>(await client.CallToolAsync("tree",
                new Dictionary<string, object?>
                    { ["treeKind"] = "Merged", ["depth"] = -1 }));
            var logicalContent = Read<ControlTreeSnapshot>(await client.CallToolAsync("tree",
                new Dictionary<string, object?>
                    { ["treeKind"] = "Logical", ["depth"] = -1 }));
            Assert.NotEmpty(merged.Entries);
            Assert.NotEmpty(logicalContent.Entries);
            Assert.True(merged.Visited > logicalContent.Visited);
            Assert.Contains(merged.Entries, item => item.Node.Role == TreeNodeRole.TemplateRoot);
            Assert.DoesNotContain(logicalContent.Entries, item => item.Node.Role == TreeNodeRole.TemplateRoot);
            Assert.Contains(logicalContent.Entries, item => item.Node.Name == "McpTarget");
            var search = Read<TreeSearchResult>(await client.CallToolAsync("search",
                new Dictionary<string, object?>
                    { ["query"] = "McpTarget", }));
            var node = Assert.Single(search.Matches);
            var properties = Read<PropertyInfoDto[]>(await client.CallToolAsync("props",
                new Dictionary<string, object?> { ["nodeId"] = node.Id }));
            var property = properties.Single(item =>
                item.Name == "Text" && item.DeclaringType == typeof(TextBlock).FullName);
            var breakpoint = Read<BreakpointInfo>(await client.CallToolAsync("breakpoint-create",
                new Dictionary<string, object?>
                {
                    ["kind"] = "Property", ["memberId"] = property.Id, ["objectId"] = node.Id, ["logMessage"] = false
                }));
            var edit = await client.CallToolAsync("set-prop",
                new Dictionary<string, object?>
                    { ["nodeId"] = node.Id, ["propertyName"] = property.Id, ["value"] = "MCP temporary edit" });
            Assert.NotEqual(true, edit.IsError);
            Assert.Equal("MCP temporary edit", text.Text);
            var runtime = Read<RuntimeObjectSnapshot>(await client.CallToolAsync("object",
                new Dictionary<string, object?> { ["objectId"] = node.Id }));
            var dataContext = Assert.Single(runtime.Members, member => member.Name == "DataContext");
            Assert.NotNull(dataContext.Value.ValueObjectId);
            var source = Read<RuntimeObjectSnapshot>(await client.CallToolAsync("object",
                new Dictionary<string, object?> { ["objectId"] = dataContext.Value.ValueObjectId }));
            Assert.Contains(source.Members,
                member => member.Name == "Text" && member.Value.Preview == "Original binding");
            var hits = Read<BreakpointHistory>(await client.CallToolAsync("breakpoints",
                new Dictionary<string, object?> { ["history"] = true }));
            Assert.Contains(hits.Hits, hit => hit.BreakpointId == breakpoint.Id && hit.ConditionMet);
            model.Text = "Latest binding value";
            Assert.Equal("MCP temporary edit", text.Text);
            var style = Read<StyleSnapshot>(await client.CallToolAsync("styles",
                new Dictionary<string, object?> { ["nodeId"] = node.Id }));
            Assert.Contains("Avalonia 12", style.Adapter);
            using var meter = new Meter("McpIntegration." + Guid.NewGuid().ToString("N"));
            var counter = meter.CreateCounter<int>("jobs");
            var metricState = Read<MetricsStatusDto>(await client.CallToolAsync("metrics-start",
                new Dictionary<string, object?>
                    { ["meterNames"] = new[] { meter.Name } }));
            Assert.True(metricState.IsRunning);
            counter.Add(3);
            var metrics =
                Read<MetricsSnapshotDto>(await client.CallToolAsync("metrics", new Dictionary<string, object?>()));
            Assert.Contains(metrics.Instruments,
                instrument => instrument.Name == "jobs" && instrument.ValueSemantics == "Delta");
            Assert.Contains(metrics.Samples, sample => sample.RawValue == "3");
            Assert.False(
                Read<MetricsStatusDto>(await client.CallToolAsync("metrics-stop", new Dictionary<string, object?>()))
                    .IsRunning);
            var screenshot =
                await client.CallToolAsync("screenshot", new Dictionary<string, object?> { ["nodeId"] = node.Id });
            Assert.NotEqual(true, screenshot.IsError);
            var image = Assert.Single(screenshot.Content.OfType<ImageContentBlock>());
            Assert.Equal("image/png", image.MimeType);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, image.DecodedData.ToArray()[..8]);
            var detach = await client.CallToolAsync("detach", new Dictionary<string, object?>());
            Assert.NotEqual(true, detach.IsError);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (agent.IsConnected)
                await Task.Delay(15, timeout.Token);
            Assert.Equal("Latest binding value", text.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Stdio_profiler_captures_real_framework_resource_lookups_and_cleans_up()
    {
        var control = new TextBlock { Text = "Profiler target" };
        var window = new Window { Content = control };
        window.Resources["McpProfiler.Found"] = "Found value";
        window.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                {
                    PipeName = "avalonia-devtool-profiler-mcp-" + Guid.NewGuid().ToString("N"),
                    TopLevelProvider = () => [window]
                });
            await using var client = await CreateClientAsync();
            Assert.NotEqual(true,
                (await McpTestTarget.AttachAsync(client, agent.PipeName)).IsError);
            var state = Read<ProfilerStatusDto>(await client.CallToolAsync("profiler-start",
                new Dictionary<string, object?>
                    { ["categories"] = new[] { "ResourceLookup" }, ["filter"] = "McpProfiler." }));
            Assert.True(state.IsRunning);
            Assert.True(control.TryFindResource("McpProfiler.Found", out _));
            Assert.False(control.TryFindResource("McpProfiler.Missing", out _));
            var snapshot =
                Read<ProfilerSnapshotDto>(await client.CallToolAsync("profiler", new Dictionary<string, object?>()));
            Assert.Equal("Avalonia.Diagnostic.Source", snapshot.Status.SourceName);
            Assert.Equal("microseconds", snapshot.Status.DurationUnit);
            Assert.Contains(snapshot.Samples, sample => sample.Key == "McpProfiler.Found" && sample.IsMatch == true);
            Assert.Contains(snapshot.Samples, sample => sample.Key == "McpProfiler.Missing" && sample.IsMatch is null);
            Assert.All(snapshot.Samples, sample => Assert.True(sample.DurationMicroseconds >= 0));
            Assert.False(
                Read<ProfilerStatusDto>(await client.CallToolAsync("profiler-stop", new Dictionary<string, object?>()))
                    .IsRunning);
            Assert.False(control.TryFindResource("McpProfiler.AfterStop", out _));
            var stopped =
                Read<ProfilerSnapshotDto>(await client.CallToolAsync("profiler", new Dictionary<string, object?>()));
            Assert.DoesNotContain(stopped.Samples, sample => sample.Key == "McpProfiler.AfterStop");
            var clear = Read<ProfilerStatusDto>(await client.CallToolAsync("profiler-clear",
                new Dictionary<string, object?>()));
            Assert.Equal(0, clear.BufferedSampleCount);
            Assert.Empty(
                Read<ProfilerSnapshotDto>(await client.CallToolAsync("profiler", new Dictionary<string, object?>()))
                    .Samples);
            var invalid = await client.CallToolAsync("profiler-start",
                new Dictionary<string, object?> { ["categories"] = new[] { "UnknownCategory" } });
            Assert.True(invalid.IsError);
            Assert.Contains("invalid_request", Text(invalid));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Stdio_extended_resources_composition_and_structured_logs_preserve_real_evidence()
    {
        var childResources = new ResourceDictionary { ["ExtendedDuplicate"] = "nested" };
        var control = new Border
        {
            Name = "ExtendedScope", Width = 100, Height = 80, Background = Brushes.Blue,
            Child = new Border
            {
                Width = 20, Height = 20, Background = Brushes.Red,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
            }
        };
        control.Resources["ExtendedDuplicate"] = "local";
        control.Resources.MergedDictionaries.Add(childResources);
        var window = new Window { Width = 160, Height = 140, Content = control };
        window.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                {
                    PipeName = DiagnosticsProtocol.PipePrefix + "extended-mcp-" + Guid.NewGuid().ToString("N"),
                    TopLevelProvider = () => [window]
                });
            await using var client = await CreateClientAsync();
            Assert.NotEqual(true,
                (await McpTestTarget.AttachAsync(client, agent.PipeName)).IsError);
            var search = Read<TreeSearchResult>(await client.CallToolAsync("search",
                new Dictionary<string, object?> { ["query"] = "ExtendedScope", }));
            var id = Assert.Single(search.Matches).Id;
            var tree = Read<ResourceTreeSnapshot>(await client.CallToolAsync("resource-tree",
                new Dictionary<string, object?> { ["nodeId"] = id }));
            var provider = Assert.Single(tree.Roots,
                node => node.Name.Contains("ExtendedScope", StringComparison.Ordinal));
            var page = Read<ResourceEntriesSnapshot>(await client.CallToolAsync("resource-query",
                new Dictionary<string, object?>
                {
                    ["dictionaryId"] = provider.Id, ["includeNested"] = true, ["keyFilter"] = "ExtendedDuplicate",
                    ["limit"] = 1, ["sort"] = "Source"
                }));
            Assert.Equal(2, page.TotalCount);
            Assert.True(page.HasMore);
            var row = Assert.Single(page.Entries);
            var read = Read<ResourceQueryEntryInfo>(await client.CallToolAsync("resource-entry",
                new Dictionary<string, object?>
                    { ["dictionaryId"] = row.OwnerDictionaryId, ["keyObjectId"] = row.KeyObjectId }));
            Assert.Equal(row.Value, read.Value);
            Assert.Equal(row.ProviderPath, read.ProviderPath);

            var capture = client.CallToolAsync("composition", new Dictionary<string, object?> { ["nodeId"] = id });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!capture.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                await Task.Delay(10, timeout.Token);
            }

            var composition = Read<CompositionSnapshot>(await capture);
            Assert.Equal("CompositorOwnDrawing", composition.CaptureKind);
            Assert.Contains(composition.Layers, layer => layer.ObjectId == id && layer.ImageStatus == "Captured");
            Assert.True(composition.Layers.Count(layer => layer.PngBase64 is not null) >= 2);
            foreach (var layer in composition.Layers.Where(layer => layer.PngBase64 is not null))
            {
                var bytes = Convert.FromBase64String(layer.PngBase64!);
                Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
            }

            Assert.NotEqual(true,
                (await client.CallToolAsync("activity-start",
                    new Dictionary<string, object?>
                    {
                        ["logs"] = true, ["events"] = false, ["categoryFilter"] = "Extended.Log",
                        ["parameterName"] = "Count", ["parameterValue"] = "7"
                    })).IsError);
            Logger.TryGet(LogEventLevel.Warning, "Extended.Ignored")?.Log(control, "Extended item {Count}", 7);
            Logger.TryGet(LogEventLevel.Warning, "Extended.Log")?.Log(control, "Extended item {Count}", 8);
            Logger.TryGet(LogEventLevel.Warning, "Extended.Log")?.Log(control, "Extended item {Count}", 7);
            var entries =
                Read<ActivityEntry[]>(await client.CallToolAsync("activity", new Dictionary<string, object?>()));
            var log = Assert.Single(entries, entry => entry.Category == "Extended.Log");
            Assert.DoesNotContain(entries, entry => entry.Category == "Extended.Ignored");
            Assert.Equal("Extended item {Count}", log.LogDetails!.MessageTemplate);
            var parameter = Assert.Single(log.LogDetails.Parameters);
            Assert.Equal(LogValueState.Known, parameter.State);
            Assert.Equal("7", parameter.Value);
            Assert.Equal(id, log.ObjectId);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Stdio_overlay_selector_renderer_and_correlated_events_use_actual_agent_services()
    {
        var button = new Button { Name = "McpRouteButton", Content = "Route", Width = 120, Height = 40 };
        button.Click += (_, args) => args.Handled = true;
        var border = new Border
        {
            Name = "McpOverlayTarget", Margin = new Thickness(9), Padding = new Thickness(6),
            Background = Avalonia.Media.Brushes.Red, Child = button
        };
        var window = new Window { Width = 300, Height = 220, Content = border };
        window.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                {
                    PipeName = DiagnosticsProtocol.PipePrefix + "overlay-route-mcp-" + Guid.NewGuid().ToString("N"),
                    TopLevelProvider = () => [window]
                });
            await using var client = await CreateClientAsync();
            Assert.NotEqual(true,
                (await McpTestTarget.AttachAsync(client, agent.PipeName)).IsError);
            var found = Read<TreeSearchResult>(await client.CallToolAsync("search",
                new Dictionary<string, object?> { ["query"] = "McpOverlayTarget", }));
            var id = Assert.Single(found.Matches).Id;
            var selector = Read<ElementSelectorResult>(await client.CallToolAsync("element-selector",
                new Dictionary<string, object?> { ["nodeId"] = id }));
            Assert.True(selector.Supported);
            Assert.True(selector.IsUnique);
            Assert.Equal(1, selector.MatchCount);
            var metadata = Read<ElementMetadata>(await client.CallToolAsync("element-metadata",
                new Dictionary<string, object?> { ["nodeId"] = id }));
            Assert.Equal("McpOverlayTarget", metadata.Name);
            Assert.Equal(id, metadata.ObjectId);
            var overlay = Read<OverlaySnapshot>(await client.CallToolAsync("overlay-set",
                new Dictionary<string, object?>
                {
                    ["nodeId"] = id, ["margin"] = true, ["padding"] = true, ["info"] = true, ["rulers"] = true,
                    ["guidelines"] = true
                }));
            Assert.True(overlay.IsActive);
            Assert.Equal(9, overlay.Margin.Left);
            Assert.Equal(6, overlay.Padding!.Left);
            Assert.NotEmpty(overlay.Regions);
            Assert.NotEmpty(overlay.Rulers);
            Assert.NotEmpty(overlay.Guidelines);
            Assert.Equal("RootDips", overlay.CoordinateSpace);
            Assert.NotEqual(true,
                (await client.CallToolAsync("overlay-clear", new Dictionary<string, object?>())).IsError);
            Assert.False(Read<OverlaySnapshot>(await client.CallToolAsync("overlay", new Dictionary<string, object?>()))
                .IsActive);
            var renderer = Read<RendererDiagnosticsStatusDto>(await client.CallToolAsync("renderer-diagnostics-set",
                new Dictionary<string, object?> { ["flags"] = "DirtyRects" }));
            Assert.Contains(renderer.Windows, item => item.CurrentFlags.HasFlag(RendererOverlayFlags.DirtyRects));
            Assert.False(Read<RendererDiagnosticsStatusDto>(await client.CallToolAsync("renderer-diagnostics-stop",
                new Dictionary<string, object?>())).IsActive);
            var catalog =
                Read<EventCatalog>(await client.CallToolAsync("events-catalog", new Dictionary<string, object?>()));
            var click = Assert.Single(catalog.Events,
                item => item.Name == "Click" && item.OwnerType == typeof(Button).FullName);
            var buttons = Read<TreeSearchResult>(await client.CallToolAsync("search",
                new Dictionary<string, object?> { ["query"] = "McpRouteButton", }));
            var buttonId = Assert.Single(buttons.Matches).Id;
            Assert.True(Read<ActivityStatus>(await client.CallToolAsync("events-start",
                    new Dictionary<string, object?> { ["eventIds"] = new[] { click.Id }, ["sourceNodeId"] = buttonId }))
                .IsRunning);
            Assert.True(Read<InputResult>(await client.CallToolAsync("ui-input",
                new Dictionary<string, object?> { ["nodeId"] = buttonId, ["kind"] = "Click" })).Delivered);
            var routes =
                Read<EventRouteInfo[]>(await client.CallToolAsync("events-routes", new Dictionary<string, object?>()));
            var route = Assert.Single(routes, item => item.EventId == click.Id);
            Assert.True(route.IsFinished);
            Assert.True(route.FinalHandled);
            Assert.Equal(buttonId, route.SourceObjectId);
            Assert.NotEmpty(route.Nodes);
            Assert.NotEmpty(route.FinishedStrategies);
            Assert.Contains(route.Nodes, node => node.HandledChangedSincePreviousObservation);
            var status =
                Read<EventRouteStatus>(await client.CallToolAsync("events-status", new Dictionary<string, object?>()));
            Assert.True(status.BufferedBytes <= status.ByteBudget);
            var released = Read<TreeObservationResult>(await client.CallToolAsync("tree-release",
                new Dictionary<string, object?> { ["nodeIds"] = new[] { id }, ["kind"] = "Logical" }));
            Assert.True(released.ObservedBranches >= 0);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Stdio_input_tools_route_text_keys_and_real_button_clicks()
    {
        var input = new TextBox { Name = "McpInput", Width = 200, Height = 40 };
        var button = new Button { Name = "McpButton", Width = 180, Height = 45, Content = "Click" };
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        var window = new Window
            { Width = 400, Height = 250, Content = new StackPanel { Children = { input, button } } };
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                {
                    PipeName = "avalonia-devtool-input-mcp-" + Guid.NewGuid().ToString("N"),
                    TopLevelProvider = () => [window]
                });
            await using var client = await CreateClientAsync();
            Assert.NotEqual(true,
                (await McpTestTarget.AttachAsync(client, agent.PipeName)).IsError);
            var found = Read<TreeSearchResult>(await client.CallToolAsync("search",
                new Dictionary<string, object?> { ["query"] = "McpInput", }));
            var inputId = Assert.Single(found.Matches).Id;
            var text = Read<InputResult>(await client.CallToolAsync("ui-input",
                new Dictionary<string, object?> { ["nodeId"] = inputId, ["kind"] = "Text", ["text"] = "abc" }));
            Assert.True(text.Delivered);
            Assert.Equal("abc", input.Text);
            Assert.True(Read<InputResult>(await client.CallToolAsync("ui-input",
                    new Dictionary<string, object?> { ["nodeId"] = inputId, ["kind"] = "KeyDown", ["key"] = "Back" }))
                .Delivered);
            Assert.Equal("ab", input.Text);
            var buttons = Read<TreeSearchResult>(await client.CallToolAsync("search",
                new Dictionary<string, object?> { ["query"] = "McpButton", }));
            var buttonId = Assert.Single(buttons.Matches).Id;
            var capture = Read<ActivityStatus>(await client.CallToolAsync("activity-start",
                new Dictionary<string, object?>
                    { ["logs"] = false, ["events"] = true, ["sourceNodeId"] = buttonId }));
            Assert.True(capture.IsRunning);
            Assert.True(Read<InputResult>(await client.CallToolAsync("ui-input",
                new Dictionary<string, object?> { ["nodeId"] = buttonId, ["kind"] = "Click" })).Delivered);
            Assert.Equal(1, clicks);
            var activity =
                Read<ActivityEntry[]>(await client.CallToolAsync("activity", new Dictionary<string, object?>()));
            Assert.Contains(activity,
                entry => entry.Kind == "Event" && entry.ObjectId == buttonId && entry.EventName == "Click");
            Assert.False(
                Read<ActivityStatus>(await client.CallToolAsync("activity-stop", new Dictionary<string, object?>()))
                    .IsRunning);
        }
        finally
        {
            window.Close();
        }
    }

    private static Task<McpClient> CreateClientAsync()
    {
        var testAssembly = typeof(McpIntegrationTests).Assembly.Location;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Name = "AvaloniaDevTool integration",
            Arguments =
            [
                "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"), typeof(Program).Assembly.Location,
                "mcp"
            ],
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        });
        return McpClient.CreateAsync(transport);
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

    private sealed class MessageModel : INotifyPropertyChanged
    {
        private string _text = "";
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }
    }
}
