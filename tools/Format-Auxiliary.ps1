param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$cacheRoot = Join-Path $repository 'artifacts/resharper/aux-cache'
$toolRoot = Join-Path $repository 'artifacts/resharper/aux-tools'
$settingsPath = Join-Path $PSScriptRoot 'PSScriptAnalyzerSettings.psd1'
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Invoke-FormatterCommand {
    param([string]$Executable, [string[]]$CommandArguments)

    & $Executable @CommandArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Formatter failed with exit code ${LASTEXITCODE}: $Executable"
    }
}

function Get-PowerShellSignature {
    param([string]$Text)

    $scriptTokens = $null
    $parseErrors = $null
    $syntax = [System.Management.Automation.Language.Parser]::ParseInput($Text, [ref]$scriptTokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        throw "PowerShell parse failed: $($parseErrors.Message -join '; ')"
    }
    $lexical = @($scriptTokens | Where-Object {
            $_.Kind -notin [System.Management.Automation.Language.TokenKind]::NewLine,
            [System.Management.Automation.Language.TokenKind]::EndOfInput
        } | ForEach-Object {
            [ordered]@{ Kind = $_.Kind.ToString(); Text = $_.Extent.Text }
        })
    $structure = @($syntax.FindAll({ $true }, $true) | ForEach-Object {
            $depth = 0
            $ancestor = $_.Parent
            while ($null -ne $ancestor) {
                $depth++
                $ancestor = $ancestor.Parent
            }
            [ordered]@{ Type = $_.GetType().FullName; Depth = $depth }
        })
    [pscustomobject]@{
        Tokens = ConvertTo-Json -InputObject $lexical -Depth 8 -Compress
        Ast    = ConvertTo-Json -InputObject $structure -Depth 8 -Compress
    }
}

$uv = (Get-Command uv -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$npm = (Get-Command npm.cmd -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$previousUvCache = [System.Environment]::GetEnvironmentVariable('UV_CACHE_DIR', 'Process')
Push-Location $repository
try {
    [System.Environment]::SetEnvironmentVariable('UV_CACHE_DIR', (Join-Path $cacheRoot 'uv'), 'Process')
    $blackArguments = @('tool', 'run', '--from', 'black==26.10.1', 'black', '--safe', '--config',
        (Join-Path $repository 'pyproject.toml'))
    if ($Check) { $blackArguments += '--check' }
    $blackArguments += (Join-Path $repository 'tests/scripts/Test-McpHttp.py')
    Invoke-FormatterCommand -Executable $uv -CommandArguments $blackArguments

    $prettierArguments = @('exec', '--yes', '--registry=https://registry.npmjs.org',
        '--noproxy=registry.npmjs.org', '--fetch-retries=1', '--fetch-timeout=30000', '--cache',
        (Join-Path $cacheRoot 'npm'), '--package=prettier@3.9.10', '--', 'prettier')
    $prettierFiles = @(
        'docs/architecture/overview.html',
        'src/AvaloniaDevTool.Cli/Contracts/official-input-contracts.json',
        'samples/AvaloniaDevTool.Sample/Assets/sample.json',
        'global.json',
        'dotnet-tools.json',
        '.prettierrc.json'
    )
    Invoke-FormatterCommand -Executable $npm -CommandArguments ($prettierArguments + '--debug-check' + $prettierFiles)
    $prettierMode = if ($Check) { '--check' } else { '--write' }
    Invoke-FormatterCommand -Executable $npm -CommandArguments ($prettierArguments + $prettierMode + $prettierFiles)

    $modulePath = Join-Path $toolRoot 'PSScriptAnalyzer/1.25.0/PSScriptAnalyzer.psd1'
    if (-not (Test-Path -LiteralPath $modulePath)) {
        New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
        Save-PSResource -Name PSScriptAnalyzer -Version '1.25.0' -Repository PSGallery -Path $toolRoot `
            -TrustRepository -AcceptLicense -ErrorAction Stop
    }
    Import-Module -Name $modulePath -Force -ErrorAction Stop
    $powerShellFiles = @((Join-Path $repository 'tests/scripts/Test-NativeAot.ps1')) + @(
        Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File |
            Where-Object { $_.Extension -in '.ps1', '.psd1' } |
            Select-Object -ExpandProperty FullName
    )
    $different = @()
    foreach ($file in ($powerShellFiles | Sort-Object -Unique)) {
        $before = Get-Content -Raw -Encoding utf8 -LiteralPath $file
        $formatted = (Invoke-Formatter -ScriptDefinition $before -Settings $settingsPath -ErrorAction Stop).
        Replace("`r`n", "`n").TrimEnd([char[]]"`r`n") + "`n"
        $beforeSignature = Get-PowerShellSignature -Text $before
        $afterSignature = Get-PowerShellSignature -Text $formatted
        if ($beforeSignature.Tokens -cne $afterSignature.Tokens -or $beforeSignature.Ast -cne $afterSignature.Ast) {
            throw "PowerShell formatting changed tokens or AST structure; original file preserved: $file"
        }
        if ($before -cne $formatted) {
            if ($Check) { $different += $file }
            else { [System.IO.File]::WriteAllText($file, $formatted, $utf8) }
        }
    }
    if ($different.Count -ne 0) {
        throw "PowerShell formatting differs: $($different -join ', ')"
    }
    Write-Host "Auxiliary formatters completed (Black 26.10.1, Prettier 3.9.10, PSScriptAnalyzer 1.25.0)."
} finally {
    [System.Environment]::SetEnvironmentVariable('UV_CACHE_DIR', $previousUvCache, 'Process')
    Pop-Location
}
