param(
    [Parameter(Mandatory)]
    [ValidateSet('Frames', 'Summaries', 'Targets', 'Facts', 'Restore', 'Exit', 'Conflict', 'FailedCommit', 'CheckpointRetry', 'Restart', 'DiskError', 'PreviewGuard', 'RemoveKeepFiles', 'QueueOrder', 'SelectedTransfer', 'MagnetDownload', 'SettingsPolicy', 'CommittedFiles', 'FilesSafety', 'FileNames', 'Pause', 'History', 'Accounting', 'WaitingMove', 'RemoveDuringMove', 'MoveAndVerify', 'DriveTurns', 'UnfinishedTasks')]
    [string] $Check,
    # Every check but Conflict, WaitingMove, RemoveDuringMove, MoveAndVerify,
    # DriveTurns and UnfinishedTasks, which make their own torrents.
    [string] $TorrentFile,
    [string] $EnginePath,
    [switch] $Transfer,
    [switch] $OptIn,
    # RemoveDuringMove moves its payload into this folder, on another volume.
    # MoveAndVerify also moves its payload there when it is given, to check a
    # copy between volumes. DriveTurns and UnfinishedTasks need it on the
    # drive that holds the evidence folder, as C: holds V:.
    [string] $Folder,
    # MoveAndVerify moves a small payload into this folder, on a network share,
    # when it is given. DriveTurns and UnfinishedTasks need it.
    [string] $Network,
    # DriveTurns moves between these two folders, on drives that -Folder and
    # -Network do not use, such as two USB drives. When either is missing, it
    # skips the cases that need them and says so.
    [string[]] $OtherFolders = @('D:\', 'E:\')
)

$ErrorActionPreference = 'Stop'
# These checks start the Transfer peer and download real payload, which takes
# minutes of the owner's machine, so they run only when the owner asks.
$transferChecks = 'MagnetDownload', 'SelectedTransfer', 'DiskError', 'PreviewGuard', 'RemoveKeepFiles'
# These checks each guard one defect that was already fixed, so a run of every
# check skips them; the owner asks for them when that code changes.
$optInChecks = 'WaitingMove', 'RemoveDuringMove'
if (-not $TorrentFile -and $Check -notin 'Conflict', 'WaitingMove', 'RemoveDuringMove', 'MoveAndVerify', 'DriveTurns', 'UnfinishedTasks') {
    throw "$Check needs -TorrentFile."
}
if ($Check -in $transferChecks -and -not $Transfer) {
    throw "$Check runs a real transfer. Run it only when the owner asks, with -Transfer."
}
if ($Check -in $optInChecks -and -not $OptIn) {
    throw "$Check runs only when the owner asks, with -OptIn."
}
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
# The drive that holds a path, through any junction on the way, because the
# repository's artifacts folder can be a junction to another drive.
function Path-Volume([string] $path) {
    $path = [IO.Path]::GetFullPath($path)
    for ($part = $path; $part; $part = Split-Path $part -Parent) {
        $item = Get-Item -LiteralPath $part -Force -ErrorAction SilentlyContinue
        if ($item -and $item.LinkTarget) {
            return Path-Volume ($item.ResolveLinkTarget($true).FullName + $path.Substring($part.Length))
        }
    }
    return [IO.Path]::GetPathRoot($path)
}
# A move on one volume is a rename that ends at once, so only a copy to another
# volume leaves time to remove the torrent while its move runs. The payload
# starts under artifacts.
if ($Check -eq 'RemoveDuringMove') {
    $payloadVolume = Path-Volume (Join-Path $repository 'artifacts')
    if (-not ($Folder -and (Test-Path -LiteralPath $Folder -PathType Container) -and (Path-Volume $Folder) -ne $payloadVolume)) {
        throw "RemoveDuringMove needs -Folder, an existing folder on another volume than $payloadVolume, which holds the check's payload."
    }
}
if ($Check -in 'DriveTurns', 'UnfinishedTasks' -and -not ($Folder -and $Network)) {
    throw "$Check needs -Folder, on the drive that holds the evidence folder, and -Network, on a network share."
}
$engineName = ([xml](Get-Content (Join-Path $repository 'Directory.Build.props'))).Project.PropertyGroup.EngineTargetName | Where-Object { $_ }
$configuration = if ($Check -eq 'Pause') { 'Debug' } else { 'Release' }
$executable = Join-Path $repository "artifacts/bin/Engine/$configuration/$engineName.exe"
if ($EnginePath) { $executable = [IO.Path]::GetFullPath($EnginePath) }
$peerExecutable = [IO.Path]::GetFullPath((Join-Path (Split-Path $executable -Parent) '../../Transfer/Release/Transfer.exe'))
$directory = Join-Path $repository ('artifacts/evidence/' + $Check + '-' + [guid]::NewGuid())
$null = New-Item -ItemType Directory -Path $directory
$payload = Join-Path $directory 'payload'
$null = New-Item -ItemType Directory -Path $payload
$volumeFolder = $null
if ($Check -eq 'RemoveDuringMove' -or ($Check -in 'MoveAndVerify', 'DriveTurns', 'UnfinishedTasks' -and $Folder)) {
    $volumeFolder = Join-Path ([IO.Path]::GetFullPath($Folder)) ('TinyTorrent-' + $Check + '-' + [guid]::NewGuid())
    $null = New-Item -ItemType Directory -Path $volumeFolder
}
$networkFolder = $null
if ($Check -in 'MoveAndVerify', 'DriveTurns', 'UnfinishedTasks' -and $Network) {
    $networkFolder = Join-Path $Network ('TinyTorrent-' + $Check + '-' + [guid]::NewGuid())
    $null = New-Item -ItemType Directory -Path $networkFolder
}
$others = @()
if ($Check -eq 'DriveTurns') {
    if ($OtherFolders.Count -eq 2 -and -not ($OtherFolders | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Container) })) {
        foreach ($other in $OtherFolders) {
            $others += Join-Path $other ('TinyTorrent-' + $Check + '-' + [guid]::NewGuid())
            $null = New-Item -ItemType Directory -Path $others[-1]
        }
    }
    else {
        Write-Warning "DriveTurns skips the moves between $($OtherFolders -join ' and '), because those folders are missing."
    }
}
. (Join-Path $PSScriptRoot 'Engine.ps1')
$stalled = $null
$heldFile = $null
$peer = $null
$peerDirectory = Join-Path $directory 'peer'
$standIn = $null

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

