using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Xunit;

namespace AvaloniaDevTool.Previewer.Tests;

public sealed class PreviewIntegrationTests
{
    [Fact]
    public void Command_line_requires_explicit_absolute_source_and_valid_options()
    {
        Assert.Throws<ArgumentException>(() => PreviewOptions.Parse([]));
        Assert.Throws<ArgumentException>(() => PreviewOptions.Parse(["--xaml", "relative.axaml"]));
        Assert.Throws<ArgumentException>(() => PreviewOptions.Parse(["--unknown"]));
        using var files = new TestFiles();
        Assert.Throws<ArgumentException>(() => PreviewOptions.Parse(["--xaml", files.Root]));
        var options = PreviewOptions.Parse(["--xaml", files.XamlPath, "--pipe", "preview-test", "--no-watch"]);
        Assert.Equal(files.XamlPath, options.XamlPath);
        Assert.Equal("preview-test", options.PipeName);
        Assert.False(options.Watch);
        Assert.Throws<ArgumentException>(() =>
            PreviewOptions.Parse(["--xaml", files.XamlPath, "--pipe", "../invalid"]));
        Assert.Throws<ArgumentException>(() =>
            PreviewOptions.Parse(["--xaml", files.XamlPath, "--xaml", files.XamlPath]));
    }

