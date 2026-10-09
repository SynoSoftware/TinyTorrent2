param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot '../../artifacts'),
    [int] $Samples = 120
)

$ErrorActionPreference = 'Stop'
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
$executable = Join-Path $ArtifactsPath 'bin/Engine/Release/TinyTorrent.exe'
$directory = Join-Path $ArtifactsPath ('evidence/Latency-' + [guid]::NewGuid())
$peerDirectory = Join-Path $directory 'peer'
$destination = Join-Path $directory 'downloads'
$null = New-Item -ItemType Directory -Path $peerDirectory, $destination
. (Join-Path $PSScriptRoot 'Engine.ps1')
$peer = $null
$measurements = [Collections.Generic.List[object]]::new()
$load = [Collections.Generic.List[object]]::new()
$pending = @{}
$context = 0

function Receive-Detail($frame) {
    Assert ($frame.type -eq 'detail') 'Unexpected unsolicited frame'
    $key = [string]$frame.context
    if (-not $pending.ContainsKey($key)) { return }
    $sample = $pending[$key]
    Assert $frame.ok 'Complete detail failed'
    Assert $frame.data.ready 'Pushed detail is incomplete'
    $measurements.Add([pscustomobject]@{
        phase = $sample.Phase; kind = 'complete'; operation = $sample.View
        milliseconds = $sample.Watch.Elapsed.TotalMilliseconds
    })
    $pending.Remove($key)
}

function Measure-Reply([hashtable] $fields, [string] $phase, [string] $kind) {
    $fields.request_id = ++$script:sequence
    $watch = [Diagnostics.Stopwatch]::StartNew()
    if ($fields.ContainsKey('context')) {
        $pending[[string]$fields.context] = @{ Watch = $watch; Phase = $phase; View = $fields.view }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($fields | ConvertTo-Json -Depth 20 -Compress))
    $script:pipe.Write([BitConverter]::GetBytes([int]$bytes.Length), 0, 4)
    $script:pipe.Write($bytes, 0, $bytes.Length)
    do {
        $frame = Read-Frame $script:pipe
        if ($frame.type -eq 'detail') { Receive-Detail $frame; continue }
        Assert ($frame.request_id -eq $fields.request_id) 'Reply belongs to another request'
        if (-not $frame.ok) { throw ('Command failed: ' + ($frame | ConvertTo-Json -Depth 20 -Compress)) }
        $measurements.Add([pscustomobject]@{
            phase = $phase; kind = $kind; operation = $(if ($fields.view) { $fields.view } else { $fields.command })
            milliseconds = $watch.Elapsed.TotalMilliseconds
        })
        return $frame
    } while ($true)
}

