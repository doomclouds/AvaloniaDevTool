namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private string _remoteBearerToken = "";
    public bool IsRemoteEndpoint => IsNativeBackend &&
        (PipeName.StartsWith("ws:", StringComparison.OrdinalIgnoreCase) || PipeName.StartsWith("wss:", StringComparison.OrdinalIgnoreCase));
    public string RemoteBearerToken
    {
        get => _remoteBearerToken;
        set { if (Set(ref _remoteBearerToken, value ?? "")) ConnectCommand?.Refresh(); }
    }
}
