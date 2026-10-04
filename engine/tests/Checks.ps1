param(
    [Parameter(Mandatory)]
    [ValidateSet('Frames', 'FailedCommit', 'Restart', 'DiskError')]
    [string] $Check,
    [Parameter(Mandatory)]
    [string] $TorrentFile
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$executable = Join-Path $repository 'artifacts/bin/Engine/Release/Engine.exe'
$directory = Join-Path $repository ('artifacts/evidence/' + $Check + '-' + [guid]::NewGuid())
$null = New-Item -ItemType Directory -Path $directory
$payload = Join-Path $directory 'payload'
$null = New-Item -ItemType Directory -Path $payload
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
public static class CheckIdentity {
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint process);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int information, IntPtr buffer, int size, out int needed);
    public static uint ServerProcess(IntPtr pipe) {
        if (!GetNamedPipeServerProcessId(pipe, out var process)) throw new Win32Exception();
        return process;
    }
    public static string LogonSid() {
        using var identity = WindowsIdentity.GetCurrent();
        var token = identity.AccessToken.DangerousGetHandle();
        GetTokenInformation(token, 28, IntPtr.Zero, 0, out var size);
        var buffer = Marshal.AllocHGlobal(size);
        try {
            if (!GetTokenInformation(token, 28, buffer, size, out size)) throw new Win32Exception();
            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer, IntPtr.Size)).Value;
        } finally { Marshal.FreeHGlobal(buffer); }
    }
}
'@
$logon = [CheckIdentity]::LogonSid()
$script:sequence = 0
$script:process = $null
$script:pipe = $null
$stalled = $null
$peer = $null
$peerDirectory = Join-Path $directory 'peer'

function Assert([bool] $condition, [string] $failure) {
    if (-not $condition) { throw $failure }
}

function Read-Bytes([IO.Stream] $stream, [int] $count) {
    $bytes = [byte[]]::new($count)
    $offset = 0
    while ($offset -lt $count) {
        $read = $stream.ReadAsync($bytes, $offset, $count - $offset)
        if (-not $read.Wait(10000)) { $stream.Dispose(); throw 'Pipe reply did not arrive' }
        if ($read.Result -eq 0) { throw 'Pipe disconnected during a frame' }
        $offset += $read.Result
    }
    return ,$bytes
}

