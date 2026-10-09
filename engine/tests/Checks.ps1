param(
    [Parameter(Mandatory)]
    [ValidateSet('Frames', 'Targets', 'Facts', 'Restore', 'Exit', 'FailedCommit', 'CheckpointRetry', 'Restart', 'DiskError', 'PreviewGuard', 'RemoveKeepFiles', 'QueueOrder', 'SelectedTransfer', 'MagnetDownload', 'SettingsPolicy', 'CommittedFiles', 'FilesSafety', 'FileNames', 'Pause', 'History')]
    [string] $Check,
    [Parameter(Mandatory)]
    [string] $TorrentFile,
    [string] $EnginePath,
    [switch] $Transfer
)

$ErrorActionPreference = 'Stop'
# These checks start the Transfer peer and download real payload, which takes
# minutes of the owner's machine, so they run only when the owner asks.
$transferChecks = 'MagnetDownload', 'SelectedTransfer', 'DiskError', 'PreviewGuard', 'RemoveKeepFiles'
if ($Check -in $transferChecks -and -not $Transfer) {
    throw "$Check runs a real transfer. Run it only when the owner asks, with -Transfer."
}
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$engineName = ([xml](Get-Content (Join-Path $repository 'Directory.Build.props'))).Project.PropertyGroup.EngineTargetName | Where-Object { $_ }
$configuration = if ($Check -eq 'Pause') { 'Debug' } else { 'Release' }
$executable = Join-Path $repository "artifacts/bin/Engine/$configuration/$engineName.exe"
if ($EnginePath) { $executable = [IO.Path]::GetFullPath($EnginePath) }
$directory = Join-Path $repository ('artifacts/evidence/' + $Check + '-' + [guid]::NewGuid())
$null = New-Item -ItemType Directory -Path $directory
$payload = Join-Path $directory 'payload'
$null = New-Item -ItemType Directory -Path $payload
. (Join-Path $PSScriptRoot 'Engine.ps1')
$stalled = $null
$heldFile = $null
$peer = $null
$peerDirectory = Join-Path $directory 'peer'

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

