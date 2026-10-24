using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.LogicalTree;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Backends.XamlMcp;
using AvaloniaDevTool.Protocol;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class XamlBackendIntegrationTests
{
    [Fact]
    public void Launch_options_require_an_explicit_server_and_keep_native_defaults()
    {
        Assert.Equal(new InspectorLaunchOptions(), InspectorLaunchOptions.Parse([]));
        Assert.Equal("local-pipe", InspectorLaunchOptions.Parse(["--pipe", "local-pipe"]).InitialEndpoint);
        Assert.Throws<ArgumentException>(() => InspectorLaunchOptions.Parse(["--backend", "unknown"]));
        Assert.Throws<ArgumentException>(() => InspectorLaunchOptions.Parse(["--backend", "xamlmcp", "--target", "1234"]));
        Assert.Throws<ArgumentException>(() => InspectorLaunchOptions.Parse(["--backend", "native", "--server", "unused.dll"]));
        Assert.Throws<ArgumentException>(() => InspectorLaunchOptions.Parse(["--backend"]));
    }

    [Fact]
    public void Launch_options_normalize_pid_and_instance_without_using_shell_command_text()
    {
        var directory = Path.Combine(Path.GetTempPath(), "avdt-inspector-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Server with spaces.dll");
        File.WriteAllText(path, "Controlled parser fixture");
        try
        {
            var parsed = InspectorLaunchOptions.Parse(["--backend", "xamlmcp", "--target", "1234", "--server", path]);
            Assert.Equal("pid:1234", parsed.InitialEndpoint);
            Assert.Equal(path, parsed.ServerPath);
            Assert.Equal("instance:opaque-id", InspectorLaunchOptions.Parse(
                ["--backend", "xamlmcp", "--target", "opaque-id", "--server", path]).InitialEndpoint);
            Assert.Throws<ArgumentException>(() => InspectorLaunchOptions.Parse(
                ["--backend", "xamlmcp", "--target", "pid:0", "--server", path]));
            Assert.Throws<ArgumentException>(() => InspectorLaunchOptions.Parse(
                ["--backend", "xamlmcp", "--target", "1234", "--server", path, "--pipe", "wrong"]));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [AvaloniaFact]
    public async Task Published_nuget_backend_connects_current_viewmodel_to_a_separate_headless_target()
    {
        await using var target = await PublishedTarget.StartAsync();
        var serverPath = XamlMcpServerLocation.BundledAssemblyPath;
        Assert.True(File.Exists(serverPath), "The upstream server payload must be copied by the backend project reference.");
        await using var backend = new XamlMcpBackend(new XamlMcpBackendOptions
        {
            ServerCommand = "dotnet", ServerArguments = [serverPath],
            DiscoveryDirectory = target.DirectoryPath, OperationTimeout = TimeSpan.FromSeconds(30)
        });
        using (var discoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            while (!(await backend.ListApplicationsAsync(discoveryTimeout.Token)).Any(app => app.Pid == target.ProcessId))
                await Task.Delay(50, discoveryTimeout.Token);
        }
        await using var viewModel = new MainWindowViewModel("pid:" + target.ProcessId, backend) { SelectedTreeIndex = 1 };
        await viewModel.InitializeAsync();
        Assert.True(viewModel.IsConnected, viewModel.Status);
        Assert.Equal(target.ProcessId, backend.Session!.ProcessId);
        Assert.False(viewModel.CanUseNativeTools);
        Assert.NotEmpty(viewModel.Roots);
        var healthy = await FindNodeAsync(viewModel, "HealthyBindingTarget");
        viewModel.SelectedNode = healthy;
        await WaitUntilAsync(() => viewModel.FilteredProperties.Any(row => row.Name == "Text" && row.CurrentValue?.Contains("绑定文本", StringComparison.Ordinal) == true));
        Assert.All(viewModel.FilteredProperties, row => Assert.True(row.IsReadOnly));
        Assert.All(viewModel.FilteredProperties, row => Assert.False(row.ApplyCommand.CanExecute(null)));

        var button = await FindNodeAsync(viewModel, "TargetIncrement");
        viewModel.SelectedNode = button;
        await WaitUntilAsync(() => viewModel.FilteredProperties.Any(row => row.Name == "Background"));
        viewModel.InspectionTabIndex = 1;
        await WaitUntilAsync(() => viewModel.StyleSummary.Contains("XamlMcp", StringComparison.Ordinal));
        Assert.NotEmpty(viewModel.StyleFrames);
        Assert.Contains(viewModel.StyleFrames, frame => frame.Setters.Any(setter => setter.PropertyName == "Background"));
        Assert.All(viewModel.StyleFrames.SelectMany(frame => frame.Setters), setter => Assert.Null(setter.MatchesEffectiveValue));

        viewModel.InspectionTabIndex = 5;
        viewModel.CaptureElementCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ElementImage is not null);
        Assert.True(viewModel.ElementImage!.PixelSize.Width > 0);
        Assert.Contains("PNG", viewModel.ElementImageSummary);
        viewModel.DisconnectCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsConnected);
        Assert.Empty(viewModel.Roots);
        Assert.Empty(viewModel.FilteredProperties);
        Assert.Empty(viewModel.StyleFrames);
        Assert.Null(viewModel.ElementImage);
        Assert.False(target.HasExited);
    }

    [AvaloniaFact]
    public async Task External_backend_window_disables_native_tabs_and_mode_toggles()
    {
        await using var backend = new ControlledBackend();
        await using var viewModel = new MainWindowViewModel("pid:1234", backend);
        await viewModel.InitializeAsync();
        var window = new MainWindow { DataContext = viewModel };
        try
        {
            var tabControl = window.FindControl<TabControl>("InspectionTabs");
            Assert.NotNull(tabControl);
            var tabs = tabControl.Items.OfType<TabItem>().ToArray();
            foreach (var title in new[] { "资源", "资产", "日志事件", "指标", "断点", "3D几何" })
                Assert.False(tabs.Single(tab => Equals(tab.Header, title)).IsEnabled);
            Assert.True(tabs.Single(tab => Equals(tab.Header, "样式绑定")).IsEnabled);
            Assert.True(tabs.Single(tab => Equals(tab.Header, "交互截图")).IsEnabled);
            foreach (var title in new[] { "跟随焦点", "高亮" })
                Assert.False(window.GetLogicalDescendants().OfType<ToggleSwitch>().Single(toggle => Equals(toggle.Content, title)).IsEnabled);
        }
        finally { window.Close(); await window.ViewModel.DisposeAsync(); }
    }

    [AvaloniaFact]
    public async Task External_backend_routes_core_reads_and_forces_readonly_capabilities()
    {
        await using var backend = new ControlledBackend();
        await using var viewModel = new MainWindowViewModel("pid:1234", backend);
        await viewModel.InitializeAsync();
        Assert.True(viewModel.IsConnected, viewModel.Status);
        Assert.False(viewModel.IsNativeBackend);
        Assert.False(viewModel.CanUseNativeTools);
        Assert.Equal("pid:1234", backend.Endpoint);
        Assert.Contains("只读快照", viewModel.BackendModeNotice);
        Assert.Contains("手动刷新", viewModel.BackendModeNotice);
        var root = Assert.Single(viewModel.Roots);
        Assert.True(backend.ChildrenCalls > 0);
        viewModel.SelectedNode = Assert.Single(root.Children);
        await WaitUntilAsync(() => viewModel.FilteredProperties.Count == 1);
        var property = Assert.Single(viewModel.FilteredProperties);
        Assert.True(property.IsReadOnly);
        Assert.False(property.CanEdit);
        Assert.False(property.CanClear);
        Assert.False(property.ApplyCommand.CanExecute(null));
        Assert.False(property.ClearCommand.CanExecute(null));
        Assert.False(viewModel.PickCommand.CanExecute(null));
        Assert.False(viewModel.SearchTreeCommand.CanExecute(null));
        Assert.False(viewModel.FocusElementCommand.CanExecute(null));
        Assert.False(viewModel.BringIntoViewCommand.CanExecute(null));
        Assert.False(viewModel.StartActivityCommand.CanExecute(null));
        Assert.False(viewModel.StartMetricsCommand.CanExecute(null));
        Assert.False(viewModel.CreateBreakpointCommand.CanExecute(null));
        viewModel.FollowFocusEnabled = true;
        Assert.False(viewModel.FollowFocusEnabled);

        viewModel.InspectionTabIndex = 1;
        await WaitUntilAsync(() => viewModel.StyleSummary.Contains("Controlled backend", StringComparison.Ordinal));
        Assert.True(backend.StylesCalls > 0);
        Assert.Single(viewModel.StyleFrames);
        viewModel.InspectionTabIndex = 5;
        Assert.True(viewModel.CaptureElementCommand.CanExecute(null));
        viewModel.CaptureElementCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ElementImage is not null);
        Assert.Equal(1, backend.CaptureCalls);
        Assert.Contains("2 × 2", viewModel.ElementImageSummary);
        Assert.DoesNotContain("not_connected", viewModel.Status);
    }

    [AvaloniaFact]
    public async Task Snapshot_refresh_discards_old_ids_selection_properties_styles_and_image()
    {
        await using var backend = new ControlledBackend();
        await using var viewModel = new MainWindowViewModel("pid:1234", backend);
        await viewModel.InitializeAsync();
        var oldRoot = Assert.Single(viewModel.Roots).Id;
        viewModel.SelectedNode = Assert.Single(viewModel.Roots[0].Children);
        await WaitUntilAsync(() => viewModel.FilteredProperties.Count == 1);
        viewModel.InspectionTabIndex = 1;
        await WaitUntilAsync(() => viewModel.StyleFrames.Count == 1);
        viewModel.InspectionTabIndex = 5;
        viewModel.CaptureElementCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ElementImage is not null);

        viewModel.RefreshCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Roots.Count == 1 && viewModel.Roots[0].Id != oldRoot);
        Assert.Null(viewModel.SelectedNode);
        Assert.Empty(viewModel.FilteredProperties);
        Assert.Empty(viewModel.StyleFrames);
        Assert.Null(viewModel.ElementImage);
        Assert.Equal(0, backend.AncestorCalls);
        Assert.DoesNotContain("not_connected", viewModel.Status);
    }

    [AvaloniaFact]
    public async Task External_ancestor_navigation_uses_the_injected_core_without_enabling_native_search()
    {
        await using var backend = new ControlledBackend();
        await using var viewModel = new MainWindowViewModel("pid:1234", backend);
        await viewModel.InitializeAsync();
        Assert.False(viewModel.SearchTreeCommand.CanExecute(null));
        // Exercise existing explicit-ID navigation; no upstream query/OR semantics are invented.
        viewModel.SelectedSearchResult = backend.Grandchild;
        await WaitUntilAsync(() => viewModel.SelectedNode?.Id == backend.Grandchild.Id && viewModel.FilteredProperties.Count == 1);
        Assert.Equal(1, backend.AncestorCalls);
        Assert.True(viewModel.Roots[0].IsExpanded);
        Assert.True(viewModel.Roots[0].Children[0].IsExpanded);
    }

    [AvaloniaFact]
    public async Task Unsupported_tabs_and_commands_are_disabled_before_any_native_request()
    {
        await using var backend = new ControlledBackend();
        await using var viewModel = new MainWindowViewModel("pid:1234", backend);
        await viewModel.InitializeAsync();
        foreach (var tab in new[] { 2, 3, 4, 6, 7, 8 })
        {
            viewModel.InspectionTabIndex = tab;
            Assert.False(viewModel.RefreshInspectionCommand.CanExecute(null));
            Assert.Contains("未支持", viewModel.Status);
        }
        Assert.Equal(0, backend.PropertiesCalls);
        Assert.Equal(0, backend.StylesCalls);
        Assert.Equal(0, backend.CaptureCalls);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(15, timeout.Token);
    }

    private static async Task<TreeNodeViewModel> FindNodeAsync(MainWindowViewModel viewModel, string name)
    {
        var pending = new Queue<TreeNodeViewModel>(viewModel.Roots);
        var visited = new HashSet<long>();
        while (pending.TryDequeue(out var node) && visited.Count < 512)
        {
            if (!visited.Add(node.Id) || node.IsPlaceholder) continue;
            if (node.Node.Name == name) return node;
            if (!node.Node.HasChildren) continue;
            node.IsExpanded = true;
            await WaitUntilAsync(() => node.Children.All(child => !child.IsPlaceholder));
            foreach (var child in node.Children) pending.Enqueue(child);
        }
        throw new InvalidOperationException("The published agent tree did not contain " + name + ". " + viewModel.Status);
    }

    private sealed class PublishedTarget : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        private PublishedTarget(string directory, Process process)
        {
            DirectoryPath = directory;
            _process = process;
            _stdout = process.StandardOutput.ReadToEndAsync();
            _stderr = process.StandardError.ReadToEndAsync();
        }

        public string DirectoryPath { get; }
        public int ProcessId => _process.Id;
        public bool HasExited => _process.HasExited;

        public static async Task<PublishedTarget> StartAsync()
        {
            var assembly = FindTargetAssembly();
            var directory = Path.Combine(Path.GetTempPath(), "avdt-inspector-xaml-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var ready = Path.Combine(directory, "ready.json");
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(assembly)!
            };
            foreach (var arg in new[] { assembly, "--headless", "--enable-inspection", "--discovery-dir", directory, "--ready-file", ready })
                start.ArgumentList.Add(arg);
            var process = Process.Start(start) ?? throw new InvalidOperationException("The controlled target could not start.");
            var target = new PublishedTarget(directory, process);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    if (process.HasExited)
                        throw new InvalidOperationException("The controlled target exited before readiness: " + await target._stderr);
                    if (File.Exists(ready))
                    {
                        try
                        {
                            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ready, timeout.Token));
                            Assert.Equal(process.Id, document.RootElement.GetProperty("pid").GetInt32());
                            return target;
                        }
                        catch (JsonException) { }
                    }
                    await Task.Delay(25, timeout.Token);
                }
            }
            catch { await target.DisposeAsync(); throw; }
        }

        private static string FindTargetAssembly([CallerFilePath] string sourcePath = "")
        {
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            var artifactsSibling = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..",
                "AvaloniaDevTool.XamlMcpTarget", configuration.ToLowerInvariant(), "AvaloniaDevTool.XamlMcpTarget.dll"));
            var repository = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));
            var standard = Path.Combine(repository, "samples", "AvaloniaDevTool.XamlMcpTarget", "bin", configuration,
                "net10.0", "AvaloniaDevTool.XamlMcpTarget.dll");
            return new[] { artifactsSibling, standard }.FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("Build the referenced XamlMcpTarget project before running the published-backend test.");
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5));
            _process.Dispose();
            var fullPath = Path.GetFullPath(DirectoryPath);
            var tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(fullPath).StartsWith("avdt-inspector-xaml-", StringComparison.Ordinal))
            {
                try { Directory.Delete(fullPath, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private sealed class ControlledBackend : IInspectionBackend
    {
        private int _generation;
        public string BackendId => "controlled-xaml";
        public string DisplayName => "Controlled XamlMcp";
        public bool IsConnected { get; private set; }
        public bool HasStableObjectIds => false;
        public bool HasPushUpdates => false;
        public string? LastNotice => "Controlled snapshot notice";
        public SessionInfo? Session { get; private set; }
        public string? Endpoint { get; private set; }
        public event Action? ConnectionChanged;
        public int ChildrenCalls { get; private set; }
        public int PropertiesCalls { get; private set; }
        public int StylesCalls { get; private set; }
        public int CaptureCalls { get; private set; }
        public int AncestorCalls { get; private set; }
        public TreeNodeInfo Root => new(_generation * 100, "External.Window", "Root", null, true);
        public TreeNodeInfo Child => new(_generation * 100 + 1, "External.Panel", "Child", null, true);
        public TreeNodeInfo Grandchild => new(_generation * 100 + 2, "External.TextBlock", "Grandchild", "Snapshot value", false);

        public Task ConnectAsync(string endpoint, CancellationToken cancellationToken = default)
        {
            Endpoint = endpoint;
            IsConnected = true;
            Session = new SessionInfo(Guid.NewGuid().ToString("N"), "Controlled target", 1234, "12.1.4", 1);
            ConnectionChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            Session = null;
            ConnectionChanged?.Invoke();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TreeNodeInfo>> GetRootsAsync(TreeKind kind, CancellationToken cancellationToken = default)
        {
            _generation++;
            return Task.FromResult<IReadOnlyList<TreeNodeInfo>>([Root]);
        }

        public Task<IReadOnlyList<TreeNodeInfo>> GetChildrenAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default)
        {
            ChildrenCalls++;
            Validate(objectId);
            return Task.FromResult<IReadOnlyList<TreeNodeInfo>>(objectId == Root.Id ? [Child] : objectId == Child.Id ? [Grandchild] : []);
        }

        public Task<IReadOnlyList<PropertyInfoDto>> GetPropertiesAsync(long objectId, CancellationToken cancellationToken = default)
        {
            Validate(objectId);
            PropertiesCalls++;
            // Intentionally advertises edits: the frontend must enforce its own read-only contract.
            return Task.FromResult<IReadOnlyList<PropertyInfoDto>>([
                new("Owner:Text", "Text", "Owner", "System.String", "Snapshot value", "External", false, true, true)]);
        }

        public Task<StyleSnapshot> GetStylesAsync(long objectId, CancellationToken cancellationToken = default)
        {
            Validate(objectId);
            StylesCalls++;
            return Task.FromResult(new StyleSnapshot([new("Controlled style", "Style", "Style", true, [])], [], [], "Controlled backend"));
        }

        public Task<IReadOnlyList<TreeNodeInfo>> GetAncestorsAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default)
        {
            Validate(objectId);
            AncestorCalls++;
            return Task.FromResult<IReadOnlyList<TreeNodeInfo>>(objectId == Root.Id ? [Root] : objectId == Child.Id ? [Root, Child] : [Root, Child, Grandchild]);
        }

        public Task<ElementScreenshot> CaptureElementAsync(long objectId, CancellationToken cancellationToken = default)
        {
            Validate(objectId);
            CaptureCalls++;
            using var bitmap = new RenderTargetBitmap(new PixelSize(2, 2), new Vector(96, 96));
            using var stream = new MemoryStream();
            bitmap.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            return Task.FromResult(new ElementScreenshot(2, 2, Convert.ToBase64String(stream.ToArray())));
        }

        private void Validate(long id)
        {
            if (id != Root.Id && id != Child.Id && id != Grandchild.Id)
                throw new RpcException("snapshot_expired", "The fake snapshot ID is no longer valid.");
        }

        public async ValueTask DisposeAsync() => await DisconnectAsync();
    }
}
