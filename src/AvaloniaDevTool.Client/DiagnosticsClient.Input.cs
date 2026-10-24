using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<InputResult> SendInputAsync(InputRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<InputResult>(InputMethods.Send, request, cancellationToken);
}

