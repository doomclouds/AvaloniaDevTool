$ErrorActionPreference = 'Stop'
try {
    $prepared = & (Join-Path $PSScriptRoot 'Prepare.ps1')
    $env:AVDT_RUNTIME_ROOT = $prepared.RuntimeRoot
    # Preparation already built this C# file and its project reference. Keep MSBuild off MCP stdout.
    [string[]]$hostArguments = if ($args.Count -eq 0) { @('mcp') } else { $args }
    & dotnet $prepared.EntryAssembly @hostArguments
    exit $LASTEXITCODE
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
