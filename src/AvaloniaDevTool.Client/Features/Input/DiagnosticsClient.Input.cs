using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<InputResult> SendInputAsync(InputRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<InputResult>(InputMethods.Send, request, cancellationToken);

    public Task<InputResult> ClickAsync(ClickInputRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<InputResult>(InputMethods.Click, request, cancellationToken);

    public Task<InputResult>
        SendKeyChordAsync(KeyChordRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<InputResult>(InputMethods.Chord, request, cancellationToken);

    public Task<InputResult> DragAsync(DragInputRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<InputResult>(InputMethods.Drag, request, cancellationToken);

    public Task<TextSelectionResult> SelectTextAsync(TextSelectionRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<TextSelectionResult>(InputMethods.TextSelection, request, cancellationToken);

    public Task<ItemSelectionResult> SelectItemAsync(SelectItemRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ItemSelectionResult>(InputMethods.SelectItem, request, cancellationToken);

    public Task<ControlActionResult> PerformControlActionAsync(ControlActionRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ControlActionResult>(InputMethods.ControlAction, request, cancellationToken);
}
