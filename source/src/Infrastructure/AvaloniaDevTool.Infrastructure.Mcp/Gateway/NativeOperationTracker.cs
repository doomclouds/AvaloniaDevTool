using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace AvaloniaDevTool.Cli.Gateway;

/// <summary>Tracks only the native operation records declared by the relevant tools.</summary>
internal sealed class NativeOperationTracker
{
    private readonly object _sync = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    internal bool HasPending
    {
        get
        {
            lock (_sync)
                return _pending.Count != 0;
        }
    }

    internal void Clear()
    {
        lock (_sync)
            _pending.Clear();
    }

    internal void Forget(string? operation)
    {
        if (operation is not null)
            lock (_sync)
                _pending.Remove(operation);
    }

    internal void Observe(string tool, CallToolResult result)
    {
        if (result.IsError == true || tool is not ("native-action" or "native-action-status" or "file-dialogs" or
                "file-dialog-inspect" or "file-dialog-set-paths" or "file-dialog-set-filter" or "file-dialog-accept" or
                "file-dialog-cancel" or "file-dialog-overwrite" or "file-dialog-wait"))
            return;
        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            JsonNode? body;
            try
            {
                body = JsonNode.Parse(block.Text);
            }
            catch (JsonException)
            {
                continue;
            }

            if (tool is "native-action" or "native-action-status")
                ObserveAction(body);
            else if (tool == "file-dialogs" && body is JsonArray dialogs)
                foreach (var dialog in dialogs)
                    ObserveAction(GatewayResultHandles.Get(dialog, "LastAction"));
            else
                ObserveAction(GatewayResultHandles.Get(body, "LastAction"));
        }
    }

    private void ObserveAction(JsonNode? action)
    {
        if (GatewayResultHandles.Get(action, "OperationId") is not JsonValue operation ||
            GatewayResultHandles.Get(action, "Status") is not JsonValue status ||
            !operation.TryGetValue<string>(out var id) || !status.TryGetValue<string>(out var value))
            return;
        lock (_sync)
            if (value == "pending")
                _pending.Add(id);
            else if (value is "completed" or "failed" or "dispatched")
                _pending.Remove(id);
    }
}
