param([Parameter(Mandatory)][string] $EnginePath)

$ErrorActionPreference = 'Stop'
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$executable = (Resolve-Path -LiteralPath $EnginePath).Path
$evidence = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts/evidence'))
$run = Join-Path $evidence ('Catalogue-' + [guid]::NewGuid())
$directory = Join-Path $run 'engine'
$payload = Join-Path $run 'payload'
$fixtures = Join-Path $run 'fixtures'
. (Join-Path $PSScriptRoot 'Engine.ps1')
$null = New-Item -ItemType Directory -Path $directory, $payload, $fixtures

function Write-Bencode([IO.Stream] $stream, [object] $value) {
    if ($value -is [string] -or $value -is [byte[]]) {
        if ($value -is [byte[]]) { [byte[]]$bytes = $value }
        else { [byte[]]$bytes = [Text.Encoding]::UTF8.GetBytes($value) }
        $prefix = [Text.Encoding]::ASCII.GetBytes("$($bytes.Length):")
        $stream.Write($prefix, 0, $prefix.Length)
        $stream.Write([byte[]]$bytes, 0, $bytes.Length)
    }
    elseif ($value -is [Collections.IDictionary]) {
        $stream.WriteByte(100)
        $keys = [string[]]@($value.Keys)
        [Array]::Sort($keys, [StringComparer]::Ordinal)
        foreach ($key in $keys) { Write-Bencode $stream $key; Write-Bencode $stream $value[$key] }
        $stream.WriteByte(101)
    }
    elseif ($value -is [Collections.IEnumerable]) {
        $stream.WriteByte(108)
        foreach ($item in $value) { Write-Bencode $stream $item }
        $stream.WriteByte(101)
    }
    else {
        $integer = [Text.Encoding]::ASCII.GetBytes("i$([long]$value)e")
        $stream.Write($integer, 0, $integer.Length)
    }
}

function Encode([object] $value) {
    $stream = [IO.MemoryStream]::new()
    try { Write-Bencode $stream $value; return ,$stream.ToArray() }
    finally { $stream.Dispose() }
}

function Fixture([string] $name, [hashtable] $info, [string] $kind = 'v1') {
    $bytes = Encode $info
    $path = Join-Path $fixtures "$name.torrent"
    [IO.File]::WriteAllBytes($path, (Encode @{ info = $info }))
    [pscustomobject]@{
        Path = $path
        V1 = if ($kind -ne 'v2') { [Convert]::ToHexString([Security.Cryptography.SHA1]::HashData($bytes)).ToLowerInvariant() } else { $null }
        V2 = if ($kind -ne 'v1') { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() } else { $null }
        Hash = if ($kind -eq 'v1') { [Convert]::ToHexString([Security.Cryptography.SHA1]::HashData($bytes)).ToLowerInvariant() }
            else { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() }
    }
}

function Command([hashtable] $fields) {
    $reply = Send-Command $fields
    Assert $reply.ok "Command '$($fields.command)' failed: $($reply.error | ConvertTo-Json -Compress)"
    return $reply.data
}

function Wait-Catalogue([scriptblock] $condition, [string] $failure) {
    $until = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $status = Command @{ command = 'catalogue_status' }
        Assert (-not $status.error) "Indexing failed: $($status.error)"
        if (& $condition $status) { return $status }
        [Threading.Thread]::Yield() | Out-Null
    } while ([DateTime]::UtcNow -lt $until)
    throw $failure
}

function Add-Fixture($fixture) {
    $preview = Command @{ command = 'preview'; source = $fixture.Path; destination = $payload }
    $added = Command @{ command = 'add'; preview_id = $preview.preview_id; destination = $payload; paused = $true }
    return $added.torrent_id
}

function Check-Metadata($fixture, [int] $count, [int] $index, [string] $name, [string] $root = '') {
    $entry = Command @{ command = 'catalogue_detail'; hash = $fixture.Hash; limit = 200 }
    Assert ($entry.torrent.v1 -eq $fixture.V1 -and $entry.torrent.v2 -eq $fixture.V2) 'Saved metadata changed its verified torrent identities'
    Assert ($entry.files.Count -eq $count -and -not $entry.more) `
        "Saved metadata lost original file entries for '$($fixture.Path)' ($($fixture.Hash)): expected $count, actual $($entry.files.Count), more=$($entry.more); response=$($entry | ConvertTo-Json -Compress -Depth 8)"
    $file = @($entry.files | Where-Object index -eq $index)
    Assert ($file.Count -eq 1 -and $file[0].name -eq $name -and $file[0].size -eq 1 -and $file[0].root -eq $root) `
        'Saved metadata changed an original file index, name, size or v2 root'
    Assert ($null -eq $entry.torrent.seen) 'Local transfer ingestion fabricated a DHT sighting'
}

