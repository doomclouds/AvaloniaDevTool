[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$NoRestore,
    [switch]$IncludeAuxiliary
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$settings = Join-Path $repository 'AvaloniaDevTool.slnx.DotSettings'
$artifactDirectory = Join-Path $repository 'artifacts/resharper'
$workDirectory = Join-Path $artifactDirectory ('format-' + [Guid]::NewGuid().ToString('N'))
$encoding = [Text.UTF8Encoding]::new($false, $true)
$originals = [Collections.Generic.Dictionary[string, byte[]]]::new()
$changedFiles = [Collections.Generic.List[string]]::new()

Push-Location $repository
try {
    if (-not $NoRestore) {
        & dotnet tool restore
        if ($LASTEXITCODE -ne 0) {
            throw 'ReSharper tool restore failed.'
        }
    }

    $listedFiles = & git -c core.quotepath=false ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) {
        throw 'Cannot enumerate repository source files.'
    }

    # Exact paths prevent the formatter from scanning ignored build outputs or historical artifacts.
    $extensions = @('.cs', '.csproj', '.props', '.targets', '.slnx', '.axaml', '.xaml', '.xml', '.svg')
    $files = @($listedFiles | Sort-Object -Unique | Where-Object {
            $extensions -contains [IO.Path]::GetExtension($_) -and
            $_ -notmatch '(^|/)(bin|obj|artifacts|\.git|\.codegraph)/|\.(g|generated|Designer)\.cs$' -and
            (Test-Path -LiteralPath $_ -PathType Leaf)
        })
    if ($files.Count -eq 0) {
        throw 'No source files matched the formatting scope.'
    }

    New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository '.editorconfig') -Destination $workDirectory
    $inputs = [Collections.Generic.List[string]]::new()
    $aliases = [Collections.Generic.Dictionary[string, string]]::new()
    foreach ($relativePath in $files) {
        $path = Join-Path $repository $relativePath
        $originals.Add($path, [IO.File]::ReadAllBytes($path))
        if ([IO.Path]::GetExtension($path) -in @('.axaml', '.xaml', '.slnx', '.svg')) {
            # File-mode CleanupCode skips these extensions; use its XML formatter on an isolated copy.
            $alias = Join-Path $workDirectory ('source-' + $aliases.Count.ToString('D4') + '.xml')
            [IO.File]::WriteAllBytes($alias, $originals[$path])
            $aliases.Add($alias, $path)
            $inputs.Add($alias)
        } else {
            $inputs.Add($path)
        }
    }

    # Batching keeps native command lines below the Windows length limit.
    for ($offset = 0; $offset -lt $inputs.Count; $offset += 80) {
        $last = [Math]::Min($offset + 79, $inputs.Count - 1)
        $commonArguments = @(
            'tool', 'run', 'jb', 'cleanupcode',
            ('--settings=' + $settings),
            '--profile=Built-in: Reformat Code',
            '--no-build', '--no-updates', '--no-buildin-settings',
            ('--caches-home=' + (Join-Path $workDirectory 'cache')),
            ('--LogFile=' + (Join-Path $workDirectory 'cleanup.log')),
            '--verbosity=WARN'
        )
        $arguments = $commonArguments + $inputs[$offset..$last]
        $output = @(& dotnet @arguments 2>&1)
        $toolExitCode = $LASTEXITCODE
        $output | ForEach-Object { Write-Host $_ }
        if ($toolExitCode -ne 0) {
            throw "ReSharper failed for source batch starting at $offset."
        }
        # The CLI may return zero when another process briefly locks a document.
        $failedSaves = @($output | ForEach-Object {
                if ($_ -match 'Failed to save document (.+)$') { $Matches[1].Trim() }
            } | Sort-Object -Unique)
        if ($failedSaves.Count -gt 0) {
            $retryArguments = $commonArguments + $failedSaves
            $retryOutput = @(& dotnet @retryArguments 2>&1)
            $retryExitCode = $LASTEXITCODE
            $retryOutput | ForEach-Object { Write-Host $_ }
            if ($retryExitCode -ne 0 -or ($retryOutput -match 'Failed to save document')) {
                throw 'ReSharper could not save all source files.'
            }
        }
    }

    foreach ($alias in $aliases.Keys) {
        [IO.File]::WriteAllBytes($aliases[$alias], [IO.File]::ReadAllBytes($alias))
    }
    foreach ($path in $originals.Keys) {
        # File-mode XML formatting can retain an old EOF. Enforce the shared UTF-8/LF policy.
        $text = $encoding.GetString([IO.File]::ReadAllBytes($path)).TrimStart([char]0xFEFF)
        $text = $text.Replace("`r`n", "`n").Replace("`r", "`n").TrimEnd("`n") + "`n"
        $normalizedBytes = $encoding.GetBytes($text)
        $normalizedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($normalizedBytes))
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $normalizedHash) {
            [IO.File]::WriteAllBytes($path, $normalizedBytes)
        }
        $hashBefore = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($originals[$path]))
        $hashAfter = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($hashBefore -ne $hashAfter) {
            $changedFiles.Add([IO.Path]::GetRelativePath($repository, $path))
        }
    }

    if ($IncludeAuxiliary) {
        & (Join-Path $PSScriptRoot 'Format-Auxiliary.ps1') -Check:$Check
    }
    if ($Check -and $changedFiles.Count -gt 0) {
        throw ("ReSharper formatting differs in {0} files:`n{1}" -f $changedFiles.Count, ($changedFiles -join "`n"))
    }
    Write-Host ("ReSharper: {0} files checked; {1} files {2}." -f $files.Count, $changedFiles.Count,
        $(if ($Check) { 'would change' } else { 'formatted' }))
} finally {
    if ($Check) {
        foreach ($path in $originals.Keys) {
            $originalHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($originals[$path]))
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $originalHash) {
                [IO.File]::WriteAllBytes($path, $originals[$path])
            }
        }
    }
    Pop-Location
    if (Test-Path -LiteralPath $workDirectory) {
        $resolvedWork = [IO.Path]::GetFullPath($workDirectory)
        $allowedRoot = [IO.Path]::GetFullPath($artifactDirectory) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedWork.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The formatting work directory escaped its artifact root.'
        }
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}
