[CmdletBinding()]
param(
    [string]$SourceRoot,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')]
    [string]$Version = '0.1.0',
    [string]$OutputDirectory,
    [string]$CliProjectPath,
    [string]$PreviewerProjectPath,
    [string]$NativeWorkerProjectPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-SourceProject {
    param([string]$ExplicitPath, [string]$ProjectName)
    if ($ExplicitPath) {
        $resolved = if ([IO.Path]::IsPathFullyQualified($ExplicitPath)) { $ExplicitPath } else {
            Join-Path $SourceRoot $ExplicitPath
        }
        return (Resolve-Path -LiteralPath $resolved).Path
    }
    $matches = @(Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'src') -Filter "$ProjectName.csproj" -Recurse -File)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one $ProjectName.csproj under src; pass its explicit project path."
    }
    return $matches[0].FullName
}

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Get-ProjectAssemblyName {
    param([string]$ProjectPath)
    $name = (& dotnet msbuild $ProjectPath -nologo -getProperty:AssemblyName `
            "-p:Configuration=$Configuration" "-p:RuntimeIdentifier=$RuntimeIdentifier") -join "`n"
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($name) -or $name.Trim() -match '[\\/]') {
        throw "Cannot determine the published assembly name for $ProjectPath."
    }
    return $name.Trim()
}

function Publish-Role {
    param([string]$ProjectPath, [string]$Destination)
    Invoke-Dotnet -Arguments @(
        'publish', $ProjectPath, '--configuration', $Configuration,
        '--runtime', $RuntimeIdentifier, '--self-contained', 'true', '--output', $Destination,
        '--disable-build-servers', '-m:1',
        '-p:PublishSingleFile=false', '-p:DebugType=embedded', '-p:DebugSymbols=true',
        '-p:BuildNativeWorker=false', '-p:SkipRuntimeAssetCopy=true', "-p:Version=$Version"
    )
}

