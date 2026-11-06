[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
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
if ($OutputDirectory) { $arguments.OutputDirectory = $OutputDirectory }
& (Join-Path $repository 'src/Packaging/AvaloniaDevTool.Packaging/Build-Bundle.ps1') @arguments
