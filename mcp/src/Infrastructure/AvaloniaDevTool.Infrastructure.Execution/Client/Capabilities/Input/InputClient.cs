using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class InputClient
{
    private readonly DiagnosticsRpcContext context;

    internal InputClient(DiagnosticsRpcContext context)
    {
        this.context = context;
    }

    public Task<ElementActionResult> PerformElementActionAsync(long objectId,
        ElementAction action,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ElementActionResult>(ExplorationMethods.Action,
            new ElementActionRequest(objectId, action),
            cancellationToken);

    public Task<InputResult> SendInputAsync(InputRequest request, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<InputResult>(InputMethods.Send, request, cancellationToken);

    public Task<InputResult> ClickAsync(ClickInputRequest request, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<InputResult>(InputMethods.Click, request, cancellationToken);

    public Task<InputResult>
        SendKeyChordAsync(KeyChordRequest request, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<InputResult>(InputMethods.Chord, request, cancellationToken);

    public Task<InputResult> DragAsync(DragInputRequest request, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<InputResult>(InputMethods.Drag, request, cancellationToken);

    public Task<TextSelectionResult> SelectTextAsync(TextSelectionRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<TextSelectionResult>(InputMethods.TextSelection, request, cancellationToken);

    public Task<ItemSelectionResult> SelectItemAsync(SelectItemRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ItemSelectionResult>(InputMethods.SelectItem, request, cancellationToken);

    public Task<ControlActionResult> PerformControlActionAsync(ControlActionRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ControlActionResult>(InputMethods.ControlAction, request, cancellationToken);
}
