[CmdletBinding()]
param([string]$SourceDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$manifestPath = Join-Path $PSScriptRoot 'ScreenRecorderLib.manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$patch = Join-Path $PSScriptRoot 'screenrecorderlib.patch'
if ((Get-FileHash -LiteralPath $patch -Algorithm SHA256).Hash -ne $manifest.patchSha256) {
    throw 'The source patch SHA-256 does not match the manifest.'
}
if (-not $SourceDirectory) {
    $SourceDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "AvaloniaDevTool/native-sources/ScreenRecorderLib/$($manifest.commit)"
}
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
if (-not (Test-Path -LiteralPath $SourceDirectory)) {
    New-Item -ItemType Directory -Path $SourceDirectory | Out-Null
    & git -C $SourceDirectory init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the source checkout.' }
    & git -C $SourceDirectory remote add origin $manifest.upstream
    & git -C $SourceDirectory fetch --depth 1 origin $manifest.commit
    if ($LASTEXITCODE -ne 0) { throw 'Could not fetch the pinned upstream source.' }
    & git -C $SourceDirectory checkout --detach --quiet FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not check out the pinned source.' }
}
$head = (& git -C $SourceDirectory rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -ne $manifest.commit) {
    throw 'The source directory must be a checkout of the pinned manifest commit.'
}
& git -C $SourceDirectory apply --check $patch 2>$null
if ($LASTEXITCODE -eq 0) {
    & git -C $SourceDirectory apply $patch
    if ($LASTEXITCODE -ne 0) { throw 'The recorder patch could not be applied.' }
} else {
    & git -C $SourceDirectory apply --reverse --check $patch 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'The checkout does not match either the pristine or patched source.' }
}
foreach ($addedFile in $manifest.patchAddedFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $SourceDirectory $addedFile) -PathType Leaf)) {
        throw "The patched helper file is missing: $addedFile"
    }
    & git -C $SourceDirectory add -N -- $addedFile
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect the added helper in the source patch: $addedFile" }
}
$expectedDiff = ([IO.File]::ReadAllText($patch) -replace "`r`n", "`n").Trim()
$actualDiff = ((& git -C $SourceDirectory diff --binary 2>$null) -join "`n").Trim()
if ($LASTEXITCODE -ne 0 -or $actualDiff -ne $expectedDiff) {
    throw 'The source checkout contains changes beyond the pinned recorder patch.'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Rebuilding requires MSVC v143 and C++/CLI; no compiler is installed automatically.' }
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $visualStudio) { throw 'MSVC x64 tools were not found.' }
$msbuild = Join-Path $visualStudio 'MSBuild/Current/Bin/MSBuild.exe'
& $msbuild (Join-Path $SourceDirectory 'ScreenRecorderLib/ScreenRecorderLib.vcxproj') /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v143 /m /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'ScreenRecorderLib build failed.' }
$binary = Join-Path $PSScriptRoot 'ScreenRecorderLib.dll'
Copy-Item -LiteralPath (Join-Path $SourceDirectory 'ScreenRecorderLib/bin/x64/Release/ScreenRecorderLib.dll') -Destination $binary -Force
$manifest.sha256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Write-Output $binary
Write-Output $manifest.sha256
