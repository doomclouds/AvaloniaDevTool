using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class PropertyPresentationTests
{
    [AvaloniaFact]
    public async Task CLR_getter_failure_is_bounded_without_losing_the_RPC_session()
    {
        var probe = new FailingProbe(new InvalidOperationException(new string('错', 900_000))) { Name = "Probe" };
        await using var scope = await Scope.Create(probe);
        var properties = await scope.Client.GetPropertiesIncludingClrAsync(scope.Id);
        var failed = properties.Single(property => property.Name == nameof(FailingProbe.FailingValue));
        Assert.Equal("Unavailable", failed.ValueSource);
        Assert.NotNull(failed.Error);
        Assert.InRange(failed.Error.Length, 1, 2048);
        Assert.Contains("[truncated]", failed.Error);
        Assert.Single(await scope.Client.GetRootsAsync(TreeKind.Logical));
    }

    [AvaloniaFact]
    public async Task Image_preview_failure_is_bounded_without_losing_the_RPC_session()
    {
        var probe = new FailingProbe(new InvalidOperationException("Short getter failure"))
        {
            Name = "Probe", Photo = new FailingImage(new InvalidOperationException(new string('图', 900_000)))
        };
        await using var scope = await Scope.Create(probe);
        var photo = (await scope.Client.GetPropertiesIncludingClrAsync(scope.Id)).Single(property => property.Name == nameof(FailingProbe.Photo));
        var preview = await scope.Client.GetPropertyPreviewAsync(Request(photo, scope.Id));
        Assert.Equal(PropertyPreviewStatus.NotAvailable, preview.Status);
        Assert.NotNull(preview.Message);
        Assert.InRange(preview.Message.Length, 1, 2048);
        Assert.Contains("[truncated]", preview.Message);
        Assert.Single(await scope.Client.GetRootsAsync(TreeKind.Logical));
    }

    [AvaloniaFact]
    public async Task Property_failures_do_not_execute_custom_exception_message_or_base_exception_overrides()
    {
        var failure = new UnsafeException();
        var probe = new FailingProbe(failure) { Name = "Probe", Photo = new FailingImage(failure) };
        await using var scope = await Scope.Create(probe);
        var properties = await scope.Client.GetPropertiesIncludingClrAsync(scope.Id);
        Assert.Contains("override unavailable", properties.Single(property => property.Name == nameof(FailingProbe.FailingValue)).Error);
        var preview = await scope.Client.GetPropertyPreviewAsync(Request(properties.Single(property => property.Name == nameof(FailingProbe.Photo)), scope.Id));
        Assert.Equal(PropertyPreviewStatus.NotAvailable, preview.Status);
        Assert.Contains("override unavailable", preview.Message);
        Assert.Equal(0, failure.MessageCalls);
        Assert.Equal(0, failure.BaseExceptionCalls);
        Assert.Single(await scope.Client.GetRootsAsync(TreeKind.Logical));
    }

    [AvaloniaFact]
    public async Task Aggregate_and_deep_inner_exception_failures_have_bounded_explicit_summaries()
    {
        var child = new UnsafeException();
        var aggregate = new AggregateException(Enumerable.Repeat<Exception>(child, 64));
        Exception deep = new InvalidOperationException("Bottom");
        for (var depth = 0; depth < 32; depth++) deep = new InvalidOperationException("Wrapper", deep);
        var probe = new FailingProbe(deep) { Name = "Probe", Photo = new FailingImage(aggregate) };
        await using var scope = await Scope.Create(probe);
        var properties = await scope.Client.GetPropertiesIncludingClrAsync(scope.Id);
        Assert.Contains("[truncated]", properties.Single(property => property.Name == nameof(FailingProbe.FailingValue)).Error);
        var preview = await scope.Client.GetPropertyPreviewAsync(Request(properties.Single(property => property.Name == nameof(FailingProbe.Photo)), scope.Id));
        Assert.Equal(PropertyPreviewStatus.NotAvailable, preview.Status);
        Assert.Contains("AggregateException", preview.Message);
        Assert.Contains("64", preview.Message);
        Assert.InRange(preview.Message!.Length, 1, 2048);
        Assert.Equal(0, child.MessageCalls);
        Assert.Single(await scope.Client.GetRootsAsync(TreeKind.Logical));
    }

    [AvaloniaFact]
    public async Task Default_property_request_does_not_execute_CLR_getters()
    {
        var probe = new PresentationProbe { Name = "Probe" };
        await using var scope = await Scope.Create(probe);
        var ordinary = await scope.Client.GetPropertiesAsync(scope.Id);
        Assert.Equal(0, probe.GetterCalls);
        Assert.DoesNotContain(ordinary, property => property.Presentation?.MemberKind == "CLR");
        var extended = await scope.Client.GetPropertiesIncludingClrAsync(scope.Id);
        Assert.Equal(1, probe.GetterCalls);
        Assert.Contains(extended, property => property.Name == nameof(PresentationProbe.ExplicitGetter) && property.Value == "Visible on request");
    }

    [AvaloniaFact]
    public async Task Unified_properties_keep_hidden_CLR_members_and_safely_describe_opaque_values()
    {
        var probe = new PresentationProbe { Name = "Probe" };
        await using var scope = await Scope.Create(probe);
        var properties = await scope.Client.GetPropertiesIncludingClrAsync(scope.Id);
        var widths = properties.Where(property => property.Name == "Width").ToArray();
        Assert.Equal(2, widths.Length);
        Assert.Contains(widths, property => property.Presentation?.MemberKind == "CLR" && property.Value == "A hidden CLR value");
        Assert.Contains(widths, property => property.Presentation?.MemberKind == "Avalonia");
        Assert.Single(properties, property => property.Name == "Bounds");
        var note = Assert.Single(properties, property => property.Name == nameof(PresentationProbe.Note));
        Assert.Equal("Probe category", note.Presentation?.Category);
        Assert.True(note.IsReadOnly);
        Assert.False(note.CanEdit);
        Assert.Contains(properties, property => property.Name == nameof(PresentationProbe.Fault) && property.Error == "Getter unavailable");
        Assert.Contains(properties, property => property.Name == nameof(PresentationProbe.Opaque) && property.Value!.Contains(nameof(ToStringBomb)));
        Assert.Equal(0, probe.Opaque.ToStringCalls);
        Assert.DoesNotContain(properties, property => property.Name == "Item");
        await Assert.ThrowsAsync<RpcException>(() => scope.Client.SetPropertyAsync(scope.Id, note.Id, "Forbidden"));
        Assert.Equal("Read only here", probe.Note);
    }

    [AvaloniaFact]
    public async Task Editor_metadata_distinguishes_color_flags_and_unsupported_brushes()
    {
        var probe = new PresentationProbe { Name = "Probe", Background = new ImageBrush() };
        await using var scope = await Scope.Create(probe);
        var properties = await scope.Client.GetPropertiesIncludingClrAsync(scope.Id);
        Assert.Equal(PropertyEditorKind.Color, properties.Single(property => property.Name == nameof(PresentationProbe.Tint)).Presentation?.Editor);
        var flags = properties.Single(property => property.Name == nameof(PresentationProbe.Options));
        Assert.Equal(PropertyEditorKind.Flags, flags.Presentation?.Editor);
        Assert.Contains(flags.Presentation!.Choices!, choice => choice.Name == nameof(ProbeOptions.Second) && choice.Value == "2");
        var brush = properties.Single(property => property.Name == "Background");
        Assert.Equal(PropertyEditorKind.ReadOnly, brush.Presentation?.Editor);
        Assert.False(brush.CanEdit);
        Assert.NotEmpty(brush.Presentation!.Notice!);
        await Assert.ThrowsAsync<RpcException>(() => scope.Client.SetPropertyAsync(scope.Id, brush.Id, "Red"));
        Assert.IsType<ImageBrush>(probe.Background);
    }

    [AvaloniaFact]
    public async Task Property_preview_draws_actual_image_and_geometry_pixels_and_rejects_stale_handles()
    {
        using var source = new RenderTargetBitmap(new PixelSize(40, 20));
        using (var context = source.CreateDrawingContext()) context.FillRectangle(Brushes.Red, new Rect(0, 0, 40, 20));
        var probe = new PresentationProbe { Name = "Probe", Photo = source, Shape = new RectangleGeometry(new Rect(10, 20, 30, 40)) };
        await using var scope = await Scope.Create(probe);
        var properties = await scope.Client.GetPropertiesIncludingClrAsync(scope.Id);
        var photo = properties.Single(property => property.Name == nameof(PresentationProbe.Photo));
        var shape = properties.Single(property => property.Name == nameof(PresentationProbe.Shape));
        Assert.Equal(PropertyPreviewKind.Image, photo.Presentation?.Preview);
        var image = await scope.Client.GetPropertyPreviewAsync(Request(photo, scope.Id, 16, 16));
        Assert.Equal(PropertyPreviewStatus.Available, image.Status);
        Assert.Equal(16, image.Width); Assert.Equal(8, image.Height);
        var imagePixel = Pixel(image);
        Assert.Equal(255, imagePixel[3]);
        Assert.True(imagePixel[0] == 255 || imagePixel[2] == 255);
        Assert.Equal(0, imagePixel[1]);
        var geometry = await scope.Client.GetPropertyPreviewAsync(Request(shape, scope.Id, 30, 40));
        Assert.Equal(PropertyPreviewStatus.Available, geometry.Status);
        Assert.Equal(30, geometry.Width); Assert.Equal(40, geometry.Height);
        var geometryPixel = Pixel(geometry);
        Assert.Equal(255, geometryPixel[3]);
        Assert.Equal(144, geometryPixel[1]);
        Assert.Same(source, probe.Photo);
        Assert.Equal(new Rect(10, 20, 30, 40), probe.Shape.Bounds);
        probe.Photo = new DrawingImage();
        Assert.Equal(PropertyPreviewStatus.Stale, (await scope.Client.GetPropertyPreviewAsync(Request(photo, scope.Id))).Status);
        Assert.Equal(PropertyPreviewStatus.InvalidValue, (await scope.Client.GetPropertyPreviewAsync(Request(shape, scope.Id, 900, 16))).Status);
        _ = await scope.Client.GetPropertiesIncludingClrAsync(scope.Id);
        Assert.Equal(PropertyPreviewStatus.Stale, (await scope.Client.GetPropertyPreviewAsync(Request(shape, scope.Id))).Status);
    }

    [AvaloniaFact]
    public async Task Empty_image_reports_not_available()
    {
        var probe = new PresentationProbe { Name = "Probe", Photo = new DrawingImage() };
        await using var scope = await Scope.Create(probe);
        var photo = (await scope.Client.GetPropertiesIncludingClrAsync(scope.Id)).Single(property => property.Name == nameof(PresentationProbe.Photo));
        var result = await scope.Client.GetPropertyPreviewAsync(Request(photo, scope.Id));
        Assert.Equal(PropertyPreviewStatus.NotAvailable, result.Status);
        Assert.Null(result.Base64);
        Assert.NotEmpty(result.Message!);
    }

    [AvaloniaFact]
    public async Task Property_preview_cache_does_not_root_replaced_values()
    {
        var probe = new PresentationProbe { Name = "Probe" };
        var weak = AssignImage(probe);
        await using var scope = await Scope.Create(probe);
        var photo = (await scope.Client.GetPropertiesIncludingClrAsync(scope.Id)).Single(property => property.Name == nameof(PresentationProbe.Photo));
        probe.Photo = new DrawingImage();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(weak.TryGetTarget(out _));
        Assert.Equal(PropertyPreviewStatus.Stale, (await scope.Client.GetPropertyPreviewAsync(Request(photo, scope.Id))).Status);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<IImage> AssignImage(PresentationProbe probe)
    {
        probe.Photo = new DrawingImage();
        return new WeakReference<IImage>(probe.Photo);
    }

    private static PropertyPreviewRequest Request(PropertyInfoDto property, long id, int width = 320, int height = 240) =>
        new(id, property.Id, property.Presentation!.ValueId!.Value, property.Presentation.Version, width, height);

    private static byte[] Pixel(PropertyPreviewResult result)
    {
        using var stream = new MemoryStream(Convert.FromBase64String(result.Base64!));
        using var image = new Bitmap(stream);
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            image.CopyPixels(new PixelRect(0, 0, 1, 1), buffer, 4, 4);
            var bytes = new byte[4]; Marshal.Copy(buffer, bytes, 0, 4); return bytes;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [Flags] private enum ProbeOptions { None = 0, First = 1, Second = 2 }
    private sealed class ToStringBomb
    {
        public int ToStringCalls { get; private set; }
        public override string ToString() { ToStringCalls++; throw new InvalidOperationException("Do not call arbitrary ToString"); }
    }
    private sealed class PresentationProbe : Border
    {
        public static readonly StyledProperty<Color> TintProperty = AvaloniaProperty.Register<PresentationProbe, Color>(nameof(Tint), Colors.Blue);
        public static readonly StyledProperty<ProbeOptions> OptionsProperty = AvaloniaProperty.Register<PresentationProbe, ProbeOptions>(nameof(Options));
        public Color Tint { get => GetValue(TintProperty); set => SetValue(TintProperty, value); }
        public ProbeOptions Options { get => GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }
        public new string Width => "A hidden CLR value";
        [Category("Probe category")] public string Note { get; set; } = "Read only here";
        public string Fault => throw new InvalidOperationException("Getter unavailable");
        public ToStringBomb Opaque { get; } = new();
        public string this[int index] => index.ToString();
        public IImage Photo { get; set; } = new DrawingImage();
        public Geometry Shape { get; set; } = new RectangleGeometry(new Rect(0, 0, 20, 10));
        public int GetterCalls { get; private set; }
        public string ExplicitGetter { get { GetterCalls++; return "Visible on request"; } }
    }
    private sealed class FailingProbe(Exception failure) : Border
    {
        public string FailingValue => throw failure;
        public IImage? Photo { get; set; }
    }
    private sealed class FailingImage(Exception failure) : IImage
    {
        public Size Size => throw failure;
        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect) => throw failure;
    }
    private sealed class UnsafeException : Exception
    {
        public int MessageCalls { get; private set; }
        public int BaseExceptionCalls { get; private set; }
        public override string Message { get { MessageCalls++; return "The custom Message getter was executed."; } }
        public override Exception GetBaseException() { BaseExceptionCalls++; return this; }
    }
    private sealed class Scope(Window window, DiagnosticsAgent agent, DiagnosticsClient client, long id) : IAsyncDisposable
    {
        public DiagnosticsClient Client => client;
        public long Id => id;
        public static async Task<Scope> Create(Control content)
        {
            var window = new Window { Content = content, Width = 160, Height = 120 }; window.Show();
            var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "avdt-property-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });
            var client = new DiagnosticsClient(); await client.ConnectAsync(agent.PipeName);
            var root = Assert.Single(await client.GetRootsAsync(TreeKind.Logical));
            var node = (await client.GetChildrenAsync(root.Id, TreeKind.Logical)).Single(child => child.Name == content.Name);
            return new Scope(window, agent, client, node.Id);
        }
        public async ValueTask DisposeAsync() { await client.DisposeAsync(); await agent.DisposeAsync(); window.Close(); }
    }
}
