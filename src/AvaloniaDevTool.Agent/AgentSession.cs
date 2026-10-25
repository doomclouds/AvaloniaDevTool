using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession : IDisposable
{
    private readonly Application _application;
    private readonly DiagnosticsAgentOptions _options;
    private readonly WeakObjectRegistry _objects = new();
    private readonly MergedTree _mergedTree = new();
    private readonly Dictionary<long, WeakReference<AvaloniaObject>> _observed = [];
    private readonly ConditionalWeakTable<AvaloniaObject, Dictionary<AvaloniaProperty, IDisposable>> _overrides = new();
    private readonly HashSet<long> _pendingProperties = [];
    private readonly Dictionary<TopLevel, RootHandlers> _rootHandlers = [];
    private readonly DispatcherTimer _timer;
    private readonly SessionInfo _info;
    private readonly ResourceInspectionService _resources;
    private readonly ActivityService _activity;
    private readonly MetricsService _metrics;
    private readonly ProfilerService _profiler;
    private readonly BreakpointService _breakpoints;
    private readonly LayoutInspectionService _layout;
    private readonly CompositionInspectionService _composition;
    private readonly RuntimeInspectionService _runtime;
    private readonly InputInspectionService _input;
    private readonly AssetExportService _assetExports = new();
    private RpcPeer? _peer;
    private string _rootsSnapshot = "";
    private readonly OverlayService _overlay;
    private readonly RendererDiagnosticsService _rendererDiagnostics;
    private readonly PopupFreezeService _popupFreeze;
    private bool _picking;
    private bool _followFocus;
    private bool _disposed;

    public AgentSession(Application application, DiagnosticsAgentOptions options)
    {
        _application = application;
        _options = options;
        _resources = new ResourceInspectionService(application, _objects);
        _activity = new ActivityService(_objects, Notify, options.LogSources, NotifyActivityAsync);
        _metrics = new MetricsService(Notify);
        _profiler = new ProfilerService(Notify);
        _breakpoints = new BreakpointService(_objects, Notify);
        _layout = new LayoutInspectionService(_objects, Observe);
        _composition = new CompositionInspectionService(_objects, Observe);
        _runtime = new RuntimeInspectionService(_objects);
        _input = new InputInspectionService(_objects);
        _overlay = new OverlayService(_objects, Observe);
        _rendererDiagnostics = new RendererDiagnosticsService(_objects);
        _popupFreeze = new PopupFreezeService(_objects);
        _info = new SessionInfo(Guid.NewGuid().ToString("N"), options.ApplicationName, Environment.ProcessId,
            typeof(Application).Assembly.GetName().Version?.ToString() ?? "unknown", DiagnosticsProtocol.Version);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += OnTick;
    }

    public void Connect(RpcPeer peer)
    {
        _peer = peer;
        _rootsSnapshot = Fingerprint(RefreshRoots());
        _timer.Start();
    }

    public object Handle(string method, JsonElement parameters)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return method switch
        {
            Methods.SessionInfo => _info with { ContentGeneration = _options.ContentGenerationProvider?.Invoke() ?? 0 },
            PreviewMethods.ContentGeneration => _options.ContentGenerationProvider?.Invoke() ?? 0,
            Methods.TreeRoots => GetRoots(ProtocolJson.Read<TreeRootsRequest>(parameters).Kind),
            Methods.TreeChildren => GetChildren(ProtocolJson.Read<TreeChildrenRequest>(parameters)),
            TreePagingMethods.Children => GetChildrenPage(ProtocolJson.Read<TreeChildrenPageRequest>(parameters)),
            TreePagingMethods.TemplateParentSelector => GetTemplateParentSelector(ProtocolJson.Read<ObjectRequest>(parameters).ObjectId),
            TreeWorkflowMethods.Observe => ObserveTree(ProtocolJson.Read<TreeObserveRequest>(parameters)),
            TreeWorkflowMethods.Release => ReleaseTree(ProtocolJson.Read<TreeReleaseRequest>(parameters)),
            TreeWorkflowMethods.Selector => GetElementSelector(ProtocolJson.Read<ObjectRequest>(parameters).ObjectId),
            TreeWorkflowMethods.Metadata => GetElementMetadata(ProtocolJson.Read<ObjectRequest>(parameters).ObjectId),
            Methods.PropertiesGet => GetProperties(ProtocolJson.Read<PropertiesRequest>(parameters)),
            PropertyPresentationMethods.Preview => GetPropertyPreview(ProtocolJson.Read<PropertyPreviewRequest>(parameters)),
            Methods.PropertiesSet => SetProperty(ProtocolJson.Read<SetPropertyRequest>(parameters)),
            OfficialPropertyMethods.Set => SetOfficialProperty(ProtocolJson.Read<OfficialSetPropertyRequest>(parameters)),
            OfficialPropertyMethods.Styles => GetOfficialStyles(ProtocolJson.Read<OfficialStylesRequest>(parameters)),
            BrushMethods.Set => SetBrush(ProtocolJson.Read<SetBrushRequest>(parameters)),
            Methods.PropertiesClear => ClearProperty(ProtocolJson.Read<ClearPropertyRequest>(parameters)),
            Methods.Highlight => SetHighlight(ProtocolJson.Read<HighlightRequest>(parameters).ObjectId),
            OverlayMethods.Configure => _overlay.Configure(ProtocolJson.Read<OverlayConfigureRequest>(parameters)),
            OverlayMethods.Snapshot => _overlay.Snapshot(ProtocolJson.Read<OverlaySnapshotRequest>(parameters)),
            RendererDiagnosticsMethods.Status => _rendererDiagnostics.Status(),
            RendererDiagnosticsMethods.Set => _rendererDiagnostics.Set(ProtocolJson.Read<RendererDiagnosticsSetRequest>(parameters)),
            RendererDiagnosticsMethods.Stop => _rendererDiagnostics.Stop(),
            PopupFreezeMethods.Status => _popupFreeze.Status(),
            PopupFreezeMethods.Set => _popupFreeze.Set(ProtocolJson.Read<PopupFreezeSetRequest>(parameters)),
            PopupFreezeMethods.Resume => _popupFreeze.Resume(),
            EventRouteMethods.Catalog => _activity.GetEventCatalog(),
            EventRouteMethods.Snapshot => _activity.EventRoutesSnapshot(),
            EventRouteMethods.Status => _activity.EventRoutesStatus(),
            Methods.SetPicking => SetPicking(ProtocolJson.Read<PickingRequest>(parameters).Enabled),
            ExplorationMethods.FollowFocus => SetFollowFocus(ProtocolJson.Read<FollowFocusRequest>(parameters).Enabled),
            InspectionMethods.Styles => Avalonia12DiagnosticsAdapter.Inspect(GetAvaloniaObject(ProtocolJson.Read<ObjectRequest>(parameters).ObjectId), _objects.GetId),
            InspectionMethods.Resources => _resources.GetTrees(ProtocolJson.Read<ResourcesRequest>(parameters)),
            InspectionMethods.ResourceEntries => _resources.GetEntries(ProtocolJson.Read<ResourceEntriesRequest>(parameters).DictionaryId),
            InspectionMethods.SetResource => _resources.Set(ProtocolJson.Read<SetResourceRequest>(parameters)),
            InspectionMethods.ResetResource => _resources.Reset(ProtocolJson.Read<ResetResourceRequest>(parameters)),
            ResourceQueryMethods.Trees => _resources.GetTree(ProtocolJson.Read<ResourceTreeQueryRequest>(parameters)),
            ResourceQueryMethods.Entries => _resources.QueryEntries(ProtocolJson.Read<ResourceEntriesQueryRequest>(parameters)),
            ResourceQueryMethods.ReadEntry => _resources.ReadEntry(ProtocolJson.Read<ResourceReadEntryRequest>(parameters)),
            InspectionMethods.Assets => AssetInspectionService.List(),
            InspectionMethods.OpenAsset => AssetInspectionService.Open(ProtocolJson.Read<OpenAssetRequest>(parameters).Uri),
            AssetExportMethods.Begin => _assetExports.Begin(ProtocolJson.Read<BeginAssetExportRequest>(parameters)),
            AssetExportMethods.Read => _assetExports.Read(ProtocolJson.Read<ReadAssetExportRequest>(parameters)),
            AssetExportMethods.Close => _assetExports.Close(ProtocolJson.Read<CloseAssetExportRequest>(parameters)),
            ActivityMethods.Start => _activity.Start(ProtocolJson.Read<ActivityStartRequest>(parameters)),
            ActivityMethods.Stop => _activity.Stop(),
            ActivityMethods.Snapshot => _activity.Snapshot(),
            ActivityMethods.Clear => _activity.Clear(),
            ActivityMethods.Status => _activity.Status(),
            MetricsMethods.Start => _metrics.Start(ProtocolJson.Read<MetricsStartRequest>(parameters)),
            MetricsMethods.Stop => _metrics.Stop(),
            MetricsMethods.Clear => _metrics.Clear(ProtocolJson.Read<MetricsClearRequest>(parameters)),
            MetricsMethods.Snapshot => _metrics.Snapshot(),
            MetricsMethods.Status => _metrics.Status(),
            ProfilerMethods.Start => _profiler.Start(ProtocolJson.Read<ProfilerStartRequest>(parameters)),
            ProfilerMethods.Stop => _profiler.Stop(),
            ProfilerMethods.Clear => _profiler.Clear(),
            ProfilerMethods.Snapshot => _profiler.Snapshot(),
            ProfilerMethods.Status => _profiler.Status(),
            BreakpointMethods.Create => _breakpoints.Create(ProtocolJson.Read<CreateBreakpointRequest>(parameters)),
            BreakpointMethods.Update => _breakpoints.Update(ProtocolJson.Read<UpdateBreakpointRequest>(parameters)),
            BreakpointMethods.Remove => _breakpoints.Remove(ProtocolJson.Read<RemoveBreakpointRequest>(parameters)),
            BreakpointMethods.List => _breakpoints.List(),
            BreakpointMethods.History => _breakpoints.History(),
            LayoutMethods.Snapshot => _layout.Snapshot(ProtocolJson.Read<LayoutSnapshotRequest>(parameters)),
            RuntimeInspectionMethods.Members => _runtime.GetMembers(ProtocolJson.Read<RuntimeMembersRequest>(parameters)),
            RuntimeInspectionMethods.Items => _runtime.GetItems(ProtocolJson.Read<RuntimeItemsRequest>(parameters)),
            InputMethods.Send => _input.Send(ProtocolJson.Read<InputRequest>(parameters)),
            InputMethods.Click => _input.Click(ProtocolJson.Read<ClickInputRequest>(parameters)),
            InputMethods.Chord => _input.Chord(ProtocolJson.Read<KeyChordRequest>(parameters)),
            InputMethods.Drag => _input.Drag(ProtocolJson.Read<DragInputRequest>(parameters)),
            InputMethods.TextSelection => _input.SelectText(ProtocolJson.Read<TextSelectionRequest>(parameters)),
            InputMethods.SelectItem => _input.SelectItem(ProtocolJson.Read<SelectItemRequest>(parameters)),
            InputMethods.ControlAction => _input.PerformAction(ProtocolJson.Read<ControlActionRequest>(parameters)),
            _ => HandleExploration(method, parameters)
        };
    }

    private IReadOnlyList<TreeNodeInfo> GetRoots(TreeKind kind)
    {
        ValidateTreeKind(kind);
        return RefreshRoots().Select(x => Describe(x, kind)).ToArray();
    }

    private IReadOnlyList<TreeNodeInfo> GetChildren(TreeChildrenRequest request)
    {
        ValidateTreeKind(request.Kind);
        var parent = _objects.Get(request.ObjectId);
        if (request.Kind == TreeKind.Merged) MergedPath(parent, RefreshRoots());
        var children = ChildObjects(parent, request.Kind).ToArray();
        ObserveTree(new TreeObserveRequest(request.ObjectId, request.Kind));
        return children.Select(x => Describe(x, request.Kind)).ToArray();
    }

    private TreeNodeInfo Describe(object value, TreeKind kind)
    {
        var id = Observe(value);
        if (kind == TreeKind.Merged)
        {
            var metadata = _mergedTree.Metadata(value);
            if (value is MergedTree.SyntheticNode synthetic)
                return new TreeNodeInfo(id, value.GetType().FullName!, synthetic.Label, null,
                    ChildObjects(value, kind).Any(), metadata.Role,
                    metadata.Owner is { } owner ? Observe(owner) : null,
                    metadata.Content is { } content ? Observe(content) : null);
        }
        var name = (value as StyledElement)?.Name;
        var preview = value switch
        {
            TextBlock text => text.Text,
            TextBox text => text.Text,
            ContentControl { Content: string text } => text,
            Window window => window.Title,
            _ => null
        };
        if (preview?.Length > 100)
            preview = preview[..100] + "…";
        var details = kind == TreeKind.Merged ? _mergedTree.Metadata(value) : default;
        return new TreeNodeInfo(id, value.GetType().FullName ?? value.GetType().Name, name, preview,
            ChildObjects(value, kind).Any(), details.Role,
            details.Owner is { } templateOwner ? Observe(templateOwner) : null);
    }

    private IEnumerable<object> ChildObjects(object parent, TreeKind kind)
    {
        IEnumerable<object> children = kind switch
        {
            TreeKind.Visual when parent is Visual visual => visual.GetVisualChildren(),
            TreeKind.Logical when parent is ILogical logical => logical.LogicalChildren,
            TreeKind.Merged => _mergedTree.Children(parent),
            _ => []
        };
        return children.Where(x => !OverlayService.IsDiagnosticVisual(x));
    }

    private long Observe(object value)
    {
        var id = _objects.GetId(value);
        if (value is AvaloniaObject obj && !_observed.ContainsKey(id))
        {
            _observed.Add(id, new WeakReference<AvaloniaObject>(obj));
            obj.PropertyChanged += OnPropertyChanged;
            if (obj is TemplatedControl owner) owner.TemplateApplied += OnMergedTemplateApplied;
        }
        return id;
    }

    private static string PropertyId(AvaloniaProperty property) =>
        (property.OwnerType.FullName ?? property.OwnerType.Name) + ":" + property.Name;

    private IEnumerable<AvaloniaProperty> RegisteredProperties(AvaloniaObject value) =>
        AvaloniaPropertyRegistry.Instance.GetRegistered(value.GetType())
            .Concat(AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(value.GetType()))
            .Distinct().OrderBy(x => x.Name).ThenBy(x => x.OwnerType.FullName);

    private IReadOnlyList<PropertyInfoDto> GetProperties(PropertiesRequest request)
    {
        var id = request.ObjectId;
        var target = GetAvaloniaObject(id);
        Observe(target);
        _propertyContextParents[id] = PropertyContextParentId(target);
        var result = new List<PropertyInfoDto>();
        var registered = RegisteredProperties(target).ToArray();
        var clrProperties = PropertyPresentationAdapter.PublicProperties(target.GetType()).ToArray();
        var wrapped = clrProperties.GroupBy(member => member.Name, StringComparer.Ordinal)
            .Select(group => PropertyPresentationAdapter.WrapperProperty(group.First(), registered))
            .OfType<AvaloniaProperty>().ToHashSet();
        foreach (var property in registered)
        {
            var context = ContextualProperties.Describe(target, property);
            if (request.Contextual && context?.IsApplicable == false) continue;
            var propertyId = PropertyId(property);
            var readOnly = property.IsReadOnly || property.IsDirect;
            var canEdit = !readOnly && PropertyValues.CanConvert(property.PropertyType);
            var hasOverride = HasOverride(target, property);
            try
            {
                var diagnostic = target.GetDiagnostic(property);
                var clrWrapper = clrProperties.FirstOrDefault(member => member.Name == property.Name &&
                    member.DeclaringType == property.OwnerType && member.PropertyType == property.PropertyType);
                var presentation = WithPreviewLease(id, propertyId, diagnostic.Value,
                    PropertyPresentationAdapter.Describe(property.PropertyType, diagnostic.Value, canEdit, "Avalonia",
                        PropertyPresentationAdapter.Category(clrWrapper, property.OwnerType.Name))) with { Context = context };
                canEdit &= presentation.Editor != PropertyEditorKind.ReadOnly;
                result.Add(new PropertyInfoDto(propertyId, property.Name,
                    property.OwnerType.FullName ?? property.OwnerType.Name,
                    property.PropertyType.FullName ?? property.PropertyType.Name,
                    PropertyPresentationAdapter.Format(diagnostic.Value),
                    hasOverride ? "DiagnosticOverride" : diagnostic.Priority.ToString(),
                    readOnly || !canEdit, canEdit, hasOverride, Presentation: presentation, HasClrWrapper: wrapped.Contains(property)));
            }
            catch (Exception ex)
            {
                result.Add(new PropertyInfoDto(propertyId, property.Name, property.OwnerType.Name,
                    property.PropertyType.Name, null, "Unavailable", true, false, false, PropertyErrors.Describe(ex), HasClrWrapper: wrapped.Contains(property)));
            }
        }
        foreach (var property in clrProperties.Where(property => request.IncludeClr && !PropertyPresentationAdapter.IsBackingWrapper(property, registered)))
        {
            var propertyId = PropertyPresentationAdapter.ClrId(property);
            try
            {
                var value = property.GetValue(target);
                var presentation = WithPreviewLease(id, propertyId, value,
                    PropertyPresentationAdapter.Describe(property.PropertyType, value, false, "CLR",
                        PropertyPresentationAdapter.Category(property, property.DeclaringType!.Name)));
                result.Add(new PropertyInfoDto(propertyId, property.Name, property.DeclaringType!.FullName!,
                    property.PropertyType.FullName ?? property.PropertyType.Name, PropertyPresentationAdapter.Format(value),
                    "CLR", true, false, false, Presentation: presentation));
            }
            catch (Exception exception)
            {
                result.Add(new PropertyInfoDto(propertyId, property.Name, property.DeclaringType!.FullName!,
                    property.PropertyType.FullName ?? property.PropertyType.Name, null, "Unavailable", true, false, false,
                    PropertyErrors.Describe(exception),
                    new PropertyPresentation(property.DeclaringType.Name, "CLR", PropertyEditorKind.ReadOnly)));
            }
        }
        return result;
    }

    private AvaloniaObject GetAvaloniaObject(long id) =>
        (id == 0 ? _application : _objects.Get(id)) as AvaloniaObject ??
            throw new RpcException("unsupported_object", "The selected object has no Avalonia properties.");

    private AvaloniaProperty GetProperty(AvaloniaObject target, string id)
    {
        var property = RegisteredProperties(target).FirstOrDefault(x => PropertyId(x) == id);
        return property ?? throw new RpcException("property_not_found", "The property is not registered for this object.");
    }

    private OperationResult SetProperty(SetPropertyRequest request)
    {
        var target = GetAvaloniaObject(request.ObjectId);
        var property = GetProperty(target, request.PropertyId);
        if (property.IsReadOnly || property.IsDirect)
            throw new RpcException("readonly_property", "Direct and read-only properties cannot be safely overridden.");
        if (!PropertyValues.CanConvert(property.PropertyType))
            throw new RpcException("unsupported_type", "This property type has no text editor.");
        if (PropertyPresentationAdapter.Describe(property.PropertyType, target.GetValue(property), true, "Avalonia", "").Editor == PropertyEditorKind.ReadOnly)
            throw new RpcException("unsupported_type", "This brush type has no safe editor.");
        object? value;
        try
        {
            value = PropertyValues.Parse(request.Value, property.PropertyType);
            if (!property.IsValidValue(value))
                throw new FormatException("The value is rejected by the property.");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or NotSupportedException)
        {
            throw new RpcException("conversion_failed", ex.Message);
        }

        // An animation-priority entry is disposable and leaves the underlying local binding intact.
        var entries = _overrides.GetOrCreateValue(target);
        var previous = entries.GetValueOrDefault(property);
        IDisposable? replacement;
        try
        {
            replacement = target.SetValue(property, value, BindingPriority.Animation);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException)
        {
            throw new RpcException("invalid_value", ex.Message);
        }
        if (replacement is null)
            throw new RpcException("unsupported_property", "The framework did not provide a reversible override.");
        entries[property] = replacement;
        previous?.Dispose();
        _pendingProperties.Add(request.ObjectId);
        return new OperationResult();
    }

    private OperationResult ClearProperty(ClearPropertyRequest request)
    {
        var target = GetAvaloniaObject(request.ObjectId);
        var property = GetProperty(target, request.PropertyId);
        if (_overrides.TryGetValue(target, out var entries) && entries.Remove(property, out var entry))
            entry.Dispose();
        _pendingProperties.Add(request.ObjectId);
        return new OperationResult();
    }

    private bool HasOverride(AvaloniaObject target, AvaloniaProperty property) =>
        _overrides.TryGetValue(target, out var entries) && entries.ContainsKey(property);

    private OperationResult SetHighlight(long? id)
    {
        ClearHighlight();
        if (id is not null)
        {
            if (_objects.Get(id.Value) is not Control control)
                throw new RpcException("unsupported_object", "Only controls can be highlighted.");
            _overlay.Show(control);
        }
        return new OperationResult();
    }

    private void ClearHighlight()
    {
        _overlay.Clear();
    }

    private OperationResult SetPicking(bool enabled)
    {
        _picking = enabled;
        if (!enabled)
            ClearHighlight();
        RefreshRoots();
        Notify(Methods.PickingChanged, new PickingChangedNotification(_picking));
        return new OperationResult();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_picking && e.Source is Control control && !OverlayService.IsDiagnosticVisual(control))
        {
            var current = _overlay.CurrentTarget;
            if (current != control)
            {
                ClearHighlight();
                _overlay.Show(control);
            }
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_picking || e.Source is not Control control || OverlayService.IsDiagnosticVisual(control))
            return;
        e.Handled = true;
        _picking = false;
        var id = Observe(control);
        SetHighlight(id);
        Notify(Methods.SelectionChanged, new SelectionChangedNotification(id));
        Notify(Methods.PickingChanged, new PickingChangedNotification(false));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_picking && e.Key == Key.Escape)
        {
            SetPicking(false);
            e.Handled = true;
        }
    }

    private List<TopLevel> RefreshRoots()
    {
        var initial = _options.TopLevelProvider?.Invoke() ??
            (_application.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.Cast<TopLevel>() ??
            [];
        var roots = initial.Distinct().ToList();
        _popupFreeze.RefreshRoots(roots);
        // OpenedPopups also includes the private Popup owned by a Flyout, ContextMenu or ToolTip.
        // Logical descendants omit those popups. Walk only popups owned by the configured roots.
        var pendingPopups = new Queue<Popup>(roots.SelectMany(root => root.OpenedPopups));
        var seenPopups = new HashSet<Popup>();
        while (pendingPopups.TryDequeue(out var popup))
        {
            if (!seenPopups.Add(popup) || !popup.IsOpen) continue;
            foreach (var nested in popup.OpenedPopups) pendingPopups.Enqueue(nested);
            if (popup.Child is Visual child && TopLevel.GetTopLevel(child) is { } popupRoot && !roots.Contains(popupRoot))
                roots.Add(popupRoot);
        }
        foreach (var removed in _rootHandlers.Keys.Except(roots).ToArray())
        {
            _rootHandlers[removed].Dispose();
            _rootHandlers.Remove(removed);
        }
        foreach (var root in roots)
        {
            Observe(root);
            if (!_rootHandlers.ContainsKey(root))
            {
                _rootHandlers.Add(root, new RootHandlers(root, OnPointerPressed, OnPointerMoved, OnKeyDown, OnGotFocus));
                // A newly opened native popup may have received focus before the discovery tick.
                if (_followFocus && root.FocusManager?.GetFocusedElement() is Control focused &&
                    ReferenceEquals(TopLevel.GetTopLevel(focused), root))
                    Notify(Methods.SelectionChanged, new SelectionChangedNotification(Observe(focused)));
            }
        }
        _rendererDiagnostics.RefreshRoots(roots);
        PruneTreeObservations(roots);
        return roots;
    }

    private string Fingerprint(IEnumerable<object> children) =>
        string.Join(",", children.Select(_objects.GetId));

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!_disposed && sender is AvaloniaObject obj)
            _pendingProperties.Add(_objects.GetId(obj));
    }

    private void OnMergedTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (_disposed || sender is not TemplatedControl owner) return;
        _mergedTree.Invalidate(owner);
        if (_treeObservations.Keys.Any(key => key.Id == _objects.GetId(owner)))
            Notify(Methods.TreeInvalidated, new TreeInvalidatedNotification(_objects.GetId(owner)));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        var roots = RefreshRoots();
        var rootsSnapshot = Fingerprint(roots);
        RefreshPropertyContexts();
        if (rootsSnapshot != _rootsSnapshot)
        {
            _rootsSnapshot = rootsSnapshot;
            Notify(Methods.TreeInvalidated, new TreeInvalidatedNotification());
        }
        foreach (var id in _pendingProperties.ToArray())
            Notify(Methods.PropertiesChanged, new PropertiesChangedNotification(id));
        _pendingProperties.Clear();
        foreach (var id in _observed.Where(x => !x.Value.TryGetTarget(out _)).Select(x => x.Key).ToArray())
            _observed.Remove(id);
        _objects.Prune();
    }

    private void Notify(string method, object parameters)
    {
        if (_peer is { } peer && !_disposed)
            _ = SendNotificationAsync(peer, method, parameters);
    }

    private static async Task SendNotificationAsync(RpcPeer peer, string method, object parameters)
    {
        try { await peer.SendNotificationAsync(method, parameters); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or RpcException) { }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _input.Dispose();
        _overlay.Dispose();
        _rendererDiagnostics.Dispose();
        _popupFreeze.Dispose();
        _assetExports.Dispose();
        _composition.Dispose();
        DisposeExploration();
        _activity.Dispose();
        _metrics.Dispose();
        _profiler.Dispose();
        _breakpoints.Dispose();
        _resources.Dispose();
        _timer.Stop();
        _timer.Tick -= OnTick;
        _picking = false;
        ClearHighlight();
        foreach (var subscription in _rootHandlers.Values)
            subscription.Dispose();
        _rootHandlers.Clear();
        foreach (var reference in _observed.Values)
        {
            if (reference.TryGetTarget(out var target))
            {
                target.PropertyChanged -= OnPropertyChanged;
                if (target is TemplatedControl owner) owner.TemplateApplied -= OnMergedTemplateApplied;
                if (_overrides.TryGetValue(target, out var entries))
                {
                    foreach (var entry in entries.Values)
                        entry.Dispose();
                    entries.Clear();
                }
            }
        }
        _overrides.Clear();
        _observed.Clear();
        _propertyContextParents.Clear();
        DisposeTreeObservations();
        _pendingProperties.Clear();
        _peer = null;
    }

    private sealed class RootHandlers : IDisposable
    {
        private readonly TopLevel _root;
        private readonly EventHandler<PointerPressedEventArgs> _pressed;
        private readonly EventHandler<PointerEventArgs> _moved;
        private readonly EventHandler<KeyEventArgs> _key;
        private readonly EventHandler<FocusChangedEventArgs> _focus;

        public RootHandlers(TopLevel root, EventHandler<PointerPressedEventArgs> pressed,
            EventHandler<PointerEventArgs> moved, EventHandler<KeyEventArgs> key, EventHandler<FocusChangedEventArgs> focus)
        {
            _root = root; _pressed = pressed; _moved = moved; _key = key;
            _focus = focus;
            root.AddHandler(InputElement.PointerPressedEvent, pressed, RoutingStrategies.Tunnel, true);
            root.AddHandler(InputElement.PointerMovedEvent, moved, RoutingStrategies.Tunnel, true);
            root.AddHandler(InputElement.KeyDownEvent, key, RoutingStrategies.Tunnel, true);
            root.AddHandler(InputElement.GotFocusEvent, focus, RoutingStrategies.Bubble, true);
        }

        public void Dispose()
        {
            _root.RemoveHandler(InputElement.PointerPressedEvent, _pressed);
            _root.RemoveHandler(InputElement.PointerMovedEvent, _moved);
            _root.RemoveHandler(InputElement.KeyDownEvent, _key);
            _root.RemoveHandler(InputElement.GotFocusEvent, _focus);
        }
    }
}






