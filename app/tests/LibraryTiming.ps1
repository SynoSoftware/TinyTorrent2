param(
    [string] $BinaryDirectory = "$PSScriptRoot/../../artifacts/bin/TinyTorrent/release_win-x64",
    [int] $Samples = 20,
    [switch] $Profile
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/SourceChecks.ps1" -BinaryDirectory $BinaryDirectory
$database = New-Value 'Services.Database' @($directory, $schema, $projection)
$store = New-Value 'Library.Store' @($database)
$measurements = [Collections.Generic.List[object]]::new()
$collections = [Collections.Generic.List[object]]::new()
if ($Profile) {
    Add-Type -ReferencedAssemblies @(
        (Join-Path $binary 'Microsoft.Data.Sqlite.dll'),
        (Join-Path $binary 'SQLitePCLRaw.core.dll'),
        (Join-Path $PSHOME 'ref/System.Collections.dll'),
        (Join-Path $PSHOME 'ref/System.Runtime.dll')
    ) -CompilerOptions '/nowarn:1701' -TypeDefinition @'
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using SQLitePCL;

public static class SqlProfile
{
    public static readonly Dictionary<string, long> Times = new();
    private static readonly strdelegate_profile Collect = (_, sql, nanoseconds) =>
        Times[sql] = Times.GetValueOrDefault(sql) + nanoseconds;

    public static void Attach(SqliteConnection connection) =>
        raw.sqlite3_profile(connection.Handle, Collect, null);
}
'@
}
try {
    Invoke-Value $store 'Search' @($query, $cancel) | Out-Null
    $connection = $database.GetType().GetField('_connection', $flags).GetValue($database)
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    [GC]::Collect()
    $process = [Diagnostics.Process]::GetCurrentProcess()
    $process.Refresh()
    $baseline = $process.PrivateMemorySize64
    foreach ($count in @(10000, 100000)) {
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = @'
BEGIN;
DROP TABLE IF EXISTS library_rows;
DELETE FROM source_files;
DELETE FROM contributions;
DELETE FROM locations;
DELETE FROM identifications;
DELETE FROM file_facts;
DELETE FROM video_information;
WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<$count)
INSERT INTO locations(entry_id,path) SELECT i,$root || '\Collection with a long folder name\Pack ' || ((i-1)/100) || '\Example.' || i ||
    CASE i%4 WHEN 0 THEN '.mkv' WHEN 1 THEN '.mp3' ELSE '.txt' END FROM n;
INSERT INTO contributions(origin,name,complete,settled,collected,save_path,stamp,checked)
SELECT DISTINCT 'torrent-' || ((entry_id-1)/100),'Pack ' || ((entry_id-1)/100),1,1,1,$root,'fixture',1 FROM locations;
INSERT INTO source_files
SELECT 'torrent-' || ((entry_id-1)/100),(entry_id-1)%100,entry_id,'Example.' || entry_id,path,entry_id*1024,1,1,
    CASE entry_id%4 WHEN 0 THEN 0 WHEN 1 THEN 1 ELSE 3 END,'Example ' || entry_id,lower(path) FROM locations;
INSERT INTO video_information(catalog_id,video_id,language,title,type,year,genres,cast,synopsis,keywords,search,retrieved)
SELECT 'timing',CAST(entry_id AS TEXT),'','Chosen title ' || entry_id,'movie',2024,'Drama · Mystery','Example Actor',
    'An investigator solves a mystery in a coastal town.','investigator mystery example actor','investigator mystery example actor',1700000000
FROM locations WHERE entry_id%4=0 AND entry_id<=4000;
INSERT INTO identifications
SELECT origin,file_index,'timing',CAST(entry_id AS TEXT),0,1700000000,name FROM source_files WHERE entry_id%4=0 AND entry_id<=4000;
INSERT INTO file_facts
SELECT origin,file_index,1700000000,1700000100,'Song ' || entry_id,'Artist ' || (entry_id%17),'Album ' || (entry_id%53),
    entry_id%12+1,2024,'Rock',180,'artist album rock' FROM source_files WHERE kind=1;
COMMIT;
'@
            $command.Parameters.AddWithValue('$count', $count) | Out-Null
            $command.Parameters.AddWithValue('$root', $directory) | Out-Null
            $command.ExecuteNonQuery() | Out-Null
        }
        finally { $command.Dispose() }
        if ($Profile) {
            Invoke-Value $store 'Search' @($query, $cancel) | Out-Null
            [SqlProfile]::Attach($connection)
            foreach ($name in @('Videos', 'Files')) {
                $configuration = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.Models.LibraryConfiguration'), $name)
                foreach ($text in @('', 'example', 'mystery')) {
                    $query = New-Value 'Library.Query' @($text, $configuration)
                    [SqlProfile]::Times.Clear()
                    $process.Refresh()
                    $cpu = $process.TotalProcessorTime
                    $collectionsBefore = @(0..2 | ForEach-Object { [GC]::CollectionCount($_) })
                    $clock = [Diagnostics.Stopwatch]::StartNew()
                    $matches = Invoke-Value $store 'Search' @($query, $cancel)
                    $clock.Stop()
                    $process.Refresh()
                    $statements = @([SqlProfile]::Times.GetEnumerator() | ForEach-Object {
                        [pscustomobject]@{ sql=$_.Key; elapsedMs=$_.Value/1000000 }
                    })
                    $measurement = [pscustomobject]@{
                        files=$count; configuration=$name; query=$text; rows=$matches.Entries.Count
                        elapsedMs=$clock.Elapsed.TotalMilliseconds
                        cpuMs=($process.TotalProcessorTime-$cpu).TotalMilliseconds
                        collections=@(0..2 | ForEach-Object { [GC]::CollectionCount($_)-$collectionsBefore[$_] })
                        statements=$statements
                    }
                    $measurements.Add($measurement)
                    Write-Output "$count $name '$text': $([Math]::Round($measurement.elapsedMs,2)) ms elapsed, $([Math]::Round($measurement.cpuMs,2)) ms CPU"
                    foreach ($statement in $statements) {
                        $sql = ($statement.sql -replace '\s+', ' ').Trim()
                        Write-Output "  $([Math]::Round($statement.elapsedMs,2)) ms: $($sql.Substring(0,[Math]::Min(130,$sql.Length)))"
                    }
                }
            }
            continue
        }
        foreach ($name in @('Videos', 'Music', 'Files')) {
            $configuration = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.Models.LibraryConfiguration'), $name)
            foreach ($text in @('', 'e', 'example', 'mystery')) {
                $query = New-Value 'Library.Query' @($text, $configuration)
                $times = [Collections.Generic.List[double]]::new()
                for ($sample = 0; $sample -le $Samples; $sample++) {
                    $clock = [Diagnostics.Stopwatch]::StartNew()
                    $matches = Invoke-Value $store 'Search' @($query, $cancel)
                    $clock.Stop()
                    if ($sample -gt 0) { $times.Add($clock.Elapsed.TotalMilliseconds) }
                }
                $ordered = @($times | Sort-Object)
                $measurement = [pscustomobject]@{
                    files = $count; configuration = $name; query = $text; rows = $matches.Entries.Count
                    medianMs = $ordered[[int][Math]::Floor($Samples/2)]
                    p95Ms = $ordered[[Math]::Ceiling($Samples*0.95)-1]
                    samplesMs = @($times)
                }
                $measurements.Add($measurement)
                Write-Output "$count $name '$text': p95 $([Math]::Round($measurement.p95Ms, 2)) ms, $($matches.Entries.Count) rows"
            }
        }
        [GC]::Collect()
        [GC]::WaitForPendingFinalizers()
        [GC]::Collect()
        $process.Refresh()
        $additional = $process.PrivateMemorySize64 - $baseline
        $collections.Add([pscustomobject]@{ files=$count; privateBytes=$process.PrivateMemorySize64; additionalPrivateBytes=$additional })
        Write-Output "$count files: $([Math]::Round($additional/1MB,2)) MiB additional private memory after collection (database/rows only)."
    }
}
finally { $database.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null }
$evidence = Join-Path $directory 'timing.json'
[IO.File]::WriteAllText($evidence, (@{
    scope = 'Production SQLite query and typed-row materialization only; excludes source transfer and UI rendering.'
    memoryScope = 'Host private bytes relative to the initialized empty database after forced GC; excludes WinUI and source transfer.'
    baselinePrivateBytes = $baseline; collections = @($collections); profile = [bool]$Profile
    binary = $binary; machine = [Environment]::MachineName; processors = [Environment]::ProcessorCount
    recorded = [DateTimeOffset]::UtcNow; measurements = @($measurements)
} | ConvertTo-Json -Depth 8))
Write-Output "Evidence: $evidence"
