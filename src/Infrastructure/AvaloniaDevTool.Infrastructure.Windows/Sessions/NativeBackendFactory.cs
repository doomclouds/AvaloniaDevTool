using System.Text.Json;
using AvaloniaDevTool.NativeAutomation;

namespace AvaloniaDevTool.NativeAutomation.Worker;

internal static class NativeBackendFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static NativeAutomationBackend Create(NativeWorkerAttach attach)
    {
        var runtime = new NativeTargetRuntime(attach);
        var queries = new NativeQueryService(runtime);
        var actions = new NativeActionService(runtime);
        var input = new NativeInputService(runtime);
        var screenshots = new NativeScreenshotService(runtime);
        return new NativeAutomationBackend(runtime.ValidateTarget,
            new Dictionary<string, Func<JsonElement, object>>(StringComparer.Ordinal)
            {
                ["windows"] = _ => queries.GetWindows(),
                ["tree"] = payload => queries.GetTree(payload.Deserialize<NativeTreeQuery>(JsonOptions)!),
                ["find"] = payload => queries.Find(payload.Deserialize<NativeFindQuery>(JsonOptions)!),
                ["element"] = payload => queries.Inspect(payload.GetString()!),
                ["action"] = payload => actions.Execute(payload.Deserialize<NativeActionRequest>(JsonOptions)!),
                ["action-status"] = payload => actions.ActionStatus(payload.GetString()!),
                ["input"] = payload => input.Input(payload.Deserialize<NativeInputRequest>(JsonOptions)!),
                ["screenshot"] = payload =>
                    screenshots.Capture(payload.Deserialize<NativeScreenshotRequest>(JsonOptions)!)
            });
    }
}
