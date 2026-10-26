using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class BrushEditorRpcTests
{
    [AvaloniaFact]
    public async Task Gradient_RPC_changes_real_pixels_and_clear_restores_latest_binding_without_mutating_original()
    {
        var original = new LinearGradientBrush
        {
            StartPoint = RelativePoint.TopLeft, EndPoint = RelativePoint.Parse("100%, 0%"),
            GradientStops = [new GradientStop(Colors.Red, 0), new GradientStop(Colors.Blue, 1)]
        };
        var model = new BrushModel { Value = original };
        var target = new Border { Name = "BrushTarget" };
        target.Bind(Border.BackgroundProperty, new Binding(nameof(BrushModel.Value)) { Source = model });
        await using var scope = await Scope.Create(target);
        var property = await scope.Property();
        Assert.Equal(PropertyEditorKind.Brush, property.Presentation!.Editor);
        var before = Pixel(target.Background!);
        var green = property.Presentation.Brush! with { Stops = [new("Lime", 0), new("Lime", 1)] };
        await scope.Client.SetBrushAsync(new(scope.Id, property.Id, green));
        var after = Pixel(target.Background!);
        Assert.True(after[1] > 240 && before[1] < 10);
        Assert.NotSame(original, target.Background);
        Assert.Equal(Colors.Red, original.GradientStops[0].Color);
        Assert.Equal(Colors.Blue, original.GradientStops[1].Color);
        var latest = new SolidColorBrush(Colors.Yellow);
        model.Value = latest;
        Assert.IsType<LinearGradientBrush>(target.Background);
        await scope.Client.ClearPropertyAsync(scope.Id, property.Id);
        Assert.Same(latest, target.Background);
        await scope.Client.SetBrushAsync(new(scope.Id, property.Id, green));
        var newest = new SolidColorBrush(Colors.Cyan);
        model.Value = newest;
        await scope.Client.DisposeAsync();
        await Until(() => ReferenceEquals(newest, target.Background));
    }

    [AvaloniaFact]
    public async Task All_four_brush_kinds_round_trip_real_fields_and_create_independent_objects()
    {
        var target = new Border { Name = "BrushTarget", Background = Brushes.White };
        await using var scope = await Scope.Create(target);
        var id = (await scope.Property()).Id;
        foreach (var kind in Enum.GetValues<BrushKind>())
        {
            var draft = new BrushDto(kind,
                0.7,
                "#FF123456",
                [new("Red", 0), new("Blue", 1)],
                "Reflect",
                "10%, 20%",
                "90%, 80%",
                "40%, 45%",
                "35%, 30%",
                "70%",
                "35%",
                67,
                "1,0,0,1,2,3",
                "50%, 50%");
            await scope.Client.SetBrushAsync(new(scope.Id, id, draft));
            var read = (await scope.Property()).Presentation!.Brush!;
            Assert.Equal(kind, read.Kind);
            Assert.Equal(0.7, read.Opacity);
            Assert.InRange(Pixel(target.Background!)[3], 170, 190);
            Assert.Equal(RelativePoint.Center, target.Background!.TransformOrigin);
            Assert.Equal(Matrix.Parse("1,0,0,1,2,3"), target.Background.Transform!.Value);
            if (kind == BrushKind.Solid)
                Assert.Equal(Color.Parse("#FF123456"),
                    Assert.IsAssignableFrom<ISolidColorBrush>(target.Background).Color);
            if (kind == BrushKind.Linear)
            {
                var linear = Assert.IsType<LinearGradientBrush>(target.Background);
                Assert.Equal(RelativePoint.Parse(draft.StartPoint), linear.StartPoint);
                Assert.Equal(RelativePoint.Parse(draft.EndPoint), linear.EndPoint);
            }

            if (kind == BrushKind.Radial)
            {
                var radial = Assert.IsType<RadialGradientBrush>(target.Background);
                Assert.Equal(RelativePoint.Parse(draft.Center), radial.Center);
                Assert.Equal(RelativePoint.Parse(draft.GradientOrigin), radial.GradientOrigin);
                Assert.Equal(RelativeScalar.Parse(draft.RadiusX), radial.RadiusX);
                Assert.Equal(RelativeScalar.Parse(draft.RadiusY), radial.RadiusY);
            }

            if (kind == BrushKind.Conic)
            {
                var conic = Assert.IsType<ConicGradientBrush>(target.Background);
                Assert.Equal(67, conic.Angle);
                Assert.Equal(RelativePoint.Parse(draft.Center), conic.Center);
            }

            if (kind != BrushKind.Solid)
            {
                Assert.Equal("Reflect", read.SpreadMethod);
                Assert.Equal(2, read.Stops!.Count);
                Assert.Equal(Colors.Red.ToString(), read.Stops[0].Color);
                Assert.Equal(1, read.Stops[1].Offset);
            }
        }
    }

    [AvaloniaFact]
    public async Task Invalid_gradient_data_and_opaque_brushes_are_rejected_without_replacing_current_value()
    {
        var original = new LinearGradientBrush();
        var target = new Border { Name = "BrushTarget", Background = original };
        await using var scope = await Scope.Create(target);
        var property = await scope.Property();
        foreach (var invalid in new[]
                 {
                     new BrushDto(BrushKind.Linear, Stops: [new("Red", -1), new("Blue", 1)]),
                     new BrushDto(BrushKind.Linear, Stops: [new("Red", 0)]),
                     new BrushDto(BrushKind.Linear, Stops: [new("bad color", 0), new("Blue", 1)]),
                     new BrushDto(BrushKind.Radial, Stops: [new("Red", 0), new("Blue", 1)], RadiusX: "-1"),
                     new BrushDto(BrushKind.Conic, Stops: [new("Red", 0), new("Blue", 1)], Center: "NaN,0"),
                     new BrushDto(BrushKind.Solid, Opacity: 5)
                 })
        {
            var error = await Assert.ThrowsAsync<RpcException>(() =>
                scope.Client.SetBrushAsync(new(scope.Id, property.Id, invalid)));
            Assert.Equal("conversion_failed", error.Code);
            Assert.Same(original, target.Background);
        }

        target.Background = new ImageBrush();
        Assert.False((await scope.Property()).CanEdit);
        Assert.Equal("unsupported_type",
            (await Assert.ThrowsAsync<RpcException>(() =>
                scope.Client.SetBrushAsync(new(scope.Id, property.Id, new(BrushKind.Solid))))).Code);
        Assert.Single(await scope.Client.GetRootsAsync(TreeKind.Logical));
    }

    private static byte[] Pixel(IBrush brush)
    {
        using var image = new RenderTargetBitmap(new PixelSize(24, 12));
        using (var context = image.CreateDrawingContext())
            context.FillRectangle(brush, new Rect(0, 0, 24, 12));
        var pointer = Marshal.AllocHGlobal(4);
        try
        {
            image.CopyPixels(new PixelRect(12, 6, 1, 1), pointer, 4, 4);
            var bytes = new byte[4];
            Marshal.Copy(pointer, bytes, 0, 4);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate())
            await Task.Delay(15, timeout.Token);
    }

    private sealed class BrushModel : INotifyPropertyChanged
    {
        private IBrush? _value;

        public IBrush? Value
        {
            get => _value;
            set
            {
                _value = value;
                PropertyChanged?.Invoke(this, new(nameof(Value)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class Scope(Window window, DiagnosticsAgent agent, DiagnosticsClient client, long id)
        : IAsyncDisposable
    {
        public DiagnosticsClient Client => client;
        public long Id => id;

        public async Task<PropertyInfoDto> Property() =>
            (await client.GetPropertiesAsync(id)).Single(property => property.Name == "Background");

        public static async Task<Scope> Create(Border target)
        {
            var window = new Window { Content = target, Width = 160, Height = 120 };
            window.Show();
            var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                    { PipeName = "avdt-brush-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });
            var client = new DiagnosticsClient();
            await client.ConnectAsync(agent.PipeName);
            var root = Assert.Single(await client.GetRootsAsync(TreeKind.Logical));
            var node = (await client.GetChildrenAsync(root.Id, TreeKind.Logical)).Single(child =>
                child.Name == target.Name);
            return new Scope(window, agent, client, node.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();
            await agent.DisposeAsync();
            window.Close();
        }
    }
}
