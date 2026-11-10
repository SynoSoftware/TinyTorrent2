param(
    [string] $ArtifactsPath = (Join-Path $PSScriptRoot '../../artifacts'),
    [int] $Samples = 120,
    # Torrent counts whose all-torrents Peers refresh is timed, and how often.
    [int[]] $Scopes = @(10, 100, 120),
    [int] $Rounds = 10
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

function Receive-Observation($frame) {
    Assert ($frame.type -eq 'observation') 'Unexpected unsolicited frame'
    $sample = $pending[[string]$frame.torrent_id]
    if (-not $sample -or $sample.Context -ne $frame.context) { return }
    Assert $frame.ok 'Observation failed'
    $elapsed = $sample.Watch.Elapsed.TotalMilliseconds
    if (-not $sample.Seen) {
        $sample.Seen = $true
        $measurements.Add([pscustomobject]@{ phase = $sample.Phase; kind = 'immediate'; operation = $sample.View; milliseconds = $elapsed })
    }
    # A later partial observation means the answers arrived in more than one alert batch.
    elseif (-not $frame.data.ready) { $sample.Partials++ }
    if (-not $frame.data.ready) { return }
    $measurements.Add([pscustomobject]@{
        phase = $sample.Phase; kind = 'complete'; operation = $sample.View; milliseconds = $elapsed; partials = $sample.Partials
    })
    $pending.Remove([string]$frame.torrent_id)
}

# Subscribes to the torrents' section and waits for every torrent's ready
# observation.
function Measure-Subscription([string[]] $ids, [string] $view, [string] $phase) {
    $context = ++$script:context
    $watch = [Diagnostics.Stopwatch]::new()
    $pending.Clear()
    foreach ($id in $ids) {
        $pending[$id] = @{ Watch = $watch; Context = $context; Phase = $phase; View = $view; Seen = $false; Partials = 0 }
    }
    # Timing starts at the request, as each reply's does.
    $watch.Start()
    $null = Measure-Reply @{ command = 'subscribe'; context = $context; section = $view; torrent_ids = $ids } $phase 'subscribe'
    while ($pending.Count) { Receive-Observation (Read-Frame $script:pipe) }
}

# The window subscribes to an all-torrents Peers scope at once. Each round
# starts cold, as the baseline's reads did, because a release ends the
# subscription's collections.
function Measure-Scope([string[]] $ids) {
    $phase = "scope-$($ids.Count)"
    for ($round = 0; $round -lt $Rounds; ++$round) {
        $released = Send-Command @{ command = 'subscribe'; torrent_ids = @() }
        Assert $released.ok 'Release was refused'
        $watch = [Diagnostics.Stopwatch]::StartNew()
        Measure-Subscription $ids 'peers' $phase
        $measurements.Add([pscustomobject]@{
            phase = $phase; kind = 'refresh'; operation = 'peers'; milliseconds = $watch.Elapsed.TotalMilliseconds
        })
    }
}

# The reply's bytes, the time until they arrive, which includes the engine's
# build and write, and the window's parse with System.Text.Json.
function Measure-Snapshot {
    for ($round = 0; $round -lt $Rounds; ++$round) {
        $id = ++$script:sequence
        $bytes = [Text.Encoding]::UTF8.GetBytes((@{ command = 'snapshot'; request_id = $id } | ConvertTo-Json -Compress))
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $script:pipe.Write([BitConverter]::GetBytes([int]$bytes.Length), 0, 4)
        $script:pipe.Write($bytes, 0, $bytes.Length)
        do {
            $size = [BitConverter]::ToInt32((Read-Bytes $script:pipe 4), 0)
            $body = Read-Bytes $script:pipe $size
            $arrived = $watch.Elapsed.TotalMilliseconds
            $parse = [Diagnostics.Stopwatch]::StartNew()
            $document = [Text.Json.JsonDocument]::Parse([ReadOnlyMemory[byte]]$body, [Text.Json.JsonDocumentOptions]::new())
            $parsed = $parse.Elapsed.TotalMilliseconds
            $request = [Text.Json.JsonElement]::new()
            $answers = $document.RootElement.TryGetProperty('request_id', [ref]$request) -and $request.GetInt64() -eq $id
            $document.Dispose()
        } while (-not $answers)
        $measurements.Add([pscustomobject]@{ phase = 'snapshot'; kind = 'reply'; operation = 'arrived'; milliseconds = $arrived; bytes = $size })
        $measurements.Add([pscustomobject]@{ phase = 'snapshot'; kind = 'reply'; operation = 'parsed'; milliseconds = $parsed; bytes = $size })
    }
}

function Measure-Reply([hashtable] $fields, [string] $phase, [string] $kind) {
    $fields.request_id = ++$script:sequence
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $bytes = [Text.Encoding]::UTF8.GetBytes(($fields | ConvertTo-Json -Depth 20 -Compress))
    $script:pipe.Write([BitConverter]::GetBytes([int]$bytes.Length), 0, 4)
    $script:pipe.Write($bytes, 0, $bytes.Length)
    do {
        $frame = Read-Frame $script:pipe
        if ($frame.type -eq 'observation') { Receive-Observation $frame; continue }
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
    $ids = @($before.torrents.torrent_id)
    foreach ($count in $Scopes) {
        Assert ($ids.Count -ge $count) "Fewer than $count torrents for the scope refresh"
        Measure-Scope $ids[0..($count - 1)]
    }
    # The scope's refresh observations would share the pipe with the snapshots.
    $released = Send-Command @{ command = 'subscribe'; torrent_ids = @() }
    Assert $released.ok 'Release was refused'
    Measure-Snapshot
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
                4 { @{ command = 'edit'; torrent_id = $torrentId; changes = @{ files = @(@{ index = 0; priority = $(if (($index / 6) % 2 -lt 1) { 1 } else { 4 }) }) } } }
                5 { @{ command = 'queue'; torrent_ids = @($torrentId); direction = $(if (($index / 6) % 2 -lt 1) { 'top' } else { 'bottom' }) } }
            }
            $null = Measure-Reply $command $phase 'command'
            $view = $views[$index % $views.Count]
            Measure-Subscription @($torrentId) $view $phase
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
            median = $values[[Math]::Ceiling($values.Count * .5) - 1]
            p95 = $values[[Math]::Ceiling($values.Count * .95) - 1]
            p99 = $values[[Math]::Ceiling($values.Count * .99) - 1]
            maximum = $values[-1]
        }
    }
    $batches = foreach ($group in $measurements | Where-Object { $_.kind -eq 'complete' -and $_.operation -in 'files', 'pieces' } |
        Group-Object operation) {
        [pscustomobject]@{ view = $group.Name; reads = $group.Count; one_batch = @($group.Group | Where-Object partials -EQ 0).Count }
    }
    @{ before = $before; after = $after; load = $load; samples = $measurements; summary = @($summary); batches = @($batches) } |
        ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $directory 'measurement.json') -Encoding utf8
    $summary | Format-Table -AutoSize
    $batches | Format-Table -AutoSize
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
