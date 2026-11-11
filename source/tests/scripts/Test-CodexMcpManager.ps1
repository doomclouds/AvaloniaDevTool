[CmdletBinding()]
param(
    [string]$CodexPath = 'codex',
    [string]$PackagePath,
    [string]$ManagerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$repository = Split-Path $sourceRoot -Parent
if (-not $PackagePath) {
    $PackagePath = Join-Path $repository 'plugins/avalonia-devtool/mcp/AvaloniaDevTool.exe'
}
if (-not $ManagerPath) {
    $ManagerPath = Join-Path $sourceRoot 'tools/Manage-CodexMcp.ps1'
}
$PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
$ManagerPath = (Resolve-Path -LiteralPath $ManagerPath).Path
. (Join-Path (Split-Path $ManagerPath -Parent) 'Codex-Mcp.ps1')

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('avdt-codex-manager-' + [Guid]::NewGuid().ToString('N'))
$codexTestHome = Join-Path $testRoot 'home'
$installRoot = Join-Path $testRoot 'share'
$binDirectory = Join-Path $testRoot 'bin'
$configFile = Join-Path $codexTestHome 'config.toml'
$stableCommand = Join-Path $binDirectory 'AvaloniaDevTool.exe'
$beforeProcessHome = [Environment]::GetEnvironmentVariable('CODEX_HOME', 'Process')
$beforeBundleCache = [Environment]::GetEnvironmentVariable('AVDT_BUNDLE_CACHE', 'Process')
$beforeUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$beforeUserHome = [Environment]::GetEnvironmentVariable('AVALONIA_DEVTOOL_HOME', 'User')
$realHome = if ($beforeProcessHome) { $beforeProcessHome } else { Join-Path $env:USERPROFILE '.codex' }
$realConfig = Join-Path $realHome 'config.toml'
$realConfigHash = if (Test-Path -LiteralPath $realConfig) { (Get-FileHash -LiteralPath $realConfig).Hash } else { $null }
$testFailure = $null

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-CodexCli {
    param([string[]]$Arguments)
    $output = & $CodexPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Codex CLI failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
    return ($output -join "`n")
}

function Read-CodexConfig {
    param([string]$Directory = $codexTestHome)
    $connection = Start-CodexMcpConnection -CodexPath $CodexPath -CodexHome $Directory
    try {
        return Invoke-CodexMcpRequest -Connection $connection -Method 'config/read' -Params @{ includeLayers = $true }
    } finally {
        Stop-CodexMcpConnection -Connection $connection
    }
}

function Invoke-Manager {
    param([string]$Action, [hashtable]$Additional = @{}, [string]$Directory = $codexTestHome)
    $parameters = @{
        Action       = $Action
        InstallRoot  = $installRoot
        BinDirectory = $binDirectory
        CodexPath    = $CodexPath
        CodexHome    = $Directory
    }
    foreach ($entry in $Additional.GetEnumerator()) { $parameters[$entry.Key] = $entry.Value }
    return & $ManagerPath @parameters
}

function Assert-Rejected {
    param([scriptblock]$Operation, [string]$Message)
    $rejected = $false
    try { & $Operation | Out-Null } catch { $rejected = $true }
    Assert-True $rejected $Message
}

try {
    New-Item -ItemType Directory -Path $codexTestHome -Force | Out-Null
    [Environment]::SetEnvironmentVariable('CODEX_HOME', $codexTestHome, 'Process')
    [Environment]::SetEnvironmentVariable('AVDT_BUNDLE_CACHE', (Join-Path $testRoot 'runtime-cache'), 'Process')
    $bundleInfo = (& $PackagePath --bundle-info | Out-String | ConvertFrom-Json)
    Assert-True ($LASTEXITCODE -eq 0) 'The test package did not expose bundle info.'
    $programVersion = [string]$bundleInfo.applicationVersion
    $packageHash = (Get-FileHash -LiteralPath $PackagePath).Hash

    Invoke-CodexCli -Arguments @('plugin', 'marketplace', 'add', $repository, '--json') | Out-Null
    Invoke-CodexCli -Arguments @('plugin', 'add', 'avalonia-devtool@avalonia-devtool', '--json') | Out-Null
    Write-Host 'Checking initial installation and standalone registration.'
    $installed = Invoke-Manager -Action Install -Additional @{ PackagePath = $PackagePath; ExpectedSha256 = $packageHash }
    Assert-True ($installed.Registered -eq $true) 'Install did not report a registered server.'
    Assert-True ($installed.ProgramVersion -eq $programVersion) 'Install used the plugin version instead of the embedded EXE version.'
    Assert-True ($installed.ToolCount -eq 94) 'The installed server did not expose the expected 94 tools.'
    Assert-True (Test-Path -LiteralPath $stableCommand) 'The stable executable is missing.'
    Assert-True ((Get-FileHash -LiteralPath $stableCommand).Hash -eq $packageHash) 'The installed command differs from the package.'

    $config = (Read-CodexConfig)['config']
    $server = $config['mcp_servers']['avalonia']
    $plugin = $config['plugins']['avalonia-devtool@avalonia-devtool']
    Assert-True ($server['command'] -eq $stableCommand -or $server['command'].Replace('/', '\') -eq $stableCommand) 'Codex is not registered against the stable command.'
    Assert-True ($plugin['enabled'] -eq $true) 'The plugin skills were not retained.'
    Assert-True ($plugin['mcp_servers']['avalonia']['enabled'] -eq $false) 'The bundled MCP was not disabled.'
    $connection = Start-CodexMcpConnection -CodexPath $CodexPath -CodexHome $codexTestHome
    try {
        $catalog = Invoke-CodexMcpRequest -Connection $connection -Method 'mcpServerStatus/list' -Params @{ serverName = 'avalonia'; detail = 'toolsAndAuthOnly' }
        $route = @($catalog['data'] | Where-Object { $_['name'] -eq 'avalonia' })
        Assert-True ($route.Count -eq 1) 'Avalonia was loaded more than once.'
        Assert-True (-not $route[0]['pluginId']) 'The server is still routed through the plugin.'
        Assert-True ($route[0]['tools'].Count -eq 94) 'Codex tool discovery truncated the standalone directory.'
        foreach ($name in @('attach-to-app', 'tree', 'recording-start', 'select-machine')) {
            Assert-True ($route[0]['tools'].ContainsKey($name)) "The standalone directory is missing $name."
        }
    } finally {
        Stop-CodexMcpConnection -Connection $connection
    }

    Write-Host 'Checking status, idempotent update, rollback, and invalid packages.'
    $status = Invoke-Manager -Action Status
    Assert-True ($status.Registered -eq $true -and $status.ProgramVersion -eq $programVersion) 'Status does not match the installed program.'
    Write-Host 'Checking same-version update.'
    $updated = Invoke-Manager -Action Update -Additional @{ PackagePath = $PackagePath }
    Assert-True ($updated.ProgramVersion -eq $programVersion -and $updated.Registered -eq $true) 'Idempotent update changed or unregistered the package.'
    Assert-True ((Get-FileHash -LiteralPath $stableCommand).Hash -eq $packageHash) 'Idempotent update changed the binary.'
    $connection = Start-CodexMcpConnection -CodexPath $CodexPath -CodexHome $codexTestHome
    try {
        $snapshot = Invoke-CodexMcpRequest -Connection $connection -Method 'config/read' -Params @{ includeLayers = $true }
        $userLayer = @($snapshot['layers'] | Where-Object { $_['name']['type'] -eq 'user' })[0]
        $null = Invoke-CodexMcpRequest -Connection $connection -Method 'config/batchWrite' -Params @{
            expectedVersion = $userLayer['version']
            reloadUserConfig = $true
            edits = @(
                @{ keyPath = 'mcp_servers.avalonia.tool_timeout_sec'; mergeStrategy = 'replace'; value = 75 },
                @{ keyPath = 'mcp_servers.avalonia.env'; mergeStrategy = 'replace'; value = @{ AVDT_MANAGER_TEST_VALUE = 'retained' } }
            )
        }
    } finally {
        Stop-CodexMcpConnection -Connection $connection
    }
    Invoke-Manager -Action Update -Additional @{ PackagePath = $PackagePath } | Out-Null
    $updatedConfig = (Read-CodexConfig)['config']['mcp_servers']['avalonia']
    Assert-True ($updatedConfig['tool_timeout_sec'] -eq 75) 'Update discarded the configured tool timeout.'
    Assert-True ($updatedConfig['env']['AVDT_MANAGER_TEST_VALUE'] -eq 'retained') 'Update discarded the configured server environment.'
    Write-Host 'Checking retained-version rollback.'
    $rolledBack = Invoke-Manager -Action Rollback -Additional @{ Version = $programVersion }
    Assert-True ($rolledBack.ProgramVersion -eq $programVersion -and $rolledBack.Registered -eq $true) 'Selecting a retained version failed.'
    $configBeforeRejection = (Get-FileHash -LiteralPath $configFile).Hash
    Assert-Rejected { Invoke-Manager -Action Rollback -Additional @{ Version = '999.999.999' } } 'An unknown rollback version was accepted.'
    Assert-True ((Get-FileHash -LiteralPath $configFile).Hash -eq $configBeforeRejection) 'Rejected rollback changed Codex configuration.'
    Assert-Rejected { Invoke-Manager -Action Update -Additional @{ PackagePath = $PackagePath; ExpectedSha256 = ('0' * 64) } } 'A mismatched package SHA-256 was accepted.'
    Assert-True ((Get-FileHash -LiteralPath $configFile).Hash -eq $configBeforeRejection) 'A rejected package changed Codex configuration.'

    Write-Host 'Checking protection for an unrelated server registration.'
    $conflictHome = Join-Path $testRoot 'conflict-home'
    New-Item -ItemType Directory -Path $conflictHome -Force | Out-Null
    [Environment]::SetEnvironmentVariable('CODEX_HOME', $conflictHome, 'Process')
    Invoke-CodexCli -Arguments @('mcp', 'add', 'avalonia', '--', (Join-Path $env:WINDIR 'System32/cmd.exe'), '/d', '/c', 'exit') | Out-Null
    $conflictConfig = Join-Path $conflictHome 'config.toml'
    $conflictHash = (Get-FileHash -LiteralPath $conflictConfig).Hash
    Assert-Rejected { Invoke-Manager -Action Install -Directory $conflictHome -Additional @{ PackagePath = $PackagePath } } 'Install overwrote an unrelated avalonia server.'
    Assert-True ((Get-FileHash -LiteralPath $conflictConfig).Hash -eq $conflictHash) 'The unrelated server configuration was changed.'
    [Environment]::SetEnvironmentVariable('CODEX_HOME', $codexTestHome, 'Process')

    Write-Host 'Checking uninstall retention and owned-file cleanup.'
    $unknownFile = Join-Path $installRoot 'user-note.txt'
    [IO.File]::WriteAllText($unknownFile, 'User data must survive uninstall.')
    $recordingDirectory = Join-Path $installRoot 'recordings'
    New-Item -ItemType Directory -Path $recordingDirectory -Force | Out-Null
    $recording = Join-Path $recordingDirectory 'user-recording.mp4'
    [IO.File]::WriteAllText($recording, 'Synthetic user recording.')
    $uninstalled = Invoke-Manager -Action Uninstall
    Assert-True ($uninstalled.Registered -eq $false) 'Uninstall left the MCP registered.'
    Assert-True (Test-Path -LiteralPath $stableCommand) 'Default uninstall deleted the retained executable.'
    Assert-True (Test-Path -LiteralPath $unknownFile) 'Default uninstall deleted unrelated data.'
    $config = (Read-CodexConfig)['config']
    Assert-True (-not $config['mcp_servers'].ContainsKey('avalonia')) 'The MCP configuration still contains avalonia.'
    Assert-True ($config['plugins']['avalonia-devtool@avalonia-devtool']['mcp_servers']['avalonia']['enabled'] -eq $false) 'Uninstall unexpectedly re-enabled the bundled MCP.'

    $markerPath = Join-Path $installRoot 'codex-mcp.json'
    $originalMarker = [IO.File]::ReadAllText($markerPath)
    try {
        $tamperedMarker = $originalMarker | ConvertFrom-Json -AsHashtable
        $tamperedMarker.files += @{ path = $unknownFile; sha256 = (Get-FileHash -LiteralPath $unknownFile).Hash.ToLowerInvariant() }
        [IO.File]::WriteAllText($markerPath, ($tamperedMarker | ConvertTo-Json -Depth 20))
        Assert-Rejected { Invoke-Manager -Action Uninstall -Additional @{ RemoveFiles = $true } } 'RemoveFiles trusted an unknown file added to the marker.'
        Assert-True (Test-Path -LiteralPath $unknownFile) 'A tampered marker caused unrelated data loss.'
        Assert-True (Test-Path -LiteralPath $stableCommand) 'A rejected marker partially deleted the installation.'
    } finally {
        [IO.File]::WriteAllText($markerPath, $originalMarker)
    }
    $installedManager = Join-Path $installRoot 'tools/Manage-CodexMcp.ps1'
    $originalManager = [IO.File]::ReadAllBytes($installedManager)
    try {
        [IO.File]::AppendAllText($installedManager, "`n# Synthetic user modification.`n")
        Assert-Rejected { Invoke-Manager -Action Uninstall -Additional @{ RemoveFiles = $true } } 'RemoveFiles deleted a modified installed script.'
        Assert-True (Test-Path -LiteralPath $installedManager) 'A user-modified script was removed.'
        Assert-True (Test-Path -LiteralPath $stableCommand) 'A rejected checksum partially deleted the installation.'
    } finally {
        [IO.File]::WriteAllBytes($installedManager, $originalManager)
    }
    Invoke-Manager -Action Uninstall -Additional @{ RemoveFiles = $true } | Out-Null
    Assert-True (-not (Test-Path -LiteralPath $stableCommand)) 'RemoveFiles retained the manager-owned stable executable.'
    Assert-True (Test-Path -LiteralPath $unknownFile) 'RemoveFiles deleted an unknown file.'
    Assert-True (Test-Path -LiteralPath $recording) 'RemoveFiles deleted user recordings.'
    Assert-True ([Environment]::GetEnvironmentVariable('Path', 'User') -ceq $beforeUserPath) 'The tests changed the user PATH.'
    Assert-True ([Environment]::GetEnvironmentVariable('AVALONIA_DEVTOOL_HOME', 'User') -ceq $beforeUserHome) 'The tests changed the user install variable.'
    if ($null -ne $realConfigHash) {
        Assert-True ((Get-FileHash -LiteralPath $realConfig).Hash -eq $realConfigHash) 'The isolated tests changed the real Codex configuration.'
    }
    Write-Output 'Codex MCP manager checks passed: isolated installation, 94 standalone tools, retained skills, idempotent update, rollback, rejected input isolation, configuration conflict protection, uninstall retention, and owned-file cleanup.'
} catch {
    $testFailure = $_
    throw
} finally {
    [Environment]::SetEnvironmentVariable('CODEX_HOME', $beforeProcessHome, 'Process')
    [Environment]::SetEnvironmentVariable('AVDT_BUNDLE_CACHE', $beforeBundleCache, 'Process')
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to clean a test directory outside the temporary root.'
    }
    # Codex can leave an optional marketplace clone running after its parent exits.
    # Only descendants naming this test's unique directory are owned by this fixture.
    $remainingProcesses = @(Get-CimInstance Win32_Process | Where-Object {
            $_.ProcessId -ne $PID -and $_.CommandLine -and
            $_.CommandLine.Replace('/', '\').Contains($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)
        })
    foreach ($remainingProcess in $remainingProcesses) {
        try {
            $ownedProcess = Get-Process -Id $remainingProcess.ProcessId -ErrorAction Stop
            $ownedProcess.Kill($true)
            [void]$ownedProcess.WaitForExit(5000)
            $ownedProcess.Dispose()
        } catch {
            Write-Warning "Temporary process $($remainingProcess.ProcessId) already exited or could not be stopped."
        }
    }
    if (Test-Path -LiteralPath $resolvedRoot) {
        $cleanupWatch = [Diagnostics.Stopwatch]::StartNew()
        while (Test-Path -LiteralPath $resolvedRoot) {
            try {
                Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
            } catch {
                if ($cleanupWatch.ElapsedMilliseconds -ge 10000) {
                    if ($testFailure) {
                        Write-Warning "Preserving the original test failure; temporary cleanup remains at $resolvedRoot."
                        break
                    }
                    throw
                }
                Start-Sleep -Milliseconds 200
            }
        }
    }
}
