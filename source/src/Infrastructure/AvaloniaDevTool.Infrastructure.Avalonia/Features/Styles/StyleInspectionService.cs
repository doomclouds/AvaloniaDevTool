using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;
using Avalonia.Data;

namespace AvaloniaDevTool.Agent;

internal sealed class StyleInspectionService : IDisposable
{
    private readonly WeakObjectRegistry _objects;
    private readonly PropertyInspectionService _properties;
    private readonly Action<long> _markChanged;

    internal StyleInspectionService(WeakObjectRegistry objects, PropertyInspectionService properties)
    {
        _objects = objects;
        _properties = properties;
        _markChanged = properties.MarkChanged;
    }

    public void Dispose() => DisposeExploration();
    private readonly Dictionary<(long ObjectId, string Name), PseudoClassOverride> _explorationPseudoClasses = [];

    private IReadOnlyList<PseudoClassInfo> GetPseudoClasses(long objectId)
    {
        var target = GetStyledElement(objectId);
        var declared = target.GetType().GetCustomAttributes<PseudoClassesAttribute>(inherit: true)
            .SelectMany(attribute => attribute.PseudoClasses).ToHashSet(StringComparer.Ordinal);
        var active = target.Classes.Where(name => name.StartsWith(':')).ToHashSet(StringComparer.Ordinal);
        var touched = _explorationPseudoClasses.Keys.Where(key => key.ObjectId == objectId).Select(key => key.Name);
        return declared.Concat(active).Concat(touched).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(name => new PseudoClassInfo(name,
                active.Contains(name),
                declared.Contains(name),
                _explorationPseudoClasses.ContainsKey((objectId, name)))).ToArray();
    }

    private StyledElement GetStyledElement(long objectId) => _objects.Get(objectId) as StyledElement ??
                                                             throw new RpcException("unsupported_object",
                                                                 "Pseudo-classes require a StyledElement.");

    private static void ValidatePseudoClass(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length is < 2 or > 128 || name[0] != ':' ||
            name.Skip(1).Any(character => !char.IsLetterOrDigit(character) && character is not ('_' or '-')))
            throw new RpcException("invalid_request",
                "A pseudo-class must start with ':' and contain letters, digits, '_' or '-'.");
    }

    private OperationResult SetPseudoClass(SetPseudoClassRequest request)
    {
        ValidatePseudoClass(request.Name);
        var target = GetStyledElement(request.ObjectId);
        var key = (request.ObjectId, request.Name);
        _explorationPseudoClasses.TryAdd(key,
            new PseudoClassOverride(new WeakReference<StyledElement>(target), target.Classes.Contains(request.Name)));
        ((IPseudoClasses)target.Classes).Set(request.Name, request.Enabled);
        _markChanged(request.ObjectId);
        return new OperationResult();
    }

    private OperationResult ResetPseudoClass(ResetPseudoClassRequest request)
    {
        ValidatePseudoClass(request.Name);
        _ = GetStyledElement(request.ObjectId);
        if (_explorationPseudoClasses.Remove((request.ObjectId, request.Name), out var entry) &&
            entry.Target.TryGetTarget(out var target))
            ((IPseudoClasses)target.Classes).Set(request.Name, entry.WasActive);
        _markChanged(request.ObjectId);
        return new OperationResult();
    }

    private void DisposeExploration()
    {
        foreach (var entry in _explorationPseudoClasses)
            if (entry.Value.Target.TryGetTarget(out var target))
                ((IPseudoClasses)target.Classes).Set(entry.Key.Name, entry.Value.WasActive);
        _explorationPseudoClasses.Clear();
    }

    private sealed record PseudoClassOverride(WeakReference<StyledElement> Target, bool WasActive);

    private StyleSnapshot GetOfficialStyles(InspectStylesRequest request)
    {
        var target = _properties.GetAvaloniaObject(request.ObjectId);

        bool Included(string name, string declaringType) => request.PropertyNames is null ||
                                                            request.PropertyNames.Any(item =>
                                                                item is not null && (item == name ||
                                                                    item == declaringType + ":" + name));

        var includedProperties = _properties.RegisteredProperties(target).Where(property => Included(property.Name,
            property.OwnerType.FullName ?? property.OwnerType.Name)).ToArray();
        var result = Avalonia12DiagnosticsAdapter.Inspect(target, _objects.GetId);
        var frames = result.Frames.Select(frame => frame with
        {
            Setters = frame.Setters.Where(setter => Included(setter.PropertyName, setter.DeclaringType)).ToArray()
        }).Where(frame => frame.Setters.Count > 0).ToList();
        if (request.IncludeDefaults)
        {
            var defaults = new List<StyleSetterInfo>();
            foreach (var property in includedProperties.Where(property => !property.IsDirect))
            {
                if (property.GetMetadata(target) is not IStyledPropertyMetadata metadata)
                    continue;
                var value = metadata.DefaultValue;
                defaults.Add(new StyleSetterInfo(property.Name,
                    property.OwnerType.FullName ?? property.OwnerType.Name,
                    PropertyValues.Format(value),
                    Equals(target.GetValue(property), value)));
            }

            if (defaults.Count > 0)
                frames.Add(new StyleFrameInfo("Property metadata defaults", "Default", "Unset", true, defaults));
        }

        return result with
        {
            Frames = frames,
            Bindings = result.Bindings.Where(binding => request.PropertyNames is null ||
                                                        includedProperties.Any(property =>
                                                            property.Name == binding.PropertyName)).ToArray()
        };
    }

    internal void Register(RpcMethodRegistry routes)
    {
        routes.Map<ObjectRequest>(ExplorationMethods.PseudoClasses, request => GetPseudoClasses(request.ObjectId));
        routes.Map<SetPseudoClassRequest>(ExplorationMethods.SetPseudoClass, SetPseudoClass);
        routes.Map<ResetPseudoClassRequest>(ExplorationMethods.ResetPseudoClass, ResetPseudoClass);
        routes.Map<InspectStylesRequest>(PropertyValueMethods.Styles, request => GetOfficialStyles(request));
        routes.Map<ObjectRequest>(InspectionMethods.Styles,
            request => Avalonia12DiagnosticsAdapter.Inspect(_properties.GetAvaloniaObject(request.ObjectId),
                _objects.GetId));
    }
}
