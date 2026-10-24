namespace AvaloniaDevTool.Backends.XamlMcp;

/// <summary>Starts an explicitly configured upstream MCP server; no user MCP configuration is changed.</summary>
public sealed record XamlMcpBackendOptions
{
    public string ServerCommand { get; init; } = "dotnet";
    public IReadOnlyList<string> ServerArguments { get; init; } = [XamlMcpServerLocation.BundledAssemblyPath];
    public string? WorkingDirectory { get; init; }
    public string? DiscoveryDirectory { get; init; }
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int MaxSnapshotNodes { get; init; } = 2048;
    public int SnapshotDepth { get; init; } = 24;
    public Action<string>? StandardErrorLines { get; init; }
}

public static class XamlMcpServerLocation
{
    public const string PackageVersion = "1.0.0-preview.3";
    public static string BundledAssemblyPath => Path.Combine(AppContext.BaseDirectory,
        "Upstream", "XamlMcpServer", "XamlMcp.Server.dll");
}

/// <summary>Sanitized app identity supplied by list-apps; credentials are never exposed.</summary>
public sealed record XamlMcpApplicationInfo(string InstanceId, int? Pid, string ProcessName,
    string? MainWindowTitle, string FrameworkId, string FrameworkVersion, string Platform);