function Assert-Paused([string] $torrentId) {
    # The stale libtorrent tick list asserts on the next one-second tick.
    $until = [DateTime]::UtcNow.AddSeconds(3)
    do {
        $snapshot = (Send-Command @{ command = 'snapshot' }).data
        Assert ($snapshot.session_paused -and $snapshot.torrents.Count -eq 1 -and
            $snapshot.torrents[0].torrent_id -eq $torrentId -and $snapshot.torrents[0].paused) `
            'Pause all lost the accepted magnet or its individual paused intent'
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
}

# A torrent of zeros: its sparse file takes no disk space and every piece of it
# hashes alike, so a large one checks for seconds. A sparse file's holes copy at
# once, so a move that must take time uses a dense file. The file goes into
# `$folder`, which is the incomplete folder when the torrent has one.
# Adds the torrent of one file, `name.bin`, with 4 MiB pieces whose SHA-1
# hashes are `pieces`, and returns its identity.
function Add-Fixture([string] $name, [long] $length, [byte[]] $pieces, [string] $destination) {
    $fixture = Join-Path $directory "$name.torrent"
    $prefix = [Text.Encoding]::ASCII.GetBytes("d4:infod6:lengthi$($length)e4:name$($name.Length + 4):$name.bin12:piece lengthi$(4MB)e6:pieces$($pieces.Length):")
    $suffix = [Text.Encoding]::ASCII.GetBytes('7:privatei1eee')
    [IO.File]::WriteAllBytes($fixture, [byte[]]($prefix + $pieces + $suffix))
    $preview = Send-Command @{ command = 'preview'; source = $fixture; destination = $destination }
    Assert $preview.ok "The $name fixture did not preview"
    $reply = Send-Command @{ command = 'add'; preview_id = $preview.data.preview_id; destination = $destination }
    Assert $reply.ok "The $name fixture could not be added"
    return $reply.data.torrent_id
}

function New-Zeros([string] $name, [long] $length, [string] $destination = $payload, [switch] $Dense, [string] $folder = $destination) {
    $digest = [Security.Cryptography.SHA1]::HashData([byte[]]::new(4MB))
    $pieces = [byte[]]::new(20 * [int]($length / 4MB))
    for ($offset = 0; $offset -lt $pieces.Length; $offset += 20) { [Array]::Copy($digest, 0, $pieces, $offset, 20) }
    $file = Join-Path $folder "$name.bin"
    $null = New-Item -ItemType File -Path $file
    if (-not $Dense) {
        $null = & fsutil sparse setflag $file
        Assert ($LASTEXITCODE -eq 0) "The $name payload could not be made sparse"
    }
    $stream = [IO.File]::Open($file, 'Open', 'ReadWrite')
    try { $stream.SetLength($length) } finally { $stream.Dispose() }
    return Add-Fixture $name $length $pieces $destination
}

# A payload whose every byte depends on its offset, so a copy that writes a
# block at the wrong place changes the file's hash: random bytes, with each
# 4 MiB piece starting with its own number.
function New-Pattern([string] $name, [long] $length, [string] $destination) {
    $block = [byte[]]::new(4MB)
    [Security.Cryptography.RandomNumberGenerator]::Fill($block)
    $count = [int]($length / 4MB)
    $pieces = [byte[]]::new(20 * $count)
    $stream = [IO.File]::Open((Join-Path $destination "$name.bin"), 'CreateNew', 'Write')
    try {
        for ($index = 0; $index -lt $count; $index++) {
            [BitConverter]::GetBytes([long]$index).CopyTo($block, 0)
            $stream.Write($block, 0, $block.Length)
            [Array]::Copy([Security.Cryptography.SHA1]::HashData($block), 0, $pieces, 20 * $index, 20)
        }
    }
    finally { $stream.Dispose() }
    return Add-Fixture $name $length $pieces $destination
}

try {
    $initial = Start-Engine
    switch ($Check) {
        'Accounting' {
            Assert ($initial.transfers.session.downloaded -eq 0 -and $initial.transfers.all_time.uploaded -eq 0) 'A new store invented transfer totals'
            $reply = Send-Command @{ command = 'settings'; changes = @{ theme = 'light' } }
            Assert $reply.ok 'The accounting fixture could not save its settings'
            Stop-Engine
            $file = Join-Path $directory 'settings.json'
            $document = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
            $document.transfers = @{ downloaded = 12345678901L; uploaded = 23456789012L }
            [IO.File]::WriteAllBytes($file, [Text.Encoding]::UTF8.GetBytes(($document | ConvertTo-Json -Depth 20)))
            foreach ($theme in 'dark', 'light') {
                $snapshot = Start-Engine
                Assert ($snapshot.transfers.session.downloaded -eq 0 -and $snapshot.transfers.session.uploaded -eq 0 -and
                    $snapshot.transfers.all_time.downloaded -eq 12345678901L -and $snapshot.transfers.all_time.uploaded -eq 23456789012L) `
                    'Restart lost or double-counted lifetime transfers'
                $reply = Send-Command @{ command = 'settings'; changes = @{ theme = $theme } }
                Assert $reply.ok 'An ordinary settings save failed with existing transfer totals'
                Stop-Engine
            }
            $document = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
            Assert ($document.transfers.downloaded -eq 12345678901L -and $document.transfers.uploaded -eq 23456789012L) `
                'A settings save or final checkpoint overwrote lifetime transfers'
            $null = $document.Remove('transfers')
            [IO.File]::WriteAllBytes($file, [Text.Encoding]::UTF8.GetBytes(($document | ConvertTo-Json -Depth 20)))
            $snapshot = Start-Engine
            Assert ($snapshot.transfers.all_time.downloaded -eq 0 -and $snapshot.transfers.all_time.uploaded -eq 0 -and
                $snapshot.settings.theme -eq 'light') 'Missing transfer totals lost the existing settings'
        }
        'Conflict' {
            # Two launches that add torrents to another data folder at once
            # each ask to exit the engine that runs. Once it ends, one launch
            # owns the folder and the other forwards to it, so neither torrent
            # is lost. The store cannot show a second delivery, because the
            # engine adds each info-hash once.
            $reply = Send-Command @{ command = 'settings'; changes = @{ torrent_dialog = $false; start_paused = $true; confirm_exit = $false } }
            Assert $reply.ok 'The launch folder could not save its settings'
            Stop-Engine
            $store = $directory
            $directory = Join-Path $store 'other'
            $null = New-Item -ItemType Directory -Path $directory
            $null = Start-Engine
            $directory = $store
            $sources = foreach ($name in 'first', 'second') {
                $source = Join-Path $store "$name.torrent"
                $content = [Text.Encoding]::ASCII.GetBytes($name * 512)
                $prefix = [Text.Encoding]::ASCII.GetBytes("d4:infod6:lengthi$($content.Length)e4:name$($name.Length + 4):$name.bin12:piece lengthi16384e6:pieces20:")
                $suffix = [Text.Encoding]::ASCII.GetBytes('7:privatei1eee')
                [IO.File]::WriteAllBytes($source, [byte[]]($prefix + [Security.Cryptography.SHA1]::HashData($content) + $suffix))
                $source
            }
            $launches = @(foreach ($source in $sources) {
                Start-Process -FilePath $executable -ArgumentList @('--background', '--data', ('"' + $store + '"'), ('"' + $source + '"')) -PassThru
            })
            $continuing = $null
            try {
                # The prompt is the launch's only visible unowned window, so .NET
                # reports it as the main window.
                $until = [DateTime]::UtcNow.AddSeconds(30)
                while (@($launches | Where-Object { $_.Refresh(); $_.MainWindowHandle -ne 0 }).Count -lt 2) {
                    Assert (@($launches | Where-Object HasExited).Count -eq 0) 'A launch ended before it asked to exit the other engine'
                    Assert ([DateTime]::UtcNow -lt $until) 'Both launches did not ask to exit the other engine'
                    Start-Sleep -Milliseconds 100
                }
                Stop-Engine
                $until = [DateTime]::UtcNow.AddSeconds(30)
                while (@($launches | Where-Object HasExited).Count -eq 0) {
                    Assert ([DateTime]::UtcNow -lt $until) 'Neither launch continued after the other engine ended'
                    Start-Sleep -Milliseconds 100
                }
                $forwarded = @($launches | Where-Object HasExited)
                Assert ($forwarded.Count -eq 1 -and $forwarded[0].ExitCode -eq 0) 'The second launch did not hand its torrent to the first'
                $continuing = $launches | Where-Object { -not $_.HasExited }
                $script:process = $continuing
                $script:pipe = Connect-Pipe
                $until = [DateTime]::UtcNow.AddSeconds(15)
                do {
                    $snapshot = (Send-Command @{ command = 'snapshot' }).data
                    if ($snapshot.torrents.Count -ge 2) { break }
                    Start-Sleep -Milliseconds 100
                } while ([DateTime]::UtcNow -lt $until)
                $names = @($snapshot.torrents | ForEach-Object name | Sort-Object)
                Assert ($names.Count -eq 2 -and $names[0] -eq 'first.bin' -and $names[1] -eq 'second.bin') `
                    "Simultaneous launches lost or duplicated a torrent: $($names -join ', ')"
                Stop-Engine
            }
            finally {
                foreach ($launch in $launches) {
                    if ($launch -ne $continuing -and -not $launch.HasExited) { $launch.Kill(); $launch.WaitForExit() }
                }
            }
        }
        'Exit' {
            function Exit-Code {
                $exiting = Start-Process -FilePath $executable -ArgumentList '--headless', '--exit' -WindowStyle Hidden -PassThru
                try {
                    Assert ($exiting.WaitForExit(35000)) 'The Exit gate did not finish'
                    return $exiting.ExitCode
                }
                finally {
                    if (-not $exiting.HasExited) { $exiting.Kill(); $exiting.WaitForExit() }
                    $exiting.Dispose()
                }
            }
            Stop-Engine
            Assert ((Exit-Code) -eq 0) 'Exit refused an absent engine and window'
            $window = [Threading.Mutex]::new($false, "Local\TinyTorrent.Window.$logon")
            $owned = $false
            try {
                $owned = $window.WaitOne(0)
                Assert $owned 'The window instance is already owned'
                Assert ((Exit-Code) -eq 1) 'Exit reported success while a window survived the engine'
                $null = Start-Engine
                Assert ((Exit-Code) -eq 1) 'Forwarded Exit ignored a surviving window'
                Assert ($script:process.WaitForExit(15000)) 'Forwarded Exit did not shut down the disposable engine'
                $script:pipe.Dispose()
                $script:pipe = $null
                $script:process.Dispose()
                $script:process = $null
                $window.ReleaseMutex()
                $owned = $false
                Assert ((Exit-Code) -eq 0) 'Exit refused a released window instance'
            }
            finally {
                if ($owned) { $window.ReleaseMutex() }
                $window.Dispose()
            }
        }
        'Restore' {
            $reply = Send-Command @{ command = 'add'; preview_id = (Preview); destination = $payload; paused = $true }
            Assert $reply.ok 'Fixture addition failed'
            $torrentId = $reply.data.torrent_id
            $files = (Send-Command @{ command = 'torrent'; torrent_id = $torrentId; view = 'files' }).data.files
            Stop-Engine
            $checkpoint = [IO.File]::ReadAllBytes((Join-Path $directory ($torrentId + '.resume')))
            $file = Join-Path $directory 'settings.json'
            $document = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
            $document.settings.all_paused = $true
            $document.settings.network_interface = '{00000000-0000-0000-0000-000000000000}'
            $first = $document.torrents[0]
            $first.hashes = @($first.hashes[0], ('1' * 64))
            $second = $first | ConvertTo-Json -Depth 20 | ConvertFrom-Json
            $second.torrent_id = 'zz-conflict'
            $second.download_limit = 1024
            $unrelated = $first | ConvertTo-Json -Depth 20 | ConvertFrom-Json
            $unrelated.torrent_id = 'unrelated'
            $unrelated.hashes = @(('2' * 40))
            $document.torrents = @($first, $second, $unrelated)
            foreach ($removed in 'zz-conflict', $torrentId) {
                [IO.File]::WriteAllBytes($file, [Text.Encoding]::UTF8.GetBytes(($document | ConvertTo-Json -Depth 20)))
                foreach ($id in $torrentId, 'zz-conflict') {
                    [IO.File]::WriteAllBytes((Join-Path $directory ($id + '.resume')), [byte[]](0))
                }
                $snapshot = Start-Engine
                Assert ($snapshot.torrents.Count -eq 3) 'Restore discarded accepted membership'
                $conflicts = @($snapshot.torrents | Where-Object { $_.error -eq 'alias_conflict' })
                Assert ($conflicts.Count -eq 2) 'Restore silently shared a live handle between identities'
                $reply = Send-Command @{ command = 'speed_limit'; torrent_ids = @('zz-conflict'); download_limit = 2048 }
                Assert $reply.ok 'The conflicting identity lost its own saved choices'
                $rows = (Send-Command @{ command = 'snapshot' }).data.torrents
                Assert (($rows | Where-Object torrent_id -eq $torrentId).download_limit -eq 0) 'An edit changed the other identity'
                $reply = Send-Command @{ command = 'remove'; torrent_ids = @($removed) }
                Assert $reply.ok 'A conflicting identity could not be removed'
                $survivor = if ($removed -eq $torrentId) { 'zz-conflict' } else { $torrentId }
                $reply = Send-Command @{ command = 'resume'; torrent_ids = @($survivor) }
                Assert $reply.ok 'The chosen survivor did not obtain its own live handle'
                Stop-Engine
                $snapshot = Start-Engine
                Assert ($snapshot.torrents.Count -eq 2 -and $removed -notin $snapshot.torrents.torrent_id -and
                    $survivor -in $snapshot.torrents.torrent_id) 'Restart lost the chosen survivor or restored removed membership'
                Stop-Engine
            }
            [IO.File]::WriteAllBytes($file, [Text.Encoding]::UTF8.GetBytes(($document | ConvertTo-Json -Depth 20)))
            foreach ($id in $torrentId, 'zz-conflict') {
                [IO.File]::WriteAllBytes((Join-Path $directory ($id + '.resume')), $checkpoint)
            }
            $shared = Join-Path $payload $files[0].disk_path
            $null = New-Item -ItemType Directory -Path (Split-Path $shared) -Force
            [IO.File]::WriteAllBytes($shared, [byte[]](11, 22, 33))
            $originalHash = Payload-Hash $shared
            $snapshot = Start-Engine
            Assert (@($snapshot.torrents | Where-Object error -eq 'alias_conflict').Count -eq 2) 'Readable checkpoints lost the conflict'
            $reply = Send-Command @{ command = 'file_scope'; torrent_ids = @('zz-conflict') }
            Assert ($reply.ok -and $reply.data.kept_files -gt 0) 'The pending identity lost its shared file ownership'
            $reply = Send-Command @{ command = 'delete_files'; torrent_ids = @('zz-conflict') }
            Assert ($reply.ok -and $reply.data.kept_files -gt 0) 'Deleting the pending identity did not preserve shared files'
            Stop-Engine
            Assert ((Payload-Hash $shared) -eq $originalHash) 'Deleting the pending identity changed the survivor payload'
            $snapshot = Start-Engine
            Assert ($snapshot.torrents.Count -eq 2 -and 'zz-conflict' -notin $snapshot.torrents.torrent_id) 'Deleted conflicting membership returned after restart'
        }
        'Targets' {
            $reply = Send-Command @{ command = 'add'; preview_id = (Preview); destination = $payload; paused = $true }
            Assert $reply.ok 'Fixture addition failed'
            $torrentId = $reply.data.torrent_id
            foreach ($command in 'pause', 'remove', 'delete_files') {
                $reply = Send-Command @{ command = $command; torrent_ids = @($torrentId, $torrentId) }
                Assert (-not $reply.ok -and $reply.error.code -eq 'invalid_torrents') 'Repeated torrents were accepted'
                $rows = (Send-Command @{ command = 'snapshot' }).data.torrents
                Assert ($rows.Count -eq 1 -and $rows[0].torrent_id -eq $torrentId) 'Refused torrents changed membership'
            }
            Stop-Engine
            $snapshot = Start-Engine
            Assert ($snapshot.torrents.Count -eq 1 -and $snapshot.torrents[0].torrent_id -eq $torrentId) 'Refused torrents changed saved membership'
        }
        'Facts' {
            $reply = Send-Command @{ command = 'add'; preview_id = (Preview); destination = $payload; paused = $true }
            Assert $reply.ok 'Fixture addition failed'
            $torrentId = $reply.data.torrent_id
            $heldFile = [IO.File]::Open((Join-Path $directory 'settings.json'), [IO.FileMode]::Open,
                [IO.FileAccess]::Read, [IO.FileShare]::Read)
            foreach ($request in @(
                @{ command = 'speed_limit'; torrent_ids = @($torrentId); download_limit = 0 },
                @{ command = 'piece_order'; torrent_ids = @($torrentId); first_last = $false })) {
                $reply = Send-Command $request
                Assert $reply.ok 'An already-satisfied choice attempted a storage write'
            }
            $reply = Send-Command @{ command = 'speed_limit'; torrent_ids = @($torrentId); download_limit = 1024 }
            Assert (-not $reply.ok -and $reply.error.code -eq 'storage_failed') 'A changed limit bypassed persistence'
            $row = (Send-Command @{ command = 'snapshot' }).data.torrents[0]
            Assert ($row.download_limit -eq 0) 'A refused limit changed live facts'
            $heldFile.Dispose()
            $heldFile = $null
            $reply = Send-Command @{ command = 'speed_limit'; torrent_ids = @($torrentId); download_limit = 1024 }
            Assert $reply.ok 'The limit did not save after storage recovered'
            Stop-Engine
            $snapshot = Start-Engine
            Assert ($snapshot.torrents[0].download_limit -eq 1024) 'The committed limit did not survive restart'
        }
        'History' {
            $reply = Send-Command @{ command = 'history'; torrent_id = 'missing'; range = 'day' }
            Assert (-not $reply.ok -and $reply.error.code -eq 'torrent_removed') 'An unknown torrent received global speed history'
            $null = Send-Command @{ command = 'session_pause'; paused = $true }
            function Add-HistoryTorrent([string] $hash) {
                $preview = Send-Command @{ command = 'preview'; source = ('magnet:?xt=urn:btih:' + $hash); destination = $payload }
                Assert $preview.ok 'The history fixture did not preview'
                $added = Send-Command @{ command = 'add'; preview_id = $preview.data.preview_id; destination = $payload; paused = $true }
                Assert $added.ok 'The history fixture was not added'
                return $added.data.torrent_id
            }
            function Read-History([string] $torrentId, [string] $range) {
                $reply = Send-Command @{ command = 'history'; torrent_id = $torrentId; range = $range }
                Assert ($reply.ok -and $reply.data.torrent_id -eq $torrentId) 'Speed history belongs to another torrent'
                return $reply.data
            }
            $first = Add-HistoryTorrent '0123456789012345678901234567890123456789'
            $until = [DateTime]::UtcNow.AddSeconds(10)
            do {
                $older = Read-History $first 'five_minutes'
                if ($older.samples.Count -ge 2) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $until)
            Assert ($older.samples.Count -ge 2) 'Torrent speed sampling did not start'
            $second = Add-HistoryTorrent '1123456789012345678901234567890123456789'
            $until = [DateTime]::UtcNow.AddSeconds(10)
            do {
                $newer = Read-History $second 'five_minutes'
                if ($newer.samples.Count) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $until)
            Assert ($newer.samples.Count -gt 0 -and $newer.samples[0].time -gt $older.samples[0].time) 'A new torrent inherited another torrent''s speed history'
            $null = Read-History $first 'day'
            $null = Read-History $second 'day'
            $reply = Send-Command @{ command = 'history'; range = 'five_minutes' }
            Assert ($reply.ok -and $reply.data.samples.Count -gt 0) 'Session speed history stopped recording'
            $reply = Send-Command @{ command = 'history'; torrent_ids = @($first, $second, $first); range = 'day' }
            Assert ($reply.ok -and $reply.data.torrent_ids.Count -eq 2 -and
                $reply.data.torrent_ids -contains $first -and $reply.data.torrent_ids -contains $second -and
                $reply.data.samples.Count -gt 0 -and
                @($reply.data.samples | Where-Object { $_.download_rate -ne 0 -or $_.upload_rate -ne 0 }).Count -eq 0) `
                'Selected paused torrents did not return their combined history'
            $reply = Send-Command @{ command = 'history'; torrent_ids = @($first, 'missing'); range = 'day' }
            Assert (-not $reply.ok -and $reply.error.code -eq 'torrent_removed') 'An unknown group member received partial or global history'
            $reply = Send-Command @{ command = 'history'; torrent_ids = @(); range = 'day' }
            Assert (-not $reply.ok -and $reply.error.code -eq 'invalid_request') 'An empty group received global history'
            $reply = Send-Command @{ command = 'history'; torrent_id = $first; torrent_ids = @($second); range = 'day' }
            Assert (-not $reply.ok -and $reply.error.code -eq 'invalid_request') 'Conflicting history selectors were accepted'
            $reply = Send-Command @{ command = 'remove'; torrent_ids = @($first) }
            Assert $reply.ok 'The history fixture could not be removed'
            $reply = Send-Command @{ command = 'history'; torrent_id = $first; range = 'five_minutes' }
            Assert (-not $reply.ok -and $reply.error.code -eq 'torrent_removed') 'A removed torrent received global speed history'
        }
        'Pause' {
            $reply = Send-Command @{ command = 'session_pause'; paused = $true }
            Assert $reply.ok 'Pause all was refused'
            $preview = Send-Command @{ command = 'preview'; source = 'magnet:?xt=urn:btih:0123456789012345678901234567890123456789'; destination = $payload }
            Assert $preview.ok 'The paused-session magnet did not preview'
            $reply = Send-Command @{ command = 'add'; preview_id = $preview.data.preview_id; destination = $payload; paused = $true }
            Assert $reply.ok 'The paused-session magnet could not be added'
            $torrentId = $reply.data.torrent_id
            Assert-Paused $torrentId

            $reply = Send-Command @{ command = 'resume'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Individual Resume was refused while all paused'
            $snapshot = (Send-Command @{ command = 'snapshot' }).data
            Assert ($snapshot.session_paused -and -not $snapshot.torrents[0].paused) 'Individual Resume changed Pause all or lost its intent'
            $reply = Send-Command @{ command = 'pause'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Individual Pause was refused while all paused'
            Assert-Paused $torrentId

            Stop-Engine
            $snapshot = Start-Engine
            Assert ($snapshot.session_paused -and $snapshot.torrents.Count -eq 1 -and
                $snapshot.torrents[0].torrent_id -eq $torrentId -and $snapshot.torrents[0].paused) `
                'Restart lost the magnet or either paused intent'
            $reply = Send-Command @{ command = 'session_pause'; paused = $false }
            Assert $reply.ok 'Resume all was refused'
            $snapshot = (Send-Command @{ command = 'snapshot' }).data
            Assert (-not $snapshot.session_paused -and $snapshot.torrents[0].paused) 'Resume all changed individual paused intent'
            Assert (@(Get-ChildItem -LiteralPath $payload -Recurse -File).Count -eq 0) 'The paused magnet created payload'
        }
        'FileNames' {
            $content = [Text.Encoding]::ASCII.GetBytes(('x' * 2048))
            $digest = [Security.Cryptography.SHA1]::HashData($content)
            $identities = @()
            foreach ($pieceLength in 16384, 32768) {
                $fixture = Join-Path $directory ("shared-$pieceLength.torrent")
                $prefix = [Text.Encoding]::ASCII.GetBytes("d4:infod6:lengthi2048e4:name10:shared.bin12:piece lengthi${pieceLength}e6:pieces20:")
                $suffix = [Text.Encoding]::ASCII.GetBytes('7:privatei1eee')
                [IO.File]::WriteAllBytes($fixture, [byte[]]($prefix + $digest + $suffix))
                $preview = Send-Command @{ command = 'preview'; source = $fixture; destination = $payload }
                Assert $preview.ok 'The unfinished-file fixture did not preview'
                $reply = Send-Command @{ command = 'add'; preview_id = $preview.data.preview_id; destination = $payload }
                Assert ($reply.ok -and -not $reply.data.duplicate) 'Distinct unfinished-file owners were merged or refused'
                $identities += $reply.data.torrent_id
                $files = (Send-Command @{ command = 'torrent'; torrent_id = $reply.data.torrent_id; view = 'files' }).data.files
                Assert ($files[0].path -eq 'shared.bin' -and $files[0].disk_path -eq 'shared.bin.!tt') 'New payload did not keep its logical name and unfinished disk name'
            }
            Stop-Engine
            $unfinished = Join-Path $payload 'shared.bin.!tt'
            $finished = Join-Path $payload 'shared.bin'
            [IO.File]::WriteAllBytes($unfinished, $content)
            $originalHash = Payload-Hash $unfinished
            $heldFile = [IO.File]::Open($unfinished, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            $log = Join-Path $directory 'engine.log'
            foreach ($restart in 1, 2) {
                $previous = if (Test-Path -LiteralPath $log) { @(Select-String -LiteralPath $log -Pattern ' rename ').Count } else { 0 }
                $snapshot = Start-Engine
                Assert ($snapshot.torrents.Count -eq 2) 'Restart lost an unfinished-file owner'
                if ($restart -eq 1) {
                    # Restored names are still being prepared, so Verify waits
                    # for them instead of asking the person to try again.
                    $reply = Send-Command @{ command = 'verify'; torrent_ids = $identities }
                    Assert $reply.ok 'Verification was refused while restored filenames were prepared'
                }
                $until = [DateTime]::UtcNow.AddSeconds(15)
                do {
                    $attempts = if (Test-Path -LiteralPath $log) { @(Select-String -LiteralPath $log -Pattern ' rename ').Count } else { 0 }
                    if ($attempts -gt $previous) { break }
                    Start-Sleep -Milliseconds 100
                } while ([DateTime]::UtcNow -lt $until)
                if ($attempts -le $previous) {
                    $failure = @{
                        snapshot = (Send-Command @{ command = 'snapshot' }).data
                        files = @($identities | ForEach-Object { (Send-Command @{ command = 'torrent'; torrent_id = $_; view = 'files' }).data })
                    } | ConvertTo-Json -Depth 20
                    [IO.File]::WriteAllBytes((Join-Path $directory 'failure.json'), [Text.Encoding]::UTF8.GetBytes($failure))
                }
                Assert ($attempts -gt $previous) 'Verified unfinished content never attempted its held-file rename'
                foreach ($torrentId in $identities) {
                    $files = (Send-Command @{ command = 'torrent'; torrent_id = $torrentId; view = 'files' }).data.files
                    Assert ($files[0].disk_path -eq 'shared.bin.!tt' -and $files[0].downloaded -eq 2048) 'A held-file rename lost verified bytes or the saved physical name'
                }
                $rows = (Send-Command @{ command = 'snapshot' }).data.torrents
                Assert (@($rows | Where-Object { $_.error }).Count -eq 0) 'A held-file rename became a download error'
                Assert ((Payload-Hash $unfinished) -eq $originalHash -and -not (Test-Path -LiteralPath $finished)) 'A refused rename copied, replaced or damaged payload'
                if ($restart -eq 1) { Stop-Engine }
            }
            $heldFile.Dispose()
            $heldFile = $null
            $until = [DateTime]::UtcNow.AddSeconds(40)
            do {
                $paths = @($identities | ForEach-Object {
                    (Send-Command @{ command = 'torrent'; torrent_id = $_; view = 'files' }).data.files[0].disk_path
                })
                if (@($paths | Where-Object { $_ -ne 'shared.bin' }).Count -eq 0) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $until)
            Assert (@($paths | Where-Object { $_ -ne 'shared.bin' }).Count -eq 0) 'Retry did not update every shared-file owner to the finished name'
            Assert ((Payload-Hash $finished) -eq $originalHash -and -not (Test-Path -LiteralPath $unfinished)) 'Finishing changed the bytes or left a second payload copy'
            $zone = $null
            try { $zone = [IO.File]::OpenRead($finished + ':Zone.Identifier') }
            catch [IO.FileNotFoundException] { }
            finally { if ($zone) { $zone.Dispose() } }
            Assert ($null -eq $zone) 'Verification marked existing bytes as a new Internet download'
            Stop-Engine
            $null = Start-Engine
            foreach ($torrentId in $identities) {
                $files = (Send-Command @{ command = 'torrent'; torrent_id = $torrentId; view = 'files' }).data.files
                Assert ($files[0].disk_path -eq 'shared.bin' -and $files[0].downloaded -eq 2048) 'The completed shared-file names or verified bytes were lost at restart'
            }
            $reply = Send-Command @{ command = 'remove'; torrent_ids = $identities }
            Assert $reply.ok 'The completed fixture could not be removed while keeping its files'
            $empty = Join-Path $directory 'empty'
            $null = New-Item -ItemType Directory -Path $empty
            $identities = @()
            foreach ($pieceLength in 16384, 32768) {
                $preview = Send-Command @{ command = 'preview'; source = (Join-Path $directory "shared-$pieceLength.torrent"); destination = $empty }
                Assert $preview.ok 'The use-existing fixture did not preview'
                $reply = Send-Command @{ command = 'add'; preview_id = $preview.data.preview_id; destination = $empty }
                Assert $reply.ok 'The use-existing fixture could not be added'
                $identities += $reply.data.torrent_id
            }
            $reply = Send-Command @{ command = 'move'; torrent_ids = $identities; destination = $payload }
            Assert $reply.ok 'The move could not discover the existing completed files'
            $until = [DateTime]::UtcNow.AddSeconds(15)
            do {
                $files = @($identities | ForEach-Object {
                    (Send-Command @{ command = 'torrent'; torrent_id = $_; view = 'files' }).data.files[0]
                })
                if (@($files | Where-Object { $_.disk_path -ne 'shared.bin' -or $_.downloaded -ne 2048 }).Count -eq 0) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $until)
            Assert (@($files | Where-Object { $_.disk_path -ne 'shared.bin' -or $_.downloaded -ne 2048 }).Count -eq 0) 'Use existing ignored the completed name and tried to download another copy'
            Assert ((Payload-Hash $finished) -eq $originalHash -and -not (Test-Path -LiteralPath $unfinished)) 'Use existing damaged the completed bytes or created another payload'
        }
        'FilesSafety' {
            $reply = Send-Command @{ command = 'session_pause'; paused = $true }
            Assert $reply.ok 'The file-safety session could not pause'
            $content = [Text.Encoding]::ASCII.GetBytes(('x' * 2048))
            $digest = [Security.Cryptography.SHA1]::HashData($content)
            $fixtures = @(16384, 32768) | ForEach-Object {
                $fixture = Join-Path $directory ("shared-$_.torrent")
                $prefix = [Text.Encoding]::ASCII.GetBytes("d4:infod6:lengthi2048e4:name10:shared.bin12:piece lengthi$($_)e6:pieces20:")
                $suffix = [Text.Encoding]::ASCII.GetBytes('7:privatei1eee')
                [IO.File]::WriteAllBytes($fixture, [byte[]]($prefix + $digest + $suffix))
                $fixture
            }
            $sourceFile = Join-Path $payload 'shared.bin'
            $unrelated = Join-Path $payload 'keep.txt'
            [IO.File]::WriteAllBytes($sourceFile, $content)
            [IO.File]::WriteAllBytes($unrelated, [Text.Encoding]::ASCII.GetBytes('keep'))
            $originalHash = Payload-Hash $sourceFile
            $identities = @()
            foreach ($fixture in $fixtures) {
                $preview = Send-Command @{ command = 'preview'; source = $fixture; destination = $payload }
                Assert $preview.ok 'The shared-file fixture did not preview'
                $reply = Send-Command @{ command = 'add'; preview_id = $preview.data.preview_id; destination = $payload; paused = $true }
                Assert ($reply.ok -and -not $reply.data.duplicate) 'Distinct shared-file torrents were merged or refused'
                $identities += $reply.data.torrent_id
            }
            $scope = Send-Command @{ command = 'file_scope'; torrent_ids = @($identities[0]) }
            Assert ($scope.ok -and $scope.data.shared.Count -eq 1 -and $scope.data.kept_files -eq 1) 'The outside shared-file owner was not reported'
            $destination = Join-Path $directory 'moved'
            $reply = Send-Command @{ command = 'move'; torrent_ids = @($identities[0]); destination = $destination }
            Assert (-not $reply.ok -and $reply.error.code -eq 'shared_files') 'A partial scope moved another torrent file'
            $settingsFile = Join-Path $directory 'settings.json'
            $backup = Join-Path $directory 'settings.saved.json'
            Move-Item -LiteralPath $settingsFile -Destination $backup
            $null = New-Item -ItemType Directory -Path $settingsFile
            try {
                $reply = Send-Command @{ command = 'delete_files'; torrent_ids = @($identities[0]) }
                Assert (-not $reply.ok -and $reply.error.code -eq 'storage_failed') 'A refused deletion commit was reported as accepted'
                $snapshot = (Send-Command @{ command = 'snapshot' }).data
                Assert ($snapshot.torrents.Count -eq 2 -and (Payload-Hash $sourceFile) -eq $originalHash) 'A refused deletion changed membership or shared payload'
            }
            finally {
                Remove-Item -LiteralPath $settingsFile
                Move-Item -LiteralPath $backup -Destination $settingsFile
            }
            $reply = Send-Command @{ command = 'delete_files'; torrent_ids = @($identities[0]) }
            Assert ($reply.ok -and $reply.data.kept_files -eq 1) 'Deletion did not report its outside-shared file'
            Stop-Engine
            $snapshot = Start-Engine
            Assert ($snapshot.torrents.Count -eq 1 -and (Payload-Hash $sourceFile) -eq $originalHash) 'Deleting one shared torrent damaged its surviving owner or restored membership'

            $collision = Join-Path $directory 'collision'
            $null = New-Item -ItemType Directory -Path $collision
            $collisionFile = Join-Path $collision 'shared.bin'
            [IO.File]::WriteAllBytes($collisionFile, [Text.Encoding]::ASCII.GetBytes(('z' * 2048)))
            $collisionHash = Payload-Hash $collisionFile
            $remaining = $identities[1]
            $reply = Send-Command @{ command = 'move'; torrent_ids = @($remaining); destination = $collision }
            Assert $reply.ok 'Collision preflight was not accepted'
            $until = [DateTime]::UtcNow.AddSeconds(15)
            do {
                Start-Sleep -Milliseconds 100
                $row = (Send-Command @{ command = 'snapshot' }).data.torrents[0]
            } while (($row.moving -or $row.error -ne 'destination_exists') -and [DateTime]::UtcNow -lt $until)
            Assert (-not $row.moving -and $row.error -eq 'destination_exists' -and -not $row.move_destination) 'Collision did not finish safely without an interrupted marker'
            Assert ((Payload-Hash $sourceFile) -eq $originalHash -and (Payload-Hash $collisionFile) -eq $collisionHash) 'A destination collision replaced existing bytes'
            # A link to the payload folder gives its file a second path.
            $alias = Join-Path $directory 'alias'
            $null = New-Item -ItemType Junction -Path $alias -Target $payload
            try {
                $reply = Send-Command @{ command = 'move'; torrent_ids = @($remaining); destination = $alias }
                Assert $reply.ok 'The move to a link was not accepted for preflight'
                $until = [DateTime]::UtcNow.AddSeconds(15)
                do {
                    Start-Sleep -Milliseconds 100
                    $row = (Send-Command @{ command = 'snapshot' }).data.torrents[0]
                } while (($row.moving -or $row.error -ne 'same_location') -and [DateTime]::UtcNow -lt $until)
                Assert (-not $row.moving -and $row.error -eq 'same_location' -and -not $row.move_destination) 'A move onto the same file through a link was not refused as same_location'
                Assert ((Payload-Hash $sourceFile) -eq $originalHash) 'A move onto the same file through a link changed its bytes'
            }
            finally {
                # Removes the link only, never the folder it points to.
                (Get-Item -LiteralPath $alias).Delete()
            }
            $reply = Send-Command @{ command = 'delete_files'; torrent_ids = @($remaining) }
            Assert $reply.ok 'A failed no-op move prevented deletion of its original files'
            Stop-Engine
            $snapshot = Start-Engine
            Assert ($snapshot.torrents.Count -eq 0 -and -not (Test-Path -LiteralPath $sourceFile) -and (Payload-Hash $collisionFile) -eq $collisionHash) 'Deletion after a collision reached the unrelated destination or restored membership'

            [IO.File]::WriteAllBytes($sourceFile, $content)
            $identities = @()
            foreach ($fixture in $fixtures) {
                $preview = Send-Command @{ command = 'preview'; source = $fixture; destination = $payload }
                $reply = Send-Command @{ command = 'add'; preview_id = $preview.data.preview_id; destination = $payload; paused = $true }
                Assert $reply.ok 'The group-move fixture could not be re-added'
                $identities += $reply.data.torrent_id
            }
            $reply = Send-Command @{ command = 'move'; torrent_ids = $identities; destination = $destination }
            Assert $reply.ok 'The complete shared group could not move'
            $until = [DateTime]::UtcNow.AddSeconds(15)
            do {
                Start-Sleep -Milliseconds 100
                $rows = (Send-Command @{ command = 'snapshot' }).data.torrents
            } while (@($rows | Where-Object { $_.moving -or $_.move_destination -or $_.status -eq 'waiting_move' }).Count -gt 0 -and [DateTime]::UtcNow -lt $until)
            $movedFile = Join-Path $destination 'shared.bin'
            Assert (@($rows | Where-Object { $_.save_path -ne $destination -or $_.moving -or $_.move_destination }).Count -eq 0 -and (Payload-Hash $movedFile) -eq $originalHash) 'The cross-seeded group did not establish one destination'
            Assert (-not (Test-Path -LiteralPath $sourceFile) -and (Test-Path -LiteralPath $unrelated)) 'Move affected unrelated files or left its payload behind'
            Stop-Engine
            $settingsFile = Join-Path $directory 'settings.json'
            $saved = [IO.File]::ReadAllText($settingsFile) | ConvertFrom-Json
            foreach ($torrent in $saved.torrents) { $torrent.move_destination = $collision }
            [IO.File]::WriteAllBytes($settingsFile, [Text.Encoding]::UTF8.GetBytes(($saved | ConvertTo-Json -Depth 30 -Compress)))
            $snapshot = Start-Engine
            Assert (@($snapshot.torrents | Where-Object error -ne 'move_unfinished').Count -eq 0) 'An interrupted move resumed as ordinary transfer state'
            $reply = Send-Command @{ command = 'move'; torrent_ids = $identities; destination = $destination }
            Assert $reply.ok 'Move could not recover the files at their known folder'
            $until = [DateTime]::UtcNow.AddSeconds(15)
            do {
                Start-Sleep -Milliseconds 100
                $rows = (Send-Command @{ command = 'snapshot' }).data.torrents
            } while (@($rows | Where-Object { $_.moving -or $_.move_destination -or $_.status -eq 'waiting_move' }).Count -gt 0 -and [DateTime]::UtcNow -lt $until)
            Assert (@($rows | Where-Object { $_.moving -or $_.move_destination }).Count -eq 0) 'Explicit move recovery never completed'
            Stop-Engine
            $saved = [IO.File]::ReadAllText($settingsFile) | ConvertFrom-Json
            foreach ($torrent in $saved.torrents) { $torrent.move_destination = $collision }
            [IO.File]::WriteAllBytes($settingsFile, [Text.Encoding]::UTF8.GetBytes(($saved | ConvertTo-Json -Depth 30 -Compress)))
            $null = Start-Engine
            $reply = Send-Command @{ command = 'delete_files'; torrent_ids = $identities }
            Assert ($reply.ok -and $reply.data.kept_files -eq 0) 'The interrupted shared group could not delete its payload'
            Stop-Engine
            $snapshot = Start-Engine
            Assert ($snapshot.torrents.Count -eq 0 -and -not (Test-Path -LiteralPath $movedFile) -and (Payload-Hash $collisionFile) -eq $collisionHash -and (Test-Path -LiteralPath $unrelated)) 'Group deletion reached the held destination, restored membership or damaged unrelated files'
            [IO.File]::WriteAllBytes($sourceFile, $content)
            $identities = @()
            foreach ($fixture in $fixtures) {
                $preview = Send-Command @{ command = 'preview'; source = $fixture; destination = $payload }
                $reply = Send-Command @{ command = 'add'; preview_id = $preview.data.preview_id; destination = $payload; paused = $true }
                Assert $reply.ok 'The remove-and-delete fixture could not be added'
                $identities += $reply.data.torrent_id
            }
            $reply = Send-Command @{ command = 'remove'; torrent_ids = @($identities[0]) }
            Assert ($reply.ok -and (Payload-Hash $sourceFile) -eq $originalHash) 'Ordinary removal damaged the surviving shared payload'
            $reply = Send-Command @{ command = 'delete_files'; torrent_ids = @($identities[1]) }
            Assert $reply.ok 'Deletion after ordinary removal was refused'
            Stop-Engine
            $snapshot = Start-Engine
            Assert ($snapshot.torrents.Count -eq 0 -and -not (Test-Path -LiteralPath $sourceFile) -and (Test-Path -LiteralPath $unrelated)) 'Deferred shared-file cleanup lost deletion or restored removed membership'
        }
        'MoveAndVerify' {
            function Row-Of([string] $id) {
                (Send-Command @{ command = 'snapshot' }).data.torrents | Where-Object torrent_id -eq $id
            }
            # Waits until a check that `started` says has begun ends, and
            # returns the highest check rate the snapshots reported.
            function Wait-Check([string] $id, [scriptblock] $started) {
                $rate = 0
                $seen = $false
                $until = [DateTime]::UtcNow.AddSeconds(300)
                do {
                    Start-Sleep -Milliseconds 200
                    $snapshot = (Send-Command @{ command = 'snapshot' }).data
                    $row = $snapshot.torrents | Where-Object torrent_id -eq $id
                    $seen = $seen -or $row.status -in 'checking', 'waiting_check' -or (& $started $row)
                    if ($row.status -eq 'checking' -and $snapshot.check_rate) { $rate = [Math]::Max($rate, $snapshot.check_rate) }
                } while ((-not $seen -or $row.status -in 'checking', 'waiting_check') -and [DateTime]::UtcNow -lt $until)
                Assert ($seen -and $row.status -notin 'checking', 'waiting_check') 'A check did not end'
                return $rate
            }
            # One changed byte fails its piece, which the torrent still counts
            # until something checks the file again.
            function Set-Changed([string] $id, [string] $name, [long] $offset) {
                $file = Join-Path (Row-Of $id).save_path "$name.bin"
                $stream = [IO.File]::Open($file, 'Open', 'ReadWrite', 'ReadWrite')
                try {
                    $stream.Position = $offset
                    $stream.WriteByte(1)
                }
                finally { $stream.Dispose() }
            }
            # Verify finds the changed byte, so a check during the moves before
            # it would have shown as fewer completed bytes too.
            function Assert-Found([string] $id, [string] $label) {
                $before = (Row-Of $id).completed
                $reply = Send-Command @{ command = 'verify'; torrent_ids = @($id) }
                Assert $reply.ok "$label could not be verified"
                $null = Wait-Check $id { param($row) $row.completed -lt $before }
                Assert ((Row-Of $id).completed -lt $before) "Verify did not find the changed byte, so the $label moves were not shown to skip a check"
            }
            function Move-Unchecked([string] $id, [string] $name, [string] $destination, [string] $label) {
                $before = Row-Of $id
                $reply = Send-Command @{ command = 'move'; torrent_ids = @($id); destination = $destination }
                Assert $reply.ok "$label`: the move to $destination was refused"
                $checked = $false
                $until = [DateTime]::UtcNow.AddSeconds(120)
                do {
                    Start-Sleep -Milliseconds 100
                    $row = Row-Of $id
                    $checked = $checked -or $row.status -in 'checking', 'waiting_check'
                } while (($row.moving -or $row.save_path -ne $destination) -and [DateTime]::UtcNow -lt $until)
                Assert (-not $row.moving -and $row.save_path -eq $destination -and -not $row.error) "$label`: the move to $destination did not finish: $($row.error)"
                $until = [DateTime]::UtcNow.AddSeconds(3)
                do {
                    Start-Sleep -Milliseconds 100
                    $row = Row-Of $id
                    $checked = $checked -or $row.status -in 'checking', 'waiting_check'
                } while ([DateTime]::UtcNow -lt $until)
                Assert (-not $checked -and $row.completed -eq $before.completed) "$label`: the move to $destination checked the files it placed itself"
                Assert ($row.status -eq $before.status -and $row.paused -eq $before.paused) "$label`: the move to $destination left the torrent $($row.status) instead of $($before.status)"
                Assert ((Get-Item -LiteralPath (Join-Path $destination "$name.bin")).Length -eq $row.size) "$label`: the move to $destination changed the payload's size"
            }
            # Moves the torrent along every route while it seeds, while it is
            # stopped and while it downloads.
            function Test-Routes([string] $id, [string] $name, [scriptblock] $routes) {
                Set-Changed $id $name 0
                foreach ($destination in (& $routes 'seeding')) { Move-Unchecked $id $name $destination 'Seeding' }
                $reply = Send-Command @{ command = 'pause'; torrent_ids = @($id) }
                Assert $reply.ok "The $name torrent could not be stopped"
                foreach ($destination in (& $routes 'stopped')) { Move-Unchecked $id $name $destination 'Stopped' }
                $reply = Send-Command @{ command = 'resume'; torrent_ids = @($id) }
                Assert $reply.ok "The $name torrent could not be started"
                Assert-Found $id 'seeding and stopped'
                # The failed first piece leaves the torrent downloading, with no
                # peer to download it from.
                Set-Changed $id $name 4MB
                $until = [DateTime]::UtcNow.AddSeconds(10)
                while ((Row-Of $id).status -ne 'downloading' -and [DateTime]::UtcNow -lt $until) { Start-Sleep -Milliseconds 100 }
                Assert ((Row-Of $id).status -eq 'downloading') "The $name torrent did not download after its piece failed"
                foreach ($destination in (& $routes 'downloading')) { Move-Unchecked $id $name $destination 'Downloading' }
                Assert-Found $id 'downloading'
            }

            $zeros = New-Zeros 'zeros' 8GB
            $rate = Wait-Check $zeros { param($row) $row.progress -eq 1 }
            Assert ((Row-Of $zeros).progress -eq 1) 'The zero fixture was not verified complete'
            Assert ($rate -gt 0) 'Verification reported no check rate'
            $until = [DateTime]::UtcNow.AddSeconds(3)
            do {
                Start-Sleep -Milliseconds 200
                $snapshot = (Send-Command @{ command = 'snapshot' }).data
            } while ($null -ne $snapshot.check_rate -and [DateTime]::UtcNow -lt $until)
            Assert ($null -eq $snapshot.check_rate) 'The check rate stayed after verification ended'

            # A stopped torrent keeps its check until it starts, and checks
            # nothing meanwhile.
            $reply = Send-Command @{ command = 'pause'; torrent_ids = @($zeros) }
            Assert $reply.ok 'The zero torrent could not be stopped'
            $reply = Send-Command @{ command = 'verify'; torrent_ids = @($zeros) }
            Assert $reply.ok 'The stopped zero torrent could not be verified'
            $until = [DateTime]::UtcNow.AddSeconds(3)
            do {
                Start-Sleep -Milliseconds 200
                $snapshot = (Send-Command @{ command = 'snapshot' }).data
                Assert ($null -eq $snapshot.check_rate) 'A stopped torrent reported a check rate'
            } while ([DateTime]::UtcNow -lt $until)
            $reply = Send-Command @{ command = 'resume'; torrent_ids = @($zeros) }
            Assert $reply.ok 'The zero torrent could not be started'
            $null = Wait-Check $zeros { param($row) $false }
            Assert ((Row-Of $zeros).progress -eq 1) 'The started torrent did not finish its check'

            # A move during a check pauses it, and the check ends at the new
            # folder with every piece found.
            $reply = Send-Command @{ command = 'verify'; torrent_ids = @($zeros) }
            Assert $reply.ok 'The zero torrent could not be verified again'
            $until = [DateTime]::UtcNow.AddSeconds(30)
            do {
                Start-Sleep -Milliseconds 100
                $row = Row-Of $zeros
            } while (-not ($row.status -eq 'checking' -and $row.progress -gt 0) -and [DateTime]::UtcNow -lt $until)
            Assert ($row.status -eq 'checking' -and $row.progress -gt 0 -and $row.progress -lt 1) 'The check did not run before the move'
            $during = Join-Path $directory 'during-check'
            $reply = Send-Command @{ command = 'move'; torrent_ids = @($zeros); destination = $during }
            Assert $reply.ok 'The move during a check was refused'
            $until = [DateTime]::UtcNow.AddSeconds(60)
            do {
                Start-Sleep -Milliseconds 100
                $row = Row-Of $zeros
            } while (($row.moving -or $row.save_path -ne $during) -and [DateTime]::UtcNow -lt $until)
            Assert (-not $row.moving -and $row.save_path -eq $during -and -not $row.error) 'The move during a check did not finish'
            $until = [DateTime]::UtcNow.AddSeconds(300)
            do {
                Start-Sleep -Milliseconds 200
                $row = Row-Of $zeros
            } while (($row.status -eq 'checking' -or $row.progress -lt 1) -and [DateTime]::UtcNow -lt $until)
            Assert ($row.progress -eq 1 -and $row.status -eq 'seeding') "The check stopped by a move did not end complete at the new folder: $($row.status) at $($row.progress)"

            # V: is the volume of the evidence folder and C: the one of -Folder.
            # The link on C: leads back to V:, as C:\temp leads to V:\temp.
            $link = $null
            if ($volumeFolder) {
                $linked = Join-Path $directory 'linked'
                $null = New-Item -ItemType Directory -Path $linked
                $link = Join-Path $volumeFolder 'link'
                $null = New-Item -ItemType Junction -Path $link -Target $linked
            }
            try {
                Test-Routes $zeros 'zeros' {
                    param($state)
                    Join-Path $directory "$state-v"
                    if ($volumeFolder) {
                        Join-Path $volumeFolder "$state-c"
                        Join-Path $volumeFolder "$state-cc"
                        Join-Path $directory "$state-cv"
                        Join-Path $link "$state-link"
                        Join-Path $directory "$state-back"
                    }
                }
                if ($networkFolder) {
                    $small = New-Zeros 'small' 16MB
                    $null = Wait-Check $small { param($row) $row.progress -eq 1 }
                    Test-Routes $small 'small' {
                        param($state)
                        Join-Path $networkFolder "$state-y"
                        Join-Path $networkFolder "$state-yy"
                        if ($volumeFolder) {
                            Join-Path $volumeFolder "$state-yc"
                            Join-Path $networkFolder "$state-cy"
                        }
                        Join-Path $directory "$state-yv"
                    }
                }
            }
            finally {
                # Removes the link only, never the folder it leads to.
                if ($link -and (Test-Path -LiteralPath $link)) { (Get-Item -LiteralPath $link).Delete() }
            }
        }
        'DriveTurns' {
            $busy = 'checking', 'waiting_check', 'moving', 'waiting_move'
            # One snapshot gives the rows of all these torrents at one moment.
            function Rows-Of([string[]] $ids) {
                $rows = @{}
                foreach ($row in (Send-Command @{ command = 'snapshot' }).data.torrents) {
                    if ($row.torrent_id -in $ids) { $rows[$row.torrent_id] = $row }
                }
                return $rows
            }
            # Verifies a torrent and waits until its check runs. A check that
            # libtorrent starts holds its drive from the next once-a-second
            # pass, so a job sent before then can go first.
            function Start-Check([string] $id, [string] $label) {
                $reply = Send-Command @{ command = 'verify'; torrent_ids = @($id) }
                Assert $reply.ok "$label`: Verify was refused"
                $until = [DateTime]::UtcNow.AddSeconds(30)
                do {
                    Start-Sleep -Milliseconds 100
                    $row = (Rows-Of $id)[$id]
                } while (-not ($row.status -eq 'checking' -and $row.progress -gt 0) -and [DateTime]::UtcNow -lt $until)
                Start-Sleep -Milliseconds 1500
                $row = (Rows-Of $id)[$id]
                Assert ($row.status -eq 'checking' -and $row.progress -gt 0 -and $row.progress -lt 1) "$label`: the check of $($row.name) did not run"
            }
            function Send-Move([string] $id, [string] $destination, [string] $label) {
                $reply = Send-Command @{ command = 'move'; torrent_ids = @($id); destination = $destination }
                Assert $reply.ok "$label`: the move to $destination was refused"
            }
            # Waits until the torrent's job waits for the drive that `holder`
            # uses, named as the drive of the waiting torrent's folder.
            function Wait-Turn([string] $id, [string] $status, [string] $holder, [string] $drive, [string] $label) {
                $until = [DateTime]::UtcNow.AddSeconds(10)
                do {
                    Start-Sleep -Milliseconds 100
                    $row = (Rows-Of $id)[$id]
                } while (-not ($row.status -eq $status -and $row.waiting_drive) -and [DateTime]::UtcNow -lt $until)
                Assert ($row.status -eq $status -and $row.waiting_drive -eq $drive -and $row.waiting_torrent_id -eq $holder) `
                    "$label`: $($row.name) is $($row.status) for '$($row.waiting_drive)' used by '$($row.waiting_torrent_id)', not $status for $drive used by $holder"
            }
            # Samples the torrents every 200 ms until each has had its job and
            # none runs or waits any more, and returns the samples.
            function Watch-Jobs([string[]] $ids, [string] $label) {
                $samples = [Collections.Generic.List[hashtable]]::new()
                $seen = @{}
                $until = [DateTime]::UtcNow.AddSeconds(600)
                do {
                    Start-Sleep -Milliseconds 200
                    $rows = Rows-Of $ids
                    $samples.Add($rows)
                    $working = @($ids | Where-Object { $rows[$_].status -in $busy })
                    foreach ($id in $working) { $seen[$id] = $true }
                } while (($working.Count -or $seen.Count -lt $ids.Count) -and [DateTime]::UtcNow -lt $until)
                Assert ($seen.Count -eq $ids.Count -and -not $working.Count) "$label`: the jobs did not end"
                foreach ($id in $ids) {
                    $row = $rows[$id]
                    Assert ($row.progress -eq 1 -and -not $row.error) "$label`: $($row.name) ended $($row.status) at $($row.progress) $($row.error)"
                }
                return ,$samples
            }
            # Both jobs ran, and both progressed between two samples.
            function Assert-Together($samples, [string[]] $ids, [string] $label) {
                $first = $null
                foreach ($rows in $samples) {
                    if (@($ids | Where-Object { $rows[$_].status -in 'checking', 'moving' }).Count -ne $ids.Count) { continue }
                    if (-not $first) { $first = $rows; continue }
                    if (-not ($ids | Where-Object { $rows[$_].progress -le $first[$_].progress })) { return }
                }
                throw "$label`: the jobs did not progress at the same time"
            }
            # A job that waited for its drive never runs beside another job.
            # A check that libtorrent starts on a busy drive runs until the
            # next pass holds it, so two jobs may run together before one of
            # them waited. Returns the torrents in the order their jobs ended.
            function Assert-Turns($samples, [string[]] $ids, [int] $waits, [string] $label) {
                $waited = @{}
                $last = @{}
                for ($index = 0; $index -lt $samples.Count; $index++) {
                    $rows = $samples[$index]
                    $running = @($ids | Where-Object { $rows[$_].status -in 'checking', 'moving' })
                    Assert ($running.Count -lt 2 -or -not ($running | Where-Object { $waited[$_] })) "$label`: a job that waited ran beside another"
                    foreach ($id in $ids) {
                        if ($rows[$id].status -in 'waiting_check', 'waiting_move') { $waited[$id] = $true }
                        if ($rows[$id].status -in $busy) { $last[$id] = $index }
                    }
                }
                Assert ($waited.Count -ge $waits) "$label`: $($waited.Count) jobs waited, not $waits"
                return @($ids | Sort-Object { $last[$_] })
            }
            function Assert-Order($samples, [string[]] $order, [string] $label) {
                $ended = Assert-Turns $samples $order ($order.Count - 1) $label
                Assert (($ended -join ',') -eq ($order -join ',')) "$label`: the jobs ran in another order"
            }

            # -Folder is on C:, and the evidence folder on V:, the virtual disk
            # that C: holds. The link on C: leads to V:, as C:\temp leads to
            # V:\temp.
            $drive = Split-Path -Qualifier $volumeFolder
            $linked = Join-Path $directory 'linked'
            $null = New-Item -ItemType Directory -Path $linked
            $link = Join-Path $volumeFolder 'link'
            $null = New-Item -ItemType Junction -Path $link -Target $linked
            try {
                # The long check outlasts the commands that make others wait
                # behind it.
                $long = New-Zeros 'long' 16GB $volumeFolder
                $first = New-Zeros 'first' 4GB $volumeFolder
                $second = New-Zeros 'second' 4GB $volumeFolder
                $third = New-Zeros 'third' 4GB $volumeFolder
                $onV = New-Zeros 'onv' 4GB
                $onLink = New-Zeros 'onlink' 4GB $link
                $onY = New-Zeros 'ony' 512MB $networkFolder
                # The moves take seconds, so a second job can be sent while
                # the first one runs.
                $moved = New-Zeros 'moved' 512MB $volumeFolder -Dense
                $all = @($long, $first, $second, $third, $onV, $onLink, $onY, $moved)
                $other = $null
                if ($others) {
                    $other = New-Zeros 'other' 1GB $others[0] -Dense
                    $all += $other
                }
                $watch = [Diagnostics.Stopwatch]::StartNew()
                $until = [DateTime]::UtcNow.AddSeconds(600)
                do {
                    Start-Sleep -Milliseconds 500
                    $rows = Rows-Of $all
                    $pending = @($all | Where-Object { $rows[$_].status -in $busy -or $rows[$_].progress -ne 1 })
                } while ($pending.Count -and [DateTime]::UtcNow -lt $until)
                Assert (-not $pending.Count) 'The added torrents were not checked complete'
                Write-Host ('The added torrents were checked in {0:N0} s' -f $watch.Elapsed.TotalSeconds)

                $reply = Send-Command @{ command = 'settings'; changes = @{ active_checking = 2 } }
                Assert (-not $reply.ok) 'The removed active_checking setting was accepted'

                $reply = Send-Command @{ command = 'verify'; torrent_ids = @($first, $onY) }
                Assert $reply.ok 'C: and Y: could not be verified'
                Assert-Together (Watch-Jobs @($first, $onY) 'C: and Y:') @($first, $onY) 'C: and Y:'

                foreach ($pair in @(@($first, $onV, 'C: and V:'), @($onLink, $onV, 'The link and V:'))) {
                    $reply = Send-Command @{ command = 'verify'; torrent_ids = $pair[0..1] }
                    Assert $reply.ok "$($pair[2]) could not be verified"
                    $null = Assert-Turns (Watch-Jobs $pair[0..1] $pair[2]) $pair[0..1] 1 $pair[2]
                }

                Start-Check $long 'A move behind a check'
                Send-Move $moved $networkFolder 'A move behind a check'
                Wait-Turn $moved 'waiting_move' $long $drive 'A move behind a check'
                Assert-Order (Watch-Jobs @($long, $moved) 'A move behind a check') @($long, $moved) 'A move behind a check'
                Assert ((Rows-Of $moved)[$moved].save_path -eq $networkFolder) 'The move behind a check did not reach Y:'

                $back = Join-Path $volumeFolder 'back'
                Start-Check $long 'Check, move, check'
                Send-Move $moved $back 'Check, move, check'
                Wait-Turn $moved 'waiting_move' $long $drive 'Check, move, check'
                $reply = Send-Command @{ command = 'verify'; torrent_ids = @($first) }
                Assert $reply.ok 'Check, move, check: the second check was refused'
                Wait-Turn $first 'waiting_check' $long $drive 'Check, move, check'
                Assert-Order (Watch-Jobs @($long, $moved, $first) 'Check, move, check') @($long, $moved, $first) 'Check, move, check'

                # A stopped check stays stopped when its drive is free.
                Start-Check $long 'Stop a waiting check'
                $reply = Send-Command @{ command = 'verify'; torrent_ids = @($first) }
                Assert $reply.ok 'Stop a waiting check: Verify was refused'
                Wait-Turn $first 'waiting_check' $long $drive 'Stop a waiting check'
                $reply = Send-Command @{ command = 'pause'; torrent_ids = @($first) }
                Assert $reply.ok 'Stop a waiting check: the torrent could not be stopped'
                $null = Watch-Jobs @($long) 'Stop a waiting check'
                $stopped = (Rows-Of $first)[$first]
                Assert ($stopped.status -eq 'paused' -and $stopped.progress -lt 1) "Stop a waiting check: the stopped torrent is $($stopped.status) at $($stopped.progress)"
                $until = [DateTime]::UtcNow.AddSeconds(3)
                do {
                    Start-Sleep -Milliseconds 200
                    $row = (Rows-Of $first)[$first]
                    Assert ($row.status -eq 'paused' -and $row.progress -eq $stopped.progress) "Stop a waiting check: the stopped torrent is $($row.status) at $($row.progress)"
                } while ([DateTime]::UtcNow -lt $until)
                $reply = Send-Command @{ command = 'resume'; torrent_ids = @($first) }
                Assert $reply.ok 'Stop a waiting check: the torrent could not be started'
                $null = Watch-Jobs @($first) 'Stop a waiting check'

                # Forced checks go first, in the order they were forced.
                foreach ($case in @(
                    @{ label = 'Force the second'; waiting = @($first, $second); forced = @($second); order = @($long, $second, $first) },
                    @{ label = 'Force the third, then the first'; waiting = @($first, $second, $third); forced = @($third, $first); order = @($long, $third, $first, $second) })) {
                    Start-Check $long $case.label
                    foreach ($id in $case.waiting) {
                        $reply = Send-Command @{ command = 'verify'; torrent_ids = @($id) }
                        Assert $reply.ok "$($case.label): Verify was refused"
                        Wait-Turn $id 'waiting_check' $long $drive $case.label
                    }
                    foreach ($id in $case.forced) {
                        $reply = Send-Command @{ command = 'force'; torrent_ids = @($id) }
                        Assert $reply.ok "$($case.label): Force start was refused"
                    }
                    Assert-Order (Watch-Jobs $case.order $case.label) $case.order $case.label
                    $reply = Send-Command @{ command = 'resume'; torrent_ids = $case.forced }
                    Assert $reply.ok "$($case.label): the forced torrents could not be started normally"
                }

                # Pause all keeps each check's place.
                Start-Check $long 'Pause all'
                $reply = Send-Command @{ command = 'verify'; torrent_ids = @($first) }
                Assert $reply.ok 'Pause all: Verify was refused'
                Wait-Turn $first 'waiting_check' $long $drive 'Pause all'
                $reply = Send-Command @{ command = 'session_pause'; paused = $true }
                Assert $reply.ok 'Pause all was refused'
                Start-Sleep -Seconds 3
                $reply = Send-Command @{ command = 'session_pause'; paused = $false }
                Assert $reply.ok 'Pause all could not end'
                Assert-Order (Watch-Jobs @($long, $first) 'Pause all') @($long, $first) 'Pause all'

                # A magnet without metadata in an unrelated folder does not
                # hold a move back.
                $label = 'A move beside a magnet'
                $preview = Send-Command @{ command = 'preview'; source = 'magnet:?xt=urn:btih:0123456789012345678901234567890123456789'; destination = $payload }
                Assert $preview.ok "$label`: the magnet did not preview"
                $reply = Send-Command @{ command = 'add'; preview_id = $preview.data.preview_id; destination = $payload; paused = $true }
                Assert $reply.ok "$label`: the magnet could not be added"
                $magnet = $reply.data.torrent_id
                Send-Move $moved $networkFolder $label
                $null = Watch-Jobs @($moved) $label
                Assert ((Rows-Of $moved)[$moved].save_path -eq $networkFolder) "$label`: the move did not reach Y:"
                Send-Move $moved $back $label
                $null = Watch-Jobs @($moved) $label
                $reply = Send-Command @{ command = 'remove'; torrent_ids = @($magnet) }
                Assert $reply.ok "$label`: the magnet could not be removed"

                # A move that waits for the metadata of a magnet in its
                # destination holds no drive, and names the drive's user once
                # only the drive holds it.
                $label = 'A move behind a magnet'
                $preview = Send-Command @{ command = 'preview'; source = 'magnet:?xt=urn:btih:0123456789012345678901234567890123456788'; destination = $networkFolder }
                Assert $preview.ok "$label`: the magnet did not preview"
                $reply = Send-Command @{ command = 'add'; preview_id = $preview.data.preview_id; destination = $networkFolder; paused = $true }
                Assert $reply.ok "$label`: the magnet could not be added"
                $magnet = $reply.data.torrent_id
                Send-Move $moved $networkFolder $label
                $until = [DateTime]::UtcNow.AddSeconds(10)
                do {
                    Start-Sleep -Milliseconds 100
                    $row = (Rows-Of $moved)[$moved]
                } while ($row.error -ne 'metadata_unavailable' -and [DateTime]::UtcNow -lt $until)
                Assert ($row.error -eq 'metadata_unavailable') "$label`: the move is $($row.status) $($row.error)"
                Start-Check $long $label
                $reply = Send-Command @{ command = 'remove'; torrent_ids = @($magnet) }
                Assert $reply.ok "$label`: the magnet could not be removed"
                Wait-Turn $moved 'waiting_move' $long $drive $label
                Assert-Order (Watch-Jobs @($long, $moved) $label) @($long, $moved) $label
                Assert ((Rows-Of $moved)[$moved].save_path -eq $networkFolder) "$label`: the move did not reach Y:"
                Send-Move $moved $back $label
                $null = Watch-Jobs @($moved) $label

                # The move to the final folder waits its turn for C:.
                $label = 'The move to the final folder'
                $reply = Send-Command @{ command = 'settings'; changes = @{ use_incomplete_folder = $true; incomplete_folder = $networkFolder } }
                Assert $reply.ok "$label`: the incomplete folder could not be set"
                try {
                    Start-Check $long $label
                    $finished = New-Zeros 'finished' 512MB $volumeFolder -folder $networkFolder
                    $until = [DateTime]::UtcNow.AddSeconds(60)
                    do {
                        Start-Sleep -Milliseconds 100
                        $row = (Rows-Of $finished)[$finished]
                    } while ($row.status -ne 'waiting_move' -and [DateTime]::UtcNow -lt $until)
                    Wait-Turn $finished 'waiting_move' $long $drive $label
                    Assert-Order (Watch-Jobs @($long, $finished) $label) @($long, $finished) $label
                    Assert ((Rows-Of $finished)[$finished].save_path -eq $volumeFolder) "$label`: the torrent did not reach its final folder"
                }
                finally {
                    $reply = Send-Command @{ command = 'settings'; changes = @{ use_incomplete_folder = $false } }
                    Assert $reply.ok "$label`: the incomplete folder could not be turned off"
                }

                if ($other) {
                    Send-Move $moved $networkFolder 'Two moves'
                    Send-Move $other $others[1] 'Two moves'
                    Assert-Together (Watch-Jobs @($moved, $other) 'Two moves') @($moved, $other) 'Two moves'

                    Start-Check $onY 'A move beside a check on Y:'
                    Send-Move $other $others[0] 'A move beside a check on Y:'
                    Assert-Together (Watch-Jobs @($onY, $other) 'A move beside a check on Y:') @($onY, $other) 'A move beside a check on Y:'

                    Send-Move $moved $back 'A move to a busy C:'
                    Send-Move $other $volumeFolder 'A move to a busy C:'
                    Wait-Turn $other 'waiting_move' $moved $drive 'A move to a busy C:'
                    Assert-Order (Watch-Jobs @($moved, $other) 'A move to a busy C:') @($moved, $other) 'A move to a busy C:'
                }
            }
            finally {
                # Removes the link only, never the folder it leads to.
                if (Test-Path -LiteralPath $link) { (Get-Item -LiteralPath $link).Delete() }
            }
        }
        'UnfinishedTasks' {
            function Row-Of([string] $id) {
                (Send-Command @{ command = 'snapshot' }).data.torrents | Where-Object torrent_id -eq $id
            }
            function Wait-Row([string] $id, [scriptblock] $done, [string] $label, [int] $seconds = 300) {
                $until = [DateTime]::UtcNow.AddSeconds($seconds)
                do {
                    Start-Sleep -Milliseconds 100
                    $row = Row-Of $id
                } while (-not (& $done $row) -and [DateTime]::UtcNow -lt $until)
                Assert (& $done $row) "$label`: the torrent is $($row.status) at $($row.progress) $($row.error)"
                return $row
            }
            # Moves the torrent and returns once the move has placed half of
            # its bytes.
            function Start-Half([string] $id, [string] $destination, [string] $label) {
                $reply = Send-Command @{ command = 'move'; torrent_ids = @($id); destination = $destination }
                Assert $reply.ok "$label`: the move to $destination was refused"
                $null = Wait-Row $id { param($row) $row.moving -and $row.progress -ge 0.5 } $label 120
            }
            function Saved-Marker([string] $id) {
                $saved = [IO.File]::ReadAllText((Join-Path $directory 'settings.json')) | ConvertFrom-Json
                return @{ torrent = $saved.torrents | Where-Object torrent_id -eq $id; tasks = @($saved.tasks) }
            }
            # Waits for the continued move and its verification, and checks
            # that the copy is the source byte for byte and that the move
            # copied only about what it lacked.
            function Finish-Continued([string] $id, [string] $destination, [string] $hash, [double] $kept, [string] $label) {
                $first = $null
                $until = [DateTime]::UtcNow.AddSeconds(300)
                do {
                    Start-Sleep -Milliseconds 50
                    $row = Row-Of $id
                    if ($null -eq $first -and $row.moving -and $row.progress -gt 0) { $first = $row.progress }
                } while (($row.moving -or $row.save_path -ne $destination -or $row.status -in 'checking', 'waiting_check' -or $row.progress -lt 1) -and [DateTime]::UtcNow -lt $until)
                Assert ($row.save_path -eq $destination -and -not $row.moving -and -not $row.error -and $row.progress -eq 1) "$label`: the continued move ended $($row.status) at $($row.progress) in $($row.save_path) $($row.error)"
                Assert ($null -ne $first -and $first -ge $kept) "$label`: the continued move started at $first, not at $kept or more, so it copied again what it had"
                $file = Join-Path $destination 'big.bin'
                Assert ((Payload-Hash $file) -eq $hash) "$label`: the continued copy differs from its source"
                Assert (-not (Test-Path -LiteralPath "$file.!ttcopy") -and -not (Test-Path -LiteralPath (Join-Path $volumeFolder 'big.bin'))) "$label`: the move left its unfinished copy or its source"
            }
            function Stop-Timed([string] $label) {
                $watch = [Diagnostics.Stopwatch]::StartNew()
                Stop-Engine
                Write-Host ('{0}: Exit took {1:N1} s' -f $label, $watch.Elapsed.TotalSeconds)
                Assert ($watch.Elapsed.TotalSeconds -lt 10) "$label`: Exit took $($watch.Elapsed.TotalSeconds) s"
            }
            function Move-Back([string] $id, [string] $label) {
                $reply = Send-Command @{ command = 'move'; torrent_ids = @($id); destination = $volumeFolder }
                Assert $reply.ok "$label`: the move back to C: was refused"
                $null = Wait-Row $id { param($row) -not $row.moving -and $row.save_path -eq $volumeFolder -and -not $row.error } "$label`: the move back"
            }

            # -Folder is on C:, and the evidence folder on V:, the virtual disk
            # that C: holds.
            $big = New-Pattern 'big' 3GB $volumeFolder
            $null = Wait-Row $big { param($row) $row.progress -eq 1 -and $row.status -notin 'checking', 'waiting_check' } 'The pattern fixture'
            $source = Join-Path $volumeFolder 'big.bin'
            $hash = Payload-Hash $source
            foreach ($target in @(@{ name = 'V:'; folder = (Join-Path $directory 'moved') }, @{ name = 'Y:'; folder = $networkFolder })) {
                $destination = $target.folder
                $partial = Join-Path $destination 'big.bin.!ttcopy'

                # Exit parks the copy with its proof. The work waits for the
                # answer, holds the torrent where it was, and continues from
                # the proof.
                $label = "Exit and continue to $($target.name)"
                Start-Half $big $destination $label
                Stop-Timed $label
                $saved = Saved-Marker $big
                Assert ($saved.torrent.move_destination -eq $destination -and $saved.torrent.move_copy_bytes -gt 0) "$label`: the proof of the copy was not saved"
                Assert (@($saved.tasks | Where-Object { $_.kind -eq 'move' -and $big -in $_.ids }).Count -eq 1) "$label`: the move was not saved as a task"
                Assert ((Get-Item -LiteralPath $partial).Length -ge $saved.torrent.move_copy_bytes) "$label`: the unfinished copy is shorter than its proof"
                $proven = $saved.torrent.move_copy_bytes
                $snapshot = Start-Engine
                Assert ($snapshot.unfinished_tasks.moves -eq 1 -and $snapshot.unfinished_tasks.checks -eq 0 -and $snapshot.unfinished_tasks.deletions -eq 0) "$label`: the snapshot reports $($snapshot.unfinished_tasks | ConvertTo-Json -Compress)"
                $written = (Get-Item -LiteralPath $partial).LastWriteTimeUtc
                Start-Sleep -Seconds 3
                $row = Row-Of $big
                Assert (-not $row.moving -and $row.error -eq 'move_unfinished' -and $row.save_path -eq $volumeFolder) "$label`: while the answer waits, the torrent is $($row.status) $($row.error)"
                Assert ((Get-Item -LiteralPath $partial).LastWriteTimeUtc -eq $written) "$label`: the copy changed before the answer"
                $reply = Send-Command @{ command = 'unfinished_tasks'; action = 'continue' }
                Assert $reply.ok "$label`: Continue was refused"
                Finish-Continued $big $destination $hash ($proven / 3GB - 0.02) $label
                Assert ($null -eq (Send-Command @{ command = 'snapshot' }).data.unfinished_tasks) "$label`: the work still waits after the answer"
                Move-Back $big $label

                # Abandon deletes what the move copied and leaves the torrent
                # where it was.
                $label = "Exit and abandon to $($target.name)"
                Start-Half $big $destination $label
                Stop-Timed $label
                $null = Start-Engine
                $reply = Send-Command @{ command = 'unfinished_tasks'; action = 'abandon' }
                Assert $reply.ok "$label`: Abandon was refused"
                $row = Wait-Row $big { param($row) -not $row.moving -and -not $row.move_destination -and -not $row.error } $label 60
                Assert ($row.save_path -eq $volumeFolder -and -not (Test-Path -LiteralPath $partial) -and -not (Test-Path -LiteralPath (Join-Path $destination 'big.bin'))) "$label`: the move left its copy or moved the torrent"
                Assert ((Payload-Hash $source) -eq $hash) "$label`: the source changed"

                # A crash leaves the copy without a parked proof, and the
                # stored setting continues it at the next start without
                # asking.
                $label = "A crash and the stored Continue to $($target.name)"
                $reply = Send-Command @{ command = 'settings'; changes = @{ unfinished_tasks = 'continue' } }
                Assert $reply.ok "$label`: the setting was refused"
                Start-Half $big $destination $label
                Start-Sleep -Seconds 2
                Stop-Process -Id $script:process.Id -Force
                $null = $script:process.WaitForExit(15000)
                $script:pipe.Dispose(); $script:pipe = $null
                $script:process.Dispose(); $script:process = $null
                $saved = Saved-Marker $big
                Assert ($saved.torrent.move_copy_bytes -ge 1GB) "$label`: no periodic proof was saved"
                $proven = $saved.torrent.move_copy_bytes
                $snapshot = Start-Engine
                Assert ($null -eq $snapshot.unfinished_tasks) "$label`: the engine asked although the setting says continue"
                Finish-Continued $big $destination $hash ($proven / 3GB - 0.02) $label
                $reply = Send-Command @{ command = 'settings'; changes = @{ unfinished_tasks = 'ask' } }
                Assert $reply.ok "$label`: the setting could not return to ask"
                Move-Back $big $label

                # Bytes changed just before the proof make the proof
                # untrusted, so the continued copy compares itself with the
                # source and copies again the blocks that differ, also those
                # changed in the middle.
                $label = "A changed copy to $($target.name)"
                Start-Half $big $destination $label
                Stop-Timed $label
                $proven = (Saved-Marker $big).torrent.move_copy_bytes
                $stream = [IO.File]::Open($partial, 'Open', 'ReadWrite', 'None')
                try {
                    foreach ($offset in @(($proven - 1000), ([long]($proven / 2)), ([long]($proven / 2) + 5MB))) {
                        $stream.Position = $offset
                        $stream.Write([byte[]](1, 2, 3, 4), 0, 4)
                    }
                }
                finally { $stream.Dispose() }
                $null = Start-Engine
                $reply = Send-Command @{ command = 'unfinished_tasks'; action = 'continue' }
                Assert $reply.ok "$label`: Continue was refused"
                Finish-Continued $big $destination $hash 0 $label
                Move-Back $big $label
            }
        }
        'CommittedFiles' {
            $reply = Send-Command @{ command = 'session_pause'; paused = $true }
            Assert $reply.ok 'The file-edit fixture session could not pause'
            $previewId = Preview
            $reply = Send-Command @{ command = 'add'; preview_id = $previewId; destination = $payload; paused = $true }
            Assert $reply.ok 'The file-edit fixture could not be added'
            $torrentId = $reply.data.torrent_id
            $files = (Send-Command @{ command = 'torrent'; torrent_id = $torrentId; view = 'files' }).data.files
            $wanted = $files | Where-Object { -not $_.padding } | Select-Object -First 1
            Assert ($null -ne $wanted) 'The file-edit fixture has no payload files'
            $reply = Send-Command @{ command = 'edit'; torrent_id = $torrentId; changes = @{ priorities = @(@{ index = $wanted.index; priority = 7 }); trackers = @(@{ url = 'http://127.0.0.1:1/announce'; tier = 3 }) } }
            Assert $reply.ok 'A committed priority and tracker edit was refused'
            Stop-Engine
            $null = Start-Engine
            $files = (Send-Command @{ command = 'torrent'; torrent_id = $torrentId; view = 'files' }).data.files
            Assert (($files | Where-Object index -eq $wanted.index).priority -eq 7) 'An older checkpoint defeated a committed priority at restart'
            $trackers = (Send-Command @{ command = 'torrent'; torrent_id = $torrentId; view = 'trackers' }).data.trackers
            Assert ($trackers.Count -eq 1 -and $trackers[0].url -eq 'http://127.0.0.1:1/announce' -and $trackers[0].tier -eq 3) 'A saved tracker choice lost its URL or tier'
            $choices = @($files | ForEach-Object { @{ index = $_.index; priority = 0 } })
            $reply = Send-Command @{ command = 'edit'; torrent_id = $torrentId; changes = @{ priorities = $choices; trackers = @() } }
            Assert $reply.ok 'Select none or clearing the tracker list was refused'
            Stop-Engine
            $snapshot = Start-Engine
            Assert ($snapshot.torrents.Count -eq 1 -and $snapshot.torrents[0].torrent_id -eq $torrentId) 'Select none removed the accepted torrent'
            $files = (Send-Command @{ command = 'torrent'; torrent_id = $torrentId; view = 'files' }).data.files
            Assert (@($files | Where-Object priority -ne 0).Count -eq 0) 'Select none did not survive restart'
            $trackers = (Send-Command @{ command = 'torrent'; torrent_id = $torrentId; view = 'trackers' }).data.trackers
            Assert (@($trackers).Count -eq 0) 'An explicit empty tracker list restored the original trackers'
        }
        'SettingsPolicy' {
            Assert ($initial.settings.notify_problems -eq $true -and $initial.settings.notifications_enabled -eq $false -and $initial.settings.notify_added -eq $false -and $initial.settings.notify_background -eq $true) 'Fresh notification settings do not keep successes quiet and problems visible'
            Assert ($initial.settings.encryption -eq 'preferred' -and $initial.settings.proxy_type -eq 'none') 'Fresh network settings do not prefer encryption without a proxy'
            $previewId = Preview
            $reply = Send-Command @{ command = 'add'; preview_id = $previewId; destination = $payload; paused = $true }
            Assert $reply.ok 'Settings policy fixture addition failed'
            $torrentId = $reply.data.torrent_id
            $period = @{ days = @(0, 1, 2, 3, 4, 5, 6); start = 0; end = 0; mode = 'paused' }
            $reply = Send-Command @{ command = 'settings'; changes = @{ schedule_enabled = $true; schedule = @($period); check_for_updates = $false; active_downloads = 1; port_mapping = $false; notify_problems = $false; notifications_enabled = $true; notify_added = $true; notify_background = $false } }
            Assert $reply.ok 'The weekly schedule could not be committed'
            $snapshot = (Send-Command @{ command = 'snapshot' }).data
            Assert $snapshot.session_paused 'An all-day paused period did not pause the session'
            $reply = Send-Command @{ command = 'session_pause'; paused = $false }
            Assert $reply.ok 'Explicit session resume was refused during a scheduled pause'
            $snapshot = (Send-Command @{ command = 'snapshot' }).data
            Assert (-not $snapshot.session_paused) 'Explicit resume did not override the current scheduled pause'
            Assert ($snapshot.torrents[0].paused -and $snapshot.torrents[0].torrent_id -eq $torrentId) 'Schedule resume changed individual pause intent'
            $period.mode = 'alternative'
            $reply = Send-Command @{ command = 'settings'; changes = @{ schedule = @($period) } }
            Assert $reply.ok 'An alternative period could not replace the paused period'
            $snapshot = (Send-Command @{ command = 'snapshot' }).data
            Assert ($snapshot.limits.mode -eq 'alternative') 'An alternative period did not select its rate pair'
            $reply = Send-Command @{ command = 'settings'; changes = @{ limit_mode = 'speed' } }
            Assert $reply.ok 'A manual limit choice was refused while the schedule applied'
            $snapshot = (Send-Command @{ command = 'snapshot' }).data
            Assert ($snapshot.limits.mode -eq 'speed') 'An explicit limit choice did not override the scheduled pair'
            $reply = Send-Command @{ command = 'settings'; changes = @{ proxy_type = 'socks5' } }
            Assert (-not $reply.ok) 'A proxy without its address was saved'
            $secret = 'proxy-' + [guid]::NewGuid()
            $reply = Send-Command @{ command = 'settings'; changes = @{ proxy_type = 'socks5'; proxy_host = '127.0.0.1'; proxy_port = 1; proxy_username = 'checks'; proxy_password = $secret } }
            Assert $reply.ok 'A complete proxy could not be saved'
            $reply = Send-Command @{ command = 'check_proxy'; proxy = @{ proxy_type = 'socks5'; proxy_host = '127.0.0.1'; proxy_port = 1 } }
            Assert ($reply.ok -and $reply.data.check_id) 'A proxy check did not start'
            $checkId = $reply.data.check_id
            # The engine's own check of the saved proxy can run first; each
            # check ends within 10 seconds.
            $until = [DateTime]::UtcNow.AddSeconds(25)
            do {
                Start-Sleep -Milliseconds 100
                $proxyCheck = (Send-Command @{ command = 'snapshot' }).data.proxy_check
            } while ($proxyCheck.check_id -eq $checkId -and -not $proxyCheck.outcome -and [DateTime]::UtcNow -lt $until)
            Assert ($proxyCheck.check_id -eq $checkId -and $proxyCheck.outcome -eq 'unreachable') 'Checking a proxy where nothing listens did not report it unreachable'
            Stop-Engine
            $snapshot = Start-Engine
            Assert ($snapshot.settings.schedule.Count -eq 1 -and $snapshot.settings.schedule[0].mode -eq 'alternative') 'A committed weekly period was lost at restart'
            Assert ($snapshot.settings.active_downloads -eq 1 -and -not $snapshot.settings.check_for_updates -and -not $snapshot.settings.port_mapping) 'Committed settings were lost at restart'
            Assert ($snapshot.settings.notify_problems -eq $false -and $snapshot.settings.notifications_enabled -eq $true -and $snapshot.settings.notify_added -eq $true -and $snapshot.settings.notify_background -eq $false) 'Notification choices were lost at restart'
            Assert ($snapshot.settings.proxy_type -eq 'socks5' -and $snapshot.settings.proxy_password -eq $secret) 'The proxy and its password were lost at restart'
            Assert (-not [IO.File]::ReadAllText((Join-Path $directory 'settings.json')).Contains($secret)) 'settings.json holds the proxy password as plain text'
            Assert ($snapshot.limits.mode -eq 'alternative' -and $snapshot.torrents[0].paused) 'Restart replayed a temporary override or lost individual pause intent'
            $missing = '{00000000-0000-0000-0000-000000000000}'
            $reply = Send-Command @{ command = 'settings'; changes = @{ network_interface = $missing } }
            Assert $reply.ok 'An unavailable saved adapter choice was refused'
            $snapshot = (Send-Command @{ command = 'snapshot' }).data
            Assert ($snapshot.session_paused -and $snapshot.missing_adapter -eq $missing) 'An unavailable selected adapter did not block the session'
            $reply = Send-Command @{ command = 'session_pause'; paused = $false }
            Assert $reply.ok 'Resume could not preserve the adapter block'
            $snapshot = (Send-Command @{ command = 'snapshot' }).data
            Assert $snapshot.session_paused 'Manual resume bypassed an unavailable selected adapter'
            $reply = Send-Command @{ command = 'settings'; changes = @{ network_interface = ''; schedule_enabled = $false } }
            Assert $reply.ok 'The saved adapter block could not be cleared'
            $snapshot = (Send-Command @{ command = 'snapshot' }).data
            Assert (-not $snapshot.session_paused -and $snapshot.limits.mode -eq 'speed') 'Disabling the schedule lost the saved limit choice'
            Assert $snapshot.torrents[0].paused 'Returning to ordinary policy resumed an individually paused torrent'
        }
        'CheckpointRetry' {
            $previewId = Preview
            $reply = Send-Command @{command='add';preview_id=$previewId;destination=$payload;paused=$true}
            Assert $reply.ok 'Checkpoint retry fixture addition failed'
            $torrentId = $reply.data.torrent_id
            $blocked = Join-Path $directory ($torrentId + '.resume.tmp')
            $until = [DateTime]::UtcNow.AddSeconds(5)
            do {
                if (-not (Test-Path -LiteralPath $blocked)) { break }
                [Threading.Thread]::Sleep(50)
            } while ([DateTime]::UtcNow -lt $until)
            $null = New-Item -ItemType Directory -Path $blocked
            $reply = Send-Command @{command='resume';torrent_ids=@($torrentId)}
            Assert $reply.ok 'Checkpoint retry fixture did not resume'
            $until = [DateTime]::UtcNow.AddSeconds(10)
            do {
                $snapshot = Send-Command @{command='snapshot'}
                if ($snapshot.data.torrents[0].error -eq 'storage_failed') { break }
                [Threading.Thread]::Sleep(50)
            } while ([DateTime]::UtcNow -lt $until)
            Assert ($snapshot.data.torrents[0].error -eq 'storage_failed') 'Blocked checkpoint was not reported'
            Remove-Item -LiteralPath $blocked
            $until = [DateTime]::UtcNow.AddSeconds(40)
            do {
                $snapshot = Send-Command @{command='snapshot'}
                if ($snapshot.data.torrents[0].error -eq '') { break }
                [Threading.Thread]::Sleep(100)
            } while ([DateTime]::UtcNow -lt $until)
            Assert ($snapshot.data.torrents[0].error -eq '') 'Failed checkpoint was skipped after libtorrent cleared its dirty flags'
            Stop-Engine
            $snapshot = Start-Engine
            Assert ($snapshot.torrents.Count -eq 1 -and $snapshot.torrents[0].torrent_id -eq $torrentId -and -not $snapshot.torrents[0].paused) 'Recovered checkpoint lost saved membership or running intent'
        }
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
            $peer = Start-Process -FilePath $peerExecutable `
                -ArgumentList @(('"' + $peerDirectory + '"'), '6881') -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $directory 'peer.log') -RedirectStandardError (Join-Path $directory 'peer-error.log')
            $until = [DateTime]::UtcNow.AddSeconds(100)
            do {
                Assert (-not $peer.HasExited) 'Magnet seed failed'
                $snapshot = Send-Command @{ command = 'snapshot' }
                if ($snapshot.data.torrents[0].complete -and $snapshot.data.torrents[0].size -eq 4194304) { break }
                [Threading.Thread]::Sleep(100)
            } while ([DateTime]::UtcNow -lt $until)
            $torrent = $snapshot.data.torrents[0]
            $snapshot.data | ConvertTo-Json -Depth 20 | Out-File -LiteralPath (Join-Path $directory 'magnet-snapshot.json') -Encoding utf8
            Assert ($torrent.complete -and $torrent.save_path -eq $payload -and $torrent.size -eq 4194304) 'Confirmed unknown magnet did not complete all wanted payload in its chosen destination'
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
            $peer = Start-Process -FilePath $peerExecutable `
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
            $reply = Send-Command @{ command = 'settings'; changes = @{ limit_mode = 'speed'; download_limit = 131072; alternative_download_limit = 524288 } }
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
            Assert ($normalRate -gt 65536 -and $normalRate -lt 170394) 'Speed limits did not constrain real local payload'
            $reply = Send-Command @{ command = 'settings'; changes = @{ limit_mode = 'alternative' } }
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
            $reply = Send-Command @{ command = 'settings'; changes = @{ limit_mode = 'none' } }
            Assert $reply.ok 'Unlimited transfer could not resume'
            $wanted = Join-Path $payload 'selection/wanted.bin'
            $until = [DateTime]::UtcNow.AddSeconds(60)
            do {
                $snapshot = Send-Command @{ command = 'snapshot' }
                if ($snapshot.data.torrents[0].complete -and (Test-Path -LiteralPath $wanted)) { break }
                [Threading.Thread]::Sleep(100)
            } while ([DateTime]::UtcNow -lt $until)
            Assert ($snapshot.data.torrents[0].complete -and (Test-Path -LiteralPath $wanted)) 'Wanted file never completed at its final name'
            $skipped = Join-Path $payload 'selection/skip.bin'
            Assert (-not (Test-Path -LiteralPath $skipped) -or (Get-Item -LiteralPath $skipped).Length -eq 0) 'Unwanted aligned file received payload'
            $wantedHash = Payload-Hash $wanted
            Assert ($wantedHash -eq (Get-FileHash -LiteralPath (Join-Path $peerDirectory 'seed/selection/wanted.bin') -Algorithm SHA256).Hash) 'Wanted payload differs from its seed'
            $reply = Send-Command @{ command = 'pause'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Completed selected torrent could not pause'
            $reply = Send-Command @{ command = 'preview'; source = ('magnet:?xt=urn:btih:' + $hash + '&tr=http%3A%2F%2F127.0.0.1%3A1%2Fannounce'); destination = (Join-Path $directory 'wrong-destination') }
            Assert ($reply.ok -and $reply.data.torrent_id -eq $torrentId -and $reply.data.merge_available) 'Duplicate new tracker was not offered for merge'
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
        'Summaries' {
            $reply = Send-Command @{ command = 'settings'; changes = @{ refresh_interval = 10000 } }
            Assert $reply.ok 'The summary fixture could not set its refresh interval'
            foreach ($paused in $true, $false) {
                $client = Connect-Pipe
                try {
                    $body = [Text.Encoding]::UTF8.GetBytes('{"request_id":101,"command":"snapshot","subscribe":true}')
                    $client.Write([BitConverter]::GetBytes([int]$body.Length), 0, 4)
                    $client.Write($body, 0, $body.Length)
                    do { $reply = Read-Frame $client } while ($reply.type -eq 'snapshot')
                    Assert ($reply.ok -and $reply.request_id -eq 101 -and $reply.data.sequence -gt 0) `
                        'Subscribing did not return a correlated snapshot'
                    $session = $reply.data.session_id
                    $watch = [Diagnostics.Stopwatch]::StartNew()
                    $reply = Send-Command @{ command = 'session_pause'; paused = $paused }
                    Assert ($reply.ok -and $reply.data.snapshot_sequence -gt 0) 'Pause did not identify its published summary'
                    $confirmed = $reply.data.snapshot_sequence
                    do {
                        $frame = Read-Frame $client
                        Assert ($frame.type -eq 'snapshot' -and $frame.ok -and $frame.data.session_id -eq $session) `
                            'The subscription received an invalid summary'
                    } while ($frame.data.sequence -lt $confirmed)
                    Assert ($frame.data.session_paused -eq $paused) 'The acknowledged summary predates the command'
                    # A ten-second timer must not be what eventually supplies command feedback.
                    Assert ($watch.Elapsed.TotalSeconds -lt 5) 'Command feedback waited for the refresh interval'
                    while ($frame.data.sequence -le $confirmed) { $frame = Read-Frame $client }
                    Assert ($frame.type -eq 'snapshot' -and $frame.ok -and $frame.data.session_paused -eq $paused) `
                        'A subsequent status push did not preserve the accepted pause state'
                    Assert ($watch.Elapsed.TotalSeconds -lt 5) 'The subsequent status push waited for the refresh interval'
                }
                finally { $client.Dispose() }
            }
        }
        'Frames' {
            $reply = Send-Command @{ command = 'registration'; action = 42 }
            Assert (-not $reply.ok -and $reply.error.code -eq 'invalid_request') 'Malformed registration terminated the download owner instead of being refused'
            $reply = Send-Command @{ command = 'snapshot' }
            Assert $reply.ok 'Malformed registration broke the subsequent valid command'
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
            Assert ($snapshot.settings.language -eq $initial.settings.language) 'Unsaved language survived as a saved setting'
        }
        'Restart' {
            $previewId = Preview
            $reply = Send-Command @{ command = 'add'; preview_id = $previewId; destination = $payload; paused = $false; sequential = $true }
            Assert $reply.ok 'Fixture addition failed'
            $torrentId = $reply.data.torrent_id
            $reply = Send-Command @{ command = 'piece_order'; torrent_ids = @($torrentId); first_last = $true }
            Assert $reply.ok 'First and last pieces choice was not saved'
            $reply = Send-Command @{ command = 'speed_limit'; torrent_ids = @($torrentId); download_limit = 51200 }
            Assert $reply.ok 'Torrent speed limit was not saved'
            $reply = Send-Command @{ command = 'pause'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Pause intent was not saved'
            $reply = Send-Command @{ command = 'session_pause'; paused = $true }
            Assert $reply.ok 'Session pause was not saved'
            $reply = Send-Command @{ command = 'settings'; changes = @{ language = 'es'; theme = 'dark' } }
            Assert $reply.ok 'Appearance settings were not saved'
            Stop-Engine
            $snapshot = Start-Engine
            Assert (@($snapshot.torrents).Count -eq 1) 'Saved torrent disappeared after restart'
            Assert ($snapshot.torrents[0].torrent_id -eq $torrentId) 'Durable identity changed after restart'
            Assert $snapshot.torrents[0].paused 'Saved pause intent was lost after restart'
            Assert ($snapshot.torrents[0].sequential -and $snapshot.torrents[0].first_last) 'Saved download order was lost after restart'
            Assert ($snapshot.torrents[0].download_limit -eq 51200 -and $snapshot.torrents[0].upload_limit -eq 0) `
                'Saved torrent speed limit was lost after restart'
            Assert $snapshot.session_paused 'Saved session pause was lost after restart'
            Assert ($snapshot.settings.language -eq 'es' -and $snapshot.settings.theme -eq 'dark') 'Saved appearance settings were lost after restart'
            $reply = Send-Command @{ command = 'session_pause'; paused = $false }
            Assert $reply.ok 'Restored session could not resume'
            $snapshot = Send-Command @{ command = 'snapshot' }
            Assert $snapshot.data.torrents[0].paused 'Session resume changed an individually paused torrent'
            $reply = Send-Command @{ command = 'resume'; torrent_ids = @($torrentId) }
            Assert $reply.ok 'Restored torrent could not resume'
            Stop-Engine
            [IO.File]::WriteAllText((Join-Path $directory ($torrentId + '.resume')), 'damaged')
            $snapshot = Start-Engine
            Assert (@($snapshot.torrents).Count -eq 1 -and $snapshot.torrents[0].torrent_id -eq $torrentId) `
                'A damaged resume file lost its torrent'
        }
        'DiskError' {
            $collision = Join-Path $payload 'transfer.bin'
            $null = New-Item -ItemType Directory -Path $collision
            $peer = Start-Process -FilePath $peerExecutable `
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
            $peer = Start-Process -FilePath $peerExecutable `
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
            $reply = Send-Command @{ command = 'settings'; changes = @{ append_suffix = $false } }
            Assert $reply.ok 'Removal fixture could not disable incomplete-file suffixes'
            $peer = Start-Process -FilePath $peerExecutable `
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
            foreach ($index in 1..4) {
                $name = 'queue' + $index + '.bin'
                $source = Join-Path $directory ($name + '.torrent')
                [IO.File]::WriteAllBytes($source, [Text.Encoding]::Latin1.GetBytes($metadata.Replace('12:transfer.bin', ($name.Length.ToString() + ':' + $name))))
                $reply = Send-Command @{ command = 'preview'; source = $source; destination = $payload }
                Assert $reply.ok 'Queue fixture did not preview'
                $addition = @{ command = 'add'; preview_id = $reply.data.preview_id; destination = $payload; paused = $true }
                if ($index -eq 4) { $addition.queue_top = $true }
                $reply = Send-Command $addition
                Assert $reply.ok 'Queue fixture addition failed'
                $ids += $reply.data.torrent_id
            }
            $expected = @($ids[3], $ids[0], $ids[1], $ids[2]) -join ','
            $until = [DateTime]::UtcNow.AddSeconds(5)
            do {
                $snapshot = Send-Command @{ command = 'snapshot' }
                $order = @($snapshot.data.torrents | Sort-Object queue | ForEach-Object torrent_id)
                if (($order -join ',') -eq $expected) { break }
                [Threading.Thread]::Sleep(50)
            } while ([DateTime]::UtcNow -lt $until)
            Assert (($order -join ',') -eq $expected) 'Add to top did not apply the chosen order'
            Stop-Engine
            $null = Start-Engine
            $reply = Send-Command @{ command = 'queue'; torrent_ids = @($ids[0]); direction = 'down' }
            Assert $reply.ok 'Move down was refused'
            $expected = @($ids[3], $ids[1], $ids[0], $ids[2]) -join ','
            $until = [DateTime]::UtcNow.AddSeconds(5)
            do {
                $snapshot = Send-Command @{ command = 'snapshot' }
                $order = @($snapshot.data.torrents | Sort-Object queue | ForEach-Object torrent_id)
                if (($order -join ',') -eq $expected) { break }
                [Threading.Thread]::Sleep(50)
            } while ([DateTime]::UtcNow -lt $until)
            Assert (($order -join ',') -eq $expected) 'Add to top or move down placed a torrent incorrectly'
            $reply = Send-Command @{ command = 'queue'; torrent_ids = @($ids[2]); before_torrent_id = $ids[0] }
            Assert $reply.ok 'Atomic row drop was refused'
            Stop-Engine
            $snapshot = Start-Engine
            $expected = @($ids[3], $ids[1], $ids[2], $ids[0]) -join ','
            $until = [DateTime]::UtcNow.AddSeconds(5)
            do {
                $order = @($snapshot.torrents | Sort-Object queue | ForEach-Object torrent_id)
                if (($order -join ',') -eq $expected) { break }
                [Threading.Thread]::Sleep(50)
                $snapshot = (Send-Command @{ command = 'snapshot' }).data
            } while ([DateTime]::UtcNow -lt $until)
            Assert (($order -join ',') -eq $expected) 'Saved queue order was lost after restart'
        }
        'WaitingMove' {
            # A Move that waits for another torrent's metadata once repeated
            # its toast every second. The engine sends toasts only to the
            # window, so a second process stands in for it. That process ends
            # before Exit, because Exit waits for the window's process.
            $windowScript = @'
param([string] $Name, [string] $Frames, [string] $Stop)
$stream = [IO.Pipes.NamedPipeClientStream]::new('.', $Name, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
$stream.Connect(10000)
function Read-Exactly([int] $count) {
    $bytes = [byte[]]::new($count)
    $offset = 0
    while ($offset -lt $count) {
        $read = $stream.ReadAsync($bytes, $offset, $count - $offset)
        while (-not $read.Wait(250)) { if (Test-Path -LiteralPath $Stop) { exit 0 } }
        if ($read.Result -eq 0) { exit 0 }
        $offset += $read.Result
    }
    return ,$bytes
}
function Read-Frame { [Text.Encoding]::UTF8.GetString((Read-Exactly ([BitConverter]::ToInt32((Read-Exactly 4), 0)))) }
$null = Read-Frame
$bytes = [Text.Encoding]::UTF8.GetBytes('{"request_id":1,"command":"ready"}')
$stream.Write([BitConverter]::GetBytes([int]$bytes.Length), 0, 4)
$stream.Write($bytes, 0, $bytes.Length)
while ($true) { Add-Content -LiteralPath $Frames -Value (Read-Frame) }
'@
            $windowFile = Join-Path $directory 'window.ps1'
            $frames = Join-Path $directory 'window.json'
            Set-Content -LiteralPath $windowFile -Value $windowScript
            $standIn = Start-Process -FilePath (Get-Process -Id $PID).Path -WindowStyle Hidden -PassThru -ArgumentList @(
                '-NoProfile', '-File', ('"' + $windowFile + '"'), '-Name', "TinyTorrent.$logon",
                '-Frames', ('"' + $frames + '"'), '-Stop', ('"' + (Join-Path $directory 'window-stop') + '"'))
            function Read-Window {
                if (-not (Test-Path -LiteralPath $frames)) { return @() }
                $stream = [IO.File]::Open($frames, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                $reader = [IO.StreamReader]::new($stream)
                try { return @($reader.ReadToEnd().Split("`n", [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_ | ConvertFrom-Json }) }
                finally { $reader.Dispose() }
            }
            $until = [DateTime]::UtcNow.AddSeconds(15)
            do {
                Start-Sleep -Milliseconds 100
                $ready = Read-Window | Where-Object request_id -eq 1
            } while (-not $ready -and [DateTime]::UtcNow -lt $until)
            Assert ($ready -and $ready.ok) 'The stand-in window could not connect'
            # Paused, so nothing reaches the network. The torrent has no data,
            # so it is not finished, which is the case that repeated.
            $reply = Send-Command @{ command = 'session_pause'; paused = $true }
            Assert $reply.ok 'The waiting-move session could not pause'
            $content = [Text.Encoding]::ASCII.GetBytes(('x' * 2048))
            $digest = [Security.Cryptography.SHA1]::HashData($content)
            $fixture = Join-Path $directory 'waiting.torrent'
            $prefix = [Text.Encoding]::ASCII.GetBytes('d4:infod6:lengthi2048e4:name11:waiting.bin12:piece lengthi16384e6:pieces20:')
            $suffix = [Text.Encoding]::ASCII.GetBytes('7:privatei1eee')
            [IO.File]::WriteAllBytes($fixture, [byte[]]($prefix + $digest + $suffix))
            $preview = Send-Command @{ command = 'preview'; source = $fixture; destination = $payload }
            Assert $preview.ok 'The waiting-move fixture did not preview'
            $reply = Send-Command @{ command = 'add'; preview_id = $preview.data.preview_id; destination = $payload; paused = $false }
            Assert $reply.ok 'The waiting-move fixture could not be added'
            $torrentId = $reply.data.torrent_id
            $preview = Send-Command @{ command = 'preview'; source = 'magnet:?xt=urn:btih:0123456789012345678901234567890123456789'; destination = $payload }
            Assert $preview.ok 'The metadata-less magnet did not preview'
            $reply = Send-Command @{ command = 'add'; preview_id = $preview.data.preview_id; destination = $payload; paused = $true }
            Assert $reply.ok 'The metadata-less magnet could not be added'
            $magnetId = $reply.data.torrent_id

            $destination = Join-Path $directory 'moved'
            $reply = Send-Command @{ command = 'move'; torrent_ids = @($torrentId); destination = $destination }
            Assert $reply.ok "A Move that waits for metadata was refused: $($reply.error.code)"
            # Twelve seconds are twelve engine ticks, and each one repeated it.
            Start-Sleep -Seconds 12
            $row = (Send-Command @{ command = 'snapshot' }).data.torrents | Where-Object torrent_id -eq $torrentId
            Assert (-not $row.complete -and $row.error -eq 'metadata_unavailable' -and $row.save_path -eq $payload) `
                "The waiting Move shows '$($row.error)' at '$($row.save_path)'"
            $toasts = @(Read-Window | Where-Object { $_.type -eq 'toast' -and $_.torrent_id -eq $torrentId })
            Assert ($toasts.Count -eq 1 -and $toasts[0].code -eq 'metadata_unavailable') "The waiting Move sent $($toasts.Count) toasts"

            $reply = Send-Command @{ command = 'remove'; torrent_ids = @($magnetId) }
            Assert $reply.ok 'The metadata-less magnet could not be removed'
            $until = [DateTime]::UtcNow.AddSeconds(20)
            do {
                Start-Sleep -Milliseconds 250
                $row = (Send-Command @{ command = 'snapshot' }).data.torrents | Where-Object torrent_id -eq $torrentId
            } while (($row.save_path -ne $destination -or $row.moving -or $row.move_destination) -and [DateTime]::UtcNow -lt $until)
            Assert ($row.save_path -eq $destination -and -not $row.moving -and -not $row.error) `
                "The waiting Move did not start: '$($row.save_path)', moving $($row.moving), error '$($row.error)'"
            $toasts = @(Read-Window | Where-Object { $_.type -eq 'toast' -and $_.torrent_id -eq $torrentId })
            Assert ($toasts.Count -eq 1) "The Move sent $($toasts.Count) toasts in total"
            $null = New-Item -ItemType File -Path (Join-Path $directory 'window-stop') -Force
            Assert ($standIn.WaitForExit(15000)) 'The stand-in window did not end'
        }
        'RemoveDuringMove' {
            # A torrent removed while its own move runs stays out of the list
            # until libtorrent releases it. Only a copy to another volume runs
            # long enough to act inside that time.
            $reply = Send-Command @{ command = 'session_pause'; paused = $true }
            Assert $reply.ok 'The removal session could not pause'
            $pieceLength = 16MB
            $pieceCount = 128
            $pieceHash = [Security.Cryptography.SHA1]::HashData([byte[]]::new($pieceLength))
            $pieces = [byte[]]::new(20 * $pieceCount)
            for ($piece = 0; $piece -lt $pieceCount; ++$piece) { [Array]::Copy($pieceHash, 0, $pieces, 20 * $piece, 20) }
            $fixture = Join-Path $directory 'removing.torrent'
            $prefix = [Text.Encoding]::ASCII.GetBytes("d4:infod6:lengthi$($pieceLength * $pieceCount)e4:name12:removing.bin12:piece lengthi$($pieceLength)e6:pieces$($pieces.Length):")
            $suffix = [Text.Encoding]::ASCII.GetBytes('7:privatei1eee')
            [IO.File]::WriteAllBytes($fixture, [byte[]]($prefix + $pieces + $suffix))
            $sourceFile = Join-Path $payload 'removing.bin'
            foreach ($command in 'delete_files', 'remove') {
                # Written, not only sized: a file sized with SetLength holds no
                # data, and its copy to another volume ends at once.
                $block = [byte[]]::new($pieceLength)
                $file = [IO.File]::Create($sourceFile)
                try { for ($piece = 0; $piece -lt $pieceCount; ++$piece) { $file.Write($block, 0, $block.Length) } } finally { $file.Dispose() }
                $originalHash = Payload-Hash $sourceFile
                $preview = Send-Command @{ command = 'preview'; source = $fixture; destination = $payload }
                Assert $preview.ok 'The removal fixture did not preview'
                $reply = Send-Command @{ command = 'add'; preview_id = $preview.data.preview_id; destination = $payload; paused = $true }
                Assert ($reply.ok -and -not $reply.data.duplicate) 'The removal fixture could not be added'
                $torrentId = $reply.data.torrent_id
                # Confirmed during the removal, as a person who opened Add
                # before removing the torrent would.
                $early = Send-Command @{ command = 'preview'; source = $fixture; destination = $payload }
                Assert $early.ok 'The second removal preview failed'
                $destination = Join-Path $volumeFolder $command
                $reply = Send-Command @{ command = 'move'; torrent_ids = @($torrentId); destination = $destination }
                Assert $reply.ok "The removal fixture could not move: $($reply.error.code)"
                $until = [DateTime]::UtcNow.AddSeconds(120)
                do {
                    Start-Sleep -Milliseconds 50
                    $row = (Send-Command @{ command = 'snapshot' }).data.torrents | Where-Object torrent_id -eq $torrentId
                    $copying = $row.moving -and $row.progress -gt 0 -and $row.progress -lt 1
                } while (-not $copying -and -not $row.error -and ($row.moving -or $row.save_path -ne $destination) -and [DateTime]::UtcNow -lt $until)
                Assert (-not $row.error) "The move failed before the removal: $($row.error) $($row.detail)"
                Assert $copying 'The check missed the copy: it ended before the removal, so nothing was tested. Use a slower volume.'

                $reply = Send-Command @{ command = $command; torrent_ids = @($torrentId); deletion = 'permanent' }
                Assert $reply.ok "$command during the move failed: $($reply.error.code)"
                $rows = @((Send-Command @{ command = 'snapshot' }).data.torrents)
                Assert ($rows.Count -eq 0) "$command during the move left the row in the list"
                $preview = Send-Command @{ command = 'preview'; source = $fixture; destination = $payload }
                Assert (-not ($preview.ok -and $preview.data.torrent_id)) 'A re-add during the removal was reported as already in the list'
                Assert (-not $preview.ok) 'The check missed the removal: the move ended before the re-add, so nothing was tested. Use a slower volume.'
                Assert ($preview.error.code -eq 'torrent_removing') "A re-add during the removal failed with '$($preview.error.code)'"
                $reply = Send-Command @{ command = 'add'; preview_id = $early.data.preview_id; destination = $payload; paused = $true }
                Assert (-not $reply.ok -and $reply.error.code -eq 'torrent_removing') "Add of a preview opened before the removal answered '$($reply.error.code)'"
                $reply = Send-Command @{ command = 'torrent'; torrent_id = $torrentId; view = 'general' }
                Assert (-not $reply.ok -and $reply.error.code -eq 'torrent_removed') 'A read of the removed torrent did not report it removed'

                $until = [DateTime]::UtcNow.AddMinutes(10)
                do {
                    Start-Sleep -Milliseconds 250
                    $preview = Send-Command @{ command = 'preview'; source = $fixture; destination = $payload }
                } while (-not $preview.ok -and $preview.error.code -eq 'torrent_removing' -and [DateTime]::UtcNow -lt $until)
                Assert ($preview.ok -and -not $preview.data.torrent_id) "The content could not be added again after its removal: '$($preview.error.code)'"
                $null = Send-Command @{ command = 'cancel_preview'; preview_id = $preview.data.preview_id }
                $movedFile = Join-Path $destination 'removing.bin'
                $kept = $command -eq 'remove'
                $until = [DateTime]::UtcNow.AddSeconds(60)
                while (((Test-Path -LiteralPath $sourceFile) -or (-not $kept -and (Test-Path -LiteralPath $movedFile))) -and [DateTime]::UtcNow -lt $until) {
                    Start-Sleep -Milliseconds 250
                }
                Assert (-not (Test-Path -LiteralPath $sourceFile)) "$command during the move left the payload in its source folder"
                if ($kept) {
                    Assert ((Payload-Hash $movedFile) -eq $originalHash) 'Remove during the move did not keep the moved payload whole'
                }
                else {
                    Assert (-not (Test-Path -LiteralPath $movedFile)) 'Delete files during the move left the payload at the destination'
                }
                Stop-Engine
                $snapshot = Start-Engine
                Assert ($snapshot.torrents.Count -eq 0) "The torrent removed during its move returned after a restart"
                $reply = Send-Command @{ command = 'session_pause'; paused = $true }
                Assert $reply.ok 'The removal session could not pause again'
            }
        }
    }
    Stop-Engine
    if ($stalled) { $stalled.Dispose(); $stalled = $null }
    [pscustomobject]@{ Check = $Check; Passed = $true; Evidence = $directory } | ConvertTo-Json
}
finally {
    if ($heldFile) { $heldFile.Dispose() }
    if ($peer -and -not $peer.HasExited) {
        $null = New-Item -ItemType File -Path (Join-Path $peerDirectory 'stop') -Force
        if (-not $peer.WaitForExit(15000)) { $peer.Kill(); $peer.WaitForExit() }
    }
    if ($stalled) { $stalled.Dispose() }
    if ($standIn -and -not $standIn.HasExited) {
        $null = New-Item -ItemType File -Path (Join-Path $directory 'window-stop') -Force
        if (-not $standIn.WaitForExit(15000)) { $standIn.Kill(); $standIn.WaitForExit() }
    }
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
    if ($volumeFolder) { Remove-Item -LiteralPath $volumeFolder -Recurse -Force -ErrorAction Continue }
    if ($networkFolder) { Remove-Item -LiteralPath $networkFolder -Recurse -Force -ErrorAction Continue }
    foreach ($other in $others) { Remove-Item -LiteralPath $other -Recurse -Force -ErrorAction Continue }
    # A run keeps only its logs and reports. Its payload, peer seed and engine
    # state are worthless once the check has ended.
    $evidence = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts/evidence')) + [IO.Path]::DirectorySeparatorChar
    $cleanup = [IO.Path]::GetFullPath($directory)
    if (-not $cleanup.StartsWith($evidence, [StringComparison]::OrdinalIgnoreCase)) { throw 'Check cleanup escaped its evidence directory' }
    foreach ($entry in Get-ChildItem -LiteralPath $cleanup | Where-Object { $_.PSIsContainer -or $_.Extension -notin '.log', '.json' }) {
        Remove-Item -LiteralPath $entry.FullName -Recurse -Force -ErrorAction Continue
    }
}
