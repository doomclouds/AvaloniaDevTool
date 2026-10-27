using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Cli;

public sealed record PreviewAttachment(
    SessionInfo Session,
    string FilePath,
    string AssemblyPath,
    string ProjectPath,
    string PipeName,
    bool Watching,
    bool Headless,
    string ApplicationAssemblyPath);
