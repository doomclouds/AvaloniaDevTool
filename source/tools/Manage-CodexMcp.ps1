#Requires -Version 7.2
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Install', 'Update', 'Rollback', 'Uninstall', 'Status')]
    [string]$Action,
    [string]$PackagePath = '',
    [ValidatePattern('^(?:\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?)?$')]
    [string]$Version = '',
    [ValidatePattern('^(?:[0-9a-fA-F]{64})?$')]
    [string]$ExpectedSha256 = '',
    [string]$InstallRoot = (Join-Path $env:USERPROFILE '.local/share/avalonia-devtool'),
    [string]$BinDirectory = (Join-Path $env:USERPROFILE '.local/bin'),
    [string]$CodexPath = 'codex',
    [string]$CodexHome = '',
    [switch]$RemoveFiles
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'This manager supports Windows x64 bundles.' }
. (Join-Path $PSScriptRoot 'Codex-Mcp.ps1')

$pluginId = 'avalonia-devtool@avalonia-devtool'
$root = [IO.Path]::GetFullPath($InstallRoot)
$bin = [IO.Path]::GetFullPath($BinDirectory)
$command = Join-Path $bin 'AvaloniaDevTool.exe'
$currentPath = Join-Path $root 'current.json'
$statePath = Join-Path $root 'codex-mcp.json'
$installer = Join-Path $PSScriptRoot 'Install-AvaloniaDevTool.ps1'
$current = $null

function Invoke-BundleInspection {
    param([string]$Executable, [string]$Argument)
    $output = & $Executable $Argument
    if ($LASTEXITCODE -ne 0) { throw "Package inspection failed: $Argument" }
    return ($output -join "`n" | ConvertFrom-Json -AsHashtable)
}

function Remove-CodexRegistration {
    $originalHome = [Environment]::GetEnvironmentVariable('CODEX_HOME', 'Process')
    try {
        if ($CodexHome) { $env:CODEX_HOME = [IO.Path]::GetFullPath($CodexHome) }
        $null = & $CodexPath mcp remove avalonia
        if ($LASTEXITCODE -ne 0) { throw 'Codex did not remove the avalonia registration.' }
    } finally {
        [Environment]::SetEnvironmentVariable('CODEX_HOME', $originalHome, 'Process')
    }
}

if ($Action -eq 'Rollback' -and -not $Version) { throw 'Rollback requires -Version.' }
if ($RemoveFiles -and $Action -ne 'Uninstall') { throw '-RemoveFiles is only valid with Uninstall.' }
if ($Action -ne 'Status' -and -not $PSCmdlet.ShouldProcess($command, "$Action the avalonia Codex MCP")) { return }