try {
    $initial = Start-Engine
    switch ($Check) {
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
                    $until = [DateTime]::UtcNow.AddSeconds(15)
                    do {
                        $reply = Send-Command @{ command = 'verify'; torrent_ids = $identities }
                        if ($reply.ok) { break }
                        Assert ($reply.error.code -eq 'files_busy') 'The copied-in fixture bytes could not be verified'
                        Start-Sleep -Milliseconds 100
                    } while ([DateTime]::UtcNow -lt $until)
                    Assert $reply.ok 'Restored filename preparation did not admit verification'
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
            Assert $reply.ok 'The final-name collision preflight was not accepted'
            $until = [DateTime]::UtcNow.AddSeconds(15)
            do {
                $rows = (Send-Command @{ command = 'snapshot' }).data.torrents
                if (@($rows | Where-Object { $_.moving -or $_.error -ne 'destination_exists' }).Count -eq 0) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $until)
            Assert (@($rows | Where-Object { $_.moving -or $_.error -ne 'destination_exists' -or $_.save_path -ne $empty }).Count -eq 0) 'An unfinished suffix concealed a collision with the final filename'
            Assert ((Payload-Hash $finished) -eq $originalHash) 'Collision preflight changed the existing completed bytes'
            $reply = Send-Command @{ command = 'move'; torrent_ids = $identities; destination = $payload; use_existing = $true }
            Assert $reply.ok 'Explicit use of existing completed files was refused'
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
            } while (@($rows | Where-Object { $_.moving -or $_.move_destination }).Count -gt 0 -and [DateTime]::UtcNow -lt $until)
            $movedFile = Join-Path $destination 'shared.bin'
            Assert (@($rows | Where-Object { $_.save_path -ne $destination -or $_.moving -or $_.move_destination }).Count -eq 0 -and (Payload-Hash $movedFile) -eq $originalHash) 'The cross-seeded group did not establish one destination'
            Assert (-not (Test-Path -LiteralPath $sourceFile) -and (Test-Path -LiteralPath $unrelated)) 'Move affected unrelated files or left its payload behind'
            Stop-Engine
            $settingsFile = Join-Path $directory 'settings.json'
            $saved = [IO.File]::ReadAllText($settingsFile) | ConvertFrom-Json
            foreach ($torrent in $saved.torrents) { $torrent.move_destination = $collision }
            [IO.File]::WriteAllBytes($settingsFile, [Text.Encoding]::UTF8.GetBytes(($saved | ConvertTo-Json -Depth 30 -Compress)))
            $snapshot = Start-Engine
            Assert (@($snapshot.torrents | Where-Object error -ne 'move_interrupted').Count -eq 0) 'An interrupted move resumed as ordinary transfer state'
            $reply = Send-Command @{ command = 'move'; torrent_ids = $identities; destination = $destination }
            Assert (-not $reply.ok -and $reply.error.code -eq 'move_interrupted') 'An ordinary retry erased the unresolved move destination'
            $reply = Send-Command @{ command = 'move'; torrent_ids = $identities; destination = $destination; use_existing = $true }
            Assert $reply.ok 'Explicit recovery could not use the files at their known folder'
            $until = [DateTime]::UtcNow.AddSeconds(15)
            do {
                Start-Sleep -Milliseconds 100
                $rows = (Send-Command @{ command = 'snapshot' }).data.torrents
            } while (@($rows | Where-Object { $_.moving -or $_.move_destination }).Count -gt 0 -and [DateTime]::UtcNow -lt $until)
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
            Assert ($initial.settings.notify_problems -eq $true -and $initial.settings.notifications_enabled -eq $false -and $initial.settings.notify_added -eq $false) 'Fresh notification settings do not keep successes quiet and problems visible'
            Assert ($initial.settings.encryption -eq 'preferred' -and $initial.settings.proxy_type -eq 'none') 'Fresh network settings do not prefer encryption without a proxy'
            $previewId = Preview
            $reply = Send-Command @{ command = 'add'; preview_id = $previewId; destination = $payload; paused = $true }
            Assert $reply.ok 'Settings policy fixture addition failed'
            $torrentId = $reply.data.torrent_id
            $period = @{ days = @(0, 1, 2, 3, 4, 5, 6); start = 0; end = 0; mode = 'paused' }
            $reply = Send-Command @{ command = 'settings'; changes = @{ schedule_enabled = $true; schedule = @($period); check_for_updates = $false; active_downloads = 1; port_mapping = $false; notify_problems = $false; notifications_enabled = $true; notify_added = $true } }
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
                $check = (Send-Command @{ command = 'snapshot' }).data.proxy_check
            } while ($check.check_id -eq $checkId -and -not $check.outcome -and [DateTime]::UtcNow -lt $until)
            Assert ($check.check_id -eq $checkId -and $check.outcome -eq 'unreachable') 'Checking a proxy where nothing listens did not report it unreachable'
            Stop-Engine
            $snapshot = Start-Engine
            Assert ($snapshot.settings.schedule.Count -eq 1 -and $snapshot.settings.schedule[0].mode -eq 'alternative') 'A committed weekly period was lost at restart'
            Assert ($snapshot.settings.active_downloads -eq 1 -and -not $snapshot.settings.check_for_updates -and -not $snapshot.settings.port_mapping) 'Committed settings were lost at restart'
            Assert ($snapshot.settings.notify_problems -eq $false -and $snapshot.settings.notifications_enabled -eq $true -and $snapshot.settings.notify_added -eq $true) 'Notification choices were lost at restart'
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
            $peer = Start-Process -FilePath (Join-Path $repository 'artifacts/bin/Transfer/Release/Transfer.exe') `
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
    # A run keeps only its logs and reports. Its payload, peer seed and engine
    # state are worthless once the check has ended.
    $evidence = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts/evidence')) + [IO.Path]::DirectorySeparatorChar
    $cleanup = [IO.Path]::GetFullPath($directory)
    if (-not $cleanup.StartsWith($evidence, [StringComparison]::OrdinalIgnoreCase)) { throw 'Check cleanup escaped its evidence directory' }
    foreach ($entry in Get-ChildItem -LiteralPath $cleanup | Where-Object { $_.PSIsContainer -or $_.Extension -notin '.log', '.json' }) {
        Remove-Item -LiteralPath $entry.FullName -Recurse -Force -ErrorAction Continue
    }
}