function Read-Frame([IO.Stream] $stream) {
    $header = Read-Bytes $stream 4
    $size = [BitConverter]::ToInt32($header, 0)
    Assert ($size -gt 0 -and $size -le 16777216) 'Reply frame violates its limit'
    $bytes = Read-Bytes $stream $size
    return [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
}

function Connect-Pipe {
    $stream = [IO.Pipes.NamedPipeClientStream]::new('.', "TinyTorrent.$logon",
        [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    $stream.Connect(10000)
    if ([CheckIdentity]::ServerProcess($stream.SafePipeHandle.DangerousGetHandle()) -ne $script:process.Id) {
        $stream.Dispose()
        throw 'TinyTorrent is already running. The check refuses to command an engine it did not start.'
    }
    $hello = Read-Frame $stream
    Assert ($hello.type -eq 'hello' -and $hello.version -eq 1) 'Invalid version handshake'
    return $stream
}

function Send-Command([hashtable] $fields) {
    $fields.request_id = ++$script:sequence
    $bytes = [Text.Encoding]::UTF8.GetBytes(($fields | ConvertTo-Json -Depth 20 -Compress))
    $header = [BitConverter]::GetBytes([int]$bytes.Length)
    $script:pipe.Write($header, 0, 4)
    $script:pipe.Write($bytes, 0, $bytes.Length)
    $reply = Read-Frame $script:pipe
    Assert ($reply.request_id -eq $fields.request_id) 'Reply belongs to another request'
    return $reply
}

function Start-Engine {
    $script:process = Start-Process -FilePath $executable -ArgumentList @('--headless', '--background', '--data', ('"' + $directory + '"')) -WindowStyle Hidden -PassThru
    $script:pipe = Connect-Pipe
    $until = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $reply = Send-Command @{ command = 'snapshot' }
        Assert $reply.ok 'Engine refused its initial snapshot'
        if (-not $reply.data.loading) {
            Assert (-not $reply.data.storage_failed) 'New store could not load'
            return $reply.data
        }
        [Threading.Thread]::Yield() | Out-Null
    } while ([DateTime]::UtcNow -lt $until)
    throw 'Engine startup did not become ready'
}

function Stop-Engine {
    if ($script:pipe) {
        try { $null = Send-Command @{ command = 'exit' } }
        finally { $script:pipe.Dispose(); $script:pipe = $null }
    }
    if ($script:process) {
        Assert ($script:process.WaitForExit(15000)) 'Coordinated Exit did not stop the engine'
        Assert ($script:process.ExitCode -eq 0) 'Final storage commit failed'
        $script:process.Dispose()
        $script:process = $null
    }
}

function Preview {
    $reply = Send-Command @{ command = 'preview'; source = $TorrentFile; destination = $payload }
    Assert $reply.ok 'Valid fixture did not preview'
    return $reply.data.preview_id
}

try {
    $initial = Start-Engine
    switch ($Check) {
        'Frames' {
            foreach ($bytes in @([byte[]](1, 2), [BitConverter]::GetBytes(16777217),
                [byte[]](1, 0, 0, 0, 123), [byte[]](1, 0, 0, 0, 255))) {
                $client = Connect-Pipe
                $client.Write($bytes, 0, $bytes.Length)
                $client.Dispose()
                $reply = Send-Command @{ command = 'snapshot' }
                Assert $reply.ok 'A malformed client broke the valid connection'
            }
            $client = Connect-Pipe
            $body = [Text.Encoding]::UTF8.GetBytes('{"request_id":101,"command":"snapshot"}')
            $header = [BitConverter]::GetBytes([int]$body.Length)
            $client.Write($header, 0, 2)
            $client.Write($header, 2, 2)
            $client.Write($body, 0, 3)
            $client.Write($body, 3, $body.Length - 3)
            $reply = Read-Frame $client
            Assert ($reply.ok -and $reply.request_id -eq 101) 'Partial frame writes were not assembled'
            $client.Dispose()
            $stalled = Connect-Pipe
            $stalled.Write([byte[]](1, 2), 0, 2)
        }
        'FailedCommit' {
            $previewId = Preview
            $marker = Join-Path $directory 'settings.json'
            $null = New-Item -ItemType Directory -Path $marker
            $reply = Send-Command @{ command = 'add'; preview_id = $previewId; destination = $payload; paused = $false }
            Assert (-not $reply.ok -and $reply.error.code -eq 'storage_failed') 'Failed membership write was reported as saved'
            $snapshot = Send-Command @{ command = 'snapshot' }
            Assert (@($snapshot.data.torrents).Count -eq 0) 'Failed addition entered membership'
            Assert (@(Get-ChildItem -LiteralPath $payload -Recurse -File).Count -eq 0) 'Uncommitted addition created payload'
            $language = if ($initial.settings.language -eq 'es') { 'en' } else { 'es' }
            $reply = Send-Command @{ command = 'settings'; changes = @{ language = $language } }
            Assert (-not $reply.ok -and $reply.error.code -eq 'storage_failed') 'Failed language save was reported as saved'
            $snapshot = Send-Command @{ command = 'snapshot' }
            Assert ($snapshot.data.settings.language -eq $language) 'Failed save reverted the live language'
            Assert (-not $snapshot.data.language_saved) 'Live language was falsely reported as saved'
            Remove-Item -LiteralPath $marker
            Stop-Engine
            $snapshot = Start-Engine
            Assert (@($snapshot.torrents).Count -eq 0) 'Orphan resume data resurrected an uncommitted addition'
            Assert ($snapshot.settings.language -eq $initial.settings.language) 'Unsaved language survived as a saved preference'
        }
        'Restart' {
            $previewId = Preview
            $reply = Send-Command @{ command = 'add'; preview_id = $previewId; destination = $payload; paused = $false }
            Assert $reply.ok 'Fixture addition failed'
            $torrentId = $reply.data.torrent_id
            $reply = Send-Command @{ command = 'pause'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Pause intent was not saved'
            $reply = Send-Command @{ command = 'settings'; changes = @{ language = 'es'; theme = 'dark' } }
            Assert $reply.ok 'Appearance preferences were not saved'
            Stop-Engine
            $snapshot = Start-Engine
            Assert (@($snapshot.torrents).Count -eq 1) 'Saved torrent disappeared after restart'
            Assert ($snapshot.torrents[0].torrent_id -eq $torrentId) 'Durable identity changed after restart'
            Assert $snapshot.torrents[0].paused 'Saved pause intent was lost after restart'
            Assert ($snapshot.settings.language -eq 'es' -and $snapshot.settings.theme -eq 'dark') 'Saved appearance preferences were lost after restart'
            $reply = Send-Command @{ command = 'resume'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Restored torrent could not resume'
        }
        'DiskError' {
            $collision = Join-Path $payload 'transfer.bin'
            $null = New-Item -ItemType Directory -Path $collision
            $peer = Start-Process -FilePath (Join-Path $repository 'artifacts/bin/Transfer/Release/Transfer.exe') `
                -ArgumentList @(('"' + $peerDirectory + '"'), '6881') -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $directory 'peer.log') -RedirectStandardError (Join-Path $directory 'peer-error.log')
            $previewId = Preview
            $reply = Send-Command @{ command = 'add'; preview_id = $previewId; destination = $payload; paused = $false }
            Assert $reply.ok 'Fixture addition failed'
            $torrentId = $reply.data.torrent_id
            $until = [DateTime]::UtcNow.AddSeconds(30)
            do {
                $snapshot = Send-Command @{ command = 'snapshot' }
                $torrent = $snapshot.data.torrents[0]
                if ($torrent.status -eq 'error') { break }
                [Threading.Thread]::Sleep(50)
            } while ([DateTime]::UtcNow -lt $until)
            Assert ($torrent.status -eq 'error' -and $torrent.error -eq 'torrent_error' -and $torrent.detail.Length -gt 0) `
                'Payload write failure was hidden by normal transfer status'
            Assert ([IO.Directory]::GetFiles($collision).Length -eq 0) 'Collision fixture unexpectedly contains files'
            Remove-Item -LiteralPath $collision
            $reply = Send-Command @{ command = 'resume'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Resume refused a repaired payload destination'
            $until = [DateTime]::UtcNow.AddSeconds(30)
            do {
                $snapshot = Send-Command @{ command = 'snapshot' }
                $torrent = $snapshot.data.torrents[0]
                if ($torrent.downloaded -gt 0 -and $torrent.status -ne 'error') { break }
                [Threading.Thread]::Sleep(50)
            } while ([DateTime]::UtcNow -lt $until)
            Assert ($torrent.downloaded -gt 0 -and $torrent.status -ne 'error') 'Resume did not recover payload transfer after a disk failure'
            $reply = Send-Command @{ command = 'pause'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Recovered torrent could not pause'
        }
    }
    Stop-Engine
    if ($stalled) { $stalled.Dispose(); $stalled = $null }
    [pscustomobject]@{ Check = $Check; Passed = $true; Evidence = $directory } | ConvertTo-Json
}
finally {
    if ($peer -and -not $peer.HasExited) {
        $null = New-Item -ItemType File -Path (Join-Path $peerDirectory 'stop') -Force
        if (-not $peer.WaitForExit(15000)) { $peer.Kill(); $peer.WaitForExit() }
    }
    if ($stalled) { $stalled.Dispose() }
    if ($script:process -and -not $script:process.HasExited) {
        try {
            if (-not $script:pipe) { $script:pipe = Connect-Pipe }
            $null = Send-Command @{ command = 'exit' }
            if (-not $script:process.WaitForExit(15000)) { throw 'Exit did not finish' }
        }
        catch {
            Write-Warning ('Forced cleanup of failed check engine PID ' + $script:process.Id)
            $script:process.Kill()
            $script:process.WaitForExit()
        }
    }
    if ($script:pipe) { $script:pipe.Dispose() }
}
