param(
    [Parameter(Mandatory)]
    [ValidateSet('Frames', 'FailedCommit', 'Restart', 'DiskError', 'PreviewGuard', 'RemoveKeepFiles', 'QueueOrder', 'SelectedTransfer', 'MagnetDownload')]
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

function Measure-Download {
    $snapshot = Send-Command @{ command = 'snapshot' }
    $bytes = $snapshot.data.torrents[0].downloaded
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        [Threading.Thread]::Sleep(100)
        $snapshot = Send-Command @{ command = 'snapshot' }
    } while ($watch.Elapsed.TotalSeconds -lt 10)
    return ($snapshot.data.torrents[0].downloaded - $bytes) / $watch.Elapsed.TotalSeconds
}

function Payload-Hash([string] $path) {
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($hasher.ComputeHash($stream)) }
    finally { $hasher.Dispose(); $stream.Dispose() }
}

try {
    $initial = Start-Engine
    switch ($Check) {
        'MagnetDownload' {
            $reply = Send-Command @{ command = 'preview'; source = $TorrentFile; destination = $payload }
            Assert $reply.ok 'Known file could not supply the magnet hash'
            $magnet = 'magnet:?xt=urn:btih:' + $reply.data.hashes[0]
            $reply = Send-Command @{ command = 'cancel_preview'; preview_id = $reply.data.preview_id }
            Assert $reply.ok 'Known file preview did not release'
            $reply = Send-Command @{ command = 'preview'; source = $magnet; destination = $payload }
            Assert ($reply.ok -and -not $reply.data.metadata_ready) 'Magnet already had metadata before confirmation'
            $reply = Send-Command @{ command = 'add'; preview_id = $reply.data.preview_id; destination = $payload; paused = $false }
            Assert $reply.ok 'Magnet could not be confirmed before metadata'
            $torrentId = $reply.data.torrent_id
            $peer = Start-Process -FilePath (Join-Path $repository 'artifacts/bin/Transfer/Release/Transfer.exe') `
                -ArgumentList @(('"' + $peerDirectory + '"'), '6881') -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $directory 'peer.log') -RedirectStandardError (Join-Path $directory 'peer-error.log')
            $until = [DateTime]::UtcNow.AddSeconds(100)
            do {
                Assert (-not $peer.HasExited) 'Magnet seed failed'
                $snapshot = Send-Command @{ command = 'snapshot' }
                if ($snapshot.data.torrents[0].complete -and $snapshot.data.torrents[0].size -eq 67108864) { break }
                [Threading.Thread]::Sleep(100)
            } while ([DateTime]::UtcNow -lt $until)
            $torrent = $snapshot.data.torrents[0]
            $snapshot.data | ConvertTo-Json -Depth 20 | Out-File -LiteralPath (Join-Path $directory 'magnet-snapshot.json') -Encoding utf8
            Assert ($torrent.complete -and $torrent.save_path -eq $payload -and $torrent.size -eq 67108864) 'Confirmed unknown magnet did not complete all wanted payload in its chosen destination'
            $file = Join-Path $payload 'transfer.bin'
            $seedHash = Payload-Hash (Join-Path $peerDirectory 'seed/transfer.bin')
            Assert ((Payload-Hash $file) -eq $seedHash) 'Confirmed magnet payload differs from its seed'
            $previewPath = Join-Path $directory 'previews'
            if (Test-Path -LiteralPath $previewPath) {
                Assert (@(Get-ChildItem -LiteralPath $previewPath -Recurse -File).Count -eq 0) 'Confirmed magnet left payload in preview storage'
            }
            $reply = Send-Command @{ command = 'pause'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Downloaded magnet could not pause before offline corruption'
            Stop-Engine
            $stream = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            try {
                $original = $stream.ReadByte()
                $stream.Position = 0
                $stream.WriteByte($original -bxor 255)
                $stream.Flush($true)
            }
            finally { $stream.Dispose() }
            Assert ((Payload-Hash $file) -ne $seedHash) 'Offline fixture corruption did not change the payload'
            $snapshot = Start-Engine
            $reply = Send-Command @{ command = 'verify'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Verify refused the accepted torrent'
            $reply = Send-Command @{ command = 'resume'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Verified magnet could not resume'
            $until = [DateTime]::UtcNow.AddSeconds(30)
            do {
                $snapshot = Send-Command @{ command = 'snapshot' }
                $torrent = $snapshot.data.torrents[0]
                if ($torrent.complete -and (Payload-Hash $file) -eq $seedHash) { break }
                [Threading.Thread]::Sleep(100)
            } while ([DateTime]::UtcNow -lt $until)
            Assert ($torrent.complete -and (Payload-Hash $file) -eq $seedHash) 'Verify and Resume did not repair a corrupted accepted piece'
            [pscustomobject]@{ SeedHash = $seedHash; RepairedHash = (Payload-Hash $file) } |
                ConvertTo-Json | Tee-Object -FilePath (Join-Path $directory 'repair.json')
        }
        'SelectedTransfer' {
            $peer = Start-Process -FilePath (Join-Path $repository 'artifacts/bin/Transfer/Release/Transfer.exe') `
                -ArgumentList @(('"' + $peerDirectory + '"'), '6881', 'seed-files') -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $directory 'peer.log') -RedirectStandardError (Join-Path $directory 'peer-error.log')
            $until = [DateTime]::UtcNow.AddSeconds(30)
            do {
                Assert (-not $peer.HasExited) 'Multifile seed failed'
                if ((Get-Content -LiteralPath (Join-Path $directory 'peer.log') -Raw) -match 'ready') { break }
                [Threading.Thread]::Sleep(50)
            } while ([DateTime]::UtcNow -lt $until)
            Assert ((Get-Content -LiteralPath (Join-Path $directory 'peer.log') -Raw) -match 'ready') 'Multifile seed did not become ready'
            $TorrentFile = Join-Path $peerDirectory 'transfer.torrent'
            $reply = Send-Command @{ command = 'settings'; changes = @{ download_limit = 131072; alternative_download_limit = 524288 } }
            Assert $reply.ok 'Global limits were refused'
            $reply = Send-Command @{ command = 'preview'; source = $TorrentFile; destination = $payload }
            Assert ($reply.ok -and $reply.data.files.Count -eq 2) 'Aligned multifile fixture did not preview'
            Assert ($reply.data.files[0].path.Replace('\', '/') -eq 'selection/skip.bin' -and $reply.data.files[1].path.Replace('\', '/') -eq 'selection/wanted.bin') 'Fixture file order is not skip then wanted'
            $hash = $reply.data.hashes[0]
            $reply = Send-Command @{ command = 'add'; preview_id = $reply.data.preview_id; destination = $payload; paused = $false; priorities = @(0, 7) }
            Assert $reply.ok 'Selected files could not be confirmed'
            $torrentId = $reply.data.torrent_id
            $until = [DateTime]::UtcNow.AddSeconds(30)
            do {
                $snapshot = Send-Command @{ command = 'snapshot' }
                if ($snapshot.data.torrents[0].downloaded -gt 262144) { break }
                [Threading.Thread]::Sleep(50)
            } while ([DateTime]::UtcNow -lt $until)
            Assert ($snapshot.data.torrents[0].downloaded -gt 262144) 'Local transfer did not start'
            $normalRate = Measure-Download
            Assert ($normalRate -gt 65536 -and $normalRate -lt 170394) 'Normal global limit did not constrain real local payload'
            $reply = Send-Command @{ command = 'settings'; changes = @{ alternative_limits = $true } }
            Assert $reply.ok 'Alternative limits could not activate'
            $until = [DateTime]::UtcNow.AddSeconds(15)
            do {
                $snapshot = Send-Command @{ command = 'snapshot' }
                if ($snapshot.data.download_rate -gt 262144) { break }
                [Threading.Thread]::Sleep(50)
            } while ([DateTime]::UtcNow -lt $until)
            $alternativeRate = Measure-Download
            Assert ($alternativeRate -gt 262144 -and $alternativeRate -lt 681575 -and $alternativeRate -gt 2 * $normalRate) 'Alternative global limit did not replace the normal limit on real local payload'
            [pscustomobject]@{ NormalLimit = 131072; NormalRate = $normalRate; AlternativeLimit = 524288; AlternativeRate = $alternativeRate } |
                ConvertTo-Json | Tee-Object -FilePath (Join-Path $directory 'rates.json')
            $reply = Send-Command @{ command = 'settings'; changes = @{ alternative_limits = $false; download_limit = 0 } }
            Assert $reply.ok 'Normal unlimited transfer could not resume'
            $until = [DateTime]::UtcNow.AddSeconds(60)
            do {
                $snapshot = Send-Command @{ command = 'snapshot' }
                if ($snapshot.data.torrents[0].complete) { break }
                [Threading.Thread]::Sleep(100)
            } while ([DateTime]::UtcNow -lt $until)
            Assert $snapshot.data.torrents[0].complete 'Wanted file never completed'
            $wanted = Join-Path $payload 'selection/wanted.bin'
            $skipped = Join-Path $payload 'selection/skip.bin'
            Assert (-not (Test-Path -LiteralPath $skipped) -or (Get-Item -LiteralPath $skipped).Length -eq 0) 'Unwanted aligned file received payload'
            $wantedHash = Payload-Hash $wanted
            Assert ($wantedHash -eq (Get-FileHash -LiteralPath (Join-Path $peerDirectory 'seed/selection/wanted.bin') -Algorithm SHA256).Hash) 'Wanted payload differs from its seed'
            $reply = Send-Command @{ command = 'pause'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Completed selected torrent could not pause'
            $reply = Send-Command @{ command = 'preview'; source = ('magnet:?xt=urn:btih:' + $hash + '&tr=http%3A%2F%2F127.0.0.1%3A1%2Fannounce'); destination = (Join-Path $directory 'wrong-destination') }
            Assert ($reply.ok -and $reply.data.duplicate -eq $torrentId -and $reply.data.merge_available) 'Duplicate new tracker was not offered for merge'
            $previewId = $reply.data.preview_id
            $reply = Send-Command @{ command = 'merge_trackers'; preview_id = $previewId; torrent_id = $torrentId }
            Assert $reply.ok 'Explicit tracker merge failed'
            $reply = Send-Command @{ command = 'cancel_preview'; preview_id = $previewId }
            Assert $reply.ok 'Duplicate preview did not release'
            Stop-Engine
            $snapshot = Start-Engine
            $torrent = $snapshot.torrents[0]
            $detail = Send-Command @{ command = 'torrent'; torrent_id = $torrentId }
            Assert ($snapshot.torrents.Count -eq 1 -and $torrent.torrent_id -eq $torrentId -and $torrent.save_path -eq $payload -and $torrent.paused -and ($detail.data.priorities -join ',') -eq '0,7') 'Tracker merge changed durable existing membership choices'
            Assert ('http://127.0.0.1:1/announce' -in $detail.data.trackers) 'Merged tracker did not survive restart'
            Assert ((Payload-Hash $wanted) -eq $wantedHash) 'Tracker merge or preview cancellation changed existing payload'
            $reply = Send-Command @{ command = 'force'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Force start failed'
            $reply = Send-Command @{ command = 'snapshot' }
            Assert ($reply.data.torrents[0].forced -and -not $reply.data.torrents[0].paused) 'Force start did not preserve explicit force intent'
            $reply = Send-Command @{ command = 'resume'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Ordinary resume failed'
            $reply = Send-Command @{ command = 'snapshot' }
            Assert (-not $reply.data.torrents[0].forced -and -not $reply.data.torrents[0].paused) 'Ordinary resume retained force intent'
        }
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
            $reply = Send-Command @{ command = 'session_pause'; paused = $true }
            Assert $reply.ok 'Session pause was not saved'
            $reply = Send-Command @{ command = 'settings'; changes = @{ language = 'es'; theme = 'dark' } }
            Assert $reply.ok 'Appearance preferences were not saved'
            Stop-Engine
            $snapshot = Start-Engine
            Assert (@($snapshot.torrents).Count -eq 1) 'Saved torrent disappeared after restart'
            Assert ($snapshot.torrents[0].torrent_id -eq $torrentId) 'Durable identity changed after restart'
            Assert $snapshot.torrents[0].paused 'Saved pause intent was lost after restart'
            Assert $snapshot.all_paused 'Saved session pause was lost after restart'
            Assert ($snapshot.settings.language -eq 'es' -and $snapshot.settings.theme -eq 'dark') 'Saved appearance preferences were lost after restart'
            $reply = Send-Command @{ command = 'session_pause'; paused = $false }
            Assert $reply.ok 'Restored session could not resume'
            $snapshot = Send-Command @{ command = 'snapshot' }
            Assert $snapshot.data.torrents[0].paused 'Session resume changed an individually stopped torrent'
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
        'PreviewGuard' {
            $reply = Send-Command @{ command = 'preview'; source = $TorrentFile; destination = $payload }
            Assert $reply.ok 'Guard fixture could not be parsed'
            $magnet = 'magnet:?xt=urn:btih:' + $reply.data.hashes[0]
            $reply = Send-Command @{ command = 'cancel_preview'; preview_id = $reply.data.preview_id }
            Assert $reply.ok 'File preview did not release'
            $peer = Start-Process -FilePath (Join-Path $repository 'artifacts/bin/Transfer/Release/Transfer.exe') `
                -ArgumentList @(('"' + $peerDirectory + '"'), '6881') -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $directory 'peer.log') -RedirectStandardError (Join-Path $directory 'peer-error.log')
            $reply = Send-Command @{ command = 'preview'; source = $magnet; destination = $payload }
            Assert $reply.ok 'Magnet preview did not start'
            $previewId = $reply.data.preview_id
            $until = [DateTime]::UtcNow.AddSeconds(30)
            do {
                $reply = Send-Command @{ command = 'preview_detail'; preview_id = $previewId; destination = $payload }
                Assert $reply.ok 'Live preview detail failed'
                if ($reply.data.metadata_ready) { break }
                [Threading.Thread]::Sleep(50)
            } while ([DateTime]::UtcNow -lt $until)
            Assert $reply.data.metadata_ready 'Local peer did not supply magnet metadata'
            $until = [DateTime]::UtcNow.AddSeconds(2)
            do {
                Assert (@(Get-ChildItem -LiteralPath $payload -Recurse -File).Count -eq 0) 'Unconfirmed magnet wrote chosen payload'
                $previewPath = Join-Path $directory 'previews'
                if (Test-Path -LiteralPath $previewPath) {
                    Assert (@(Get-ChildItem -LiteralPath $previewPath -Recurse -File).Count -eq 0) 'Unconfirmed magnet created guarded payload files'
                }
                $snapshot = Send-Command @{ command = 'snapshot' }
                Assert (@($snapshot.data.torrents).Count -eq 0) 'Unconfirmed preview entered accepted membership'
                [Threading.Thread]::Sleep(50)
            } while ([DateTime]::UtcNow -lt $until)
            $peerLog = Get-Content -LiteralPath (Join-Path $directory 'peer.log') -Raw
            Assert ($peerLog -notmatch 'uploaded=[1-9]') 'Peer uploaded payload to an unconfirmed preview'
            $reply = Send-Command @{ command = 'cancel_preview'; preview_id = $previewId }
            Assert $reply.ok 'Guarded magnet did not cancel'
            Stop-Engine
            $snapshot = Start-Engine
            Assert (@($snapshot.torrents).Count -eq 0) 'Cancelled preview became membership after restart'
        }
        'RemoveKeepFiles' {
            $peer = Start-Process -FilePath (Join-Path $repository 'artifacts/bin/Transfer/Release/Transfer.exe') `
                -ArgumentList @(('"' + $peerDirectory + '"'), '6881') -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $directory 'peer.log') -RedirectStandardError (Join-Path $directory 'peer-error.log')
            $previewId = Preview
            $reply = Send-Command @{ command = 'add'; preview_id = $previewId; destination = $payload; paused = $false; priorities = @(4) }
            Assert $reply.ok 'Removal fixture addition failed'
            $torrentId = $reply.data.torrent_id
            $until = [DateTime]::UtcNow.AddSeconds(30)
            do {
                $snapshot = Send-Command @{ command = 'snapshot' }
                if ($snapshot.data.torrents[0].downloaded -gt 1048576) { break }
                [Threading.Thread]::Sleep(50)
            } while ([DateTime]::UtcNow -lt $until)
            Assert ($snapshot.data.torrents[0].downloaded -gt 1048576) 'Removal fixture did not obtain real payload'
            $reply = Send-Command @{ command = 'remove'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Remove was not acknowledged'
            Stop-Engine
            $file = Join-Path $payload 'transfer.bin'
            Assert (Test-Path -LiteralPath $file -PathType Leaf) 'Remove deleted payload'
            $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
            $snapshot = Start-Engine
            Assert (@($snapshot.torrents).Count -eq 0) 'Late checkpoint resurrected a removed torrent'
            Assert ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -eq $hash) 'Restart changed removed payload'
        }
        'QueueOrder' {
            $metadata = [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($TorrentFile))
            Assert (($metadata.Split('12:transfer.bin').Length - 1) -eq 1) 'Queue fixture requires the single-file transfer torrent'
            $ids = @()
            foreach ($index in 1..3) {
                $name = 'queue' + $index + '.bin'
                $source = Join-Path $directory ($name + '.torrent')
                [IO.File]::WriteAllBytes($source, [Text.Encoding]::Latin1.GetBytes($metadata.Replace('12:transfer.bin', ($name.Length.ToString() + ':' + $name))))
                $reply = Send-Command @{ command = 'preview'; source = $source; destination = $payload }
                Assert $reply.ok 'Queue fixture did not preview'
                $reply = Send-Command @{ command = 'add'; preview_id = $reply.data.preview_id; destination = $payload; paused = $true }
                Assert $reply.ok 'Queue fixture addition failed'
                $ids += $reply.data.torrent_id
            }
            $reply = Send-Command @{ command = 'queue'; torrent_ids = @($ids[0]); direction = 'down' }
            Assert $reply.ok 'Move down was refused'
            $snapshot = Send-Command @{ command = 'snapshot' }
            $order = @($snapshot.data.torrents | Sort-Object queue | ForEach-Object torrent_id)
            Assert (($order -join ',') -eq (@($ids[1], $ids[0], $ids[2]) -join ',')) 'Move down left the queue unchanged or moved the wrong torrent'
            $reply = Send-Command @{ command = 'queue'; torrent_ids = @($ids[2]); before_torrent_id = $ids[0] }
            Assert $reply.ok 'Atomic row drop was refused'
            Stop-Engine
            $snapshot = Start-Engine
            $order = @($snapshot.torrents | Sort-Object queue | ForEach-Object torrent_id)
            Assert (($order -join ',') -eq (@($ids[1], $ids[2], $ids[0]) -join ',')) 'Saved queue order was lost after restart'
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
