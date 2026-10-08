param(
    [ValidateSet('1', 'smoke', 'shell', 'schedule', 'desktop', 'details', 'details-files', 'files', 'files-layout',
        'search', 'library', 'traffic', 'edits', 'add-layout', 'settings-layout', 'footer')]
    [string] $Review = '1',
    [ValidateSet('en', 'es')]
    [string] $Language,
    [string] $ArtifactsPath,
    [switch] $Transfer
)

$ErrorActionPreference = 'Stop'
# The traffic review downloads real payload from the Transfer peer, which takes
# minutes of the owner's machine, so it runs only when the owner asks.
if ($Review -eq 'traffic' -and -not $Transfer) {
    throw 'The traffic review runs a real transfer. Run it only when the owner asks, with -Transfer.'
}
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path $repository 'artifacts' }
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
# The engine reports its store with links resolved, and the review refuses a
# store path that differs from that report.
for ($parent = $ArtifactsPath; $parent; $parent = Split-Path $parent -Parent) {
    $target = (Get-Item -LiteralPath $parent).Target
    if ($target) {
        $ArtifactsPath = [IO.Path]::GetFullPath((Join-Path $target ([IO.Path]::GetRelativePath($parent, $ArtifactsPath))))
        break
    }
}
$properties = ([xml](Get-Content (Join-Path $repository 'Directory.Build.props'))).Project.PropertyGroup
$engineName = $properties.EngineTargetName | Where-Object { $_ }
$windowName = $properties.WindowTargetName | Where-Object { $_ }
$output = Join-Path $ArtifactsPath 'bin/TinyTorrent/debug_win-x64_capture'
$executable = Join-Path $output "$engineName.exe"
$window = Join-Path $output "$windowName.exe"
if (-not (Test-Path -LiteralPath $window)) {
    throw "$output has no capture build. Build the app with /p:EnableCapture=true first."
}
if (Get-Process -Name $engineName, $windowName, 'Transfer' -ErrorAction SilentlyContinue) {
    throw 'The capture review refuses to attach while TinyTorrent or the Transfer peer is running.'
}
. (Join-Path $repository 'engine/tests/Engine.ps1')

$run = Join-Path $ArtifactsPath "evidence/Capture-$Review-$([guid]::NewGuid())"
$directory = Join-Path $run 'store'
$payload = Join-Path $run 'payload'
$captures = Join-Path $run 'captures'
$null = New-Item -ItemType Directory -Path $directory, $payload

function Write-Json([string] $path, $value) {
    [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
}

function Write-File([string] $path, [byte[]] $bytes) {
    $null = New-Item -ItemType Directory -Path (Split-Path $path) -Force
    [IO.File]::WriteAllBytes($path, $bytes)
}

# Writes a private single-piece torrent over $files, each a path inside the
# torrent and its bytes, and returns the torrent's path.
function New-Torrent([string] $name, [object[]] $files, [int] $pieceSize = 262144) {
    $content = [IO.MemoryStream]::new()
    foreach ($file in $files) { $content.Write($file.bytes, 0, $file.bytes.Length) }
    Assert ($content.Length -le $pieceSize) "Fixture torrent $name exceeds its single piece"
    $info = if ($files.Count -eq 1) { "d6:lengthi$($content.Length)e" } else {
        'd5:filesl' + (($files | ForEach-Object {
            "d6:lengthi$($_.bytes.Length)e4:pathl" + (($_.path.Split('/') | ForEach-Object { "$($_.Length):$_" }) -join '') + 'ee'
        }) -join '') + 'e'
    }
    $info += "4:name$($name.Length):${name}12:piece lengthi${pieceSize}e6:pieces20:"
    $path = Join-Path $run "torrents/$([guid]::NewGuid()).torrent"
    Write-File $path ([byte[]]([Text.Encoding]::ASCII.GetBytes("d4:info$info") +
        [Security.Cryptography.SHA1]::HashData($content.ToArray()) + [Text.Encoding]::ASCII.GetBytes('7:privatei1eee')))
    return $path
}

function Add-Torrent([string] $source, [string] $destination, [hashtable] $choices = @{ paused = $true }) {
    $preview = Send-Command @{ command = 'preview'; source = $source; destination = $destination }
    Assert $preview.ok "$source did not preview: $($preview.error | ConvertTo-Json -Compress)"
    $reply = Send-Command (@{ command = 'add'; preview_id = $preview.data.preview_id; destination = $destination } + $choices)
    Assert ($reply.ok -and -not $reply.data.duplicate) "$source was refused or merged"
    return $reply.data.torrent_id
}

function Wait-Until([scriptblock] $condition, [string] $failure) {
    $until = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (& $condition)) {
        Assert ([DateTime]::UtcNow -lt $until) $failure
        [Threading.Thread]::Sleep(100)
    }
}

