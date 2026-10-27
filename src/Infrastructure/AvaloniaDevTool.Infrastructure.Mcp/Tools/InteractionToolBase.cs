using AvaloniaDevTool.NativeAutomation;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

/// <summary>Independent interaction tools using the canonical managed node identity and a separate native domain.</summary>
[McpServerToolType]
public abstract class InteractionToolBase
{
    protected readonly ToolSession session;
    protected readonly ManagedToolContext managed;

    protected InteractionToolBase(ToolSession session, ManagedToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(session, context.OwnerSession))
            throw new ArgumentException("The managed context must belong to the supplied tool session.",
                nameof(context));
        this.session = session;
        managed = context;
    }

    // Native operations must remain available while a managed click is waiting inside a modal dialog.
    protected async Task<T> WithNativeAsync<T>(CancellationToken token,
        Func<NativeAutomationSession, CancellationToken, Task<T>> action)
    {
        try
        {
            var native = await session.GetNativeSessionAsync(token).ConfigureAwait(false);
            var result = await action(native, token).ConfigureAwait(false);
            if (result is NativeActionResult { Status: "failed" } failed)
                throw new McpException(
                    $"{failed.ErrorCode ?? "native_action_failed"}: {failed.ErrorMessage ?? "The native action failed."}");
            return result;
        }
        catch (NativeAutomationException error)
        {
            throw new McpException($"{error.Code}: {error.Message}");
        }
        catch (ArgumentException error)
        {
            throw new McpException($"invalid_argument: {error.Message}");
        }
        catch (ObjectDisposedException)
        {
            throw new McpException(
                "stale_session: The native automation session ended. List windows again after attaching.");
        }
    }
}
