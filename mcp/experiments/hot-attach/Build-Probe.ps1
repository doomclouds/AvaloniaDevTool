[CmdletBinding()]
param(
    [string]$OutputRoot,
    [string]$AvaloniaVersion = '12.1.4'
)
$ErrorActionPreference = 'Stop'
if (-not $OutputRoot) {
    $OutputRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AvaloniaDevTool/hot-attach-probe'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'The native probe requires an installed MSVC x64 toolchain; no toolchain is installed automatically.' }
$netfxHeader = Get-ChildItem -LiteralPath (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/NETFXSDK') -Filter mscoree.h -Recurse |
    Select-Object -First 1
if (-not $netfxHeader) { throw 'mscoree.h is required to build the native probe.' }
$native = Join-Path $OutputRoot 'NativeBootstrap.dll'
$commandFile = Join-Path $OutputRoot 'build-native.cmd'
@"
@call "$vs\VC\Auxiliary\Build\vcvars64.bat" >nul
@if errorlevel 1 exit /b 1
@cl.exe /nologo /LD /O2 /MT /EHsc /std:c++17 /I "$($netfxHeader.DirectoryName)" "$PSScriptRoot\NativeBootstrap.cpp" /Fo"$OutputRoot\NativeBootstrap.obj" /link /OUT:"$native" /IMPLIB:"$OutputRoot\NativeBootstrap.lib"
"@ | Set-Content -LiteralPath $commandFile -Encoding ascii
& cmd.exe /d /c $commandFile
if ($LASTEXITCODE -ne 0) { throw 'Native bootstrap build failed.' }
$artifacts = Join-Path $OutputRoot 'artifacts'
foreach ($project in @('UninstrumentedApp/UninstrumentedApp.csproj', 'ManagedBootstrap/ManagedBootstrap.csproj', 'Injector.cs')) {
    & dotnet build (Join-Path $PSScriptRoot $project) -c Release --artifacts-path $artifacts --disable-build-servers -v:minimal "-p:AvaloniaVersion=$AvaloniaVersion"
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
[pscustomobject]@{
    OutputRoot = $OutputRoot; NativeBootstrap = $native
    ManagedBootstrap = Join-Path $artifacts 'bin/ManagedBootstrap/release/AvaloniaDevTool.AttachProbe.Bootstrap.dll'
    Fixture = Join-Path $artifacts 'bin/UninstrumentedApp/release/UninstrumentedApp.dll'
}
