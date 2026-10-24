using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class ProfilerViewTests
{
    [AvaloniaFact]
    public async Task Profiler_commands_show_real_lookup_samples_clear_and_reset_on_disconnect()
    {
        var control = new TextBlock { Text = "Profiler UI target" };
        var target = new Window { Content = control };
        target.Resources["ProfilerView.Found"] = "Available";
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            {
                PipeName = "profiler-view-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target]
            });
            await using var vm = new MainWindowViewModel(agent.PipeName)
            {
                ProfileStyleAttach = false, ProfileStyleEvaluate = false, ProfilerFilter = "ProfilerView."
            };
            await vm.InitializeAsync();
            vm.InspectionTabIndex = 10;
            Assert.True(vm.StartProfilerCommand.CanExecute(null));
            vm.StartProfilerCommand.Execute(null);
            await WaitUntilAsync(() => vm.CurrentProfilerStatus?.IsRunning == true && vm.StopProfilerCommand.CanExecute(null));
            Assert.True(control.TryFindResource("ProfilerView.Found", out _));
            Assert.False(control.TryFindResource("ProfilerView.Missing", out _));
            await WaitUntilAsync(() => vm.ProfilerSamples.Count >= 2, vm.RefreshProfilerAsync);
            Assert.Contains(vm.ProfilerGroups, row => row.Name.Contains("ProfilerView.Found", StringComparison.Ordinal));
            Assert.Contains(vm.ProfilerSamples, row => row.Name == "ProfilerView.Missing" && row.Result == "未知");
            Assert.Contains("μs", vm.ProfilerSamples[0].Duration);
            Assert.Contains("retained_samples", vm.ProfilerGroupDetails);
            vm.StopProfilerCommand.Execute(null);
            await WaitUntilAsync(() => vm.CurrentProfilerStatus?.IsRunning == false && vm.StartProfilerCommand.CanExecute(null));
            Assert.False(vm.StopProfilerCommand.CanExecute(null));
            Assert.NotEmpty(vm.ProfilerSamples);
            vm.ClearProfilerCommand.Execute(null);
            await WaitUntilAsync(() => vm.ProfilerSamples.Count == 0 && vm.ClearProfilerCommand.CanExecute(null));
            Assert.Empty(vm.ProfilerGroups);
            vm.StartProfilerCommand.Execute(null);
            await WaitUntilAsync(() => vm.CurrentProfilerStatus?.IsRunning == true && vm.StopProfilerCommand.CanExecute(null));
            vm.DisconnectCommand.Execute(null);
            await WaitUntilAsync(() => !vm.IsConnected && vm.ConnectCommand.CanExecute(null) && vm.CurrentProfilerStatus is null);
            Assert.Null(vm.CurrentProfilerStatus);
            Assert.Empty(vm.ProfilerSamples);
            Assert.False(vm.StartProfilerCommand.CanExecute(null));
            vm.ConnectCommand.Execute(null);
            await WaitUntilAsync(() => vm.IsConnected && vm.DisconnectCommand.CanExecute(null));
            await vm.RefreshProfilerAsync();
            Assert.False(vm.CurrentProfilerStatus!.IsRunning);
            Assert.Empty(vm.ProfilerSamples);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Foreign_backend_has_a_disabled_profiler_page_and_commands()
    {
        await using var vm = new MainWindowViewModel(backend: new ForeignBackend());
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var tabs = window.FindControl<TabControl>("InspectionTabs")!;
            var profiler = Assert.Single(tabs.Items.OfType<TabItem>(), item => Equals(item.Header, "性能"));
            Assert.False(profiler.IsEnabled);
            Assert.False(vm.StartProfilerCommand.CanExecute(null));
            Assert.False(vm.StopProfilerCommand.CanExecute(null));
            Assert.False(vm.ClearProfilerCommand.CanExecute(null));
            Assert.False(vm.RefreshProfilerCommand.CanExecute(null));
            Assert.Null(vm.CurrentProfilerStatus);
            Assert.Empty(vm.ProfilerGroups);
        }
        finally { window.Close(); }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, Func<Task>? refresh = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate())
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (refresh is not null) await refresh();
            await Task.Delay(15, timeout.Token);
        }
    }

    private sealed class ForeignBackend : IInspectionBackend
    {
        public string BackendId => "foreign-profiler-test";
        public string DisplayName => "Foreign test";
        public bool IsConnected => true;
        public bool HasStableObjectIds => false;
        public bool HasPushUpdates => false;
        public string? LastNotice => null;
        public SessionInfo? Session => new("foreign", "Foreign", 1, "12.1.4", DiagnosticsProtocol.Version);
        public event Action? ConnectionChanged { add { } remove { } }
        public Task ConnectAsync(string endpoint, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<IReadOnlyList<TreeNodeInfo>> GetRootsAsync(TreeKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TreeNodeInfo>> GetChildrenAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PropertyInfoDto>> GetPropertiesAsync(long objectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StyleSnapshot> GetStylesAsync(long objectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TreeNodeInfo>> GetAncestorsAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ElementScreenshot> CaptureElementAsync(long objectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