try {
    $null = Start-Engine
    $port = 48731
    $configured = Send-Command @{ command = 'settings'; changes = @{
        listen_port = $port; port_mapping = $false; dht = $false; lsd = $false
        active_downloads = 0; active_total = 0; connection_limit = 300; append_suffix = $false
        seeding_minutes = 60; inactive_time = 60; layout = 'keep'
    } }
    Assert $configured.ok 'Load settings were refused'
    $peer = Start-Process -FilePath (Join-Path $ArtifactsPath 'bin/Transfer/Release/Transfer.exe') `
        -ArgumentList @(('"' + $peerDirectory + '"'), $port, 'load') -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $peerDirectory 'stdout.log') `
        -RedirectStandardError (Join-Path $peerDirectory 'stderr.log')
    $until = [DateTime]::UtcNow.AddSeconds(45)
    do {
        if ($peer.HasExited) { throw 'Load peer exited during preparation' }
        $ready = (Get-Content (Join-Path $peerDirectory 'stdout.log') -Raw) -match 'ready'
        if (-not $ready) { Start-Sleep -Milliseconds 100 }
    } while (-not $ready -and [DateTime]::UtcNow -lt $until)
    Assert $ready 'Load peer did not become ready'
    Copy-Item -LiteralPath (Join-Path $peerDirectory 'seed/transfer.bin') -Destination (Join-Path $destination 'load-0.bin')
    foreach ($file in Get-ChildItem -LiteralPath $peerDirectory -Filter 'load-*.torrent') {
        $preview = Send-Command @{ command = 'preview'; source = $file.FullName; destination = $destination }
        Assert $preview.ok 'Load torrent preview failed'
        $added = Send-Command @{ command = 'add'; preview_id = $preview.data.preview_id; destination = $destination }
        Assert $added.ok 'Load torrent addition failed'
    }
    $until = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $before = (Send-Command @{ command = 'snapshot' }).data
        $peers = ($before.torrents | Measure-Object -Property peer_count -Sum).Sum
        if ($peers -lt 100) { Start-Sleep -Milliseconds 100 }
    } while ($peers -lt 100 -and [DateTime]::UtcNow -lt $until)
    Assert ($peers -ge 100) 'Fewer than 100 live peer connections; measurement did not start'
    Assert (@($before.torrents | Where-Object status -EQ 'seeding').Count -gt 0) 'No finished seed for policy-query measurement'
    $torrentId = ($before.torrents | Where-Object { $_.peer_count -gt 0 -and $_.progress -lt 1 } | Select-Object -First 1).torrent_id
    $views = 'general', 'files', 'peers', 'trackers', 'pieces'
    foreach ($interval in 1000, 10000) {
        $configured = Send-Command @{ command = 'settings'; changes = @{ refresh_interval = $interval } }
        Assert $configured.ok 'Refresh interval was refused'
        for ($index = 0; $index -lt $Samples * $views.Count; ++$index) {
            $phase = "refresh-$interval"
            $command = switch ($index % 6) {
                0 { @{ command = 'piece_order'; torrent_ids = @($torrentId); sequential = [bool](($index / 6) % 2) } }
                1 { @{ command = 'speed_limit'; torrent_ids = @($torrentId); upload_limit = $(if (($index / 6) % 2 -lt 1) { 0 } else { 65536 }) } }
                2 { @{ command = 'reannounce'; torrent_id = $torrentId } }
                3 { @{ command = 'file_scope'; torrent_ids = @($torrentId) } }
                4 { @{ command = 'edit'; torrent_id = $torrentId; changes = @{ priorities = @(@{ index = 0; priority = $(if (($index / 6) % 2 -lt 1) { 1 } else { 4 }) }) } } }
                5 { @{ command = 'queue'; torrent_ids = @($torrentId); direction = $(if (($index / 6) % 2 -lt 1) { 'top' } else { 'bottom' }) } }
            }
            $null = Measure-Reply $command $phase 'command'
            $view = $views[$index % $views.Count]
            $null = Measure-Reply @{ command = 'torrent'; torrent_id = $torrentId; view = $view; context = ++$context; include_files = $true } $phase 'immediate'
            while ($pending.Count) { Receive-Detail (Read-Frame $script:pipe) }
            if ($index % 25 -eq 0) {
                $snapshot = (Send-Command @{ command = 'snapshot' }).data
                $live = ($snapshot.torrents | Measure-Object -Property peer_count -Sum).Sum
                Assert ($live -ge 100) 'Peer load fell below 100 during measurement'
                $load.Add([pscustomobject]@{
                    time = [DateTime]::UtcNow; peers = $live; download_rate = $snapshot.download_rate
                    downloaded = ($snapshot.torrents | Measure-Object -Property downloaded -Sum).Sum
                })
            }
        }
    }
    $after = (Send-Command @{ command = 'snapshot' }).data
    $peersAfter = ($after.torrents | Measure-Object -Property peer_count -Sum).Sum
    Assert ($peersAfter -ge 100) 'Peer load fell below 100 during measurement'
    Assert (($after.torrents | Measure-Object -Property downloaded -Sum).Sum -gt
        ($before.torrents | Measure-Object -Property downloaded -Sum).Sum) 'No payload transferred during measurement'
    $summary = foreach ($group in $measurements | Group-Object phase, kind, operation) {
        $values = @($group.Group.milliseconds | Sort-Object)
        [pscustomobject]@{
            path = $group.Name; count = $values.Count
            p95 = $values[[Math]::Ceiling($values.Count * .95) - 1]
            p99 = $values[[Math]::Ceiling($values.Count * .99) - 1]
            maximum = $values[-1]
        }
    }
    @{ before = $before; after = $after; load = $load; samples = $measurements; summary = @($summary) } |
        ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $directory 'measurement.json') -Encoding utf8
    $summary | Format-Table -AutoSize
    Write-Output "Evidence: $directory"
}
finally {
    if ($peer) {
        New-Item -ItemType File -Path (Join-Path $peerDirectory 'stop') -Force | Out-Null
        if (-not $peer.WaitForExit(10000)) { $peer.Kill(); $peer.WaitForExit() }
        $peer.Dispose()
    }
    Stop-Engine
}
