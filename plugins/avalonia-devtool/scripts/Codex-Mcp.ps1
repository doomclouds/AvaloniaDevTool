Set-StrictMode -Version Latest

function Start-CodexMcpConnection {
    [CmdletBinding()]
    param(
        [string]$CodexPath = 'codex',
        [string]$CodexHome = ''
    )

    $command = Get-Command -Name $CodexPath -ErrorAction Stop | Select-Object -First 1
    if ($command.CommandType -notin @('Application', 'ExternalScript')) {
        throw 'CodexPath must resolve to an executable or PowerShell script.'
    }
    $commandPath = $command.Source
    $extension = [IO.Path]::GetExtension($commandPath)
    if ($extension -in @('.cmd', '.bat')) {
        throw 'Use the codex.ps1 launcher or a native codex executable instead of a batch launcher.'
    }

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    if ($extension -ieq '.ps1') {
        $start.FileName = (Get-Command pwsh -CommandType Application -ErrorAction Stop).Source
        foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $commandPath)) {
            $start.ArgumentList.Add($argument)
        }
    } else {
        $start.FileName = $commandPath
    }
    $start.ArgumentList.Add('app-server')
    $start.ArgumentList.Add('--stdio')
    if ($CodexHome) {
        $resolvedHome = [IO.Path]::GetFullPath($CodexHome)
        if (-not [IO.Directory]::Exists($resolvedHome)) {
            throw 'CodexHome must point to an existing directory.'
        }
        $start.Environment['CODEX_HOME'] = $resolvedHome
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) {
        $process.Dispose()
        throw 'Could not start Codex app-server.'
    }
    $connection = [PSCustomObject]@{
        Process          = $process
        ProcessId        = $process.Id
        CodexPath        = $CodexPath
        Sequence         = 0L
        Busy             = $false
        TransportFaulted = $false
        Stopped          = $false
        StandardError    = $process.StandardError.ReadToEndAsync()
    }
    try {
        $null = Invoke-CodexMcpRequest -Connection $connection -Method 'initialize' -Params @{
            clientInfo   = @{ name = 'avalonia-devtool-mcp-manager'; version = '1' }
            capabilities = @{ experimentalApi = $true }
        }
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"initialized"}')
        $process.StandardInput.Flush()
        return $connection
    } catch {
        Stop-CodexMcpConnection -Connection $connection
        throw
    }
}

function Invoke-CodexMcpRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Connection,
        [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Method,
        [AllowNull()]$Params = $null,
        [ValidateRange(1, 3600)][int]$TimeoutSeconds = 60
    )

    if ($Connection.Stopped -or $Connection.TransportFaulted) {
        throw 'The Codex connection is closed or has an unconfirmed request. Open a new connection and inspect state before retrying.'
    }
    if ($Connection.Busy) {
        throw 'Codex requests must run sequentially on each connection.'
    }
    if ($Connection.Process.HasExited) {
        $Connection.TransportFaulted = $true
        throw 'Codex app-server has exited.'
    }

    $Connection.Busy = $true
    try {
        $Connection.Sequence++
        $requestId = $Connection.Sequence
        $request = @{ jsonrpc = '2.0'; id = $requestId; method = $Method; params = $Params }
        $Connection.Process.StandardInput.WriteLine(($request | ConvertTo-Json -Depth 100 -Compress))
        $Connection.Process.StandardInput.Flush()
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            $remaining = [int][Math]::Ceiling($TimeoutSeconds * 1000 - $deadline.Elapsed.TotalMilliseconds)
            if ($remaining -le 0) {
                $Connection.TransportFaulted = $true
                throw "Codex request '$Method' timed out. Its result is unknown; inspect state before retrying."
            }
            $read = $Connection.Process.StandardOutput.ReadLineAsync()
            if (-not $read.Wait($remaining)) {
                $Connection.TransportFaulted = $true
                throw "Codex request '$Method' timed out. Its result is unknown; inspect state before retrying."
            }
            $line = $read.GetAwaiter().GetResult()
            if ($null -eq $line) {
                $Connection.TransportFaulted = $true
                throw "Codex app-server closed before answering '$Method'. Its result is unknown; inspect state before retrying."
            }
            try {
                $message = ConvertFrom-Json -InputObject $line -AsHashtable -Depth 100 -ErrorAction Stop
            } catch {
                $Connection.TransportFaulted = $true
                throw 'Codex app-server returned invalid JSON. Its output was not included in this error.'
            }
            if ($message -isnot [System.Collections.IDictionary]) {
                $Connection.TransportFaulted = $true
                throw 'Codex app-server returned an invalid JSON-RPC message.'
            }
            if (-not $message.Contains('id')) {
                continue
            }
            if ($message.Contains('method')) {
                $Connection.TransportFaulted = $true
                throw 'Codex app-server requested client interaction. No approval response was sent.'
            }
            if ($message['id'] -ne $requestId) {
                $Connection.TransportFaulted = $true
                throw 'Codex app-server returned an unexpected response identifier.'
            }
            if ($message.Contains('error')) {
                $code = $message['error']['code']
                $exception = [InvalidOperationException]::new("Codex app-server rejected '$Method' (RPC $code).")
                $exception.Data['RpcCode'] = $code
                $exception.Data['RpcMessage'] = $message['error']['message']
                throw $exception
            }
            if (-not $message.Contains('result')) {
                $Connection.TransportFaulted = $true
                throw 'Codex app-server returned a response without a result.'
            }
            return ,$message['result']
        }
    } catch {
        if (-not $_.Exception.Data.Contains('RpcCode')) {
            $Connection.TransportFaulted = $true
        }
        throw
    } finally {
        $Connection.Busy = $false
    }
}

function Stop-CodexMcpConnection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Connection,
        [ValidateRange(0, 60)][int]$TimeoutSeconds = 5
    )

    if ($Connection.Stopped) {
        return
    }
    $Connection.Stopped = $true
    try {
        try { $Connection.Process.StandardInput.Close() } catch { }
        if (-not $Connection.Process.HasExited -and
            -not $Connection.Process.WaitForExit($TimeoutSeconds * 1000)) {
            $Connection.Process.Kill($true)
            [void]$Connection.Process.WaitForExit(5000)
        }
    } finally {
        $Connection.Process.Dispose()
    }
}
