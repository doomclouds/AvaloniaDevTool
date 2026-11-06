namespace AvaloniaDevTool.Protocol;

public sealed class RpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
