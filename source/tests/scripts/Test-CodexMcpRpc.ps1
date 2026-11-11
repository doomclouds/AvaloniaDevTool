[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../tools/Codex-Mcp.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('avdt-codex-rpc-' + [Guid]::NewGuid().ToString('N'))
$fakeCodex = Join-Path $testRoot 'fake-codex.ps1'
$isolatedHome = Join-Path $testRoot 'home'
$beforeHome = [Environment]::GetEnvironmentVariable('CODEX_HOME', 'Process')
$connections = [Collections.Generic.List[object]]::new()

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-RequestFailure {
    param([scriptblock]$Action, [string]$Pattern)
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_ }
    Assert-True ($null -ne $caught) 'The request unexpectedly succeeded.'
    Assert-True ($caught.Exception.Message -match $Pattern) "The request failed with the wrong error: $($caught.Exception.Message)"
    Assert-True (-not $caught.Exception.Message.Contains('SYNTHETIC-PRIVATE')) 'An error exposed private input or server output.'
    return $caught.Exception
}

try {
    New-Item -ItemType Directory -Path $isolatedHome -Force | Out-Null
    $fixture = @'
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$launchArguments = @($args)
$initialized = $false
$experimental = $false
:requests while ($null -ne ($line = [Console]::ReadLine())) {
    $message = ConvertFrom-Json -InputObject $line -AsHashtable
    if (-not $message.Contains('id')) {
        if ($message['method'] -eq 'initialized') { $initialized = $true }
        continue
    }
    if (-not $message.Contains('method')) {
        [IO.File]::WriteAllText((Join-Path $env:CODEX_HOME 'approval.txt'), 'unexpected response')
        continue
    }
    $result = switch ($message['method']) {
        'initialize' {
            $experimental = $message['params']['capabilities']['experimentalApi']
            @{ serverInfo = @{ name = 'fake-codex'; version = '1' }; capabilities = @{} }
        }
        'inspect' { @{ home = $env:CODEX_HOME; arguments = $launchArguments; initialized = $initialized; experimental = $experimental } }
        'echo' {
            [Console]::WriteLine('{"jsonrpc":"2.0","method":"progress","params":{"value":1}}')
            $message['params']
        }
        'stderr' {
            [Console]::Error.Write(('x' * 100000))
            @{ drained = $true }
        }
        'error' {
            [Console]::WriteLine((@{ jsonrpc = '2.0'; id = $message['id']; error = @{ code = -32001; message = 'SYNTHETIC-PRIVATE server detail' } } | ConvertTo-Json -Compress))
            continue requests
        }
        'server-request' {
            [Console]::WriteLine('{"jsonrpc":"2.0","id":999,"method":"item/commandExecution/requestApproval","params":{"secret":"SYNTHETIC-PRIVATE"}}')
            continue requests
        }
        'malformed' {
            [Console]::WriteLine('invalid-json-SYNTHETIC-PRIVATE')
            continue requests
        }
        'timeout' {
            Start-Sleep -Seconds 30
            @{ done = $true }
        }
        'exit' { exit 0 }
        default { @{ unexpected = $true } }
    }
    [Console]::WriteLine((@{ jsonrpc = '2.0'; id = $message['id']; result = $result } | ConvertTo-Json -Depth 30 -Compress))
}
'@
    [IO.File]::WriteAllText($fakeCodex, $fixture, [Text.UTF8Encoding]::new($false))
    $connection = Start-CodexMcpConnection -CodexPath $fakeCodex -CodexHome $isolatedHome
    $connections.Add($connection)
    $state = Invoke-CodexMcpRequest -Connection $connection -Method 'inspect' -Params @{}
    Assert-True ($state -is [System.Collections.IDictionary]) 'The response was not returned as a dictionary.'
    Assert-True ($state['home'] -ceq $isolatedHome) 'CODEX_HOME was not confined to the child.'
    Assert-True (($state['arguments'] -join '|') -ceq 'app-server|--stdio') 'The launcher arguments were not preserved.'
    Assert-True ($state['initialized'] -and $state['experimental']) 'The initialization handshake was not completed.'
    Assert-True ([Environment]::GetEnvironmentVariable('CODEX_HOME', 'Process') -ceq $beforeHome) 'The helper changed the caller environment.'

    $reply = Invoke-CodexMcpRequest -Connection $connection -Method 'echo' -Params @{ text = '中文 round trip'; nested = @{ enabled = $true }; values = @(1, 2) }
    Assert-True ($reply['text'] -ceq '中文 round trip' -and $reply['nested']['enabled'] -and $reply['values'].Count -eq 2) 'Notifications or UTF-8 input damaged the reply.'
    $stderr = Invoke-CodexMcpRequest -Connection $connection -Method 'stderr' -Params @{}
    Assert-True $stderr['drained'] 'The stderr pipe blocked a request.'
    $rpcError = Assert-RequestFailure { Invoke-CodexMcpRequest -Connection $connection -Method 'error' -Params @{ secret = 'SYNTHETIC-PRIVATE input' } } 'RPC -32001'
    Assert-True ($rpcError.Data['RpcMessage'] -ceq 'SYNTHETIC-PRIVATE server detail') 'The RPC message was not preserved in exception data.'
    $state = Invoke-CodexMcpRequest -Connection $connection -Method 'inspect' -Params @{}
    Assert-True $state['initialized'] 'A normal RPC error poisoned the transport.'
    $processId = $connection.ProcessId
    Stop-CodexMcpConnection -Connection $connection
    Stop-CodexMcpConnection -Connection $connection
    Assert-True (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) 'Closing stdin left the owned server running.'

    foreach ($failure in @(
            @{ Method = 'server-request'; Pattern = 'No approval response was sent' },
            @{ Method = 'malformed'; Pattern = 'invalid JSON' },
            @{ Method = 'exit'; Pattern = 'closed before answering' },
            @{ Method = 'timeout'; Pattern = 'timed out' }
        )) {
        $connection = Start-CodexMcpConnection -CodexPath $fakeCodex -CodexHome $isolatedHome
        $connections.Add($connection)
        $null = Assert-RequestFailure { Invoke-CodexMcpRequest -Connection $connection -Method $failure.Method -Params @{} -TimeoutSeconds 1 } $failure.Pattern
        Assert-True $connection.TransportFaulted 'An uncertain request was left reusable.'
        $null = Assert-RequestFailure { Invoke-CodexMcpRequest -Connection $connection -Method 'inspect' -Params @{} } 'unconfirmed request'
        $processId = $connection.ProcessId
        Stop-CodexMcpConnection -Connection $connection -TimeoutSeconds 1
        Assert-True (-not (Get-Process -Id $processId -ErrorAction SilentlyContinue)) 'Stopping a faulted connection left its server running.'
    }
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $isolatedHome 'approval.txt'))) 'The helper replied to an approval request.'
    Write-Output 'Codex MCP RPC checks passed: PowerShell launcher, handshake, isolated environment, UTF-8, notifications, stderr draining, RPC error, refusal of client interaction, malformed JSON, early exit, timeout, and owned-process shutdown.'
} finally {
    foreach ($connection in $connections) { Stop-CodexMcpConnection -Connection $connection -TimeoutSeconds 1 }
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to remove a fixture outside the temporary root.' }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
