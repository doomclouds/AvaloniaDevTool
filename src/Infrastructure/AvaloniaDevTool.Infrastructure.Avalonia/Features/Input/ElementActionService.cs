using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed class ElementActionService
{
    private readonly WeakObjectRegistry _objects;
    private readonly PropertyInspectionService _properties;

    internal ElementActionService(WeakObjectRegistry objects, PropertyInspectionService properties)
    {
        _objects = objects;
        _properties = properties;
    }

    internal ElementActionResult Perform(ElementActionRequest request)
    {
        if (_objects.Get(request.ObjectId) is not Control control)
            throw new RpcException("unsupported_object", "Element actions require a control.");
        switch (request.Action)
        {
            case ElementAction.Focus:
                var focused = control.Focus();
                return new ElementActionResult(focused, focused ? null : "The control did not accept focus.");
            case ElementAction.BringIntoView:
                if (TopLevel.GetTopLevel(control) is null)
                    throw new RpcException("object_detached", "BringIntoView requires an attached control.");
                control.BringIntoView();
                return new ElementActionResult(true);
            case ElementAction.Enable:
            case ElementAction.Disable:
                _properties.SetProperty(new SetPropertyRequest(request.ObjectId,
                    PropertyInspectionService.PropertyId(InputElement.IsEnabledProperty),
                    request.Action == ElementAction.Enable ? "true" : "false"));
                return new ElementActionResult(true);
            default:
                throw new RpcException("invalid_request", "The requested element action is unsupported.");
        }
    }
}
