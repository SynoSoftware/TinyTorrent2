param(
    [Parameter(Mandatory)][string] $BinaryDirectory
)

$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'TinyTorrentUI.dll'))
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$type = $assembly.GetType('Syno.TinyTorrent.Library.Imdb.Dataset', $true)
$directory = Join-Path $binary ('evidence/Imdb-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$dataset = $type.GetConstructors($flags)[0].Invoke(@($null, $directory.PSObject.BaseObject, $null))

function Require([bool] $Condition, [string] $Failure) {
    if (-not $Condition) { throw $Failure }
}

function Write-Archives {
    $rows = [Collections.Generic.List[string]]::new()
    $rows.Add('tconst\ttitleType\tprimaryTitle\toriginalTitle\tisAdult\tstartYear\tendYear\truntimeMinutes\tgenres')
    $rows.Add('tt0000001\tmovie\tAmélie "quoted"\t別の題名\t0\t2001\t\N\t90\tComedy')
    $rows.Add('tt0000002\ttvMovie\tNo original\tNo original\t0\t2002\t\N\t\N\t\N')
    $rows.Add('tt0000003\ttvSeries\tSeries\t\N\t0\t2003\t\N\t\N\tDrama')
    $rows.Add('tt0000004\ttvEpisode\tFirst\tFirst\t0\t2003\t\N\t\N\tDrama')
    $rows.Add('tt0000005\ttvEpisode\tUnknown year\tUnknown year\t0\t\N\t\N\t\N\t\N')
    $rows.Add('tt0000006\ttvEpisode\tDuplicate number\tDuplicate number\t0\t2003\t\N\t\N\tDrama')
    $rows.Add('tt25000\ttvEpisode\tUnnumbered\t\N\t0\t2003\t\N\t\N\tDrama')
    $rows.Add('tt2147483648\ttvEpisode\tOutside episode IDs\t\N\t0\t2003\t\N\t\N\tDrama')
    # Cross a full index batch and leave a partial batch with a searchable last row.
    for ($index = 1; $index -le 10001; $index++) {
        $rows.Add("tt$($index + 10000)\tmovie\tBatch $index\t\N\t0\t2000\t\N\t\N\t\N")
    }
    $episodes = [Collections.Generic.List[string]]::new()
    $episodes.Add('tconst\tparentTconst\tseasonNumber\tepisodeNumber')
    $episodes.Add('tt0000006\ttt0000003\t1\t1')
    $episodes.Add('tt0000004\ttt0000003\t1\t1')
    $episodes.Add('tt0000005\ttt0000003\t1\t2')
    for ($index = 1; $index -le 101; $index++) {
        $rows.Add("tt$($index + 30000)\ttvEpisode\tEpisode $index\t\N\t0\t\N\t\N\t\N\t\N")
        $episodes.Add("tt$($index + 30000)\ttt0000003\t2\t$index")
    }
    $archives = @{
        'title.basics.tsv.gz' = $rows
        'title.episode.tsv.gz' = $episodes
        'title.ratings.tsv.gz' = @(
            'tconst\taverageRating\tnumVotes',
            'tt0000001\t8.3\t5000000000',
            'tt0000003\t7.2\t12',
            'tt0000004\t9.9\t100'
        )
    }
    foreach ($name in $archives.Keys) {
        $file = [IO.File]::Create((Join-Path $directory $name))
        $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Compress)
        $writer = [IO.StreamWriter]::new($gzip, [Text.UTF8Encoding]::new($false))
        try {
            foreach ($row in $archives[$name]) { $writer.WriteLine($row.Replace('\t', "`t")) }
        } finally { $writer.Dispose() }
    }
}

