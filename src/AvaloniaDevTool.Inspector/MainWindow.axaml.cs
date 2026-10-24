using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainWindowViewModel(Program.InitialPipeName, Program.Options.CreateBackend(), Program.Options.ReadRemoteToken());
        DataContext = ViewModel;
        DataContextChanged += (_, _) => AttachTreeClipboard();
        AttachTreeClipboard();
        Opened += OnFirstOpened;
        Closed += (_, _) =>
        {
            if (DataContext is MainWindowViewModel current) current.CopyTreeTextAsync = null;
            ViewModel.CopyTreeTextAsync = null;
            _ = ViewModel.DisposeAsync().AsTask();
        };
    }

    public MainWindowViewModel ViewModel { get; }

    private void AttachTreeClipboard()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.CopyTreeTextAsync = async text =>
        {
            var clipboard = GetTopLevel(this)?.Clipboard ?? throw new RpcException("unsupported_clipboard", "当前窗口没有可用剪贴板。");
            await clipboard.SetTextAsync(text);
        };
    }
    private void OnTreeRowPointerEntered(object? sender, PointerEventArgs args)
    {
        if (DataContext is MainWindowViewModel vm && sender is Control { DataContext: TreeNodeViewModel { IsPlaceholder: false, IsSynthetic: false } node })
            _ = vm.PreviewTreeHighlightAsync(node.Id);
    }
    private void OnTreeRowPointerExited(object? sender, PointerEventArgs args)
    {
        if (DataContext is MainWindowViewModel vm && sender is Control { DataContext: TreeNodeViewModel node })
            _ = vm.PreviewTreeHighlightAsync(null, node.Id);
    }

    private void OnFirstOpened(object? sender, EventArgs args)
    {
        Opened -= OnFirstOpened;
        FitInitialWorkingArea();
        _ = ViewModel.InitializeAsync();
    }

    private void FitInitialWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this);
        var scaling = RenderScaling;
        if (screen is null || screen.WorkingArea.Width <= 0 || screen.WorkingArea.Height <= 0 ||
            !double.IsFinite(scaling) || scaling <= 0) return;
        var frame = FrameSize;
        var extraWidth = frame is { } size ? Math.Max(0, size.Width - ClientSize.Width) : 0;
        var extraHeight = frame is { } frameSize ? Math.Max(0, frameSize.Height - ClientSize.Height) : 0;
        var width = screen.WorkingArea.Width / scaling - extraWidth;
        var height = screen.WorkingArea.Height / scaling - extraHeight;
        // Only clamp the initial window. Respect user resize and the existing minimum dimensions.
        if (width >= MinWidth && Width > width) Width = width;
        if (height >= MinHeight && Height > height) Height = height;
        // Windows may cascade a new window beyond the bottom/right edge even when its size fits.
        var outerWidth = (int)Math.Ceiling((Width + extraWidth) * scaling);
        var outerHeight = (int)Math.Ceiling((Height + extraHeight) * scaling);
        var area = screen.WorkingArea;
        var position = new Avalonia.PixelPoint(
            Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - outerWidth)),
            Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - outerHeight)));
        if (position != Position) Position = position;
    }
}

