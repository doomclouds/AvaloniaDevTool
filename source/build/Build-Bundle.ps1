[CmdletBinding()]
param(
    [string]$Version = '0.1.4',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$Configuration = 'Release',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$arguments = @{
    SourceRoot        = $repository
    Version           = $Version
    RuntimeIdentifier = $RuntimeIdentifier
    Configuration     = $Configuration
}
$arguments.OutputDirectory = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $repository '../mcp' }
& (Join-Path $repository 'src/Packaging/AvaloniaDevTool.Packaging/Build-Bundle.ps1') @arguments
