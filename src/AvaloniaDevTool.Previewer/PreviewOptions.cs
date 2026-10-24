using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Previewer;

public sealed record PreviewOptions(string XamlPath, string PipeName, string? AssemblyPath = null, bool Watch = true,
    string? ProjectPath = null, bool Headless = false, string? ControlPipe = null, string? ApplicationType = null,
    string? ApplicationAssemblyPath = null)
{
    public static PreviewOptions Parse(string[] args)
    {
        string? xaml = null, pipe = null, assembly = null, project = null, control = null, applicationType = null, applicationAssembly = null;
        var watch = true;
        var headless = false;
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option == "--no-watch") { watch = false; continue; }
            if (option == "--headless") { headless = true; continue; }
            if (option is not ("--xaml" or "--pipe" or "--assembly" or "--project" or "--control-pipe" or "--application-type" or "--application-assembly"))
                throw new ArgumentException($"Unknown option '{option}'.");
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                throw new ArgumentException($"Option '{option}' requires a value.");
            switch (option)
            {
                case "--xaml" when xaml is null: xaml = args[index]; break;
                case "--pipe" when pipe is null: pipe = args[index]; break;
                case "--assembly" when assembly is null: assembly = args[index]; break;
                case "--project" when project is null: project = args[index]; break;
                case "--control-pipe" when control is null: control = args[index]; break;
                case "--application-type" when applicationType is null: applicationType = args[index]; break;
                case "--application-assembly" when applicationAssembly is null: applicationAssembly = args[index]; break;
                default: throw new ArgumentException($"Option '{option}' was provided more than once.");
            }
        }
        if (xaml is null)
            throw new ArgumentException("Specify an absolute source file with --xaml.");
        xaml = AbsoluteFilePath(xaml, "--xaml");
        if (assembly is not null)
            assembly = AbsoluteFilePath(assembly, "--assembly");
        if (applicationAssembly is not null)
        {
            applicationAssembly = AbsoluteFilePath(applicationAssembly, "--application-assembly");
            if (project is null) throw new ArgumentException("--application-assembly requires --project and --assembly.");
        }
        if (project is not null)
        {
            project = AbsoluteFilePath(project, "--project");
            if (assembly is null)
                throw new ArgumentException("--project requires --assembly pointing to an existing build output.");
        }
        pipe ??= DiagnosticsProtocol.PipePrefix + "previewer-" + Environment.ProcessId;
        if (string.IsNullOrWhiteSpace(pipe) || pipe.Length > 200 || pipe.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new ArgumentException("Pipe name must be a nonempty simple local name of at most 200 characters.");
        if (control is not null && (control.Length > 200 || control.IndexOfAny(['/', '\\', ':']) >= 0))
            throw new ArgumentException("Control pipe name must be a simple local name of at most 200 characters.");
        return new PreviewOptions(xaml, pipe, assembly, watch, project, headless, control, applicationType, applicationAssembly);
    }

    internal static string AbsoluteFilePath(string path, string option)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException($"{option} requires an absolute path, received '{path}'.");
        var absolute = Path.GetFullPath(path);
        if (string.IsNullOrEmpty(Path.GetFileName(absolute)) || Directory.Exists(absolute))
            throw new ArgumentException($"{option} requires a file path, not a directory: '{path}'.");
        return absolute;
    }
}

public sealed class PreviewDocumentException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

