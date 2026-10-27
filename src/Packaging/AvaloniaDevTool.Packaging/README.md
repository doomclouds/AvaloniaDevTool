# Standalone distribution

`Build-Bundle.ps1` publishes the console, Previewer and Windows native worker
independently as self-contained applications, embeds their complete distribution
in a self-contained bootstrap, and delivers only `AvaloniaDevTool.exe`.

```powershell
pwsh -File src/Packaging/AvaloniaDevTool.Packaging/Build-Bundle.ps1 -RuntimeIdentifier win-x64
artifacts/package/win-x64/AvaloniaDevTool.exe --bundle-info
artifacts/package/win-x64/AvaloniaDevTool.exe --bundle-verify
artifacts/package/win-x64/AvaloniaDevTool.exe mcp
artifacts/package/win-x64/AvaloniaDevTool.exe serve
```

The build requires the .NET SDK and restore access. Running the bundle does not
require `dotnet` on PATH. Evaluating or building a user's XAML project still needs
the SDK. Target applications still consume the Agent SDK.

The bootstrap verifies SHA-256 hashes and extracts under
`%LOCALAPPDATA%/AvaloniaDevTool/bundles/<RID>/<payload SHA-256>/`. Extraction is
serialized across processes; every launch verifies the cached files before use.
`AVDT_BUNDLE_CACHE` can select another cache directory. The console receives the
resolved distribution root in `AVDT_RUNTIME_ROOT` and retains its caller's working
directory, environment, standard streams, arguments and exit code. A Windows Job
Object ends the console and its descendants if the bootstrap dies. Normal Ctrl+C
allows ten seconds for the console's own shutdown, then terminates remaining
descendants.

The single distribution file contains multiple isolated runtime roles. Its runtime
files, native dependencies, licenses and notices are extracted when needed;
single-file distribution does not mean that runtime execution uses no files or
only one process. Application symbols are embedded in their assemblies; native
symbols supplied by third-party packages remain inside the embedded payload.

`-SourceRoot` selects the source tree. Project discovery uses project file names,
so moving them into solution-level layers does not require changing this script.
`-CliProjectPath`, `-PreviewerProjectPath`, and `-NativeWorkerProjectPath` override
project discovery when project names change. `-OutputDirectory` must be a dedicated
empty directory or contain only an earlier `AvaloniaDevTool.exe`.