function Set-Age([string] $sql) {
    Assert ($null -eq $script:process) 'Fixture ages can change only while the disposable engine is stopped'
    [CatalogueFixture]::Execute((Join-Path $directory 'index.db'), $sql)
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CatalogueFixture {
    public static void Load(string path) {
        NativeLibrary.SetDllImportResolver(typeof(CatalogueFixture).Assembly,
            (name, assembly, search) => name == "e_sqlite3" ? NativeLibrary.Load(path) : IntPtr.Zero);
    }
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr argument, IntPtr error);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr sqlite3_errmsg(IntPtr database);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    static extern int sqlite3_close_v2(IntPtr database);
    public static void Execute(string path, string sql) {
        var code = sqlite3_open_v2(path, out var database, 2, IntPtr.Zero);
        try {
            if (code == 0) code = sqlite3_exec(database, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (code != 0) throw new InvalidOperationException(Marshal.PtrToStringUTF8(sqlite3_errmsg(database)));
        } finally { if (database != IntPtr.Zero) sqlite3_close_v2(database); }
    }
}
'@

$clock = [Diagnostics.Stopwatch]::StartNew()
try {
    [CatalogueFixture]::Load((Resolve-Path -LiteralPath (Join-Path (Split-Path $executable -Parent) 'e_sqlite3.dll')).Path)
    $digest = [Security.Cryptography.SHA1]::HashData([byte[]]@(0))
    $rootBytes = [Security.Cryptography.SHA256]::HashData([byte[]]@(0))
    $root = [Convert]::ToHexString($rootBytes).ToLowerInvariant()
    $v1 = Fixture 'v1' @{
        name = 'v1-root'; 'piece length' = 16384; pieces = [byte[]]::new(40); private = 1
        files = @(@{ length = 1; path = @('first.bin') },
            @{ attr = 'p'; length = 16383; path = @('.pad', '16383') },
            @{ length = 1; path = @('manual.pdf') })
    }
    $v2 = Fixture 'v2' @{
        name = 'v2-root'; 'piece length' = 16384; 'meta version' = 2; private = 1
        'file tree' = @{ 'guide.pdf' = @{ '' = @{ length = 1; 'pieces root' = $rootBytes } } }
    } 'v2'
    $hybrid = Fixture 'hybrid' @{
        name = 'hybrid-root'; 'piece length' = 16384; 'meta version' = 2; private = 1; pieces = $digest
        files = @(@{ length = 1; path = @('manual.pdf') })
        'file tree' = @{ 'manual.pdf' = @{ '' = @{ length = 1; 'pieces root' = $rootBytes } } }
    } 'hybrid'
    $excluded = Fixture 'excluded' @{ name = 'excluded-root'; 'piece length' = 16384; pieces = $digest; private = 1; files = @(@{ length = 1; path = @('sample.tmp') }) }
    $both = Fixture 'both' @{ name = 'both-root'; 'piece length' = 16384; pieces = $digest; private = 1; files = @(@{ length = 1; path = @('sample.tmp') }) }
    $ruleA = @{ rule_id = 'temporary'; name = 'Temporary files'; extension = @{ value = 'tmp'; exclude = $false } }
    $ruleB = @{ rule_id = 'both'; name = 'Both'; title = @{ value = 'both-root'; exclude = $false } }

    $initial = @{ format = 1; torrents = @(); settings = @{ dht = $false; pex = $false; lsd = $false; port_mapping = $false; indexing = $false; confirm_exit = $false } } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllBytes((Join-Path $directory 'settings.json'), [Text.Encoding]::UTF8.GetBytes($initial))
    $null = Start-Engine
    $null = Command @{ command = 'catalogue_rules'; action = 'save'; rules = @($ruleA, $ruleB) }
    $v1Id = Add-Fixture $v1
    $null = Add-Fixture $v2
    $null = Add-Fixture $hybrid
    $null = Add-Fixture $excluded
    $null = Add-Fixture $both
    Assert (-not (Command @{ command = 'catalogue_status' }).exists) 'Indexing-off addition created the catalogue'
    $null = Command @{ command = 'settings'; changes = @{ indexing = $true } }
    $searchStatus = Wait-Catalogue { param($status) $status.torrents -eq 3 -and $status.files -eq 4 -and $status.rejected -eq 2 -and -not $status.projecting } `
        'Existing transfer metadata did not populate the catalogue with DHT disabled'
    $null = Command @{ command = 'settings'; changes = @{ indexing = $false } }
    Check-Metadata $v1 3 2 'manual.pdf'
    Check-Metadata $v2 2 0 'guide.pdf' $root
    Check-Metadata $hybrid 1 0 'manual.pdf' $root

    $firstPageClock = [Diagnostics.Stopwatch]::StartNew()
    $page = Command @{ command = 'catalogue_query'; target = 'files'; text = 'torrent:"v1-root" ext:pdf' }
    $firstPageClock.Stop()
    Assert ($page.hits.Count -eq 1 -and $page.hits[0].preferred.hash -eq $v1.Hash -and $page.hits[0].preferred.file.index -eq 2) `
        'File search lost the parent restriction or the original index across hidden padding'
    $scope = @{ text = 'torrent:"v1-root" ext:pdf'; cursor = @{ entry = 0; file = -1; watermark = $page.watermark; generation = $page.generation } }
    $limited = Command @{ command = 'catalogue_query'; target = 'files'; limit = 1 }
    Assert ($limited.hits.Count -eq 1 -and $limited.limited -and -not $limited.exhaustive -and $null -ne $limited.next) `
        'A bounded result claimed exhaustive catalogue coverage'
    $descending = Command @{ command = 'catalogue_query'; target = 'files'; sort = 'name'; descending = $true; limit = 1 }
    Assert ($descending.hits.Count -eq 1 -and $descending.hits[0].matched.file.name -eq 'manual.pdf') `
        'Name sorting limited the earliest file-ID batch before ordering the catalogue'
    $continued = Command @{ command = 'catalogue_query'; target = 'files'; sort = 'name'; descending = $true; limit = 1; cursor = $descending.next }
    Assert ($continued.hits.Count -eq 1 -and $continued.hits[0].matched.file.name -eq 'guide.pdf') `
        'Global sort continuation repeated or skipped the equal-content group'
    $titles = Command @{ command = 'catalogue_query'; target = 'torrents'; text = 'v1-root'; size = @{ minimum = 100 } }
    Assert ($titles.hits.Count -eq 1 -and $titles.hits[0].matched.hash -eq $v1.Hash -and $titles.hits[0].matched.size -eq 16385) `
        'Torrent search applied filename candidates or individual file sizes to the torrent title and total size'
    foreach ($query in @('*gui*', 'guide|manual ext:pdf', '!guide ext:pdf')) {
        $matches = Command @{ command = 'catalogue_query'; target = 'files'; text = $query }
        $expected = if ($query -eq '*gui*') { 1 } else { 2 }
        Assert ($matches.hits.Count -eq $expected) 'Trigram candidates changed wildcard, alternative or exclusion matching'
    }

    $null = Command @{ command = 'remove'; torrent_ids = @($v1Id) }
    $prepared = Command @{ command = 'catalogue_prepare'; hash = $v1.Hash; index = 2; length = 1; default_priority = 0; choices = @(@{ index = 2; priority = 4 }); destination = $payload; scope = $scope }
    Assert ($prepared.selection.default_priority -eq 0 -and $prepared.selection.choices.Count -eq 1 -and $prepared.selection.choices[0].index -eq 2) `
        'Catalogue preparation changed the selected source occurrence'
    $added = Command @{ command = 'add'; preview_id = $prepared.preview_id; destination = $payload; paused = $true; default_priority = 0; choices = @(@{ index = 2; priority = 4 }) }
    $files = Command @{ command = 'torrent'; torrent_id = $added.torrent_id; view = 'files' }
    Assert (@($files.files | Where-Object { $_.index -eq 2 -and $_.priority -eq 4 }).Count -eq 1 -and
        @($files.files | Where-Object { $_.index -ne 2 -and $_.priority -ne 0 }).Count -eq 0) `
        'Existing Add applied compact choices to the wrong original file'

    Stop-Engine
    $legacy = Join-Path $fixtures 'version-1.db'
    [IO.File]::WriteAllBytes($legacy, [byte[]]::new(0))
    $current = (Join-Path $directory 'index.db').Replace("'", "''")
    [CatalogueFixture]::Execute($legacy, @"
ATTACH DATABASE '$current' AS current;
BEGIN;
CREATE TABLE catalogue(catalogue_id INTEGER PRIMARY KEY AUTOINCREMENT,v1 TEXT UNIQUE,v2 TEXT UNIQUE,
 name TEXT NOT NULL,size INTEGER NOT NULL,count INTEGER NOT NULL,admitted INTEGER NOT NULL,seen INTEGER,
 selectable_count INTEGER NOT NULL DEFAULT 0,projected INTEGER NOT NULL DEFAULT 0,published INTEGER UNIQUE);
CREATE TABLE metadata(catalogue_id INTEGER PRIMARY KEY REFERENCES catalogue ON DELETE CASCADE,
 info BLOB NOT NULL,metainfo BLOB NOT NULL,resume BLOB NOT NULL,extras TEXT NOT NULL);
CREATE TABLE files(catalogue_id INTEGER NOT NULL REFERENCES catalogue ON DELETE CASCADE,file_index INTEGER NOT NULL,
 name TEXT NOT NULL,path TEXT NOT NULL,size INTEGER NOT NULL,padding INTEGER NOT NULL,symlink INTEGER NOT NULL,
 root TEXT,extension TEXT NOT NULL,PRIMARY KEY(catalogue_id,file_index)) WITHOUT ROWID;
CREATE INDEX files_root ON files(root,size) WHERE root IS NOT NULL;
CREATE INDEX files_extension ON files(extension,catalogue_id,file_index);
CREATE INDEX catalogue_seen ON catalogue(seen,admitted);
CREATE INDEX catalogue_published ON catalogue(published,catalogue_id);
CREATE TABLE lookups(lookup TEXT PRIMARY KEY,catalogue_id INTEGER NOT NULL REFERENCES catalogue ON DELETE CASCADE) WITHOUT ROWID;
CREATE INDEX lookup_catalogue ON lookups(catalogue_id);
CREATE TABLE pending(lookup TEXT PRIMARY KEY,eligible INTEGER NOT NULL,failures INTEGER NOT NULL DEFAULT 0,seen INTEGER NOT NULL) WITHOUT ROWID;
CREATE INDEX pending_due ON pending(eligible);
CREATE TABLE rejections(lookup TEXT PRIMARY KEY,identity TEXT NOT NULL,decided INTEGER NOT NULL) WITHOUT ROWID;
CREATE INDEX rejections_identity ON rejections(identity);
CREATE TABLE reasons(lookup TEXT NOT NULL REFERENCES rejections ON DELETE CASCADE,fingerprint TEXT NOT NULL,reason TEXT NOT NULL,
 PRIMARY KEY(lookup,fingerprint)) WITHOUT ROWID;
CREATE TABLE nodes(address TEXT NOT NULL,port INTEGER NOT NULL,node_id TEXT NOT NULL,eligible INTEGER NOT NULL,
 PRIMARY KEY(address,port)) WITHOUT ROWID;
CREATE INDEX nodes_due ON nodes(eligible);
CREATE TABLE observations(catalogue_id INTEGER PRIMARY KEY REFERENCES catalogue ON DELETE CASCADE,
 seeds INTEGER,peers INTEGER,observed INTEGER NOT NULL,responses INTEGER NOT NULL);
CREATE TABLE state(state_id INTEGER PRIMARY KEY,generation INTEGER NOT NULL,publication INTEGER NOT NULL,
 rebuild_cursor INTEGER,rebuild_end INTEGER,rebuild_entry INTEGER,rebuild_active INTEGER NOT NULL DEFAULT 0);
INSERT INTO catalogue SELECT catalogue_id,v1,v2,name,size,count,admitted,seen,selectable_count,projected,published FROM current.catalogue;
INSERT INTO metadata SELECT * FROM current.metadata;
INSERT INTO files SELECT catalogue_id,file_index,name,path,size,padding,symlink,root,extension FROM current.files;
INSERT INTO lookups SELECT * FROM current.lookups;
INSERT INTO pending SELECT lookup,eligible,failures,seen FROM current.pending;
INSERT INTO rejections SELECT * FROM current.rejections;
INSERT INTO reasons SELECT * FROM current.reasons;
INSERT INTO nodes SELECT * FROM current.nodes;
INSERT INTO observations SELECT catalogue_id,seeds,peers,observed,responses FROM current.catalogue WHERE observed>0;
INSERT INTO state SELECT state_id,generation,publication,rebuild_cursor,rebuild_end,rebuild_entry,rebuild_active FROM current.state;
PRAGMA user_version=1;
COMMIT;
DETACH DATABASE current;
"@)
    Copy-Item -LiteralPath $legacy -Destination (Join-Path $directory 'index.db') -Force
    $null = Start-Engine
    $status = Command @{ command = 'catalogue_status' }
    Assert (-not $status.enabled -and $status.torrents -eq 3 -and $status.files -eq 4 -and $status.rejected -eq 2) `
        'Version-1 catalogue migration lost retained metadata, counters or remembered exclusions'
    $null = Command @{ command = 'catalogue_rebuild' }
    $null = Wait-Catalogue { param($status) -not $status.rebuilding -and -not $status.projecting -and $status.files -eq 4 } 'Offline search rebuild did not finish'
    Check-Metadata $v1 3 2 'manual.pdf'
    Check-Metadata $v2 2 0 'guide.pdf' $root
    Check-Metadata $hybrid 1 0 'manual.pdf' $root

    $stale = Send-Command @{ command = 'catalogue_prepare'; hash = $v1.Hash; index = 2; length = 1; scope = $scope }
    Assert (-not $stale.ok) 'Catalogue preparation accepted a search scope invalidated by rebuilding'
    $blocked = Send-Command @{ command = 'catalogue_prepare'; hash = $excluded.Hash }
    Assert (-not $blocked.ok) 'Rejected torrent metadata remained available for Add'
    $preview = Command @{ command = 'catalogue_rules'; action = 'preview'; rules = @($ruleB) }
    Assert ($preview.suppressed -eq 2 -and $preview.retained -eq 1 -and $preview.released -eq 1) `
        'Removing a rule failed to distinguish released bans from another recorded reason'
    $null = Command @{ command = 'catalogue_rules'; action = 'save'; rules = @($ruleB) }
    Stop-Engine
    $null = Start-Engine
    Assert ((Command @{ command = 'catalogue_status' }).rejected -eq 1) 'A still-applicable exclusion was lost across restart'
    $null = Command @{ command = 'catalogue_rules'; action = 'save'; rules = @() }
    $null = Command @{ command = 'settings'; changes = @{ indexing = $true } }
    $null = Wait-Catalogue { param($status) $status.torrents -eq 5 -and $status.rejected -eq 0 -and -not $status.projecting } `
        'Lifting exclusions did not let already-available metadata enter the catalogue again'

    foreach ($number in 1..12) {
        $name = if ($number -eq 12) { 'Éléphant-12' } else { "extra-$number" }
        $fixture = Fixture "extra-$number" @{ name = $name; 'piece length' = 16384; pieces = $digest; private = 1; length = $number }
        $null = Add-Fixture $fixture
    }
    $null = Wait-Catalogue { param($status) $status.torrents -eq 17 -and -not $status.projecting } 'Additional metadata did not enter the catalogue'
    $largest = Command @{ command = 'catalogue_query'; target = 'files'; sort = 'size'; descending = $true; limit = 1 }
    Assert ($largest.hits.Count -eq 1 -and $largest.hits[0].matched.file.size -eq 12) `
        'Size sorting returned an early smaller file instead of the global largest match'
    $unicode = Command @{ command = 'catalogue_query'; target = 'files'; text = 'ÉLÉPHANT' }
    Assert ($unicode.hits.Count -eq 1 -and $unicode.hits[0].matched.file.name -eq 'Éléphant-12') `
        'Trigram search lost the canonical Unicode case-folding semantics'
    $null = Command @{ command = 'settings'; changes = @{ indexing = $false } }
    $removal = Command @{ command = 'catalogue_preview'; kind = 'clear' }
    Assert $removal.more 'A bounded removal preview incorrectly claimed to review every torrent in one batch'
    $token = $removal.token
    $incomplete = Send-Command @{ command = 'catalogue_remove'; token = $token }
    Assert (-not $incomplete.ok) 'Incomplete removal preview permitted deletion'
    $until = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $removal = Command @{ command = 'catalogue_preview'; kind = 'clear' }
        Assert ($removal.token -eq $token) 'Removal preview lost its scope between batches'
    } while ($removal.more -and [DateTime]::UtcNow -lt $until)
    Assert (-not $removal.more) 'Removal preview did not finish its bounded continuations'
    $sample = Command @{ command = 'catalogue_removal_rows'; token = $token }
    Assert ($removal.torrents -eq 17 -and $sample.entries.Count -eq 17 -and -not $sample.more) `
        'Removal preview lost reviewed torrents across bounded continuations'

    $old = Fixture 'old' @{ name = 'old'; 'piece length' = 16384; pieces = $digest; private = 1; length = 1; padding = [byte[]]::new(2MB) }
    $fresh = Fixture 'fresh' @{ name = 'fresh'; 'piece length' = 16384; pieces = $digest; private = 1; length = 1; padding = [byte[]]::new(2MB) }
    $next = Fixture 'next' @{ name = 'next'; 'piece length' = 16384; pieces = $digest; private = 1; length = 1; padding = [byte[]]::new(2MB) }
    $null = Add-Fixture $old
    $null = Add-Fixture $fresh
    $null = Command @{ command = 'settings'; changes = @{ indexing = $true } }
    $null = Wait-Catalogue { param($status) $status.torrents -eq 19 -and -not $status.projecting } 'Storage fixtures did not enter the catalogue'
    $null = Command @{ command = 'settings'; changes = @{ indexing = $false; index_budget_mib = 10; index_stale_hours = 1 } }
    $transfers = (Command @{ command = 'snapshot' }).torrents.torrent_id
    $null = Command @{ command = 'remove'; torrent_ids = @($transfers) }
    Stop-Engine
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $age = $now - 7200
    Set-Age "UPDATE catalogue SET admitted=$age,seen=NULL; UPDATE catalogue SET admitted=$($age - 1) WHERE v1='$($old.Hash)'; UPDATE catalogue SET seen=$now WHERE v1='$($fresh.Hash)';"
    $null = Start-Engine
    $null = Command @{ command = 'settings'; changes = @{ indexing = $true } }
    $status = Wait-Catalogue { param($status) $status.torrents -eq 18 -and $status.reusable -gt 2MB } 'Storage cleanup did not evict the oldest stale metadata'
    Assert ($status.allocated -gt 10MB -and $status.allocated - $status.reusable -lt 10MB) `
        'Freed SQLite pages did not satisfy the storage budget before physical shrinking'
    $gone = Send-Command @{ command = 'catalogue_detail'; hash = $old.Hash }
    Assert (-not $gone.ok) 'Storage cleanup retained the oldest stale torrent'
    $entry = Command @{ command = 'catalogue_detail'; hash = $fresh.Hash }
    Assert ($entry.torrent.seen -eq $now) 'Storage cleanup removed or changed a recently seen torrent'
    $null = Command @{ command = 'catalogue_detail'; hash = $v1.Hash }

    $null = Command @{ command = 'settings'; changes = @{ indexing = $false; index_budget_mib = 1 } }
    Stop-Engine
    Set-Age "UPDATE catalogue SET seen=$now;"
    $null = Start-Engine
    $null = Command @{ command = 'settings'; changes = @{ indexing = $true } }
    $null = Add-Fixture $next
    $status = Wait-Catalogue { param($status) $status.torrents -eq 19 -and -not $status.projecting } `
        'Collection stopped at its budget even though no stale metadata was eligible for removal'
    Assert ($status.enabled -and $status.allocated - $status.reusable -gt 1MB) `
        'A storage budget became a strict collection cap'
    Stop-Engine
    [pscustomobject]@{
        Check = 'Catalogue'
        Passed = $true
        Seconds = [math]::Round($clock.Elapsed.TotalSeconds, 2)
        FirstPageMilliseconds = [math]::Round($firstPageClock.Elapsed.TotalMilliseconds, 2)
        FirstPageTorrentCount = $searchStatus.torrents
        FirstPageFiles = $searchStatus.files
        TorrentCount = $status.torrents
        Files = $status.files
        AllocatedBytes = $status.allocated
        ReusableBytes = $status.reusable
    } | ConvertTo-Json
}
finally {
    if ($script:process -and -not $script:process.HasExited) {
        try { Stop-Engine }
        catch {
            Write-Warning ('Forced cleanup of failed check engine PID ' + $script:process.Id)
            $script:process.Kill()
            $script:process.WaitForExit()
        }
    }
    if ($script:pipe) { $script:pipe.Dispose() }
    $cleanup = [IO.Path]::GetFullPath($run)
    $parent = $evidence.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $cleanup.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Catalogue check cleanup escaped its evidence directory' }
    Remove-Item -LiteralPath $cleanup -Recurse -Force -ErrorAction Continue
}
