param(
    [string] $BinaryDirectory = "$PSScriptRoot/../../artifacts/bin/TinyTorrent/debug_win-x64"
)

$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'TinyTorrentUI.dll'))
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$httpType = $assembly.GetType('Syno.TinyTorrent.Services.ProviderHttp', $true)
$routeType = $assembly.GetType('Syno.TinyTorrent.Services.HttpRoute', $true)
$proxyType = $assembly.GetType('Syno.TinyTorrent.Models.ProxyType', $true)
$configure = $httpType.GetMethod('Configure', $flags)
$send = $httpType.GetMethod('Send', $flags)
$destination = 'provider-route.invalid'

function Require([bool] $Condition, [string] $Failure) {
    if (!$Condition) { throw $Failure }
}

function Read-Bytes([IO.Stream] $Stream, [int] $Count) {
    $bytes = [byte[]]::new($Count)
    $Stream.ReadExactly($bytes, 0, $Count)
    return ,$bytes
}

foreach ($case in @('Http', 'Socks4', 'Socks5', 'UnavailableAdapter')) {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $http = $null
    $client = $null
    $request = $null
    $direct = $null
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(5))
    try {
        $listener.Start()
        $uri = "https://$destination/check"
        if ($case -eq 'UnavailableAdapter') {
            $direct = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $direct.Start()
            $uri = "https://127.0.0.1:$($direct.LocalEndpoint.Port)/check"
        }
        $route = [Activator]::CreateInstance($routeType, $true)
        $name = if ($case -eq 'UnavailableAdapter') { 'Http' } else { $case }
        $properties = @{
            Type = [Enum]::Parse($proxyType, $name)
            Host = '127.0.0.1'
            Port = $listener.LocalEndpoint.Port
            Adapter = if ($case -eq 'UnavailableAdapter') { 'provider-check-unavailable-adapter' } else { '' }
        }
        foreach ($key in $properties.Keys) {
            $routeType.GetProperty($key, $flags).SetValue($route.PSObject.BaseObject, $properties[$key].PSObject.BaseObject)
        }
        $http = [Activator]::CreateInstance($httpType, $true)
        $configure.Invoke($http.PSObject.BaseObject, [object[]]@($route.PSObject.BaseObject)) | Out-Null
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $uri)
        $pending = $send.Invoke($http.PSObject.BaseObject,
            [object[]]@($request.PSObject.BaseObject, [int]1024, $deadline.Token.PSObject.BaseObject))

        if ($case -ne 'UnavailableAdapter') {
            $client = $listener.AcceptTcpClientAsync().WaitAsync([TimeSpan]::FromSeconds(3)).GetAwaiter().GetResult()
            $stream = $client.GetStream()
            $stream.ReadTimeout = 2000
            $stream.WriteTimeout = 2000
            switch ($case) {
                'Http' {
                    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 1024, $true)
                    try {
                        Require ($reader.ReadLine() -eq "CONNECT ${destination}:443 HTTP/1.1") 'HTTP did not tunnel the requested destination through the proxy.'
                        $lines = 0
                        do {
                            $line = $reader.ReadLine()
                            Require ($null -ne $line -and ++$lines -le 32) 'HTTP CONNECT headers were incomplete or unbounded.'
                        } while ($line.Length -gt 0)
                    }
                    finally { $reader.Dispose() }
                    $reply = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 403 Forbidden`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
                }
                'Socks4' {
                    $header = Read-Bytes $stream 8
                    Require ($header[0] -eq 4 -and $header[1] -eq 1 -and
                        $header[2] -eq 1 -and $header[3] -eq 187 -and
                        $header[4] -eq 0 -and $header[5] -eq 0 -and $header[6] -eq 0 -and $header[7] -ne 0) 'SOCKS4a did not request remote hostname resolution on port 443.'
                    Require ($stream.ReadByte() -eq 0) 'SOCKS4a unexpectedly supplied a user ID.'
                    $bytes = [Collections.Generic.List[byte]]::new()
                    while (($value = $stream.ReadByte()) -ne 0) {
                        Require ($value -ge 0 -and $bytes.Count -lt 255) 'SOCKS4a hostname was incomplete or unbounded.'
                        $bytes.Add([byte]$value)
                    }
                    Require ([Text.Encoding]::ASCII.GetString($bytes.ToArray()) -eq $destination) 'SOCKS4a bypassed the requested hostname.'
                    $reply = [byte[]]@(0, 91, 0, 0, 0, 0, 0, 0)
                }
                'Socks5' {
                    $greeting = Read-Bytes $stream 2
                    Require ($greeting[0] -eq 5 -and $greeting[1] -gt 0) 'SOCKS5 greeting was invalid.'
                    $methods = Read-Bytes $stream $greeting[1]
                    Require ($methods -contains [byte]0) 'SOCKS5 did not offer anonymous negotiation.'
                    $stream.Write([byte[]]@(5, 0), 0, 2)
                    $header = Read-Bytes $stream 4
                    Require ($header[0] -eq 5 -and $header[1] -eq 1 -and $header[2] -eq 0 -and $header[3] -eq 3) 'SOCKS5 did not send the destination as a hostname.'
                    $length = $stream.ReadByte()
                    Require ($length -gt 0) 'SOCKS5 hostname was missing.'
                    $bytes = Read-Bytes $stream $length
                    Require ([Text.Encoding]::ASCII.GetString($bytes) -eq $destination) 'SOCKS5 bypassed the requested hostname.'
                    $port = Read-Bytes $stream 2
                    Require ($port[0] -eq 1 -and $port[1] -eq 187) 'SOCKS5 requested the wrong destination port.'
                    $reply = [byte[]]@(5, 2, 0, 1, 0, 0, 0, 0, 0, 0)
                }
            }
            $stream.Write($reply, 0, $reply.Length)
        }

        $refused = $false
        try {
            $pending.WaitAsync([TimeSpan]::FromSeconds(2)).GetAwaiter().GetResult() | Out-Null
        }
        catch {
            $failure = $_.Exception
            while ($failure -isnot [Net.Http.HttpRequestException] -and $null -ne $failure.InnerException) {
                $failure = $failure.InnerException
            }
            $refused = $failure -is [Net.Http.HttpRequestException]
        }
        if ($case -eq 'UnavailableAdapter') {
            Require (!$listener.Pending()) 'An unavailable adapter fell back to contacting the proxy.'
            Require (!$direct.Pending()) 'An unavailable adapter fell back to contacting the destination directly.'
        }
        Require ($refused -and !$deadline.IsCancellationRequested) "$case did not fail promptly after the route refused the request."
    }
    finally {
        $deadline.Cancel()
        if ($null -ne $http) { $http.Dispose() }
        if ($null -ne $client) { $client.Dispose() }
        if ($null -ne $request) { $request.Dispose() }
        if ($null -ne $direct) { $direct.Stop() }
        $listener.Stop()
        $deadline.Dispose()
    }
}

Write-Output 'PASS: HTTP CONNECT, SOCKS4a and SOCKS5 reach the configured proxy; unavailable adapters fail without fallback.'