$connection = $null
$previousConfig = $null
$configurationAttempted = $false
$programChanged = $false
$installation = $null
$packageDigest = ''
$marker = $null
$managementMutex = $null
$managementLockHeld = $false
$scriptBackups = @{}
$scriptWrites = [Collections.Generic.List[string]]::new()
$temporaryFiles = [Collections.Generic.List[string]]::new()
$previousCommandExisted = $false
$previousCurrentText = $null
try {
    if ($Action -ne 'Status') {
        $lockHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($root.ToLowerInvariant())))
        $managementMutex = [Threading.Mutex]::new($false, ('Local\AvaloniaDevTool.CodexMcp.' + $lockHash.Substring(0, 24)))
        try { $managementLockHeld = $managementMutex.WaitOne(15000) }
        catch [Threading.AbandonedMutexException] { $managementLockHeld = $true }
        if (-not $managementLockHeld) { throw 'Another management operation is using this installation. Retry after it completes.' }
    }
    $previousCommandExisted = Test-Path -LiteralPath $command -PathType Leaf
    if (Test-Path -LiteralPath $currentPath) { $previousCurrentText = [IO.File]::ReadAllText($currentPath) }
    $current = if ($null -ne $previousCurrentText) { $previousCurrentText | ConvertFrom-Json -AsHashtable } else { $null }
    $marker = if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json -AsHashtable } else { $null }
    if ($Action -in @('Install', 'Update', 'Rollback') -and (Test-Path -LiteralPath $command) -and -not $current) {
        throw 'The stable command already exists without an installation manifest. It was preserved.'
    }
    if ($current -and ($current.command -cne $command -or
            -not ([IO.Path]::GetFullPath($current.executable)).StartsWith((Join-Path $root 'versions') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
        throw 'The current installation manifest does not match this installation.'
    }
    $connectionArguments = @{ CodexPath = $CodexPath }
    if ($CodexHome) { $connectionArguments.CodexHome = $CodexHome }
    $connection = Start-CodexMcpConnection @connectionArguments
    $before = Invoke-CodexMcpRequest -Connection $connection -Method 'config/read' -Params @{ includeLayers = $true }
    $configuration = $before.config
    $originalUserLayer = @($before.layers | Where-Object { $_.name.type -eq 'user' })[0]
    $userConfiguration = $originalUserLayer.config
    $registered = $configuration.mcp_servers.ContainsKey('avalonia')
    if ($registered) {
        $existing = $configuration.mcp_servers.avalonia
        if ($existing.command -cne $command -or @($existing.args).Count -ne 1 -or $existing.args[0] -ne 'mcp') {
            throw 'An avalonia server already uses a different command. Its configuration was preserved.'
        }
        if (-not $userConfiguration.ContainsKey('mcp_servers') -or -not $userConfiguration.mcp_servers.ContainsKey('avalonia')) {
            throw 'The avalonia server belongs to another configuration layer. Its configuration was preserved.'
        }
    }
    $hasPlugin = $configuration.plugins.ContainsKey($pluginId)
    $plugin = if ($hasPlugin) { $configuration.plugins[$pluginId] } else { $null }
    $bundledEnabled = if ($plugin -and $plugin.ContainsKey('mcp_servers') -and $plugin.mcp_servers.ContainsKey('avalonia')) {
        $plugin.mcp_servers.avalonia.enabled
    } else { $null }

    if ($Action -eq 'Status') {
        $count = 0
        if ($registered) {
            $catalog = Invoke-CodexMcpRequest -Connection $connection -Method 'mcpServerStatus/list' -Params @{ serverName = 'avalonia'; detail = 'toolsAndAuthOnly' }
            $server = @($catalog.data | Where-Object name -eq 'avalonia')
            if ($server.Count -ne 1 -or $server[0].toolsError) { throw 'The registered avalonia MCP catalog is unavailable.' }
            $count = $server[0].tools.Count
        }
        return [pscustomobject]@{
            Action = $Action; ProgramVersion = $(if ($current) { $current.version } else { $null })
            Command = $command; Registered = $registered; ToolCount = $count; PluginBundledMcpEnabled = $bundledEnabled
        }
    }

    if ($Action -eq 'Uninstall') {
        if ($RemoveFiles) {
            if (-not $marker -or $marker.command -cne $command -or $marker.installRoot -cne $root) {
                throw 'This installation has no matching management marker; files were preserved.'
            }
            foreach ($file in $marker.files) {
                $ownedPath = [IO.Path]::GetFullPath($file.path)
                $withinRoot = $ownedPath.StartsWith($root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
                if (-not $withinRoot -and $ownedPath -cne $command) { throw 'Management marker contains an unrelated path.' }
                if ($ownedPath -cne $command) {
                    $relative = [IO.Path]::GetRelativePath($root, $ownedPath).Replace('\', '/')
                    if ($relative -notmatch '^versions/\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?/AvaloniaDevTool\.exe$' -and
                        $relative -notin @('tools/Manage-CodexMcp.ps1', 'tools/Install-AvaloniaDevTool.ps1', 'tools/Codex-Mcp.ps1')) {
                        throw 'Management marker contains a file outside the program file list.'
                    }
                }
                if ((Test-Path -LiteralPath $ownedPath) -and (Get-FileHash -LiteralPath $ownedPath).Hash -ine $file.sha256) {
                    throw "An installed file has changed; it was preserved: $ownedPath"
                }
            }
        }
        if ($registered) { Remove-CodexRegistration }
        $removed = Invoke-CodexMcpRequest -Connection $connection -Method 'config/read' -Params @{ includeLayers = $true }
        if ($removed.config.mcp_servers.ContainsKey('avalonia')) { throw 'The avalonia registration is still present; program files were preserved.' }
        if ($RemoveFiles) {
            foreach ($file in $marker.files) {
                if (Test-Path -LiteralPath $file.path) { Remove-Item -LiteralPath $file.path -ErrorAction Stop }
            }
            foreach ($metadata in @($currentPath, $statePath)) {
                if (Test-Path -LiteralPath $metadata) { Remove-Item -LiteralPath $metadata -ErrorAction Stop }
            }
        }
        return [pscustomobject]@{
            Action = $Action; ProgramVersion = $(if ($current) { $current.version } else { $null })
            Command = $command; Registered = $false; ToolCount = 0; PluginBundledMcpEnabled = $bundledEnabled
        }
    }

    if ($Action -eq 'Rollback') {
        $PackagePath = Join-Path $root "versions/$Version/AvaloniaDevTool.exe"
        if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw "Version $Version is not installed." }
    } elseif (-not $PackagePath) {
        foreach ($candidate in @(
                (Join-Path $PSScriptRoot 'AvaloniaDevTool.exe'),
                (Join-Path $PSScriptRoot '../mcp/AvaloniaDevTool.exe'),
                (Join-Path $PSScriptRoot '../../plugins/avalonia-devtool/mcp/AvaloniaDevTool.exe')
            )) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $PackagePath = $candidate; break }
        }
        if (-not $PackagePath) { throw 'Provide -PackagePath from the new distribution, or run its management script beside AvaloniaDevTool.exe.' }
    }
    $PackagePath = (Resolve-Path -LiteralPath $PackagePath).Path
    $packageDigest = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ExpectedSha256 -and $packageDigest -ine $ExpectedSha256) { throw 'Package SHA-256 does not match. Nothing was installed.' }
    $bundle = Invoke-BundleInspection -Executable $PackagePath -Argument '--bundle-info'
    if ($bundle.applicationVersion -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$' -or $bundle.runtimeIdentifier -ne 'win-x64') {
        throw 'This management script requires a versioned Windows x64 bundle.'
    }
    if ($Version -and $Version -cne $bundle.applicationVersion) { throw 'Version does not match the executable bundle; plugin and EXE versions can differ.' }
    $Version = $bundle.applicationVersion
    $verified = Invoke-BundleInspection -Executable $PackagePath -Argument '--bundle-verify'
    if (-not $verified.verified) { throw 'Bundle verification did not succeed.' }
    $installation = & $installer -PackagePath $PackagePath -Version $Version -ExpectedSha256 $packageDigest `
        -InstallRoot $root -BinDirectory $bin -SkipEnvironmentUpdate -Confirm:$false
    $programChanged = $true

    $fresh = Invoke-CodexMcpRequest -Connection $connection -Method 'config/read' -Params @{ includeLayers = $true }
    if (($fresh.config.mcp_servers.ContainsKey('avalonia')) -ne $registered -or
        ($registered -and ($fresh.config.mcp_servers.avalonia | ConvertTo-Json -Depth 30 -Compress) -cne ($configuration.mcp_servers.avalonia | ConvertTo-Json -Depth 30 -Compress))) {
        throw 'The avalonia registration changed during installation.'
    }
    $userLayer = @($fresh.layers | Where-Object { $_.name.type -eq 'user' })[0]
    $previousConfig = $userLayer.config
    $serverConfiguration = if ($registered) {
        $previousConfig.mcp_servers.avalonia | ConvertTo-Json -Depth 30 | ConvertFrom-Json -AsHashtable
    } else { @{} }
    $serverConfiguration.command = $command
    $serverConfiguration.args = @('mcp')
    $serverConfiguration.cwd = $bin
    if (-not $serverConfiguration.ContainsKey('startup_timeout_sec')) { $serverConfiguration.startup_timeout_sec = 120 }
    $edits = @(@{ keyPath = 'mcp_servers.avalonia'; mergeStrategy = 'replace'; value = $serverConfiguration })
    if ($hasPlugin) {
        $edits += @{ keyPath = "plugins.$pluginId.enabled"; mergeStrategy = 'replace'; value = $true }
        $edits += @{ keyPath = "plugins.$pluginId.mcp_servers.avalonia.enabled"; mergeStrategy = 'replace'; value = $false }
    }
    $configurationAttempted = $true
    $null = Invoke-CodexMcpRequest -Connection $connection -Method 'config/batchWrite' -Params @{
        edits = $edits; expectedVersion = $userLayer.version; reloadUserConfig = $true
    }
    $null = Invoke-CodexMcpRequest -Connection $connection -Method 'config/mcpServer/reload' -Params $null
    $catalog = Invoke-CodexMcpRequest -Connection $connection -Method 'mcpServerStatus/list' -Params @{ serverName = 'avalonia'; detail = 'toolsAndAuthOnly' }
    $servers = @($catalog.data | Where-Object name -eq 'avalonia')
    if ($servers.Count -ne 1 -or $servers[0].pluginId -or $servers[0].toolsError) { throw 'The standalone avalonia MCP route was not confirmed.' }
    foreach ($requiredTool in @('select-machine', 'attach-to-app', 'tree', 'recording-start')) {
        if (-not $servers[0].tools.ContainsKey($requiredTool)) { throw "MCP catalog is missing $requiredTool." }
    }
    $toolCount = $servers[0].tools.Count
    $ownedFiles = @{}
    if ($marker) { foreach ($file in $marker.files) { $ownedFiles[$file.path] = $file.sha256 } }
    foreach ($file in @($installation.Command, $installation.VersionedExecutable)) {
        $ownedFiles[$file] = (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()
    }
    $toolsDirectory = Join-Path $root 'tools'
    New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
    foreach ($name in @('Manage-CodexMcp.ps1', 'Install-AvaloniaDevTool.ps1', 'Codex-Mcp.ps1')) {
        $source = Join-Path $PSScriptRoot $name
        $target = Join-Path $toolsDirectory $name
        if ([IO.Path]::GetFullPath($source) -cne [IO.Path]::GetFullPath($target)) {
            $scriptBackups[$target] = if (Test-Path -LiteralPath $target) { , [IO.File]::ReadAllBytes($target) } else { $null }
            $stagedScript = Join-Path $toolsDirectory ('.script-' + [Guid]::NewGuid().ToString('N') + '.tmp')
            $temporaryFiles.Add($stagedScript)
            [IO.File]::WriteAllBytes($stagedScript, [IO.File]::ReadAllBytes($source))
            [IO.File]::Move($stagedScript, $target, $true)
            $scriptWrites.Add($target)
        }
        $ownedFiles[$target] = (Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant()
    }
    $state = [ordered]@{
        formatVersion = 1; serverName = 'avalonia'; command = $command; installRoot = $root
        programVersion = $Version; toolCount = $toolCount
        files = @($ownedFiles.GetEnumerator() | Sort-Object Key | ForEach-Object { @{ path = $_.Key; sha256 = $_.Value } })
    }
    $stagedState = Join-Path $root ('.codex-mcp-' + [Guid]::NewGuid().ToString('N') + '.json')
    $temporaryFiles.Add($stagedState)
    [IO.File]::WriteAllText($stagedState, ($state | ConvertTo-Json -Depth 8) + "`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($stagedState, $statePath, $true)
    [pscustomobject]@{
        Action = $Action; ProgramVersion = $Version; Command = $command; Registered = $true
        ToolCount = $toolCount; PluginBundledMcpEnabled = $(if ($hasPlugin) { $false } else { $null })
    }
} catch {
    $failure = $_
    foreach ($writtenScript in $scriptWrites) {
        try {
            if ($null -eq $scriptBackups[$writtenScript]) { [IO.File]::Delete($writtenScript) }
            else { [IO.File]::WriteAllBytes($writtenScript, $scriptBackups[$writtenScript]) }
        } catch { Write-Warning "A management script could not be restored: $writtenScript" }
    }
    $safeToRestoreProgram = -not $configurationAttempted
    if ($configurationAttempted -and $connection) {
        try {
            if ($connection.TransportFaulted) {
                Stop-CodexMcpConnection -Connection $connection
                $connection = $null
                $connection = Start-CodexMcpConnection @connectionArguments
            }
            $actual = Invoke-CodexMcpRequest -Connection $connection -Method 'config/read' -Params @{ includeLayers = $true }
            if ($actual.config.mcp_servers.ContainsKey('avalonia') -and $actual.config.mcp_servers.avalonia.command -ceq $command) {
                if ($previousConfig.ContainsKey('mcp_servers') -and $previousConfig.mcp_servers.ContainsKey('avalonia')) {
                    $undo = @(@{ keyPath = 'mcp_servers.avalonia'; mergeStrategy = 'replace'; value = $previousConfig.mcp_servers.avalonia })
                } else {
                    Remove-CodexRegistration
                    $undo = @()
                    $actual = Invoke-CodexMcpRequest -Connection $connection -Method 'config/read' -Params @{ includeLayers = $true }
                }
                if ($hasPlugin) {
                    $actualUserLayer = @($actual.layers | Where-Object { $_.name.type -eq 'user' })[0]
                    $actualUser = $actualUserLayer.config
                    $restoredPlugin = if ($actualUser.ContainsKey('plugins') -and $actualUser.plugins.ContainsKey($pluginId)) {
                        $actualUser.plugins[$pluginId] | ConvertTo-Json -Depth 30 | ConvertFrom-Json -AsHashtable
                    } else { @{} }
                    $originalPlugin = if ($previousConfig.ContainsKey('plugins') -and $previousConfig.plugins.ContainsKey($pluginId)) {
                        $previousConfig.plugins[$pluginId]
                    } else { @{} }
                    if ($originalPlugin.ContainsKey('enabled')) { $restoredPlugin.enabled = $originalPlugin.enabled }
                    else { $restoredPlugin.Remove('enabled') }
                    if ($originalPlugin.ContainsKey('mcp_servers') -and $originalPlugin.mcp_servers.ContainsKey('avalonia') -and
                        $originalPlugin.mcp_servers.avalonia.ContainsKey('enabled')) {
                        $restoredPlugin.mcp_servers.avalonia.enabled = $originalPlugin.mcp_servers.avalonia.enabled
                    } elseif ($restoredPlugin.ContainsKey('mcp_servers') -and $restoredPlugin.mcp_servers.ContainsKey('avalonia')) {
                        $restoredPlugin.mcp_servers.avalonia.Remove('enabled')
                        if ($restoredPlugin.mcp_servers.avalonia.Count -eq 0) { $restoredPlugin.mcp_servers.Remove('avalonia') }
                        if ($restoredPlugin.mcp_servers.Count -eq 0) { $restoredPlugin.Remove('mcp_servers') }
                    }
                    $undo += @{ keyPath = "plugins.$pluginId"; mergeStrategy = 'replace'; value = $restoredPlugin }
                }
                if ($undo.Count -gt 0) {
                    $layer = @($actual.layers | Where-Object { $_.name.type -eq 'user' })[0]
                    $null = Invoke-CodexMcpRequest -Connection $connection -Method 'config/batchWrite' -Params @{ edits = $undo; expectedVersion = $layer.version; reloadUserConfig = $true }
                }
            }
            $safeToRestoreProgram = $true
        } catch { Write-Warning "Configuration rollback is unconfirmed; the installed executable was retained: $($_.Exception.Message)" }
    }
    if ($connection) { Stop-CodexMcpConnection -Connection $connection; $connection = $null }
    if ($programChanged -and $safeToRestoreProgram) {
        try {
            if ($current -and $previousCommandExisted) {
                $null = & $installer -PackagePath $current.executable -Version $current.version -ExpectedSha256 $current.sha256 `
                    -InstallRoot $root -BinDirectory $bin -SkipEnvironmentUpdate -Confirm:$false
            } elseif (-not $previousCommandExisted) {
                if (Test-Path -LiteralPath $command) {
                    if ((Get-FileHash -LiteralPath $command).Hash -ine $packageDigest) { throw 'The stable command changed after installation; it was preserved.' }
                    Remove-Item -LiteralPath $command
                }
                if ($null -ne $previousCurrentText) { [IO.File]::WriteAllText($currentPath, $previousCurrentText, [Text.UTF8Encoding]::new($false)) }
                elseif (Test-Path -LiteralPath $currentPath) { Remove-Item -LiteralPath $currentPath }
            }
        } catch { Write-Warning "The previous executable could not be restored: $($_.Exception.Message)" }
    }
    throw $failure
} finally {
    if ($connection) { Stop-CodexMcpConnection -Connection $connection }
    foreach ($temporaryFile in $temporaryFiles) {
        if (Test-Path -LiteralPath $temporaryFile) { [IO.File]::Delete($temporaryFile) }
    }
    if ($managementLockHeld) { $managementMutex.ReleaseMutex() }
    if ($managementMutex) { $managementMutex.Dispose() }
}
