param(
    [ValidateSet('All', 'Trimmed', 'NativeAot')]
    [string]$Mode = 'All',
    [string]$OutputDirectory,
    [switch]$AllowCompatibilityWarnings
)

$ErrorActionPreference = 'Stop'
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('AvaloniaDevTool-Aot-' + [Guid]::NewGuid().ToString('N'))
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$sample = Join-Path $repository 'samples/AvaloniaDevTool.AotSample/AvaloniaDevTool.AotSample.csproj'
$probe = Join-Path $repository 'tests/AvaloniaDevTool.AotSmoke/AvaloniaDevTool.AotSmoke.csproj'
$probeArtifacts = Join-Path $OutputDirectory 'probe-artifacts'
$probeLog = Join-Path $OutputDirectory 'probe-build.log'
& dotnet build $probe -c Release --artifacts-path $probeArtifacts 2>&1 | Out-File -LiteralPath $probeLog -Encoding utf8
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath $probeLog -Encoding utf8 -Tail 30
    throw "Probe build failed. See $probeLog"
}
$probeDll = Join-Path $probeArtifacts 'bin/AvaloniaDevTool.AotSmoke/release/AvaloniaDevTool.AotSmoke.dll'
$modes = if ($Mode -eq 'All') { @('Trimmed', 'NativeAot') } else { @($Mode) }
if ($AllowCompatibilityWarnings) {
    Write-Warning 'Diagnostic evidence mode: compatibility warnings remain in publish.log. A passing smoke test does not establish warning-free or full-feature AOT compatibility.'
}
foreach ($currentMode in $modes) {
    $modeDirectory = Join-Path $OutputDirectory $currentMode
    New-Item -ItemType Directory -Path $modeDirectory -Force | Out-Null
    $publishDirectory = Join-Path $modeDirectory 'publish'
    $publishLog = Join-Path $modeDirectory 'publish.log'
    $publishArguments = @('publish', $sample, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '--artifacts-path', (Join-Path $modeDirectory 'artifacts'), '-o', $publishDirectory, '-p:PublishTrimmed=true')
    $publishArguments += if ($currentMode -eq 'NativeAot') { '-p:PublishAot=true' } else { '-p:PublishAot=false' }
    if ($AllowCompatibilityWarnings) {
        $publishArguments += @('-p:TreatWarningsAsErrors=false', '-p:ILLinkTreatWarningsAsErrors=false')
    }
    & dotnet @publishArguments 2>&1 | Out-File -LiteralPath $publishLog -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $publishLog -Encoding utf8 -Tail 30
        throw "$currentMode publish failed. See $publishLog"
    }
    $target = Join-Path $publishDirectory 'AvaloniaDevTool.AotSample.exe'
    $runtime = if ($currentMode -eq 'NativeAot') { 'aot' } else { 'trimmed' }
    & dotnet $probeDll $target (Join-Path $modeDirectory 'smoke') $runtime
    if ($LASTEXITCODE -ne 0) { throw "$currentMode real-process smoke failed. See $modeDirectory/smoke" }
}
Write-Output "Evidence: $OutputDirectory"
