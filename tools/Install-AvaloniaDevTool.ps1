[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$InstallRoot = (Join-Path $env:USERPROFILE '.local\share\avalonia-devtool'),
    [string]$BinDirectory = (Join-Path $env:USERPROFILE '.local\bin'),

    [ValidatePattern('^(?:[0-9a-fA-F]{64})?$')]
    [string]$ExpectedSha256 = '',

    # Allows an installation to an isolated directory without changing the user's environment.
    [switch]$SkipEnvironmentUpdate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-OwnedPath {
    param([string]$Path, [string]$Root)
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installation path escaped its root: $resolvedPath"
    }
}

function Get-FileDigest {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Send-EnvironmentChanged {
    if (-not ('AvaloniaDevTool.Install.EnvironmentNotification' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace AvaloniaDevTool.Install {
    public static class EnvironmentNotification {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr window, uint message,
            IntPtr parameter, string text, uint flags, uint timeout, out IntPtr result);
    }
}
'@
    }
    $messageResult = [IntPtr]::Zero
    [void][AvaloniaDevTool.Install.EnvironmentNotification]::SendMessageTimeout(
        [IntPtr]0xffff, 0x1a, [IntPtr]::Zero, 'Environment', 2, 3000, [ref]$messageResult)
}

if (-not $IsWindows) {
    throw 'This installer is for the Windows executable. Run it with PowerShell 7 on Windows.'
}

$source = (Resolve-Path -LiteralPath $PackagePath).Path
if (-not [IO.File]::Exists($source) -or [IO.Path]::GetExtension($source) -ine '.exe') {
    throw 'PackagePath must identify the published AvaloniaDevTool.exe.'
}
$sourceDigest = Get-FileDigest $source
if ($ExpectedSha256 -and $sourceDigest -ine $ExpectedSha256) {
    throw 'The package SHA-256 does not match ExpectedSha256. Nothing was installed.'
}
$header = [IO.File]::OpenRead($source)
try {
    if ($header.ReadByte() -ne 0x4d -or $header.ReadByte() -ne 0x5a) {
        throw 'The package does not have a Windows executable header.'
    }
} finally {
    $header.Dispose()
}

$root = [IO.Path]::GetFullPath($InstallRoot)
$bin = [IO.Path]::GetFullPath($BinDirectory)
$versions = Join-Path $root 'versions'
$versionDirectory = Join-Path $versions $Version
$versionExecutable = Join-Path $versionDirectory 'AvaloniaDevTool.exe'
$stableExecutable = Join-Path $bin 'AvaloniaDevTool.exe'
$currentManifest = Join-Path $root 'current.json'
Assert-OwnedPath $versionDirectory $root
Assert-OwnedPath $stableExecutable $bin
if ($source.Equals($stableExecutable, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use the published package or a versioned executable as PackagePath, not the stable command being replaced.'
}

if (-not $PSCmdlet.ShouldProcess($stableExecutable, "Install version $Version and update the user command")) {
    return
}

$installationId = [Guid]::NewGuid().ToString('N')
$stagingDirectory = Join-Path $versions ".install-$installationId"
$stagingCommand = Join-Path $bin ".AvaloniaDevTool-$installationId.exe"
$backupCommand = Join-Path $bin ".AvaloniaDevTool-$installationId.previous"
$manifestTemporary = Join-Path $root ".current-$installationId.json"
$oldUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$oldUserHome = [Environment]::GetEnvironmentVariable('AVALONIA_DEVTOOL_HOME', 'User')
$environmentTouched = $false
$commandSwitched = $false
$hadCommand = [IO.File]::Exists($stableExecutable)
$installationLock = $null

try {
    New-Item -ItemType Directory -Path $versions, $bin -Force | Out-Null
    $lockDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    while ($null -eq $installationLock) {
        try {
            $installationLock = [IO.File]::Open((Join-Path $root '.install.lock'),
                [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        } catch [IO.IOException] {
            if ([DateTimeOffset]::UtcNow -ge $lockDeadline) {
                throw 'Another installation is still running. Wait for it to finish and retry.'
            }
            Start-Sleep -Milliseconds 100
        }
    }
    $hadCommand = [IO.File]::Exists($stableExecutable)
    $oldUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $oldUserHome = [Environment]::GetEnvironmentVariable('AVALONIA_DEVTOOL_HOME', 'User')
    if (Test-Path -LiteralPath $versionDirectory) {
        if (-not [IO.File]::Exists($versionExecutable) -or
            (Get-FileDigest $versionExecutable) -ine $sourceDigest) {
            throw "Version $Version already exists with different content. Use a new version; existing versions are preserved."
        }
    } else {
        New-Item -ItemType Directory -Path $stagingDirectory | Out-Null
        Copy-Item -LiteralPath $source -Destination (Join-Path $stagingDirectory 'AvaloniaDevTool.exe')
        if ((Get-FileDigest (Join-Path $stagingDirectory 'AvaloniaDevTool.exe')) -ine $sourceDigest) {
            throw 'The versioned copy failed SHA-256 verification.'
        }
        Move-Item -LiteralPath $stagingDirectory -Destination $versionDirectory
    }

    Copy-Item -LiteralPath $versionExecutable -Destination $stagingCommand
    if ((Get-FileDigest $stagingCommand) -ine $sourceDigest) {
        throw 'The stable command copy failed SHA-256 verification.'
    }

    $manifest = [ordered]@{
        version     = $Version
        executable  = $versionExecutable
        command     = $stableExecutable
        sha256      = $sourceDigest
        installedAt = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json
    [IO.File]::WriteAllText($manifestTemporary, $manifest + "`n", [Text.UTF8Encoding]::new($false))

    # File.Replace keeps the previous command intact if it is locked by a running MCP/Console.
    if ($hadCommand) {
        [IO.File]::Replace($stagingCommand, $stableExecutable, $backupCommand)
    } else {
        [IO.File]::Move($stagingCommand, $stableExecutable)
    }
    $commandSwitched = $true

    if (-not $SkipEnvironmentUpdate) {
        $entries = @($oldUserPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $alreadyPresent = @($entries | Where-Object {
                [Environment]::ExpandEnvironmentVariables($_).TrimEnd('\', '/') -ieq $bin.TrimEnd('\', '/')
            }).Count -gt 0
        $newUserPath = if ($alreadyPresent) { $oldUserPath } else { (@($entries) + $bin) -join ';' }
        $environmentTouched = $true
        [Environment]::SetEnvironmentVariable('AVALONIA_DEVTOOL_HOME', $root, 'User')
        [Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User')
    }

    [IO.File]::Move($manifestTemporary, $currentManifest, $true)
    if (-not $SkipEnvironmentUpdate) {
        $env:AVALONIA_DEVTOOL_HOME = $root
        $processEntries = @($env:Path -split ';')
        if (-not ($processEntries | Where-Object { $_.TrimEnd('\', '/') -ieq $bin.TrimEnd('\', '/') })) {
            $env:Path = $env:Path.TrimEnd(';') + ';' + $bin
        }
        try { Send-EnvironmentChanged } catch { Write-Warning "Installed, but environment broadcast failed: $_" }
    }
    [PSCustomObject]@{
        Version                = $Version
        VersionedExecutable    = $versionExecutable
        Command                = $stableExecutable
        Sha256                 = $sourceDigest
        UserEnvironmentUpdated = -not $SkipEnvironmentUpdate
    }
} catch {
    $installationError = $_
    if ($environmentTouched) {
        [Environment]::SetEnvironmentVariable('Path', $oldUserPath, 'User')
        [Environment]::SetEnvironmentVariable('AVALONIA_DEVTOOL_HOME', $oldUserHome, 'User')
    }
    if ($commandSwitched) {
        if ($hadCommand -and [IO.File]::Exists($backupCommand)) {
            [IO.File]::Move($backupCommand, $stableExecutable, $true)
        } elseif (-not $hadCommand -and [IO.File]::Exists($stableExecutable)) {
            [IO.File]::Delete($stableExecutable)
        }
    }
    throw $installationError
} finally {
    foreach ($temporaryFile in @($stagingCommand, $backupCommand, $manifestTemporary)) {
        if ([IO.File]::Exists($temporaryFile)) { [IO.File]::Delete($temporaryFile) }
    }
    if (Test-Path -LiteralPath $stagingDirectory) {
        Assert-OwnedPath $stagingDirectory $versions
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
    if ($null -ne $installationLock) { $installationLock.Dispose() }
}
