[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$installer = Join-Path $repository 'tools/Install-AvaloniaDevTool.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('avdt-install-test-' + [Guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $testRoot 'share'
$binDirectory = Join-Path $testRoot 'bin'
$source = Join-Path $env:WINDIR 'System32/cmd.exe'
$beforePath = [Environment]::GetEnvironmentVariable('Path', 'User')
$beforeHome = [Environment]::GetEnvironmentVariable('AVALONIA_DEVTOOL_HOME', 'User')

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

try {
    & $installer -PackagePath $source -Version 1.0.0 -InstallRoot $installRoot -BinDirectory $binDirectory -SkipEnvironmentUpdate -WhatIf
    Assert-True (-not (Test-Path -LiteralPath $testRoot)) 'WhatIf created files.'

    $first = & $installer -PackagePath $source -Version 1.0.0 -InstallRoot $installRoot -BinDirectory $binDirectory -SkipEnvironmentUpdate
    Assert-True (Test-Path -LiteralPath $first.Command) 'The stable command is missing.'
    $again = & $installer -PackagePath $source -Version 1.0.0 -InstallRoot $installRoot -BinDirectory $binDirectory -SkipEnvironmentUpdate
    Assert-True ($first.Sha256 -eq $again.Sha256) 'Idempotent installation changed the command.'

    $second = & $installer -PackagePath $source -Version 1.1.0 -InstallRoot $installRoot -BinDirectory $binDirectory -SkipEnvironmentUpdate
    Assert-True (Test-Path -LiteralPath $first.VersionedExecutable) 'Installing a new version deleted the old one.'
    Assert-True ((Get-Content (Join-Path $installRoot 'current.json') -Raw | ConvertFrom-Json).version -eq '1.1.0') 'The selected version was not updated.'

    $replacementRejected = $false
    try {
        & $installer -PackagePath (Join-Path $env:WINDIR 'System32/where.exe') -Version 1.0.0 -InstallRoot $installRoot -BinDirectory $binDirectory -SkipEnvironmentUpdate
    } catch { $replacementRejected = $true }
    Assert-True $replacementRejected 'Different content replaced an already-installed version.'
    Assert-True ((Get-FileHash $first.VersionedExecutable).Hash.ToLowerInvariant() -eq $first.Sha256) 'An existing version was overwritten.'

    $badHashRejected = $false
    try {
        & $installer -PackagePath $source -Version 2.0.0 -ExpectedSha256 ('0' * 64) -InstallRoot $installRoot -BinDirectory $binDirectory -SkipEnvironmentUpdate
    } catch { $badHashRejected = $true }
    Assert-True $badHashRejected 'A mismatched checksum was accepted.'
    Assert-True (-not (Test-Path (Join-Path $installRoot 'versions/2.0.0'))) 'A rejected package was installed.'

    $locked = [IO.File]::Open($second.Command, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    $lockedRejected = $false
    try {
        try { & $installer -PackagePath $source -Version 1.2.0 -InstallRoot $installRoot -BinDirectory $binDirectory -SkipEnvironmentUpdate }
        catch { $lockedRejected = $true }
    } finally { $locked.Dispose() }
    Assert-True $lockedRejected 'Replacing an exclusively locked command unexpectedly succeeded.'
    Assert-True ((Get-Content (Join-Path $installRoot 'current.json') -Raw | ConvertFrom-Json).version -eq '1.1.0') 'A failed switch changed the selected version.'
    Assert-True ((Get-FileHash $second.Command).Hash.ToLowerInvariant() -eq $first.Sha256) 'A failed switch damaged the previous command.'
    Assert-True ([Environment]::GetEnvironmentVariable('Path', 'User') -ceq $beforePath) 'The isolated tests changed the user PATH.'
    Assert-True ([Environment]::GetEnvironmentVariable('AVALONIA_DEVTOOL_HOME', 'User') -ceq $beforeHome) 'The isolated tests changed the user home variable.'
    Write-Output 'Installer checks passed: WhatIf, initial install, idempotence, version retention, immutable versions, checksum rejection, locked-command rollback, and environment isolation.'
} finally {
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to delete a test directory outside the temporary root.'
    }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