function Test-Idle($torrents) {
    return -not ($torrents | Where-Object { -not $_.paused -or $_.peer_count -or $_.download_rate -or $_.upload_rate })
}

if ($Review -in 'files', 'files-layout', 'library', 'traffic') {
    $document = @{ format = 1; torrents = @(); settings = @{ language = 'en'; theme = 'light'; port_mapping = $false;
        check_for_updates = $false; show_splash = $false; notifications_enabled = $false; prevent_sleep = $false } }
} else {
    $fixture = Join-Path $PSScriptRoot 'Store'
    Copy-Item -Path (Join-Path $fixture '*.resume') -Destination $directory
    $document = Get-Content -LiteralPath (Join-Path $fixture 'settings.json') -Raw | ConvertFrom-Json -AsHashtable
}
$document.settings.default_destination = $payload
$document.settings.all_paused = $Review -notin 'add-layout', 'traffic'
if ($Language) { $document.settings.language = $Language }
foreach ($torrent in $document.torrents) { $torrent.save_path = $payload }
if ($Review -eq 'traffic') {
    $adapter = [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
        Where-Object { $_.NetworkInterfaceType -eq 'Loopback' -and $_.OperationalStatus -eq 'Up' } |
        Select-Object -First 1
    if (-not $adapter) { throw 'The traffic review requires an available Windows loopback adapter.' }
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); $port = $listener.LocalEndpoint.Port }
    finally { $listener.Stop() }
    $document.settings.network_interface = $adapter.Id
    $document.settings.listen_port = $port
    # The limit keeps the download incomplete for the whole review.
    $document.settings.download_limit = 65536
    $document.settings.limit_mode = 'speed'
}
Write-Json (Join-Path $directory 'settings.json') $document

