#:project ./src/Hosts/AvaloniaDevTool.Cli/AvaloniaDevTool.Cli.csproj
#:property PublishAot=false

using System.Runtime.CompilerServices;
using System.Text.Json;

var sourceRoot = Path.GetDirectoryName(SourceFile())!;
var runtimeRoot = Path.Combine(sourceRoot, "src", "Hosts", "AvaloniaDevTool.Cli", "bin", "Release", "net10.0");
Environment.SetEnvironmentVariable("AVDT_SOURCE_ROOT", sourceRoot);
Environment.SetEnvironmentVariable("AVDT_RUNTIME_ROOT", runtimeRoot);
if (args is ["--runtime-info"])
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        EntryAssembly = typeof(Program).Assembly.Location,
        RuntimeRoot = runtimeRoot,
        SourceRoot = sourceRoot
    }));
    return 0;
}
return await AvaloniaDevTool.Cli.Program.Main(args);

static string SourceFile([CallerFilePath] string path = "") => path;