Add-Type -TypeDefinition @'
using System;
using System.Reflection;
using System.Threading;
public sealed class ImdbProgress
{
    public readonly CancellationTokenSource Cancellation = new();
    public bool CancelIndex;
    public long Indexed;
    public void Report<TPhase, TProgress>(TPhase phase, TProgress progress)
    {
        if (phase.ToString() != "Indexing") return;
        var completed = progress.GetType().GetProperty("Completed", BindingFlags.Instance | BindingFlags.NonPublic);
        Indexed = Convert.ToInt64(completed.GetValue(progress));
        if (CancelIndex && Indexed > 0) Cancellation.Cancel();
    }
}
'@
$phase = $assembly.GetType('Syno.TinyTorrent.ImdbPhase', $true)
$progress = $assembly.GetType('Syno.TinyTorrent.Shared.WorkProgress', $true)
$observer = [ImdbProgress]::new()
$report = [Delegate]::CreateDelegate([Action`2].MakeGenericType($phase, $progress), $observer,
    [ImdbProgress].GetMethod('Report').MakeGenericMethod($phase, $progress))

function Build {
    $task = $type.GetMethod('Build', $flags).Invoke($dataset, @($report, $observer.Cancellation.Token))
    $null = $task.GetAwaiter().GetResult()
}

Write-Archives
Build
Require ($observer.Indexed -eq 10004) 'The final partial search-index batch was not reported.'
$type.GetMethod('Install', $flags).Invoke($dataset, @())
[Reflection.Assembly]::LoadFrom((Join-Path $binary 'Microsoft.Data.Sqlite.dll')) | Out-Null
$installed = Join-Path $directory 'imdb-2.db'
$connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$installed;Pooling=False")
$connection.Open()
function Sql([string] $Text) {
    $command = $connection.CreateCommand()
    try { $command.CommandText = $Text; return $command.ExecuteScalar() }
    finally { $command.Dispose() }
}
try {
    Require ((Sql 'SELECT count(*) FROM titles') -eq 10004) 'The imported title count changed.'
    Require ((Sql @'
SELECT count(*) FROM titles WHERE title_id=1 AND title='Amélie "quoted"' AND original='別の題名'
AND title_key='amelie quoted' AND year=2001 AND genres='Comedy' AND rating=8.3 AND votes=5000000000
AND typeof(title_id)='integer' AND typeof(votes)='integer' AND typeof(rating)='real';
'@) -eq 1) 'A typed binding changed a title, Unicode text or rating.'
    Require ((Sql 'SELECT count(*) FROM titles WHERE title_id=2 AND original IS NULL AND original_key IS NULL AND genres IS NULL AND rating IS NULL AND votes IS NULL') -eq 1) 'A missing value reused a previous row binding.'
    Require ((Sql 'SELECT count(*) FROM titles WHERE title_id=10098 AND original IS NULL AND original_key IS NULL AND genres IS NULL') -eq 1) 'A reused insert batch retained a previous title binding.'
    Require ((Sql 'SELECT count(*) FROM episodes') -eq 103) 'An episode insert batch lost or repeated rows.'
    Require ((Sql 'SELECT count(*) FROM episode_titles') -eq 104) 'An episode-title insert batch lost or repeated rows.'
    Require ((Sql 'SELECT count(*) FROM episodes WHERE series_id=3 AND season=2 AND number=101 AND episode_id=30101') -eq 1) 'The final partial episode batch is missing.'
    Require ((Sql "SELECT count(*) FROM episode_titles WHERE episode_id=30101 AND title='Episode 101' AND year IS NULL") -eq 1) 'The final partial episode-title batch is missing or retained a previous year.'
    Require ((Sql 'SELECT count(*) FROM episodes WHERE series_id=3 AND season=1 AND number=1 AND episode_id=4') -eq 1) 'A duplicate episode number no longer keeps the lower identifier.'
    Require ((Sql 'SELECT count(*) FROM episode_titles WHERE episode_id=5 AND year IS NULL') -eq 1) 'An episode without a year inherited the preceding year.'
    Require ((Sql 'SELECT count(*) FROM episode_titles WHERE episode_id=30098 AND year IS NULL') -eq 1) 'A reused episode-title batch retained a previous year.'
    Require ((Sql "SELECT count(*) FROM title_search WHERE title_search MATCH 'amelie'") -eq 1) 'The title search lost Unicode tokenization.'
    Require ((Sql 'SELECT count(*) FROM title_search WHERE title_search MATCH ''"amelie quoted"''') -eq 1) 'The title search lost phrase matching.'
    Require ((Sql "SELECT count(*) FROM title_search WHERE title_search MATCH '10001'") -eq 1) 'The last partial index batch is missing.'
    $null = Sql "INSERT INTO title_search(title_search,rank) VALUES('integrity-check',1)"
    Require ((Sql 'PRAGMA quick_check') -eq 'ok') 'The imported database is damaged.'
} finally { $connection.Dispose() }

$before = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
Write-Archives
$observer.CancelIndex = $true
$failed = $false
try { Build } catch { $failed = $true }
Require ($failed -and $observer.Cancellation.IsCancellationRequested) 'Cancellation did not interrupt indexing.'
$type.GetMethod('Discard', $flags).Invoke($dataset, @())
Require (-not (Test-Path -LiteralPath (Join-Path $directory 'imdb-2.db.tmp'))) 'The cancelled build could not be discarded.'
Require ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -eq $before) 'Cancellation changed the installed database.'
$observer.Cancellation.Dispose()
"IMDb bindings, insert and index batches, integrity and cancellation passed: $directory"
