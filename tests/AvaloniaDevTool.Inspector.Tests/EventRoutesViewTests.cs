using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using System.Runtime.CompilerServices;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class EventRoutesViewTests
{
    [AvaloniaFact]
    public async Task Actual_event_page_selects_catalog_captures_route_and_navigates_source_and_receiver()
    {
        var source = new EventControl { Name = "EventSource" };
        var parent = new StackPanel { Name = "RouteParent", Children = { source } };
        var target = new Window { Content = parent };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "event-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var vm = new MainWindowViewModel(agent.PipeName)
            { SelectedTreeIndex = 1, CaptureLogs = false, EventDefaultsStore = TemporaryDefaults() };
            await vm.InitializeAsync();
            vm.InspectionTabIndex = 4;
            await vm.RefreshEventRoutesAsync();
            await WaitUntilAsync(() => vm.EventGroupChoices.Count == 6, vm);
            Assert.Equal(6, vm.EventGroupChoices.Count);
            var view = new EventRoutesView { DataContext = vm };
            var host = new Window { Width = 1100, Height = 850, Content = view };
            host.Show();
            try
            {
                view.FindControl<TextBox>("EventCatalogFilter")!.Text = "InspectorEventProbe";
                Assert.Single(vm.FilteredEventCatalog);
                Assert.Equal(1, view.FindControl<ListBox>("EventCatalog")!.ItemCount);
                host.UpdateLayout();
                var groupBoxes = view.GetVisualDescendants().OfType<CheckBox>().Where(item => item.DataContext is EventGroupChoice).ToArray();
                Assert.Equal(6, groupBoxes.Length);
                foreach (var check in groupBoxes) check.IsChecked = false;
                var eventCheck = Assert.Single(view.GetVisualDescendants().OfType<CheckBox>(), item => item.DataContext is EventCatalogChoice);
                eventCheck.IsChecked = true;
                Assert.All(vm.EventGroupChoices, item => Assert.False(item.IsSelected));
                Assert.True(Assert.Single(vm.FilteredEventCatalog).IsSelected);
                Click(host, view.FindControl<Button>("StartEventRoutes")!);
                await WaitUntilAsync(() => vm.CurrentActivityStatus?.IsRunning == true, vm);
                source.AddHandler(EventControl.Probe, (_, args) => args.Handled = true);
                source.RaiseEvent(new RoutedEventArgs(EventControl.Probe));
                await WaitUntilAsync(() => vm.EventRoutes.Any(item => item.IsFinished), vm);
                var route = Assert.Single(vm.EventRoutes);
                Assert.True(route.FinalHandled);
                view.FindControl<ListBox>("EventRoutes")!.SelectedItem = route;
                Assert.Equal(route.RouteId, vm.SelectedEventRoute?.RouteId);
                Assert.Equal(route.Nodes.Count, view.FindControl<ListBox>("EventRouteNodes")!.ItemCount);
                Click(host, view.FindControl<Button>("JumpToEventSource")!);
                await WaitUntilAsync(() => vm.SelectedNode?.Node.Name == "EventSource", vm);
                var receiver = route.Nodes.First(item => item.ReceiverName == "RouteParent");
                view.FindControl<ListBox>("EventRouteNodes")!.SelectedItem = receiver;
                Click(host, view.FindControl<Button>("JumpToEventNode")!);
                await WaitUntilAsync(() => vm.SelectedNode?.Node.Name == "RouteParent", vm);
                await vm.RefreshEventRoutesAsync();
                Assert.Equal(route.RouteId, vm.SelectedEventRoute?.RouteId);
                Assert.Contains("不能归因", vm.SelectedEventRouteSummary);
                vm.ClearEventRoutesCommand.Execute(null);
                await WaitUntilAsync(() => vm.EventRoutes.Count == 0 && vm.SelectedEventRoute is null, vm);
                vm.DisconnectCommand.Execute(null);
                await WaitUntilAsync(() => !vm.IsConnected && vm.EventCatalogChoices.Count == 0, vm);
                Assert.Empty(vm.EventCatalogChoices);
                Assert.False(view.FindControl<Button>("StartEventRoutes")!.IsEffectivelyEnabled);
            }
            finally { host.Close(); }
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Foreign_backend_event_view_and_its_commands_are_disabled()
    {
        await using var backend = new ForeignBackend();
        await using var vm = new MainWindowViewModel("foreign", backend) { EventDefaultsStore = TemporaryDefaults() };
        await vm.InitializeAsync();
        var view = new EventRoutesView { DataContext = vm };
        var host = new Window { Content = view };
        host.Show();
        try
        {
            Assert.True(vm.IsConnected);
            Assert.False(vm.CanUseNativeTools);
            Assert.False(vm.StartEventRoutesCommand.CanExecute(null));
            Assert.False(vm.RefreshEventRoutesCommand.CanExecute(null));
            Assert.False(vm.JumpToEventSourceCommand.CanExecute(null));
            Assert.False(view.FindControl<Button>("StartEventRoutes")!.IsEffectivelyEnabled);
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public async Task Expired_event_source_actual_jump_button_reports_expiry_and_preserves_route_details()
    {
        _ = EventControl.Probe;
        var target = new Window { Content = new TextBlock { Text = "Weak event source" } };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "event-weak-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var vm = new MainWindowViewModel(agent.PipeName) { CaptureLogs = false, EventDefaultsStore = TemporaryDefaults() };
            await vm.InitializeAsync();
            vm.InspectionTabIndex = 4;
            await vm.RefreshEventRoutesAsync();
            await WaitUntilAsync(() => vm.EventGroupChoices.Count == 6, vm);
            foreach (var group in vm.EventGroupChoices) group.IsSelected = false;
            Assert.Single(vm.EventCatalogChoices, item => item.Entry.Name == "InspectorEventProbe").IsSelected = true;
            vm.StartEventRoutesCommand.Execute(null);
            await WaitUntilAsync(() => vm.CurrentActivityStatus?.IsRunning == true, vm);
            var source = RaiseTransientEvent();
            await WaitUntilAsync(() => vm.EventRoutes.Count == 1, vm);
            for (var index = 0; index < 3; index++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            Assert.False(source.IsAlive);
            await vm.RefreshEventRoutesAsync();
            var route = Assert.Single(vm.EventRoutes);
            Assert.True(route.SourceExpired);
            var view = new EventRoutesView { DataContext = vm };
            var host = new Window { Width = 1100, Height = 850, Content = view };
            host.Show();
            try
            {
                view.FindControl<ListBox>("EventRoutes")!.SelectedItem = route;
                Click(host, view.FindControl<Button>("JumpToEventSource")!);
                await WaitUntilAsync(() => vm.Status.Contains("已失效", StringComparison.Ordinal), vm);
                Assert.Equal(route.RouteId, vm.SelectedEventRoute?.RouteId);
                Assert.Contains("源已失效", vm.SelectedEventRouteSummary);
                Assert.NotEmpty(vm.SelectedEventRouteNodes);
            }
            finally { host.Close(); }
        }
        finally { target.Close(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RaiseTransientEvent()
    {
        var source = new EventControl { Name = "TransientEventSource" };
        source.RaiseEvent(new RoutedEventArgs(EventControl.Probe));
        return new(source);
    }

    [AvaloniaFact]
    public async Task Actual_save_defaults_button_restores_overrides_on_reconnect_and_reports_missing_registered_identities()
    {
        _ = EventControl.Probe;
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "avdt-event-defaults-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new EventSelectionDefaultsStore(path);
        var target = new Window { Content = new EventControl { Name = "DefaultsSource" } };
        target.Show();
        try
        {
            await store.SaveAsync(new(["missing:Unregistered.Event"], ["Keyboard"]));
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "event-default-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var vm = new MainWindowViewModel(agent.PipeName) { EventDefaultsStore = store };
            await vm.InitializeAsync();
            vm.InspectionTabIndex = 4;
            await vm.RefreshEventRoutesAsync();
            await WaitUntilAsync(() => vm.EventGroupChoices.Count == 6, vm);
            Assert.Equal("Keyboard", Assert.Single(vm.EventGroupChoices, item => item.IsSelected).Id);
            Assert.Contains("尚未注册", vm.MissingEventDefaultsSummary);
            var choice = Assert.Single(vm.EventCatalogChoices, item => item.Entry.Name == "InspectorEventProbe");
            choice.IsSelected = true;
            foreach (var group in vm.EventGroupChoices) group.IsSelected = group.Id == "Focus";
            var view = new EventRoutesView { DataContext = vm };
            var host = new Window { Width = 1100, Height = 850, Content = view };
            host.Show();
            try
            {
                Click(host, view.FindControl<Button>("SaveEventDefaults")!);
                await WaitUntilAsync(() => vm.EventDefaultsSummary.Contains("已保存", StringComparison.Ordinal), vm);
                var saved = await store.LoadAsync();
                Assert.NotNull(saved);
                Assert.Equal(["Focus"], saved.Groups);
                Assert.Equal([choice.Entry.Id], saved.EventIds);
                using var json = System.Text.Json.JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(path, System.Text.Encoding.UTF8));
                Assert.Equal(["eventIds", "groups", "includeAll"], json.RootElement.EnumerateObject().Select(item => item.Name));
                vm.DisconnectCommand.Execute(null);
                await WaitUntilAsync(() => !vm.IsConnected && vm.EventCatalogChoices.Count == 0, vm);
                await using var reconnected = new MainWindowViewModel(agent.PipeName) { EventDefaultsStore = store };
                await reconnected.InitializeAsync();
                reconnected.InspectionTabIndex = 4;
                await reconnected.RefreshEventRoutesAsync();
                await WaitUntilAsync(() => reconnected.EventGroupChoices.Count == 6, reconnected);
                Assert.Equal("Focus", Assert.Single(reconnected.EventGroupChoices, item => item.IsSelected).Id);
                Assert.Equal(choice.Entry.Id, Assert.Single(reconnected.EventCatalogChoices, item => item.IsSelected).Entry.Id);
                Assert.Empty(reconnected.MissingEventDefaultsSummary);
            }
            finally { host.Close(); }
        }
        finally { target.Close(); if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }

    [Fact]
    public async Task Defaults_store_rejects_invalid_groups_and_oversized_files_without_replacing_saved_selection()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "avdt-event-store-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new EventSelectionDefaultsStore(path);
        try
        {
            var token = TestContext.Current.CancellationToken;
            await store.SaveAsync(new(Groups: ["Pointer"]), token);
            await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => store.SaveAsync(new(Groups: ["Unknown"]), token));
            Assert.Equal(["Pointer"], (await store.LoadAsync(token))!.Groups);
            await System.IO.File.WriteAllTextAsync(path, new string('x', 70 * 1024), System.Text.Encoding.UTF8, token);
            await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => store.LoadAsync(token));
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }

    private static EventSelectionDefaultsStore TemporaryDefaults() => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "avdt-event-unsaved-" + Guid.NewGuid().ToString("N") + ".json"));

    private static void Click(Window host, Control control)
    {
        host.UpdateLayout();
        using var frame = host.CaptureRenderedFrame();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), host);
        Assert.NotNull(point);
        host.MouseMove(point.Value);
        host.MouseDown(point.Value, MouseButton.Left);
        host.MouseUp(point.Value, MouseButton.Left);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, MainWindowViewModel vm)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(15);
        Assert.True(condition(), vm.Status);
    }

    private sealed class EventControl : Control
    {
        public static readonly RoutedEvent<RoutedEventArgs> Probe = RoutedEvent.Register<EventControl, RoutedEventArgs>("InspectorEventProbe", RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        static EventControl() { }
    }

    private sealed class ForeignBackend : IInspectionBackend
    {
        public string BackendId => "foreign-event-test";
        public string DisplayName => "Foreign";
        public bool IsConnected { get; private set; }
        public bool HasStableObjectIds => false;
        public bool HasPushUpdates => false;
        public string? LastNotice => null;
        public SessionInfo? Session { get; private set; }
        public event Action? ConnectionChanged;
        public Task ConnectAsync(string endpoint, CancellationToken cancellationToken = default)
        { IsConnected = true; Session = new("foreign", "Foreign", Environment.ProcessId, "12.1.4", DiagnosticsProtocol.Version); ConnectionChanged?.Invoke(); return Task.CompletedTask; }
        public Task DisconnectAsync() { IsConnected = false; Session = null; ConnectionChanged?.Invoke(); return Task.CompletedTask; }
        public Task<IReadOnlyList<TreeNodeInfo>> GetRootsAsync(TreeKind kind, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TreeNodeInfo>>([new(1, "Foreign.Control", "Foreign", null, false)]);
        public Task<IReadOnlyList<TreeNodeInfo>> GetChildrenAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TreeNodeInfo>>([]);
        public Task<IReadOnlyList<PropertyInfoDto>> GetPropertiesAsync(long objectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PropertyInfoDto>>([]);
        public Task<StyleSnapshot> GetStylesAsync(long objectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TreeNodeInfo>> GetAncestorsAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ElementScreenshot> CaptureElementAsync(long objectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async ValueTask DisposeAsync() => await DisconnectAsync();
    }
}
