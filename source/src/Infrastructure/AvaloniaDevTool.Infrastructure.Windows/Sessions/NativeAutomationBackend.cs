using System.Text.Json;
using AvaloniaDevTool.NativeAutomation;

namespace AvaloniaDevTool.NativeAutomation.Worker;

internal sealed class NativeAutomationBackend(
    Action validateTarget,
    IReadOnlyDictionary<string, Func<JsonElement, object>> handlers)
{
    internal object Dispatch(string command, JsonElement payload)
    {
        validateTarget();
        return command is not null && handlers.TryGetValue(command, out var handler)
            ? handler(payload)
            : throw new NativeAutomationException("unknown_command", $"Unknown native command '{command}'.");
    }
}
