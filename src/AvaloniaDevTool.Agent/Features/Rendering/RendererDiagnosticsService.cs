using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Rendering;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Owns additive renderer flags and restores only values that the application has not changed.</summary>
internal sealed class RendererDiagnosticsService(WeakObjectRegistry objects) : IDisposable
{
    internal const RendererOverlayFlags SupportedFlags = RendererOverlayFlags.Fps | RendererOverlayFlags.DirtyRects |
                                                         RendererOverlayFlags.LayoutTimeGraph |
                                                         RendererOverlayFlags.RenderTimeGraph;

    private readonly Dictionary<TopLevel, Entry> _roots = [];
    private readonly List<string> _conflicts = [];
    private RendererOverlayFlags _futureFlags;
    private bool _followNewWindows;
    private bool _disposed;

    public void RefreshRoots(IReadOnlyList<TopLevel> roots)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;
        foreach (var removed in _roots.Keys.Except(roots).ToArray())
        {
            Restore(_roots[removed]);
            _roots[removed].Detach();
            _roots.Remove(removed);
        }

        foreach (var root in roots)
        {
            if (_roots.ContainsKey(root))
                continue;
            var entry = new Entry(root, objects.GetId(root), OnApplicationChanged);
            _roots.Add(root, entry);
            if (_followNewWindows)
                Apply(entry, _futureFlags);
        }
    }

    public RendererDiagnosticsStatusDto Set(RendererDiagnosticsSetRequest request)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((request.Flags & ~SupportedFlags) != 0 || request.WindowId is not null && request.FollowNewWindows)
            throw new RpcException("invalid_request",
                "Unknown renderer flags, or future-window tracking with a single window.");
        var entries = request.WindowId is { } id
            ? _roots.Values.Where(entry => entry.Id == id).ToArray()
            : _roots.Values.ToArray();
        if (request.WindowId is not null && entries.Length == 0)
            throw new RpcException("window_not_found", "The selected window is no longer a current presentation root.");
        if (request.WindowId is null)
        {
            _futureFlags = request.Flags;
            _followNewWindows = request.FollowNewWindows && request.Flags != RendererOverlayFlags.None;
        }

        foreach (var entry in entries)
            Apply(entry, request.Flags);
        return Status();
    }

    public RendererDiagnosticsStatusDto Status()
    {
        Dispatcher.UIThread.VerifyAccess();
        return new RendererDiagnosticsStatusDto(_followNewWindows || _roots.Values.Any(entry => entry.Owned),
            _followNewWindows,
            SupportedFlags,
            _roots.Values.Select(entry => new RendererWindowDiagnosticsDto(
                entry.Id,
                entry.Root is Window window ? window.Title ?? "Window" : entry.Root.GetType().Name,
                (RendererOverlayFlags)entry.Diagnostics.DebugOverlays,
                entry.Owned ? (RendererOverlayFlags)(entry.Expected & ~entry.Original) : RendererOverlayFlags.None,
                entry.Owned,
                entry.Conflict)).ToArray(),
            _conflicts.ToArray());
    }

    public RendererDiagnosticsStatusDto Stop()
    {
        Dispatcher.UIThread.VerifyAccess();
        _followNewWindows = false;
        _futureFlags = RendererOverlayFlags.None;
        foreach (var entry in _roots.Values)
            Restore(entry);
        return Status();
    }

    private static void Apply(Entry entry, RendererOverlayFlags flags)
    {
        if (flags == RendererOverlayFlags.None)
        {
            Restore(entry);
            return;
        }

        if (!entry.Owned)
            entry.Original = entry.Diagnostics.DebugOverlays;
        entry.Expected = entry.Original | (RendererDebugOverlays)flags;
        entry.Owned = true;
        entry.Write(entry.Expected);
    }

    private static void Restore(Entry entry)
    {
        if (!entry.Owned)
            return;
        // The property observer relinquishes ownership on any application write.
        if (entry.Diagnostics.DebugOverlays == entry.Expected)
            entry.Write(entry.Original);
        entry.Owned = false;
    }

    private void OnApplicationChanged(Entry entry)
    {
        if (!entry.Owned)
            return;
        entry.Owned = false;
        entry.Conflict = true;
        if (_conflicts.Count == 64)
            _conflicts.RemoveAt(0);
        _conflicts.Add($"Window #{entry.Id}: application changed DebugOverlays; its later value is preserved.");
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;
        Stop();
        foreach (var entry in _roots.Values)
            entry.Detach();
        _roots.Clear();
        _disposed = true;
    }

    private sealed class Entry
    {
        private readonly Action<Entry> _changed;
        private bool _writing;
        private bool _changedDuringWrite;
        private RendererDebugOverlays _writeValue;

        public Entry(TopLevel root, long id, Action<Entry> changed)
        {
            Root = root;
            Id = id;
            _changed = changed;
            Diagnostics = root.RendererDiagnostics;
            Diagnostics.PropertyChanged += OnChanged;
        }

        public TopLevel Root { get; }
        public long Id { get; }
        public RendererDiagnostics Diagnostics { get; }
        public RendererDebugOverlays Original { get; set; }
        public RendererDebugOverlays Expected { get; set; }
        public bool Owned { get; set; }
        public bool Conflict { get; set; }

        public void Write(RendererDebugOverlays flags)
        {
            _writing = true;
            _changedDuringWrite = false;
            _writeValue = flags;
            try
            {
                Diagnostics.DebugOverlays = flags;
            }
            finally
            {
                _writing = false;
            }

            if (_changedDuringWrite || Diagnostics.DebugOverlays != flags)
                _changed(this);
        }

        public void Detach() => Diagnostics.PropertyChanged -= OnChanged;

        private void OnChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(RendererDiagnostics.DebugOverlays))
                return;
            if (!_writing)
                _changed(this);
            else if (Diagnostics.DebugOverlays != _writeValue)
                _changedDuringWrite = true;
        }
    }
}
