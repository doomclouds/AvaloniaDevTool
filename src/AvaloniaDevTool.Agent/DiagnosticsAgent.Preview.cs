using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

public sealed partial class DiagnosticsAgent
{
    public void NotifyContentChanged(long generation)
    {
        Dispatcher.UIThread.VerifyAccess();
        _session?.NotifyContentChanged(generation);
    }
}

internal sealed partial class AgentSession
{
    internal void NotifyContentChanged(long generation) => Notify(PreviewMethods.ContentGenerationChanged, generation);
}
