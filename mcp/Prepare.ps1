[CmdletBinding()]
param([string]$CacheRoot, [switch]$Force)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'unsupported_platform: Windows x64 is required for native attachment.'
}
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
Push-Location $PSScriptRoot
try { $sdkVersion = (& $dotnet --version).Trim() } finally { Pop-Location }
if ($LASTEXITCODE -ne 0 -or [version]$sdkVersion -lt [version]'10.0.100') {
    throw 'sdk_required: .NET 10 SDK is required; the runtime alone cannot build a C# file-based app.'
}
if (-not $CacheRoot) {
    $CacheRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AvaloniaDevTool/source-builds'
}
$entry = Join-Path $PSScriptRoot 'AvaloniaDevTool.cs'
$hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
try {
    $hash.AppendData([Text.Encoding]::UTF8.GetBytes("$PSScriptRoot|$sdkVersion|direct-file-run-v2"))
    $inputs = @(Get-Item -LiteralPath $entry)
    $inputs += Get-Item -LiteralPath (Join-Path $PSScriptRoot '.editorconfig')
    $inputs += Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object Extension -in '.props', '.targets', '.json', '.editorconfig'
    foreach ($directory in @('src','native')) {
        if (Test-Path -LiteralPath (Join-Path $PSScriptRoot $directory)) {
            $inputs += Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $directory) -Recurse -File |
                Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
        }
    }
    $inputs += Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../assets') -File
    foreach ($inputFile in ($inputs | Sort-Object FullName -Unique)) {
        $hash.AppendData([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetRelativePath($PSScriptRoot, $inputFile.FullName)))
        $hash.AppendData([Convert]::FromHexString((Get-FileHash -LiteralPath $inputFile.FullName -Algorithm SHA256).Hash))
    }
    $fingerprint = [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
} finally { $hash.Dispose() }
$buildRoot = Join-Path $CacheRoot $fingerprint
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
$buildLock = $null
$deadline = [DateTime]::UtcNow.AddMinutes(3)
while ($null -eq $buildLock) {
    try { $buildLock = [IO.File]::Open((Join-Path $buildRoot 'build.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch [IO.IOException] {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'build_busy: Another process is preparing this source version.' }
        Start-Sleep -Milliseconds 200
    }
}
function Invoke-SourceDotnet([string[]]$Arguments, [switch]$Capture) {
    $start = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $start.WorkingDirectory = $PSScriptRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['DOTNET_NOLOGO'] = '1'
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $text = $stdout.GetAwaiter().GetResult()
        [Console]::Error.Write($stderr.GetAwaiter().GetResult())
        if (-not $Capture) { [Console]::Error.Write($text) }
        if ($process.ExitCode -ne 0) { throw "build_failed: dotnet exited with $($process.ExitCode). $text" }
        if ($Capture) { return $text }
    } finally { $process.Dispose() }
}
try {
    $stamp = Join-Path $buildRoot 'ready.json'
    if (-not $Force -and (Test-Path -LiteralPath $stamp)) {
        $ready = Get-Content -LiteralPath $stamp -Raw | ConvertFrom-Json
        $cliAssembly = Join-Path $ready.RuntimeRoot 'AvaloniaDevTool.Cli.dll'
        $bootstrapAssembly = Join-Path $PSScriptRoot 'src/Hosts/AvaloniaDevTool.Bootstrap/bin/Release/net10.0/AvaloniaDevTool.Bootstrap.dll'
        if ((Test-Path -LiteralPath $ready.EntryAssembly) -and
            (Test-Path -LiteralPath $cliAssembly) -and (Test-Path -LiteralPath $bootstrapAssembly)) {
            $ready | Add-Member -NotePropertyName Reused -NotePropertyValue $true -Force
            return $ready
        }
    }
    [Console]::Error.WriteLine("Building the C# file-based MCP with .NET SDK $sdkVersion; no publish.")
    Invoke-SourceDotnet @('build', $entry, '-c', 'Release', '--disable-build-servers', '-v:minimal')
    $info = Invoke-SourceDotnet @('run', '--file', $entry, '--no-build', '--no-restore', '-c', 'Release', '--', '--runtime-info') -Capture
    $ready = $info | ConvertFrom-Json
    $ready | Add-Member -NotePropertyMembers @{ SdkVersion=$sdkVersion; Fingerprint=$fingerprint; Reused=$false }
    $ready | ConvertTo-Json | Set-Content -LiteralPath $stamp -Encoding utf8NoBOM
    return $ready
} finally { $buildLock.Dispose() }
