[CmdletBinding()]
param(
    [string]$PackagePath,
    [string]$OutputDirectory,
    [switch]$SyncOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$pluginRoot = Join-Path $repository 'plugins/avalonia-devtool'
$manifest = Get-Content -LiteralPath (Join-Path $pluginRoot 'plugin.json') -Raw | ConvertFrom-Json
if ($manifest.name -cne 'avalonia-devtool' -or
    $manifest.version -cnotmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw 'The plugin identity or distribution version is invalid.'
}

$scripts = @('Install-AvaloniaDevTool.ps1', 'Manage-CodexMcp.ps1', 'Codex-Mcp.ps1')
foreach ($script in $scripts) {
    if (-not (Test-Path -LiteralPath (Join-Path $repository "source/tools/$script") -PathType Leaf)) {
        throw "A distribution script is missing: $script"
    }
}

$package = (Resolve-Path -LiteralPath $(if ($PackagePath) { $PackagePath } else {
    Join-Path $pluginRoot 'mcp/AvaloniaDevTool.exe'
})).Path
$infoText = & $package --bundle-info
if ($LASTEXITCODE -ne 0) { throw 'The executable did not return a valid bundle manifest.' }
$info = $infoText -join "`n" | ConvertFrom-Json
if ($info.applicationVersion -cnotmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$' -or
    $info.runtimeIdentifier -cnotin @('win-x64', 'win-arm64')) {
    throw 'The bundle application version or runtime identifier is invalid.'
}
$verifiedText = & $package --bundle-verify
if ($LASTEXITCODE -ne 0) { throw 'The executable failed bundle verification.' }
$verified = $verifiedText -join "`n" | ConvertFrom-Json
if ($verified.verified -ne $true) { throw 'The executable did not confirm bundle verification.' }

$output = [IO.Path]::GetFullPath($(if ($OutputDirectory) { $OutputDirectory } else {
    Join-Path $repository "artifacts/distributions/$($manifest.version)"
}))
$pluginPrefix = [IO.Path]::GetFullPath($pluginRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $SyncOnly -and ($output.Equals($pluginRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($pluginPrefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'The standalone distribution output must be outside the installable plugin directory.'
}
$archiveName = "avalonia-devtool-$($manifest.version)-$($info.runtimeIdentifier).zip"
$archivePath = Join-Path $output $archiveName
$hashPath = "$archivePath.sha256"
if (-not $SyncOnly -and ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath $hashPath))) {
    throw "Versioned distribution artifacts already exist: $archivePath"
}

$pluginScripts = Join-Path $pluginRoot 'scripts'
New-Item -ItemType Directory -Path $pluginScripts -Force | Out-Null
foreach ($script in $scripts) {
    $source = Join-Path $repository "source/tools/$script"
    $destination = Join-Path $pluginScripts $script
    Copy-Item -LiteralPath $source -Destination $destination
    if ((Get-FileHash -LiteralPath $source).Hash -cne (Get-FileHash -LiteralPath $destination).Hash) {
        throw "The synchronized script failed SHA-256 verification: $script"
    }
}
if ($SyncOnly) {
    [pscustomobject]@{ PluginVersion = $manifest.version; ApplicationVersion = $info.applicationVersion; SynchronizedScripts = $scripts }
    return
}

$packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
$readme = @"
# Avalonia DevTool 独立 MCP

分发版本：$($manifest.version)；程序版本：$($info.applicationVersion)；平台：$($info.runtimeIdentifier)。
程序 SHA-256：$packageHash

使用 Windows 和 PowerShell 7，先安装 Codex。解压整个 ZIP 后在本目录运行：

``````powershell
pwsh -NoProfile -File ./Manage-CodexMcp.ps1 -Action Install
pwsh -NoProfile -File ./Manage-CodexMcp.ps1 -Action Status
``````

安装会将 EXE 保存到用户的版本目录，以固定路径注册普通 avalonia MCP；已安装的 Avalonia 插件保留技能，内置 MCP 单独关闭，避免重复加载。新开连接后检查工具目录和应用发现；目录读取成功不代表热附加或硬件验证通过。

升级：获取新分发 ZIP，核对 ZIP 的 .sha256 文件并解压，再在新目录运行：

``````powershell
pwsh -NoProfile -File ./Manage-CodexMcp.ps1 -Action Update
``````

也可用 -PackagePath 指定新的 AvaloniaDevTool.exe，-ExpectedSha256 核对其可信校验值。程序版本从 EXE 的内嵌清单读取，不使用插件版本代替。若固定 EXE 被运行中的 MCP 占用，先关闭对应连接再升级；失败不强行结束 Codex。

回退：运行 Manage-CodexMcp.ps1 -Action Rollback -Version 已保留的程序版本。回退后 Codex 的启动路径不变。

卸载：运行 Manage-CodexMcp.ps1 -Action Uninstall，仅移除普通 MCP 注册，默认保留程序、历史版本和用户录屏；需要清理本工具管理的程序文件时增加 -RemoveFiles。卸载不会自动启用插件内置 MCP，也不删除共享运行缓存或用户录屏。

默认固定入口为 %USERPROFILE%/.local/bin/AvaloniaDevTool.exe，版本及 current.json 位于 %USERPROFILE%/.local/share/avalonia-devtool/。使用 -InstallRoot、-BinDirectory 时，后续维护操作传入相同目录。

脚本不后台更新，不修改其他 MCP。更新或注册后，已有 Codex 聊天可能需要重新加载连接或重启客户端，才能使用新工具目录。
"@

New-Item -ItemType Directory -Path $output -Force | Out-Null
$created = [Collections.Generic.List[string]]::new()
$archive = $null
$stream = $null
try {
    $stream = [IO.File]::Open($archivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $created.Add($archivePath)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $package, 'AvaloniaDevTool.exe', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    foreach ($script in $scripts) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $pluginScripts $script), $script, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $entry = $archive.CreateEntry('README.md', [IO.Compression.CompressionLevel]::Optimal)
    $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write($readme) } finally { $writer.Dispose() }
    $archive.Dispose()
    $archive = $null
    $stream.Dispose()
    $stream = $null

    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashStream = [IO.File]::Open($hashPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $created.Add($hashPath)
    try {
        $hashBytes = [Text.UTF8Encoding]::new($false).GetBytes("$archiveHash  $archiveName`n")
        $hashStream.Write($hashBytes, 0, $hashBytes.Length)
    } finally { $hashStream.Dispose() }
} catch {
    if ($null -ne $archive) { $archive.Dispose(); $archive = $null }
    if ($null -ne $stream) { $stream.Dispose(); $stream = $null }
    foreach ($createdFile in $created) { [IO.File]::Delete($createdFile) }
    throw
} finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if ($null -ne $stream) { $stream.Dispose() }
}

[pscustomobject]@{
    DistributionVersion = $manifest.version
    ApplicationVersion = $info.applicationVersion
    RuntimeIdentifier = $info.runtimeIdentifier
    ExecutableSha256 = $packageHash
    ArchivePath = $archivePath
    ArchiveSha256 = $archiveHash
    SynchronizedScripts = $scripts
}