function Remove-StagedDirectory {
    param([string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    $boundary = [IO.Path]::GetFullPath($stage) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside this run's staging area: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

function Assert-SelfContainedRole {
    param([string]$Directory, [string]$Role, [string]$AssemblyName)
    $runtimeConfig = Join-Path $Directory "$AssemblyName.runtimeconfig.json"
    if (-not (Test-Path -LiteralPath $runtimeConfig)) {
        throw "The $Role publish must contain its runtime configuration: $AssemblyName.runtimeconfig.json"
    }
    $configuration = Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json
    if ($configuration.runtimeOptions.PSObject.Properties.Name -notcontains 'includedFrameworks' -or
        @($configuration.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').Count -ne 1) {
        throw "The $Role runtime configuration is not self-contained."
    }
    foreach ($runtimeFile in @('coreclr.dll', 'hostpolicy.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $runtimeFile))) {
            throw "The $Role publish is missing its bundled runtime: $runtimeFile"
        }
    }
    $appHost = "$AssemblyName.exe"
    if (-not (Test-Path -LiteralPath (Join-Path $Directory $appHost))) {
        throw "The $Role published apphost is missing: $appHost"
    }
    return $appHost
}

if (-not $SourceRoot) {
    $ancestor = [IO.DirectoryInfo]::new($PSScriptRoot)
    while ($null -ne $ancestor -and -not (Test-Path -LiteralPath (Join-Path $ancestor.FullName 'Directory.Build.props'))) {
        $ancestor = $ancestor.Parent
    }
    if ($null -eq $ancestor) {
        throw 'Cannot locate the source root. Pass -SourceRoot explicitly.'
    }
    $SourceRoot = $ancestor.FullName
}
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$cli = Resolve-SourceProject -ExplicitPath $CliProjectPath -ProjectName 'AvaloniaDevTool.Cli'
$previewer = Resolve-SourceProject -ExplicitPath $PreviewerProjectPath -ProjectName 'AvaloniaDevTool.Previewer'
$worker = Resolve-SourceProject -ExplicitPath $NativeWorkerProjectPath -ProjectName 'AvaloniaDevTool.NativeAutomation.Worker'
$cliAssembly = Get-ProjectAssemblyName -ProjectPath $cli
$previewerAssembly = Get-ProjectAssemblyName -ProjectPath $previewer
$workerAssembly = Get-ProjectAssemblyName -ProjectPath $worker
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $SourceRoot "artifacts/package/$RuntimeIdentifier"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$unexpected = @(Get-ChildItem -LiteralPath $OutputDirectory -Force | Where-Object Name -ne 'AvaloniaDevTool.exe')
if ($unexpected.Count -gt 0) {
    throw 'The output directory must be empty or contain only the previous AvaloniaDevTool.exe. Choose a dedicated output directory.'
}

$work = Join-Path $SourceRoot ("artifacts/packaging/{0}/{1}" -f $RuntimeIdentifier, [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $work 'runtime'
$bootstrap = Join-Path $work 'bootstrap'
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# Each child is published independently with its own runtime configuration and complete
# self-contained runtime. The CLI's build-only copies do not establish that guarantee.
Publish-Role -ProjectPath $cli -Destination $stage
Remove-StagedDirectory -Path (Join-Path $stage 'previewer')
Remove-StagedDirectory -Path (Join-Path $stage 'native-worker')
Publish-Role -ProjectPath $previewer -Destination (Join-Path $stage 'previewer')
Publish-Role -ProjectPath $worker -Destination (Join-Path $stage 'native-worker')

$entryPoint = Assert-SelfContainedRole -Directory $stage -Role 'console' -AssemblyName $cliAssembly
Assert-SelfContainedRole -Directory (Join-Path $stage 'previewer') -Role 'previewer' -AssemblyName $previewerAssembly | Out-Null
Assert-SelfContainedRole -Directory (Join-Path $stage 'native-worker') -Role 'native worker' -AssemblyName $workerAssembly | Out-Null

$payload = Join-Path $work 'bundle.zip'
$manifestPath = Join-Path $work 'bundle.json'
$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName)
$entries = @()
$archive = [IO.Compression.ZipFile]::Open($payload, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\', '/')
        if ($file.Name -like 'AvaloniaDevTool*.pdb') {
            throw "An application symbol file escaped embedded-symbol publishing: $relative"
        }
        $entries += [ordered]@{
            path   = $relative
            length = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::SmallestSize)
        $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $inputStream = [IO.File]::OpenRead($file.FullName)
        $outputStream = $entry.Open()
        try {
            $inputStream.CopyTo($outputStream)
        } finally {
            $outputStream.Dispose()
            $inputStream.Dispose()
        }
    }
} finally {
    $archive.Dispose()
}

$manifest = [ordered]@{
    schemaVersion      = 1
    applicationVersion = $Version
    runtimeIdentifier  = $RuntimeIdentifier
    entryPoint         = $entryPoint
    payloadSha256      = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
    files              = $entries
}
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))

Invoke-Dotnet -Arguments @(
    'publish', (Join-Path $PSScriptRoot 'AvaloniaDevTool.Packaging.csproj'),
    '--configuration', $Configuration, '--runtime', $RuntimeIdentifier, '--self-contained', 'true',
    '--output', $bootstrap, '--disable-build-servers', '-m:1',
    "-p:SourceRoot=$SourceRoot", "-p:BundlePayloadPath=$payload", "-p:BundleManifestPath=$manifestPath",
    "-p:Version=$Version", '-p:DebugType=embedded', '-p:DebugSymbols=true'
)
$published = @(Get-ChildItem -LiteralPath $bootstrap -File)
if ($published.Count -ne 1 -or $published[0].Name -ne 'AvaloniaDevTool.exe' -or
    @(Get-ChildItem -LiteralPath $bootstrap -Directory).Count -ne 0) {
    throw 'The bootstrap publish did not produce exactly one standalone AvaloniaDevTool.exe.'
}
$destination = Join-Path $OutputDirectory 'AvaloniaDevTool.exe'
Copy-Item -LiteralPath $published[0].FullName -Destination $destination -Force
[PSCustomObject]@{
    Executable        = $destination
    RuntimeIdentifier = $RuntimeIdentifier
    Version           = $Version
    PayloadSha256     = $manifest.payloadSha256
    EmbeddedFiles     = $entries.Count
    ExecutableBytes   = (Get-Item -LiteralPath $destination).Length
    WorkDirectory     = $work
}
