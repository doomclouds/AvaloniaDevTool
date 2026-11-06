[CmdletBinding()]
param(
    [string]$CacheRoot,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'unsupported_platform: This plugin verification currently requires Windows.' }
if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'unsupported_architecture: The initial external-attach verification requires Windows x64.'
}
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
Push-Location $PSScriptRoot
try { $sdkVersion = (& $dotnet --version).Trim() }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0 -or [version]$sdkVersion -lt [version]'10.0.100') {
    throw 'sdk_required: Install .NET SDK 10.0.100 or later; the runtime alone cannot build a C# file-based app.'
}
if (-not $CacheRoot) {
    $CacheRoot = if ($env:PLUGIN_DATA) { Join-Path $env:PLUGIN_DATA 'build' }
        else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AvaloniaDevTool/source-builds' }
}
$CacheRoot = [IO.Path]::GetFullPath($CacheRoot)
$entry = Join-Path $PSScriptRoot 'AvaloniaDevTool.cs'
$hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
try {
    $hash.AppendData([Text.Encoding]::UTF8.GetBytes("$PSScriptRoot|$sdkVersion|Release"))
    $inputs = @(Get-Item -LiteralPath $entry)
    $inputs += Get-Item -LiteralPath (Join-Path $PSScriptRoot '.editorconfig')
    $inputs += Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../assets') -File
    $inputs += Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object Extension -in '.props', '.targets', '.json'
    $inputs += Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and
            $_.Extension -in '.cs', '.csproj', '.props', '.targets', '.axaml', '.xaml', '.resx', '.json' }
    foreach ($inputFile in ($inputs | Sort-Object FullName -Unique)) {
        $hash.AppendData([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetRelativePath($PSScriptRoot, $inputFile.FullName)))
        $hash.AppendData([Convert]::FromHexString((Get-FileHash -LiteralPath $inputFile.FullName -Algorithm SHA256).Hash))
    }
    $fingerprint = [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
} finally { $hash.Dispose() }
$buildRoot = Join-Path $CacheRoot $fingerprint
$artifactsPath = Join-Path $buildRoot 'artifacts'
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
$lockPath = Join-Path $buildRoot 'build.lock'
$deadline = [DateTime]::UtcNow.AddMinutes(3)
$buildLock = $null
while ($null -eq $buildLock) {
    try { $buildLock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
    catch [IO.IOException] {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'build_busy: Another process is still preparing this source version.' }
        Start-Sleep -Milliseconds 200
    }
}
try {
    $stampPath = Join-Path $buildRoot 'ready.json'
    if (-not $Force -and (Test-Path -LiteralPath $stampPath)) {
        $ready = Get-Content -LiteralPath $stampPath -Raw | ConvertFrom-Json
        if ((Test-Path -LiteralPath $ready.EntryAssembly) -and (Test-Path -LiteralPath $ready.RuntimeRoot)) {
            $ready | Add-Member -NotePropertyName Reused -NotePropertyValue $true -Force
            return $ready
        }
    }
    [Console]::Error.WriteLine("Preparing AvaloniaDevTool with .NET SDK $sdkVersion (source build, no publish).")
    $start = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $start.WorkingDirectory = $PSScriptRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('build', $entry, '-c', 'Release', '--artifacts-path', $artifactsPath,
            '--disable-build-servers', '-v:minimal')) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.BaseStream.CopyToAsync([Console]::OpenStandardError())
        $errorOutput = $process.StandardError.BaseStream.CopyToAsync([Console]::OpenStandardError())
        $process.WaitForExit()
        [void]$output.GetAwaiter().GetResult()
        [void]$errorOutput.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "build_failed: dotnet build exited with $($process.ExitCode)." }
    } finally { $process.Dispose() }
    $entryAssembly = Join-Path $artifactsPath 'bin/release/AvaloniaDevTool.dll'
    $runtimeRoot = Join-Path $artifactsPath 'bin/AvaloniaDevTool.Cli/release'
    if (-not (Test-Path -LiteralPath $entryAssembly) -or -not (Test-Path -LiteralPath $runtimeRoot)) {
        throw 'build_output_missing: The file-based app or its referenced CLI output is missing.'
    }
    $ready = [pscustomobject]@{
        SdkVersion = $sdkVersion; Fingerprint = $fingerprint; EntryAssembly = $entryAssembly
        RuntimeRoot = $runtimeRoot; ArtifactsPath = $artifactsPath; Reused = $false
    }
    $ready | ConvertTo-Json | Set-Content -LiteralPath $stampPath -Encoding utf8NoBOM
    return $ready
} finally { $buildLock.Dispose() }