# The torrents the store must hold when the review ends, or $null to skip that
# check, and the files whose bytes the review must leave unchanged.
$members = $null
$preserved = @{}
$peer = $null
$ui = $null
$variables = @{}
foreach ($name in 'TINYTORRENT_CAPTURE_DIRECTORY', 'TINYTORRENT_CAPTURE_REVIEW', 'TINYTORRENT_CAPTURE_STORE') {
    $variables[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    $null = Start-Engine
    switch ($Review) {
        { $_ -in 'files', 'files-layout' } {
            # Three torrents share one payload file through different piece
            # sizes; a fourth names a different file of the same name elsewhere.
            $shared = @(@{ bytes = [Text.Encoding]::ASCII.GetBytes('x' * 2048) })
            $source = Join-Path $payload 'shared.bin'
            $collision = Join-Path $run 'collision'
            $clash = Join-Path $collision 'shared.bin'
            $unrelated = Join-Path $payload 'keep.txt'
            $destination = Join-Path $run 'moved'
            Write-File $source $shared[0].bytes
            Write-File $clash ([Text.Encoding]::ASCII.GetBytes('z' * 2048))
            Write-File $unrelated ([Text.Encoding]::ASCII.GetBytes('keep'))
            $null = New-Item -ItemType Directory -Path $destination
            $torrents = foreach ($size in 16384, 32768, 65536) { Add-Torrent (New-Torrent 'shared.bin' $shared $size) $payload }
            $outside = Add-Torrent (New-Torrent 'shared.bin' $shared 131072) $collision
            foreach ($path in $source, $clash, $unrelated) { $preserved[$path] = Payload-Hash $path }
            Write-Json (Join-Path $directory 'files-capture.json') ([ordered]@{
                source = $payload; collision = $collision; destination = $destination; torrents = @($torrents); outside = $outside
                source_hash = $preserved[$source]; collision_hash = $preserved[$clash]; unrelated_hash = $preserved[$unrelated]
            })
            if ($Review -eq 'files') {
                $preserved.Remove($source)
                $members = @()
            } else {
                $members = @($torrents) + $outside
            }
        }
        'library' {
            $bundle = [ordered]@{
                'Readme.txt' = 2048
                'Maps/Atlantic coastline.dat' = 32768
                'Maps/Harbors and ferry routes - western islands.dat' = 24576
                'Photos/Morning harbor panorama.dat' = 49152
                'Photos/Lighthouse and rocky headland.dat' = 40960
                'Notes/Survey log - June 2026.txt' = 16384
                'Notes/Species index.csv' = 8192
                'License.txt' = 1024
            }
            $series = 'Atlas mapping samples', 'Harbor collection notes', 'Granite field records', 'Cedar image samples',
                'Orion observatory tables', 'Marina coast survey', 'Nimbus climate archive', 'Willow botany samples'
            $members = for ($index = 1; $index -le 300; $index++) {
                $name = switch ($index) {
                    1 { 'Atlas coastal survey - Europe 2026' }
                    300 { 'Atlas coastal survey - northern Atlantic observations, weather records and species index - collection 300.dat' }
                    default { '{0} - volume {1:000}.dat' -f $series[$index % $series.Count], $index }
                }
                $root = if ($index -eq 1) { Join-Path $payload $name } else { $payload }
                $layout = if ($index -eq 1) { $bundle } else { @{ $name = 32768 + ($index % 8) * 16384 } }
                $files = foreach ($path in $layout.Keys) {
                    $full = Join-Path $root $path
                    Assert ($full.Length -lt 240) "Library payload $full exceeds the safe path length"
                    $bytes = [Text.Encoding]::ASCII.GetBytes([string][char](65 + $index % 26) * $layout[$path])
                    Write-File $full $bytes
                    $preserved[$full] = Payload-Hash $full
                    @{ path = $path; bytes = $bytes }
                }
                Add-Torrent (New-Torrent $name @($files)) $payload
            }
            $snapshot = (Send-Command @{ command = 'snapshot' }).data
            Assert ($snapshot.session_paused -and $snapshot.torrents.Count -eq 300 -and (Test-Idle $snapshot.torrents)) 'The library fixture is not 300 idle paused torrents'
            Write-Json (Join-Path $directory 'library-capture.json') ([ordered]@{
                torrents = @($members); target = $members[-1]; bundle = $members[0]; files = $bundle.Count
            })
        }
        'traffic' {
            $seed = Join-Path $run 'peer'
            $null = New-Item -ItemType Directory -Path $seed
            $log = Join-Path $run 'peer.log'
            $peer = Start-Process -FilePath (Join-Path $ArtifactsPath 'bin/Transfer/Debug/Transfer.exe') `
                -ArgumentList @(('"' + $seed + '"'), $port, 'seed-files') -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput $log -RedirectStandardError (Join-Path $run 'peer-error.log')
            Wait-Until { -not $peer.HasExited -and (Get-Content -LiteralPath $log -Raw) -match 'ready' } 'The loopback seed did not become ready'
            $target = Add-Torrent (Join-Path $seed 'transfer.torrent') $payload @{ paused = $false; priorities = @(4, 4) }
            Wait-Until {
                $torrent = (Send-Command @{ command = 'snapshot' }).data.torrents[0]
                $torrent.downloaded -ge 524288 -and $torrent.download_rate -gt 0 -and $torrent.peer_count -gt 0
            } 'The loopback transfer did not become active'
            $torrent = (Send-Command @{ command = 'snapshot' }).data.torrents[0]
            Assert ($torrent.size -eq 17039360 -and -not $torrent.complete) 'The loopback transfer is not the partial two-file fixture'
            $members = @($target)
            foreach ($file in 'skip.bin', 'wanted.bin') {
                $full = Join-Path $seed "seed/selection/$file"
                $preserved[$full] = Payload-Hash $full
            }
            Write-Json (Join-Path $directory 'traffic-capture.json') ([ordered]@{
                target = $target; size = $torrent.size; destination = $payload; interface = $adapter.Id
            })
        }
    }

    $env:TINYTORRENT_CAPTURE_DIRECTORY = $captures
    $env:TINYTORRENT_CAPTURE_REVIEW = $Review
    $env:TINYTORRENT_CAPTURE_STORE = $directory
    $ui = Start-Process -FilePath $window -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddMinutes(10)
    # The desktop journey writes 'restart' to this file to have its engine
    # stopped, then 'disconnected' once it shows that state, to have the engine
    # started again.
    $signal = Join-Path $captures 'restart.txt'
    $restarted = $false
    while (-not $ui.WaitForExit(100)) {
        Assert ([DateTime]::UtcNow -lt $deadline) "The $Review review did not close within ten minutes"
        Assert (-not ($script:process -and $script:process.HasExited)) 'The review engine exited during the journey'
        Assert (-not ($peer -and $peer.HasExited)) 'The loopback seed exited during the journey'
        $state = if (Test-Path -LiteralPath $signal) { Get-Content -LiteralPath $signal -Raw }
        if ($state -eq 'restart' -and -not $restarted) {
            $snapshot = Send-Command @{ command = 'snapshot' }
            Assert ($snapshot.ok -and -not $snapshot.data.stopping) 'Keep input did not leave the engine reachable and running'
            $script:pipe.Dispose()
            $script:pipe = $null
            Stop-Process -InputObject $script:process
            Assert ($script:process.WaitForExit(15000)) 'The review engine did not stop'
            $script:process.Dispose()
            $script:process = $null
            $restarted = $true
        }
        if ($state -eq 'disconnected' -and $restarted -and -not $script:process) {
            $null = Start-Engine
        }
    }

    $report = Get-Content -LiteralPath (Join-Path $captures 'review.json') -Raw | ConvertFrom-Json
    $report | ConvertTo-Json -Depth 10
    if ($report.failure) { throw $report.failure }
    Assert ($ui.ExitCode -eq 0) "The review window exited with $($ui.ExitCode)"
    if ($Review -eq 'desktop') {
        Assert ($restarted -and $script:process) 'The desktop review did not finish its engine restart'
        $before = @((Send-Command @{ command = 'history'; range = 'five_minutes' }).data.samples)[-1].time
        Start-Sleep -Seconds 2
        Assert (-not (Get-Process -Name $windowName -ErrorAction SilentlyContinue)) 'A window is open, so background sampling is unproven'
        $after = @((Send-Command @{ command = 'history'; range = 'five_minutes' }).data.samples)[-1].time
        Assert ($null -ne $before -and $after -gt $before) 'History did not advance while no window existed'
    }
    if ($Review -eq 'files') {
        Assert (-not (Test-Path -LiteralPath $source) -and -not (Test-Path -LiteralPath (Join-Path $destination 'shared.bin'))) 'The files journey left intended payload behind'
    }
    foreach ($path in $preserved.Keys) {
        Assert ((Test-Path -LiteralPath $path) -and (Payload-Hash $path) -eq $preserved[$path]) "The $Review review changed $path"
    }
    if ($null -ne $members) {
        $snapshot = (Send-Command @{ command = 'snapshot' }).data
        Assert ((@($snapshot.torrents.torrent_id | Sort-Object) -join ',') -eq (@($members | Sort-Object) -join ',')) "The $Review review changed torrent membership"
        if ($Review -eq 'library') { Assert (Test-Idle $snapshot.torrents) 'The library review activated a torrent' }
        Stop-Engine
        $saved = Get-Content -LiteralPath (Join-Path $directory 'settings.json') -Raw | ConvertFrom-Json
        Assert (@($saved.torrents).Count -eq @($members).Count) 'Final storage changed torrent membership'
    }
} finally {
    foreach ($name in $variables.Keys) { [Environment]::SetEnvironmentVariable($name, $variables[$name], 'Process') }
    if ($ui -and -not $ui.HasExited) {
        $null = $ui.CloseMainWindow()
        if (-not $ui.WaitForExit(10000)) { Stop-Process -InputObject $ui }
    }
    try { Stop-Engine }
    finally {
        if ($script:process -and -not $script:process.HasExited) { Stop-Process -InputObject $script:process }
        if ($peer -and -not $peer.HasExited) {
            $null = New-Item -ItemType File -Path (Join-Path $seed 'stop') -Force
            if (-not $peer.WaitForExit(10000)) { Stop-Process -InputObject $peer }
        }
        Write-Output "Evidence: $run"
    }
}
