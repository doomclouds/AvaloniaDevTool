[CmdletBinding()]
param(
    [string]$PackagePath,
    [string]$ManagerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repository = Split-Path $sourceRoot -Parent
if (-not $PackagePath) { $PackagePath = Join-Path $repository 'plugins/avalonia-devtool/mcp/AvaloniaDevTool.exe' }
if (-not $ManagerPath) { $ManagerPath = Join-Path $sourceRoot 'tools/Manage-CodexMcp.ps1' }
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$ManagerPath = (Resolve-Path -LiteralPath $ManagerPath).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('avdt-manager-failure-' + [Guid]::NewGuid().ToString('N'))
$toolsDirectory = Join-Path $testRoot 'tools'
$fixtureStateVariable = 'AVDT_MANAGER_FAILURE_STATE'
$beforeStatePath = [Environment]::GetEnvironmentVariable($fixtureStateVariable, 'Process')
$beforeBundleCache = [Environment]::GetEnvironmentVariable('AVDT_BUNDLE_CACHE', 'Process')
if (Get-Variable -Name AvdtManagerFailureHeldLocks -Scope Global -ErrorAction SilentlyContinue) {
    throw 'Another failure fixture is using this process.'
}
$global:AvdtManagerFailureHeldLocks = @{}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$mockHelper = @'
Set-StrictMode -Version Latest
function Read-FixtureState {
    return (Get-Content -LiteralPath $env:AVDT_MANAGER_FAILURE_STATE -Raw | ConvertFrom-Json -AsHashtable)
}
function Save-FixtureState {
    param($State)
    [IO.File]::WriteAllText($env:AVDT_MANAGER_FAILURE_STATE, ($State | ConvertTo-Json -Depth 40))
}
function Start-CodexMcpConnection {
    param([string]$CodexPath, [string]$CodexHome)
    $state = Read-FixtureState
    $state.startCount++
    $state.events += "start:$($state.startCount)"
    Save-FixtureState $state
    return [pscustomobject]@{ Id = $state.startCount; TransportFaulted = $false; Stopped = $false }
}
function Invoke-CodexMcpRequest {
    param($Connection, [string]$Method, $Params, [int]$TimeoutSeconds = 60)
    if ($Connection.TransportFaulted) { throw 'A faulted fixture connection was reused.' }
    $state = Read-FixtureState
    $state.events += "request:$($Connection.Id):$Method"
    switch ($Method) {
        'config/read' {
            if ($Connection.Id -gt 1 -and $state.scenario -eq 'TimeoutUnconfirmed') {
                $state.events += 'recovery-read-failed'
                Save-FixtureState $state
                $Connection.TransportFaulted = $true
                throw 'Synthetic recovery read failed.'
            }
            Save-FixtureState $state
            $copy = $state.config | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable
            return @{ config = $copy; layers = @(@{ name = @{ type = 'user' }; version = "v$($state.revision)"; config = $copy }) }
        }
        'config/batchWrite' {
            foreach ($edit in $Params.edits) {
                $segments = $edit.keyPath -split '\.'
                $target = $state.config
                for ($index = 0; $index -lt $segments.Count - 1; $index++) {
                    if (-not $target.ContainsKey($segments[$index])) { $target[$segments[$index]] = @{} }
                    $target = $target[$segments[$index]]
                }
                $target[$segments[-1]] = $edit.value
                if ($edit.keyPath -eq 'mcp_servers.avalonia') { $state.registrationWriteCount++ }
            }
            $state.revision++
            $state.events += 'write-applied'
            Save-FixtureState $state
            if ($state.scenario -in @('TimeoutConfirmed', 'TimeoutUnconfirmed')) {
                $global:AvdtManagerFailureHeldLocks[$Connection.Id] = [IO.File]::Open($state.command, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                $Connection.TransportFaulted = $true
                throw 'Synthetic write response timeout after applying the write.'
            }
            return @{ status = 'ok' }
        }
        'config/mcpServer/reload' { Save-FixtureState $state; return @{} }
        'mcpServerStatus/list' {
            $global:AvdtManagerFailureHeldLocks[$Connection.Id] = [IO.File]::Open($state.command, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            $state.events += 'catalog-failed-with-command-locked'
            Save-FixtureState $state
            throw 'Synthetic late catalog failure.'
        }
        default { throw "Unexpected fixture RPC: $Method" }
    }
}
function Stop-CodexMcpConnection {
    param($Connection, [int]$TimeoutSeconds = 5)
    if ($Connection.Stopped) { return }
    $Connection.Stopped = $true
    if ($global:AvdtManagerFailureHeldLocks.ContainsKey($Connection.Id)) {
        $global:AvdtManagerFailureHeldLocks[$Connection.Id].Dispose()
        $global:AvdtManagerFailureHeldLocks.Remove($Connection.Id)
    }
    $state = Read-FixtureState
    $state.events += "stop:$($Connection.Id)"
    Save-FixtureState $state
}
'@

$fakeCli = @'
param([string]$Verb, [string]$Operation, [string]$Name)
if ($Verb -ne 'mcp' -or $Operation -ne 'remove' -or $Name -ne 'avalonia') { throw 'Unexpected fixture CLI.' }
$state = Get-Content -LiteralPath $env:AVDT_MANAGER_FAILURE_STATE -Raw | ConvertFrom-Json -AsHashtable
$state.config.mcp_servers.Remove('avalonia')
$state.removeCount++
$state.revision++
$state.events += 'cli-remove'
[IO.File]::WriteAllText($env:AVDT_MANAGER_FAILURE_STATE, ($state | ConvertTo-Json -Depth 40))
'@

function New-FailureCase {
    param([string]$Name, [string]$Scenario)
    $caseRoot = Join-Path $testRoot $Name
    $installRoot = Join-Path $caseRoot 'share'
    $binDirectory = Join-Path $caseRoot 'bin'
    $statePath = Join-Path $caseRoot 'fixture.json'
    New-Item -ItemType Directory -Path $installRoot, $binDirectory -Force | Out-Null
    $state = @{
        config = @{ mcp_servers = @{}; plugins = @{} }
        scenario = $Scenario; revision = 1; startCount = 0; registrationWriteCount = 0; removeCount = 0
        command = (Join-Path $binDirectory 'AvaloniaDevTool.exe'); events = @()
    }
    [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 20))
    return @{ Root = $caseRoot; InstallRoot = $installRoot; BinDirectory = $binDirectory; StatePath = $statePath; Command = $state.command }
}

function Invoke-RejectedInstall {
    param([hashtable]$Case)
    [Environment]::SetEnvironmentVariable($fixtureStateVariable, $Case.StatePath, 'Process')
    $rejected = $false
    try {
        & (Join-Path $toolsDirectory 'Manage-CodexMcp.ps1') -Action Install -PackagePath $PackagePath `
            -InstallRoot $Case.InstallRoot -BinDirectory $Case.BinDirectory -CodexPath (Join-Path $toolsDirectory 'fake-codex.cmd') | Out-Null
    } catch { $rejected = $true }
    Assert-True $rejected 'The transaction fixture unexpectedly reported success.'
    return (Get-Content -LiteralPath $Case.StatePath -Raw | ConvertFrom-Json -AsHashtable)
}

try {
    New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
    Copy-Item -LiteralPath $ManagerPath -Destination (Join-Path $toolsDirectory 'Manage-CodexMcp.ps1')
    Copy-Item -LiteralPath (Join-Path (Split-Path $ManagerPath -Parent) 'Install-AvaloniaDevTool.ps1') -Destination $toolsDirectory
    [IO.File]::WriteAllText((Join-Path $toolsDirectory 'Codex-Mcp.ps1'), $mockHelper, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $toolsDirectory 'Fake-CodexCli.ps1'), $fakeCli, [Text.UTF8Encoding]::new($false))
    $pwsh = (Get-Command pwsh -CommandType Application -ErrorAction Stop).Source
    $batch = "@echo off`r`n`"$pwsh`" -NoLogo -NoProfile -NonInteractive -File `"%~dp0Fake-CodexCli.ps1`" %*`r`nexit /b %errorlevel%`r`n"
    [IO.File]::WriteAllText((Join-Path $toolsDirectory 'fake-codex.cmd'), $batch, [Text.ASCIIEncoding]::new())
    [Environment]::SetEnvironmentVariable('AVDT_BUNDLE_CACHE', (Join-Path $testRoot 'bundle-cache'), 'Process')
    $bundle = & $PackagePath --bundle-info | Out-String | ConvertFrom-Json
    Assert-True ($LASTEXITCODE -eq 0) 'Could not inspect the real bundle.'
    $programVersion = [string]$bundle.applicationVersion
    $packageHash = (Get-FileHash -LiteralPath $PackagePath).Hash.ToLowerInvariant()

    Write-Host 'Checking an unowned stable executable is preserved.'
    $unowned = New-FailureCase -Name unowned -Scenario CatalogFailure
    [IO.File]::WriteAllText($unowned.Command, 'Unowned executable placeholder.')
    $unownedHash = (Get-FileHash -LiteralPath $unowned.Command).Hash
    $state = Invoke-RejectedInstall -Case $unowned
    Assert-True ((Get-FileHash -LiteralPath $unowned.Command).Hash -eq $unownedHash) 'The unowned command was changed.'
    Assert-True ($state.startCount -eq 0 -and $state.registrationWriteCount -eq 0) 'The unowned command rejection occurred after configuration side effects.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $unowned.InstallRoot 'current.json'))) 'An unowned command was adopted.'

    Write-Host 'Checking late failure restores metadata without resurrecting a missing command.'
    $missing = New-FailureCase -Name missing-command -Scenario CatalogFailure
    $versionDirectory = Join-Path $missing.InstallRoot "versions/$programVersion"
    New-Item -ItemType Directory -Path $versionDirectory -Force | Out-Null
    $versionExecutable = Join-Path $versionDirectory 'AvaloniaDevTool.exe'
    Copy-Item -LiteralPath $PackagePath -Destination $versionExecutable
    $manifestPath = Join-Path $missing.InstallRoot 'current.json'
    $originalManifest = @{
        version = $programVersion; executable = $versionExecutable; command = $missing.Command
        sha256 = $packageHash; installedAt = '2026-01-01T00:00:00Z'
    } | ConvertTo-Json
    [IO.File]::WriteAllText($manifestPath, $originalManifest, [Text.UTF8Encoding]::new($false))
    $state = Invoke-RejectedInstall -Case $missing
    Assert-True ([IO.File]::ReadAllText($manifestPath) -ceq $originalManifest) 'The previous manifest bytes were not restored.'
    Assert-True (-not (Test-Path -LiteralPath $missing.Command)) 'Rollback resurrected a command that had originally been missing.'
    Assert-True (-not $state.config.mcp_servers.ContainsKey('avalonia')) 'Late failure left its configuration registered.'
    Assert-True ($state.events -contains 'catalog-failed-with-command-locked') 'The late failure did not hold the intended command lock.'
    Assert-True ($global:AvdtManagerFailureHeldLocks.Count -eq 0) 'The owning connection was not stopped before rollback.'

    Write-Host 'Checking an applied write with a lost response is inspected and revoked once.'
    $confirmed = New-FailureCase -Name timeout-confirmed -Scenario TimeoutConfirmed
    $state = Invoke-RejectedInstall -Case $confirmed
    Assert-True ($state.registrationWriteCount -eq 1) 'The original registration write was replayed.'
    Assert-True ($state.startCount -eq 2 -and $state.removeCount -eq 1) 'The result was not inspected through one fresh connection and revoked once.'
    Assert-True (-not $state.config.mcp_servers.ContainsKey('avalonia')) 'The confirmed applied registration was not revoked.'
    Assert-True (-not (Test-Path -LiteralPath $confirmed.Command)) 'A confirmed rollback did not restore the missing command state.'
    Assert-True ([array]::IndexOf($state.events, 'stop:1') -lt [array]::IndexOf($state.events, 'start:2')) 'A faulted connection remained active during recovery.'

    Write-Host 'Checking an unconfirmed write keeps the executable usable.'
    $unconfirmed = New-FailureCase -Name timeout-unconfirmed -Scenario TimeoutUnconfirmed
    $state = Invoke-RejectedInstall -Case $unconfirmed
    Assert-True ($state.registrationWriteCount -eq 1 -and $state.removeCount -eq 0) 'An unconfirmed result was mutated or replayed.'
    Assert-True ($state.events -contains 'recovery-read-failed') 'The recovery read failure was not exercised.'
    Assert-True ($state.config.mcp_servers.ContainsKey('avalonia')) 'The applied but unconfirmed registration was silently removed.'
    Assert-True (Test-Path -LiteralPath $unconfirmed.Command) 'An unconfirmed write left a registration pointing at a missing command.'
    Assert-True ((Get-FileHash -LiteralPath $unconfirmed.Command).Hash.ToLowerInvariant() -eq $packageHash) 'The retained executable is not the verified package.'
    Assert-True (Test-Path -LiteralPath (Join-Path $unconfirmed.InstallRoot 'current.json')) 'The retained executable lost its installation metadata.'
    Write-Output 'Manager failure checks passed: unowned files, missing-command rollback, locked command release, applied-response timeout recovery without replay, and unconfirmed executable retention. RPC outcomes were simulated; no real Codex configuration was used.'
} finally {
    [Environment]::SetEnvironmentVariable($fixtureStateVariable, $beforeStatePath, 'Process')
    [Environment]::SetEnvironmentVariable('AVDT_BUNDLE_CACHE', $beforeBundleCache, 'Process')
    foreach ($heldLock in @($global:AvdtManagerFailureHeldLocks.Values)) { $heldLock.Dispose() }
    Remove-Variable -Name AvdtManagerFailureHeldLocks -Scope Global
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to clean outside the temporary root.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