    [AvaloniaFact]
    public void Classless_source_loads_into_a_real_control_and_renders_png()
    {
        using var files = new TestFiles();
        files.Write(Markup("Rendered preview"));
        using var loader = new PreviewDocumentLoader(files.XamlPath);
        var control = loader.Load();
        var border = Assert.IsType<Border>(control);
        Assert.Equal("Rendered preview", Assert.IsType<TextBlock>(border.Child).Text);
        var window = new Window { Content = control, Width = 400, Height = 250 };
        window.Show();
        try
        {
            window.UpdateLayout();
            using var bitmap = new RenderTargetBitmap(new PixelSize(400, 250));
            bitmap.Render(control);
            using var output = new MemoryStream();
            bitmap.Save(output, PngBitmapEncoderOptions.Default);
            Assert.True(output.Length > 100);
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, output.ToArray()[..4]);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Loader_applies_root_design_dimensions_without_mutating_global_design_mode()
    {
        using var files = new TestFiles();
        files.Write("""
            <Border xmlns="https://github.com/avaloniaui" Design.Width="320" Design.Height="180">
              <TextBlock Text="Design properties" />
            </Border>
            """);
        var before = Design.IsDesignMode;
        using var loader = new PreviewDocumentLoader(files.XamlPath);
        var control = loader.Load();
        Assert.Equal(320, control.Width);
        Assert.Equal(180, control.Height);
        Assert.Equal(before, Design.IsDesignMode);
    }

    [AvaloniaFact]
    public void Invalid_markup_and_unsupported_roots_keep_the_last_successful_control()
    {
        using var files = new TestFiles();
        files.Write(Markup("Original"));
        using var session = new PreviewSession(new PreviewDocumentLoader(files.XamlPath), watch: false);
        session.Start();
        var original = session.Content;
        files.Write("<Border");
        Assert.False(session.Reload());
        Assert.Same(original, session.Content);
        Assert.Contains("xaml_load_failed", session.Error);
        files.Write("""<Window xmlns="https://github.com/avaloniaui" />""");
        Assert.False(session.Reload());
        Assert.Contains("unsupported_root", session.Error);
        Assert.Same(original, session.Content);
        files.Write("""
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" x:Class="Some.App" />
            """);
        Assert.False(session.Reload());
        Assert.Contains("xclass_not_supported", session.Error);
        files.Write(Markup("Corrected"));
        Assert.True(session.Reload());
        Assert.NotSame(original, session.Content);
        Assert.Null(session.Error);
    }

    [AvaloniaFact]
    public async Task Watcher_recovers_from_incomplete_writes_and_deleted_files()
    {
        using var files = new TestFiles();
        files.Write(Markup("Original"));
        using var session = new PreviewSession(new PreviewDocumentLoader(files.XamlPath),
            debounce: TimeSpan.FromMilliseconds(100));
        session.Start();
        var original = session.Content;
        files.Write("<Border");
        await WaitUntilAsync(() => session.Error is not null);
        Assert.Same(original, session.Content);
        files.Write(Markup("Recovered"));
        await WaitUntilAsync(() => session.Error is null && session.SuccessfulLoads == 2);
        Assert.Equal("Recovered", ContentText(session));
        File.Delete(files.XamlPath);
        await WaitUntilAsync(() => session.Error?.Contains("file_not_found", StringComparison.Ordinal) == true);
        Assert.Equal("Recovered", ContentText(session));
        files.Write(Markup("Recreated"));
        await WaitUntilAsync(() => session.Error is null && ContentText(session) == "Recreated");
    }

    [AvaloniaFact]
    public async Task Atomic_save_and_bursts_are_debounced_and_dispose_silences_pending_reload()
    {
        using var files = new TestFiles();
        files.Write(Markup("Original"));
        var calls = 0;
        using var watcher = new PreviewFileWatcher(files.XamlPath,
            () => calls++,
            TimeSpan.FromMilliseconds(200));
        for (var index = 0; index < 6; index++)
        {
            files.Write(Markup("Burst " + index));
            await Task.Delay(15);
        }

        await WaitUntilAsync(() => calls > 0);
        await Task.Delay(250);
        Assert.Equal(1, calls);
        var temporary = Path.Combine(files.Root, "saving.tmp");
        File.WriteAllText(temporary, Markup("Atomic"), Encoding.UTF8);
        File.Move(temporary, files.XamlPath, overwrite: true);
        await WaitUntilAsync(() => calls == 2);
        files.Write(Markup("Cancelled"));
        watcher.Dispose();
        await Task.Delay(350);
        Assert.Equal(2, calls);
    }

    [AvaloniaFact]
    public async Task No_watch_mode_requires_manual_reload_and_missing_source_is_recoverable()
    {
        using var files = new TestFiles();
        using var session = new PreviewSession(new PreviewDocumentLoader(files.XamlPath), watch: false);
        session.Start();
        Assert.Null(session.Content);
        Assert.Contains("file_not_found", session.Error);
        files.Write(Markup("Created"));
        await Task.Delay(150);
        Assert.Null(session.Content);
        Assert.False(session.WatchEnabled);
        Assert.True(session.Reload());
        Assert.Equal("Created", ContentText(session));
    }

    [AvaloniaFact]
    public void Missing_watch_directory_is_a_visible_error_instead_of_a_startup_crash()
    {
        using var files = new TestFiles();
        var missingPath = Path.Combine(files.Root, "not-created", "Preview.axaml");
        using var session = new PreviewSession(new PreviewDocumentLoader(missingPath));
        session.Start();
        Assert.False(session.WatchEnabled);
        Assert.NotNull(session.WatchError);
        Assert.NotNull(session.Error);
        Assert.Null(session.Content);
    }

    [AvaloniaFact]
    public void Optional_custom_assembly_uses_the_existing_default_context()
    {
        using var files = new TestFiles();
        files.Write("""
            <local:PreviewBadge xmlns="https://github.com/avaloniaui"
                                xmlns:local="clr-namespace:AvaloniaDevTool.Previewer.Tests" />
            """);
        using var loader = new PreviewDocumentLoader(files.XamlPath, typeof(PreviewBadge).Assembly.Location);
        var control = Assert.IsType<PreviewBadge>(loader.Load());
        Assert.Equal("Custom assembly", Assert.IsType<TextBlock>(control.Child).Text);
        Assert.Same(typeof(PreviewBadge).Assembly, control.GetType().Assembly);
    }

    [AvaloniaFact]
    public void Identical_build_copies_share_the_loaded_assembly_instead_of_loading_duplicate_types()
    {
        using var files = new TestFiles();
        files.Write("""
            <local:PreviewBadge xmlns="https://github.com/avaloniaui"
                                xmlns:local="clr-namespace:AvaloniaDevTool.Previewer.Tests" />
            """);
        var copy = Path.Combine(files.Root, "CopiedBuild.dll");
        File.Copy(typeof(PreviewBadge).Assembly.Location, copy);
        using var loader = new PreviewDocumentLoader(files.XamlPath, copy);
        Assert.Same(typeof(PreviewBadge).Assembly, Assert.IsType<PreviewBadge>(loader.Load()).GetType().Assembly);
    }

    [AvaloniaFact]
    public void Incompatible_avalonia_reference_is_rejected_before_loading_the_assembly()
    {
        using var files = new TestFiles();
        var path = Path.Combine(files.Root, "Incompatible.dll");
        var metadata = new MetadataBuilder();
        metadata.AddModule(0,
            metadata.GetOrAddString("Incompatible.dll"),
            metadata.GetOrAddGuid(Guid.NewGuid()),
            default,
            default);
        metadata.AddAssembly(metadata.GetOrAddString("Incompatible"),
            new Version(1, 0, 0, 0),
            default,
            default,
            default,
            default);
        metadata.AddAssemblyReference(metadata.GetOrAddString("Avalonia.Controls"),
            new Version(99, 0, 0, 0),
            default,
            default,
            default,
            default);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic,
            default,
            metadata.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));
        var pe = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata),
            new BlobBuilder(),
            flags: CorFlags.ILOnly);
        var content = new BlobBuilder();
        pe.Serialize(content);
        File.WriteAllBytes(path, content.ToArray());
        var error = Assert.Throws<PreviewDocumentException>(() => new PreviewDocumentLoader(files.XamlPath, path));
        Assert.Equal("avalonia_version_mismatch", error.Code);
        Assert.Contains("99.0.0.0", error.Message);
    }

    [AvaloniaFact]
    public void Oversized_source_is_rejected_and_session_disposal_releases_content()
    {
        using var files = new TestFiles();
        files.Write(new string('x', PreviewDocumentLoader.MaximumXamlBytes + 1));
        using var loader = new PreviewDocumentLoader(files.XamlPath);
        Assert.Equal("xaml_too_large", Assert.Throws<PreviewDocumentException>(() => loader.Load()).Code);
        files.Write(Markup("Loaded"));
        var session = new PreviewSession(new PreviewDocumentLoader(files.XamlPath), watch: false);
        session.Start();
        Assert.NotNull(session.Content);
        session.Dispose();
        Assert.Null(session.Content);
        Assert.Throws<ObjectDisposedException>(() => session.Reload());
    }

    [AvaloniaFact]
    public void Included_preview_sample_loads_resources_styles_bindings_and_design_dimensions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "PreviewCard.axaml");
        using var loader = new PreviewDocumentLoader(path);
        var root = Assert.IsType<UserControl>(loader.Load());
        var window = new Window { Content = root, Width = 800, Height = 600 };
        window.Show();
        try
        {
            window.UpdateLayout();
            Assert.Equal(720, root.Width);
            Assert.Equal(480, root.Height);
            Assert.Equal("独立 XAML 源文件预览", root.FindControl<TextBlock>("PreviewBoundTitle")?.Text);
            Assert.Contains(root.FindControl<Button>("PreviewButton")!.Classes, item => item == "preview-action");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Preview_window_agent_exposes_reloaded_document_through_the_existing_pipe_protocol()
    {
        using var files = new TestFiles();
        files.Write(
            """<Border xmlns="https://github.com/avaloniaui"><TextBlock Name="PreviewValue" Text="First" /></Border>""");
        using var session = new PreviewSession(new PreviewDocumentLoader(files.XamlPath),
            debounce: TimeSpan.FromMilliseconds(100));
        var window = new PreviewWindow(session, "preview-window-test");
        window.Show();
        try
        {
            Assert.Null(session.Error);
            await using var agent = AvaloniaDevTool.Agent.DiagnosticsAgent.Attach(Application.Current!,
                new AvaloniaDevTool.Agent.DiagnosticsAgentOptions
                {
                    PipeName = "preview-rpc-" + Guid.NewGuid().ToString("N"),
                    TopLevelProvider = () => [window]
                });
            await using var pipe = new System.IO.Pipes.NamedPipeClientStream(".",
                agent.PipeName,
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous);
            await pipe.ConnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await using var peer = new AvaloniaDevTool.Protocol.RpcPeer(pipe);
            peer.Start();
            var first = await FindPreviewValueAsync(peer);
            Assert.Equal("First", first.Preview);
            files.Write(
                """<Border xmlns="https://github.com/avaloniaui"><TextBlock Name="PreviewValue" Text="Second" /></Border>""");
            await WaitUntilAsync(() => session.SuccessfulLoads == 2);
            var second = await FindPreviewValueAsync(peer);
            Assert.Equal("Second", second.Preview);
            Assert.NotEqual(first.Id, second.Id);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Window_root_has_a_visible_host_notice_and_closing_the_document_keeps_the_host_open()
    {
        using var files = new TestFiles();
        var project = Path.Combine(files.Root, "Preview.csproj");
        File.WriteAllText(project, "<Project />", Encoding.UTF8);
        files.Write("""
            <Window xmlns="https://github.com/avaloniaui" Title="Detached preview target" Width="320" Height="200">
              <TextBlock Text="Window document" />
            </Window>
            """);
        using var session =
            new PreviewSession(
                new PreviewDocumentLoader(files.XamlPath, typeof(PreviewBadge).Assembly.Location, project),
                watch: false);
        var host = new PreviewWindow(session, "window-notice-test");
        host.Show();
        try
        {
            var document = Assert.IsType<Window>(session.Content);
            Assert.True(document.IsVisible);
            var notice = Assert.Single(host.GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Name == "PreviewWindowNotice");
            Assert.Equal("预览内容已在独立窗口显示：Detached preview target", notice.Text);
            Assert.True(notice.IsVisible);
            document.Close();
            Assert.True(host.IsVisible);
            Assert.Contains("已关闭", notice.Text);
            Assert.True(session.Reload());
            Assert.True(Assert.IsType<Window>(session.Content).IsVisible);
            Assert.Equal("预览内容已在独立窗口显示：Detached preview target", notice.Text);
            files.Write(Markup("Embedded source"));
            Assert.True(session.Reload());
            var viewport = Assert.Single(host.GetLogicalDescendants().OfType<ContentControl>(),
                control => control.Name == "PreviewDocumentHost");
            Assert.Same(session.Content, viewport.Content);
            Assert.DoesNotContain(host.GetLogicalDescendants(), control => ReferenceEquals(control, notice));
        }
        finally
        {
            host.Close();
        }
    }

    private static async Task<AvaloniaDevTool.Protocol.TreeNodeInfo> FindPreviewValueAsync(
        AvaloniaDevTool.Protocol.RpcPeer peer)
    {
        var roots = await peer.InvokeAsync<AvaloniaDevTool.Protocol.TreeNodeInfo[]>(
            AvaloniaDevTool.Protocol.Methods.TreeRoots,
            new AvaloniaDevTool.Protocol.TreeRootsRequest(AvaloniaDevTool.Protocol.TreeKind.Logical));
        var pending = new Queue<AvaloniaDevTool.Protocol.TreeNodeInfo>(roots);
        var visited = new HashSet<long>();
        while (pending.TryDequeue(out var node) && visited.Count < 200)
        {
            if (!visited.Add(node.Id))
                continue;
            if (node.Name == "PreviewValue")
                return node;
            if (!node.HasChildren)
                continue;
            var children = await peer.InvokeAsync<AvaloniaDevTool.Protocol.TreeNodeInfo[]>(
                AvaloniaDevTool.Protocol.Methods.TreeChildren,
                new AvaloniaDevTool.Protocol.TreeChildrenRequest(node.Id, AvaloniaDevTool.Protocol.TreeKind.Logical));
            foreach (var child in children)
                pending.Enqueue(child);
        }

        throw new InvalidOperationException("The source document was not reachable through the preview host Agent.");
    }

    private static string ContentText(PreviewSession session) =>
        Assert.IsType<TextBlock>(Assert.IsType<Border>(session.Content).Child).Text ?? "";

    private static string Markup(string text) =>
        $"""<Border xmlns="https://github.com/avaloniaui" Background="#E8EEFF" Padding="20"><TextBlock Text="{text}" /></Border>""";

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate())
            await Task.Delay(15, timeout.Token);
    }

    private sealed class TestFiles : IDisposable
    {
        private static readonly string TestRoot =
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AvaloniaDevTool.Previewer.Tests"));

        public string Root { get; } = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        public string XamlPath => Path.Combine(Root, "Preview.axaml");
        public TestFiles() => Directory.CreateDirectory(Root);
        public void Write(string content) => File.WriteAllText(XamlPath, content, new UTF8Encoding(false));

        public void Dispose()
        {
            var absolute = Path.GetFullPath(Root);
            if (!absolute.StartsWith(TestRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(absolute))
                Directory.Delete(absolute, recursive: true);
        }
    }
}

public sealed class PreviewBadge : Border
{
    public PreviewBadge() => Child = new TextBlock { Text = "Custom assembly" };
}
