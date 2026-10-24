namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public string BackendId => "native";
    public string DisplayName => "本机 Agent";
    public bool HasStableObjectIds => true;
    public bool HasPushUpdates => true;
    public string? LastNotice => null;
}

