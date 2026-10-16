param(
    [string] $BinaryDirectory = "$PSScriptRoot/../../artifacts/bin/TinyTorrent/debug_win-x64"
)

$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'TinyTorrentUI.dll'))
$lookupKind = $assembly.GetType('Syno.TinyTorrent.LookupKind', $true)
$automatic = [Enum]::Parse($lookupKind, 'Automatic')
$explicit = [Enum]::Parse($lookupKind, 'Explicit')
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$cancel = [Threading.CancellationToken]::None
$movieKind = $assembly.GetType('Syno.TinyTorrent.VideoKind', $true).GetField('Movie').GetValue($null)
$directory = Join-Path $binary ('evidence/Source-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null

function Unwrap([object[]] $Arguments) {
    $values = [object[]]::new($Arguments.Count)
    for ($index = 0; $index -lt $Arguments.Count; $index++) {
        $values[$index] = $Arguments[$index].PSObject.BaseObject
    }
    return ,$values
}

function New-Value([string] $Name, [object[]] $Arguments, [hashtable] $Properties = @{}) {
    $type = $assembly.GetType('Syno.TinyTorrent.' + $Name, $true)
    $values = Unwrap $Arguments
    $constructor = $type.GetConstructors($flags) | Where-Object { $_.GetParameters().Count -eq $values.Count } | Select-Object -First 1
    $value = $constructor.Invoke($values)
    foreach ($key in $Properties.Keys) {
        $type.GetProperty($key, $flags).SetValue($value, $Properties[$key])
    }
    return $value
}

function New-Values([string] $Name, [object[]] $Values) {
    $array = [Array]::CreateInstance($assembly.GetType('Syno.TinyTorrent.' + $Name, $true), $Values.Count)
    for ($index = 0; $index -lt $Values.Count; $index++) { $array.SetValue($Values[$index], $index) }
    return ,$array
}

function Invoke-Value($Target, [string] $Name, [object[]] $Arguments) {
    $result = $Target.GetType().InvokeMember($Name, $flags -bor [Reflection.BindingFlags]::InvokeMethod,
        $null, $Target, (Unwrap $Arguments))
    if ($result -is [Threading.Tasks.Task]) { return $result.GetAwaiter().GetResult() }
    return $result
}

function Require([bool] $Condition, [string] $Failure) {
    if (!$Condition) { throw $Failure }
}

function Sql([string] $Text, [hashtable] $Values = @{}, $Connection = $null) {
    $connection = if ($null -ne $Connection) { $Connection } else { $database.GetType().GetField('_connection', $flags).GetValue($database) }
    $command = $connection.CreateCommand()
    try {
        $command.CommandText = $Text
        foreach ($key in $Values.Keys) { $command.Parameters.AddWithValue($key, $Values[$key]) | Out-Null }
        return $command.ExecuteScalar()
    }
    finally { $command.Dispose() }
}

$database = New-Value 'Shared.Storage.Database' @($directory)
$subtitles = $null
$policy = New-Value 'Shared.Network.RequestPolicy' @($database)
# No IMDb data is downloaded in the evidence folder, so ratings stay the providers'.
$imdb = New-Value 'Library.Imdb.Dataset' @($null, $directory, $policy)
$http = $null
$dispatcher = $null
$work = $null
try {
    $projection = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'Microsoft.InteractiveExperiences.Projection.dll'))
    $controller = $projection.GetType('Microsoft.UI.Dispatching.DispatcherQueueController', $true)
    $dispatcher = $controller.GetMethod('CreateOnDedicatedThread').Invoke($null, @())
    $work = New-Value 'Shared.BackgroundWork' @($dispatcher.DispatcherQueue)
    $staticFlags = [Reflection.BindingFlags]'Static,NonPublic'
    $confidence = $assembly.GetType('Syno.TinyTorrent.Library.VideoLookup').GetMethod('Confidence', $staticFlags)
    foreach ($case in @(
        @('The Long Road Home', 'Long Road Home', 2024, $true),
        @('The Long Road', 'Long Road', 2024, $false),
        @('The Long Road', 'The Long Road', 2023, $false),
        @('The Long Road 1', 'The Long Road 2', 2024, $false),
        @('The Long Road', 'The Long Road', 2024, $true)
    )) {
        $release = New-Value 'Shared.Release' @($case[0], $case[0]) @{ Year = 2024 }
        $choice = New-Value 'Library.VideoChoice' @('fixture', 'movie', $movieKind, $case[1]) @{ Year = $case[2] }
        $score = $confidence.Invoke($null, (Unwrap @($release, $choice)))
        Require (($score -gt 0.8) -eq $case[3]) ('Unsafe automatic metadata confidence for ' + $case[0] + ' / ' + $case[1])
    }
    $release = New-Value 'Shared.Release' @('Mission Impossible - Rogue Nation', 'Mission Impossible - Rogue Nation') @{ Year = 2015 }
    $choice = New-Value 'Library.VideoChoice' @('fixture', 'movie', $movieKind, 'Mission: Impossible - Rogue Nation') @{ Year = 2015 }
    Require ($confidence.Invoke($null, (Unwrap @($release, $choice))) -gt 0.8) 'Punctuation prevented Rogue Nation from matching.'
    $store = New-Value 'Library.Store' @($database, $imdb)
    $sources = New-Value 'Shared.FileSources' @($database)
    $configuration = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.LibraryConfiguration'), 'Videos')
    $query = New-Value 'Library.Query' @('', $configuration)
    $path = Join-Path $directory 'Example.2024.mkv'
    $file = New-Value 'Shared.SourceFile' @(0, $path, $path, [long]100) @{ Complete = $true; Wanted = $true }
    $files = New-Values 'Shared.SourceFile' @($file)
    $first = New-Value 'Shared.Contribution' @('first', 'First', $true, $true) @{ SavePath = $directory; Stamp = 'one' }
    $second = New-Value 'Shared.Contribution' @('second', 'Second', $true, $true) @{ SavePath = $directory; Stamp = 'one' }
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($first)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries.Count -eq 1) 'A finished file was not listed.'
    $entry = $matches.Entries[0]
    $lateTarget = New-Value 'Library.VideoTarget' @($entry, 'tmdb', $automatic)
    $late = Invoke-Value $store 'Begin' @($lateTarget, $null, $cancel)
    Require ($null -ne $late) 'An unattempted movie could not start enrichment.'
    $video = New-Value 'Library.VideoInformation' @('tmdb', 'movie/1', 'Chosen title', $movieKind) @{
        Ratings = (New-Value 'Library.Ratings' @() @{ Tmdb = (New-Value 'Library.Rating' @([double]8.25, $movieKind)) })
    }
    $chosen = Invoke-Value $store 'Begin' @((New-Value 'Library.VideoTarget' @($entry, 'tmdb', $explicit)), $null, $cancel)
    Require (Invoke-Value $store 'Complete' @($chosen, $video, 'success', $cancel)) 'An explicit identification was not saved.'
    $mixed = New-Value 'Library.Query' @('2024 chosen', $configuration)
    $matches = Invoke-Value $store 'Search' @($mixed, $cancel)
    Require ($matches.Entries.Count -eq 1 -and $matches.Counts[$configuration] -eq 1 -and $matches.Entries[0].Title -eq 'Chosen title') 'Search and its row summary did not reflect the saved identification.'
    Require ($matches.Entries[0].Ratings.Tmdb.Value -eq 8.25 -and $matches.Entries[0].Ratings.Tmdb.Kind -eq $movieKind -and
        $null -eq $matches.Entries[0].Ratings.Imdb) 'An identified movie lost its rating scope or acquired another provider rating.'
    $detail = Invoke-Value $store 'Detail' @($entry.EntryId, $null, $cancel)
    Require ($detail.Entry.Ratings.Tmdb.Value -eq 8.25) 'The Inspector read did not carry the saved rating.'
    $filters = $mixed.GetType().GetProperty('Configurations', $flags).GetValue($mixed)
    $videoFilters = [Collections.Generic.Dictionary[string,string]]::new()
    $videoFilters.Add('type', 'episode')
    $filters.Add($configuration, $videoFilters)
    $fileConfiguration = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.LibraryConfiguration'), 'Files')
    $fileFilters = [Collections.Generic.Dictionary[string,string]]::new()
    $fileFilters.Add('kind', '0')
    $filters.Add($fileConfiguration, $fileFilters)
    $matches = Invoke-Value $store 'Search' @($mixed, $cancel)
    Require ($matches.Entries.Count -eq 0 -and $matches.Counts[$configuration] -eq 0 -and $matches.Counts[$fileConfiguration] -eq 1) 'Rows and configuration counts did not use their own saved filters.'
    $fileQuery = New-Value 'Library.Query' @('', $fileConfiguration) @{ IncludesFacets = $true }
    $fileFilters.Add('year', '1999')
    $fileFilters.Add('genres', 'Absent genre')
    $fileQuery.GetType().GetProperty('Configurations', $flags).GetValue($fileQuery).Add($fileConfiguration, $fileFilters)
    $matches = Invoke-Value $store 'Search' @($fileQuery, $cancel)
    Require ($matches.Entries.Count -eq 0 -and @($matches.Facets | Where-Object { $_.Section -eq 'year' -and $_.Value -eq '1999' }).Count -eq 1 -and
        @($matches.Facets | Where-Object { $_.Section -eq 'genres' -and $_.Value -eq 'Absent genre' }).Count -eq 1) 'Active Info filters disappeared from the Files filter pane when no rows matched.'
    $mixed = New-Value 'Library.Query' @('2024 nonexistentfilm', $configuration)
    $matches = Invoke-Value $store 'Search' @($mixed, $cancel)
    Require ($matches.Entries.Count -eq 0 -and $matches.Counts[$configuration] -eq 0) 'An unmatched term reused an earlier video match or behaved as OR across terms.'
    $connection = $database.GetType().GetField('_connection', $flags).GetValue($database)
    $command = $connection.CreateCommand()
    try {
        $command.CommandText = "INSERT INTO file_facts(origin,file_index,created,modified,title,artist,album,track,year,genre,duration,search) VALUES ('first',0,1700000000,1700000001,'','','',NULL,NULL,'',NULL,'');"
        $command.ExecuteNonQuery() | Out-Null
    }
    finally { $command.Dispose() }

    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($first, $second)), $cancel) | Out-Null
    Require ((Sql 'SELECT group_concat(origin) FROM new_origins;') -eq 'second') 'A new database searched its first torrents, or a torrent added later was not new.'
    Invoke-Value $sources 'Replace' @($second, $files, $cancel) | Out-Null
    $path = Join-Path $directory 'Moved/Example.2024.mkv'
    $file = New-Value 'Shared.SourceFile' @(0, $path, $path, [long]100) @{ Complete = $true; Wanted = $true }
    $files = New-Values 'Shared.SourceFile' @($file)
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($second, $files, $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries.Count -eq 1 -and $matches.Entries[0].EntryId -eq $entry.EntryId -and $matches.Entries[0].Path -eq $path) 'Moving shared contributors lost their identity or retained their old path.'
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($second)), $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries.Count -eq 1 -and $matches.Entries[0].Title -eq 'Chosen title') 'Removing one contributor lost the shared identification.'
    Require ($matches.Entries[0].Created -eq 1700000000) 'Removing the original contributor lost shared file properties.'

    $database.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $database = New-Value 'Shared.Storage.Database' @($directory)
    $sources = New-Value 'Shared.FileSources' @($database)
    $store = New-Value 'Library.Store' @($database, $imdb)
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($second)), $cancel) | Out-Null
    Require ((Sql 'SELECT COUNT(*) FROM new_origins;') -eq 0) 'Reopening treated a known torrent as new.'
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries.Count -eq 1 -and $matches.Entries[0].EntryId -eq $entry.EntryId) 'Reopening lost the cached list or changed its row identity before engine file replies.'
    Require ((Sql 'SELECT COUNT(*) FROM collected_files;') -eq 0 -and (Sql 'SELECT readable FROM current_files;') -eq 0) 'A saved file list authorized work before current confirmation.'
    Require (!(Invoke-Value $sources 'Replace' @($second, $files, $cancel))) 'Confirming an identical saved list reported a content change.'
    Require ((Sql 'SELECT valid FROM library_state;') -eq 1) 'Unchanged confirmation invalidated the published search snapshot.'
    Require ((Sql 'SELECT readable FROM current_files;') -eq 1) 'Current confirmation did not permit file work.'
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries[0].Title -eq 'Chosen title' -and $matches.Entries[0].Created -eq 1700000000) 'Saved identification or properties did not survive reopening.'
    Require ($matches.Entries[0].Ratings.Tmdb.Value -eq 8.25 -and $matches.Entries[0].Ratings.Tmdb.Kind -eq $movieKind -and
        $null -eq $matches.Entries[0].Ratings.Imdb) 'A saved rating or its scope did not survive move, withdrawal and reopening.'

    $pending = New-Value 'Shared.Contribution' @('second', 'Second', $true, $false) @{ SavePath = $directory; Stamp = 'moving' }
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($pending)), $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries.Count -eq 1 -and (Sql 'SELECT readable FROM current_files;') -eq 0) 'A pending path discarded the cache or remained readable.'
    Require ((Sql "SELECT stamp FROM contributions WHERE origin='second';") -eq 'one') 'An incoming summary overwrote the stamp belonging to the accepted files.'
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($second)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($second, $files, $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries[0].Title -eq 'Chosen title') 'Pending metadata/path confirmation deleted a saved decision.'

    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @()), $cancel) | Out-Null
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($first)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    Require (!(Invoke-Value $store 'Complete' @($late, $video, 'complete', $cancel))) 'A withdrawn request was accepted after its file was re-added.'
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries.Count -eq 1 -and $matches.Entries[0].Title -ne 'Chosen title') 'A late identification resurrected a removed decision after re-add.'

    $catalog = 'imdb'
    $entry = $matches.Entries[0]
    $missTarget = New-Value 'Library.VideoTarget' @($entry, 'tmdb', $automatic)
    $miss = Invoke-Value $store 'Begin' @($missTarget, $null, $cancel)
    Require ($null -ne $miss -and (Invoke-Value $store 'Complete' @($miss, $null, 'no_match', $cancel))) 'An ordinary miss was not recorded.'
    Require (@(Invoke-Value $store 'Pending' @('tmdb', $cancel)).Count -eq 0) 'A miss was tried again in the same session.'
    $target = New-Value 'Library.VideoTarget' @($entry, $catalog, $automatic)
    $request = Invoke-Value $store 'Begin' @($target, $null, $cancel)
    Require ($null -ne $request) 'An unattempted empty movie could not start its lookup.'
    Invoke-Value $store 'Complete' @($request, $null, 'failed', $cancel) | Out-Null
    Invoke-Value $store 'SelectProvider' @('wikidata', $cancel) | Out-Null
    Require (!(Invoke-Value $store 'AutomaticDownload' @($cancel))) 'Choosing a provider started automatic downloads.'
    Invoke-Value $store 'EnableAutomaticDownload' @('wikidata', $cancel) | Out-Null
    $database.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $database = New-Value 'Shared.Storage.Database' @($directory)
    $sources = New-Value 'Shared.FileSources' @($database)
    $store = New-Value 'Library.Store' @($database, $imdb)
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($first)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    $entry = $matches.Entries[0]
    Require (@(Invoke-Value $store 'Pending' @('tmdb', $cancel)).Count -gt 0) 'Session attempts survived reopening instead of consulting durable request protection.'
    $retryTarget = New-Value 'Library.VideoTarget' @($entry, 'tmdb', $explicit)
    $retry = Invoke-Value $store 'Begin' @($retryTarget, $null, $cancel)
    Require ($null -ne $retry) 'A remembered no-match prevented an explicit retry.'
    Invoke-Value $store 'Complete' @($retry, $null, 'no_match', $cancel) | Out-Null
    $policy = New-Value 'Shared.Network.RequestPolicy' @($database)
    Invoke-Value $policy 'Initialize' @($cancel) | Out-Null
    $key = New-Value 'Shared.Network.RequestKey' @('tmdb', 'fixture-query')
    $lookup = New-Value 'Shared.Network.LookupRequest' @($key, $key)
    $answer = New-Value 'Library.VideoSearch' @((New-Values 'Library.VideoChoice' @()), $true)
    1..2 | ForEach-Object { Invoke-Value $store 'SaveSearch' @($lookup, $answer, $cancel) | Out-Null }
    Require ((Sql 'SELECT total FROM request_counts WHERE provider_id=''tmdb'' AND question_id=''fixture-query'';') -eq 2 -and
        (Sql 'SELECT reason FROM request_blocks WHERE provider_id=''tmdb'' AND question_id=''fixture-query'';') -eq 0) 'Completed empty searches were not counted and remembered by the shared policy.'
    $checked = Sql 'SELECT checked FROM video_searches WHERE question_id=''fixture-query'';'
    $cached = $store.GetType().InvokeMember('Search', $flags -bor [Reflection.BindingFlags]::InvokeMethod,
        $null, $store, (Unwrap @($key, $cancel, [long]$checked))).GetAwaiter().GetResult()
    Require ($null -ne $cached -and $cached.Count -eq 0) 'A completed empty answer could not be reused by a fresh batch.'
    Require ($null -eq (Invoke-Value $store 'Search' @($key, $cancel, [long]($checked + 1)))) 'An old answer satisfied a newer explicit refresh.'
    $connection = $database.GetType().GetField('_connection', $flags).GetValue($database)
    $store.GetType().GetMethod('ResetProtection', $staticFlags).Invoke($null, @($connection, 'tmdb')) | Out-Null
    Require ((Sql 'SELECT COUNT(*) FROM video_searches WHERE question_id=''fixture-query'';') -eq 0) 'Reset left an empty answer reusable after clearing its protection.'
    Require ((Invoke-Value $store 'Provider' @($cancel)) -eq 'wikidata') 'Source selection did not survive reopening.'
    Require (Invoke-Value $store 'AutomaticDownload' @($cancel)) 'The automatic download setting did not survive reopening.'

    $http = New-Value 'Shared.Network.ProviderHttp' @()
    $document = [Text.Json.JsonDocument]::Parse('{"aggregateRating":{"ratingCount":25}}')
    $content = $document.RootElement.Clone()
    $document.Dispose()
    $savedRecord = New-Value 'Library.VideoRecord' @('title/tt1234567', $content, [long]1)
    $recordKey = New-Value 'Shared.Network.RequestKey' @($catalog, 'fixture-record')
    Invoke-Value $store 'SaveRecord' @((New-Value 'Shared.Network.LookupRequest' @($recordKey, $null)), $savedRecord, $null) | Out-Null
    Sql 'UPDATE video_records SET checked=1 WHERE catalog_id=$catalog AND path=''title/tt1234567'';' @{ '$catalog' = $catalog } | Out-Null
    $savedVideo = New-Value 'Library.VideoInformation' @($catalog, 'tt1234567', 'Example', $movieKind) @{
        Year = 2024; Records = (New-Values 'Library.VideoRecord' @($savedRecord))
    }
    $target = New-Value 'Library.VideoTarget' @($entry, $catalog, $explicit)
    $request = Invoke-Value $store 'Begin' @($target, $null, $cancel)
    Invoke-Value $store 'SaveChoices' @($savedVideo, $cancel) | Out-Null
    Require (Invoke-Value $store 'Complete' @($request, $savedVideo, 'complete', $cancel)) 'A requested result was not saved.'
    Require ((Sql 'SELECT checked FROM video_records WHERE catalog_id=$catalog AND path=''title/tt1234567'';' @{ '$catalog' = $catalog }) -eq 1) 'Reusing cached metadata made an old response satisfy a fresh fetch.'
    $cached = Invoke-Value $store 'Saved' @($target, 'es', $cancel)
    $records = $cached.GetType().GetProperty('Records', $flags).GetValue($cached)
    Require ($cached.Title -eq 'Example' -and $records.Count -eq 1 -and $records[0].Content.GetProperty('aggregateRating').GetProperty('ratingCount').GetInt32() -eq 25) 'Caching discarded normalized facts or movie data not yet shown by the UI.'
    $automaticTarget = New-Value 'Library.VideoTarget' @($entry, $catalog, $automatic)
    Require ($null -eq (Invoke-Value $store 'Begin' @($automaticTarget, $null, $cancel))) 'A saved movie remained eligible for automatic lookups.'
    $request = Invoke-Value $store 'Begin' @($target, $null, $cancel)
    Invoke-Value $store 'Complete' @($request, $null, 'failed', $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries[0].Title -eq 'Example') 'A failed refresh discarded the saved movie.'
    $request = Invoke-Value $store 'Begin' @($target, $null, $cancel)
    Invoke-Value $store 'EraseMetadata' @((New-Values 'Library.Entry' @($entry)), $cancel) | Out-Null
    Require (!(Invoke-Value $store 'Complete' @($request, $savedVideo, 'complete', $cancel))) 'A late provider reply overwrote a newer Clear identification.'

    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($first, $second)), $cancel) | Out-Null
    $otherPath = Join-Path $directory 'Other/Example.2024.mkv'
    $otherFile = New-Value 'Shared.SourceFile' @(0, $otherPath, $otherPath, [long]100) @{ Complete = $true; Wanted = $true }
    Invoke-Value $sources 'Replace' @($second, (New-Values 'Shared.SourceFile' @($otherFile)), $cancel) | Out-Null
    $request = Invoke-Value $store 'Begin' @($target, $null, $cancel)
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    $otherEntry = $matches.Entries | Where-Object Path -eq $otherPath
    $chosen = Invoke-Value $store 'Begin' @((New-Value 'Library.VideoTarget' @($otherEntry, 'tmdb', $explicit)), $null, $cancel)
    Require (Invoke-Value $store 'Complete' @($chosen, $video, 'success', $cancel)) 'An explicit identification was not saved.'
    Invoke-Value $sources 'Replace' @($second, $files, $cancel) | Out-Null
    Require (!(Invoke-Value $store 'Complete' @($request, $savedVideo, 'complete', $cancel))) 'A late provider reply overwrote a newer identification merged by source reconciliation.'
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries.Count -eq 1 -and $matches.Entries[0].Title -eq 'Chosen title') 'Reconciliation lost the newer explicit identification.'
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($first)), $cancel) | Out-Null

    $isolated = New-Value 'Shared.Contribution' @('catalogues', 'Catalogues', $true, $true) @{ SavePath = $directory; Stamp = 'one' }
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($first, $isolated)), $cancel) | Out-Null
    $alphaPath = Join-Path $directory 'Alpha.2024.mkv'
    $betaPath = Join-Path $directory 'Beta.2024.mkv'
    $alphaFile = New-Value 'Shared.SourceFile' @(0, $alphaPath, $alphaPath, [long]100) @{ Complete = $true; Wanted = $true }
    $betaFile = New-Value 'Shared.SourceFile' @(1, $betaPath, $betaPath, [long]100) @{ Complete = $true; Wanted = $true }
    Invoke-Value $sources 'Replace' @($isolated, (New-Values 'Shared.SourceFile' @($alphaFile, $betaFile)), $cancel) | Out-Null
    $catalogueCases = @(
        @{ Catalog = $catalog; Path = $alphaPath; Title = 'Alpha'; Keyword = 'uniquealpha'; Count = 25 },
        @{ Catalog = 'tmdb'; Path = $betaPath; Title = 'Beta'; Keyword = 'uniquebeta'; Count = 50 }
    )
    foreach ($case in $catalogueCases) {
        $matches = Invoke-Value $store 'Search' @($query, $cancel)
        $isolatedEntry = $matches.Entries | Where-Object Path -eq $case.Path
        $isolatedTarget = New-Value 'Library.VideoTarget' @($isolatedEntry, $case.Catalog, $explicit)
        $document = [Text.Json.JsonDocument]::Parse('{"ratingCount":' + $case.Count + '}')
        $content = $document.RootElement.Clone()
        $document.Dispose()
        $record = New-Value 'Library.VideoRecord' @('shared-record', $content, [long]1)
        $recordKey = New-Value 'Shared.Network.RequestKey' @($case.Catalog, 'fixture-record')
        Invoke-Value $store 'SaveRecord' @((New-Value 'Shared.Network.LookupRequest' @($recordKey, $null)), $record, $null) | Out-Null
        $information = New-Value 'Library.VideoInformation' @($case.Catalog, 'shared-id', $case.Title, $movieKind) @{
            Year = 2024; Keywords = [string[]]@($case.Keyword); Records = (New-Values 'Library.VideoRecord' @($record))
        }
        $attempt = Invoke-Value $store 'Begin' @($isolatedTarget, $null, $cancel)
        Require (Invoke-Value $store 'Complete' @($attempt, $information, 'complete', $cancel)) 'A catalogue-qualified result could not be saved.'
    }
    foreach ($case in $catalogueCases) {
        $isolatedQuery = New-Value 'Library.Query' @($case.Keyword, $configuration)
        $matches = Invoke-Value $store 'Search' @($isolatedQuery, $cancel)
        Require ($matches.Entries.Count -eq 1 -and $matches.Entries[0].Path -eq $case.Path -and $matches.Entries[0].Title -eq $case.Title) 'The same opaque movie ID mixed catalogue rows or search results.'
        $isolatedTarget = New-Value 'Library.VideoTarget' @($matches.Entries[0], $case.Catalog, $explicit)
        $cached = Invoke-Value $store 'Saved' @($isolatedTarget, 'en', $cancel)
        $records = $cached.GetType().GetProperty('Records', $flags).GetValue($cached)
        Require ($cached.CatalogId -eq $case.Catalog -and $cached.Title -eq $case.Title -and $records.Count -eq 1 -and $records[0].Content.GetProperty('ratingCount').GetInt32() -eq $case.Count) 'The same opaque record key mixed catalogue cache payloads.'
    }
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($first)), $cancel) | Out-Null

    $renames = New-Value 'Shared.Contribution' @('renames', 'Renames', $true, $true) @{ SavePath = $directory; Stamp = 'one' }
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($first, $renames)), $cancel) | Out-Null
    $beforePath = Join-Path $directory 'Before.2024.mkv'
    $deliberatePath = Join-Path $directory 'Deliberate.2024.mkv'
    $before = New-Value 'Shared.SourceFile' @(0, $beforePath, $beforePath, [long]100) @{ Complete = $true; Wanted = $true }
    $deliberate = New-Value 'Shared.SourceFile' @(1, $deliberatePath, $deliberatePath, [long]100) @{ Complete = $true; Wanted = $true }
    Invoke-Value $sources 'Replace' @($renames, (New-Values 'Shared.SourceFile' @($before, $deliberate)), $cancel) | Out-Null
    $renameCases = @(
        @{ Path = $beforePath; Id = 'automatic'; Title = 'Before'; Manual = $false },
        @{ Path = $deliberatePath; Id = 'deliberate'; Title = 'Kept choice'; Manual = $true }
    )
    foreach ($case in $renameCases) {
        $matches = Invoke-Value $store 'Search' @($query, $cancel)
        $renameEntry = $matches.Entries | Where-Object Path -eq $case.Path
        $renameTarget = New-Value 'Library.VideoTarget' @($renameEntry, $catalog, $(if ($case.Manual) { $explicit } else { $automatic }))
        $information = New-Value 'Library.VideoInformation' @($catalog, $case.Id, $case.Title, $movieKind) @{
            Year = 2024; Cast = [string[]]@('Saved actor'); Synopsis = 'Saved synopsis'
        }
        $attempt = Invoke-Value $store 'Begin' @($renameTarget, $null, $cancel)
        Require (Invoke-Value $store 'Complete' @($attempt, $information, 'complete', $cancel)) 'A rename fixture could not save its initial identification.'
    }
    $afterPath = Join-Path $directory 'After.2025.mkv'
    $renamedPath = Join-Path $directory 'Renamed.2025.mkv'
    $after = New-Value 'Shared.SourceFile' @(0, $afterPath, $afterPath, [long]100) @{ Complete = $true; Wanted = $true }
    $renamed = New-Value 'Shared.SourceFile' @(1, $renamedPath, $renamedPath, [long]100) @{ Complete = $true; Wanted = $true }
    Invoke-Value $sources 'Replace' @($renames, (New-Values 'Shared.SourceFile' @($after, $renamed)), $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    $afterEntry = $matches.Entries | Where-Object Path -eq $afterPath
    $detail = Invoke-Value $store 'Detail' @($afterEntry.EntryId, $null, $cancel)
    Require ($afterEntry.Title -eq 'After' -and $afterEntry.Type -eq 'movie' -and !$afterEntry.HasMetadata -and $detail.Entry.Title -eq 'After' -and
        $detail.Cast -eq '' -and $detail.Synopsis -eq '') 'A renamed file displayed stale automatic movie details before lookup.'
    $oldTitle = New-Value 'Library.Query' @('Before', $configuration)
    Require ((Invoke-Value $store 'Search' @($oldTitle, $cancel)).Entries.Count -eq 0) 'A renamed file remained searchable by its stale automatic movie title.'
    $renameTarget = New-Value 'Library.VideoTarget' @($afterEntry, $catalog, $automatic)
    $attempt = Invoke-Value $store 'Begin' @($renameTarget, $null, $cancel)
    Require ($null -ne $attempt -and $null -eq $attempt.Choice) 'A renamed file reused its old automatic choice instead of matching again.'
    Require (Invoke-Value $store 'Complete' @($attempt, $null, 'no_match', $cancel)) 'A renamed file could not record an ordinary miss.'
    $detail = Invoke-Value $store 'Detail' @($afterEntry.EntryId, $null, $cancel)
    Require (!$detail.Entry.HasMetadata -and $detail.Entry.Title -eq 'After') 'A renamed-file miss displayed stale automatic metadata.'
    Require ($null -eq (Invoke-Value $store 'Begin' @($renameTarget, $null, $cancel))) 'A renamed-file miss was not remembered for automatic lookup.'
    $deliberateEntry = $matches.Entries | Where-Object Path -eq $renamedPath
    $detail = Invoke-Value $store 'Detail' @($deliberateEntry.EntryId, $null, $cancel)
    Require ($deliberateEntry.Title -eq 'Kept choice' -and $detail.Synopsis -eq 'Saved synopsis') 'Renaming discarded an explicit movie choice.'
    $renameTarget = New-Value 'Library.VideoTarget' @($deliberateEntry, $catalog, $explicit)
    $attempt = Invoke-Value $store 'Begin' @($renameTarget, $null, $cancel)
    Require ($null -ne $attempt -and $attempt.Choice.VideoId -eq 'deliberate') 'An explicit refresh lost the accepted choice after rename.'
    Require (Invoke-Value $store 'Complete' @($attempt, $null, 'failed', $cancel)) 'A renamed file could not record its failed explicit refresh.'
    $detail = Invoke-Value $store 'Detail' @($deliberateEntry.EntryId, $null, $cancel)
    Require ($detail.Entry.Title -eq 'Kept choice' -and $detail.Synopsis -eq 'Saved synopsis') 'A failed refresh discarded the explicit choice of a renamed file.'
    $seriesKind = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.VideoKind'), 'Series')
    $episodeKind = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.VideoKind'), 'Episode')
    $episode = New-Value 'Library.Episode' @(1, 2, 'Second chapter', 'Episode synopsis')
    $information = New-Value 'Library.VideoInformation' @($catalog, 'episode', 'Example series', $episodeKind) @{
        Series = (New-Value 'Library.VideoChoice' @($catalog, 'series', $seriesKind, 'Example series'))
        Episodes = (New-Values 'Library.Episode' @($episode))
    }
    $attempt = Invoke-Value $store 'Begin' @($renameTarget, $null, $cancel)
    Require (Invoke-Value $store 'Complete' @($attempt, $information, 'success', $cancel)) 'An episode identification was not saved.'
    $detail = Invoke-Value $store 'Detail' @($deliberateEntry.EntryId, $null, $cancel)
    $series = $detail.GetType().GetProperty('Series', $flags).GetValue($detail)
    $episodes = $detail.GetType().GetProperty('Episodes', $flags).GetValue($detail)
    Require ($series.CatalogId -eq $catalog -and $series.VideoId -eq 'series' -and
        $series.Title -eq 'Example series' -and $episodes.Count -eq 1 -and
        $episodes[0].Season -eq 1 -and $episodes[0].Number -eq 2 -and $episodes[0].Name -eq 'Second chapter') 'The Inspector lost structured episode identity.'
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($first)), $cancel) | Out-Null

    $omdb = $assembly.GetType('Syno.TinyTorrent.Library.Omdb.Provider', $true).GetProperty('Option', $staticFlags).GetValue($null)
    # The fixture key stands in for the key built into a release.
    $option = New-Value 'Library.ProviderOption' @('omdb', 'OMDb', [uri]'https://example.invalid', [uri]'https://example.invalid', $omdb.Create) @{
        Access = $omdb.GetType().GetProperty('Access', $flags).GetValue($omdb); BuiltInKey = 'offline-fixture'
    }
    $provider = $option.Create.Invoke('offline-fixture')
    Require ((Invoke-Value $provider 'Prepare' @('Mission Impossible - Rogue Nation')) -eq 'Mission Impossible Rogue Nation') 'OMDb search kept punctuation that hides the movie.'
    $videos = New-Value 'Library.VideoLookup' @($store, [Threading.Tasks.Task]::CompletedTask, (New-Values 'Library.ProviderOption' @($option)), $policy, $http) @{ Work = $work }
    try {
        $remakes = @(2000, 2024) | ForEach-Object {
            $movie = Join-Path $directory "Remake.$_.mkv"
            New-Value 'Shared.SourceFile' @($_, $movie, $movie, [long]100) @{ Complete = $true; Wanted = $true }
        }
        $unsupported = Join-Path $directory 'Series.S01E01.mkv'
        $remakes += New-Value 'Shared.SourceFile' @(9, $unsupported, $unsupported, [long]100) @{ Complete = $true; Wanted = $true }
        Invoke-Value $sources 'Replace' @($first, (New-Values 'Shared.SourceFile' @($remakes)), $cancel) | Out-Null
        $entries = (Invoke-Value $store 'Search' @($query, $cancel)).Entries
        $context = New-Value 'Library.VideoLookup+Context' @($option, $provider, 'en', $cancel)
        $searchRequest = $videos.GetType().GetMethod('SearchRequest', $staticFlags)
        $request = $searchRequest.Invoke($null, (Unwrap @($context, 'Remake', $null)))
        $choice = New-Value 'Library.VideoChoice' @('omdb', 'remake-2024', $movieKind, 'Remake') @{ Year = 2024 }
        $answer = New-Value 'Library.VideoSearch' @((New-Values 'Library.VideoChoice' @($choice)), $false)
        Invoke-Value $store 'SaveSearch' @($request, $answer, $cancel) | Out-Null
        $dated = $searchRequest.Invoke($null, (Unwrap @($context, 'Remake', 2000)))
        $missing = New-Value 'Library.VideoSearch' @((New-Values 'Library.VideoChoice' @()), $true)
        Invoke-Value $store 'SaveSearch' @($dated, $missing, $cancel) | Out-Null
        Invoke-Value $videos 'Refresh' @($true, 'en') | Out-Null
        $fetchNew = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.FetchScope'), 'New')
        $cached = Invoke-Value $videos 'Search' @('Remake', 'Remake.2000.mkv', $option, $cancel, $fetchNew, 2000, $true)
        Require ($cached.Choices.Count -eq 1 -and
            (Sql 'SELECT rejected FROM video_searches WHERE catalog_id=''omdb'' AND question_id=$question;' @{ '$question' = $request.Key.QuestionId }) -eq 1) 'A chooser-only no-match hid cached candidates or failed to make its query resettable.'
        $plan = @(Invoke-Value $videos 'Plan' @($entries, $option, $cancel))
        Require ($plan.Count -eq 2) ("Batch counts included unsupported input: " + (($plan | ForEach-Object { $_.Entry.Name }) -join ', '))
        Require ((Invoke-Value $videos 'Protection' @('Remake', 'Remake.2000.mkv', $option, $cancel, 2000, $true)).Kind.ToString() -eq 'NoMatch' -and
            $null -eq (Invoke-Value $videos 'Protection' @('Remake', 'Remake.2024.mkv', $option, $cancel, 2024, $true))) 'The individual retry ignored the current file evidence.'
        $resourceKey = New-Value 'Shared.Network.RequestKey' @('omdb', 'missing-detail')
        $resource = New-Value 'Shared.Network.LookupRequest' @($resourceKey, $null)
        $outcomeType = $assembly.GetType('Syno.TinyTorrent.RequestOutcome')
        Invoke-Value $policy 'Record' @($resource, [Enum]::Parse($outcomeType, 'Missing')) | Out-Null
        $currentEntry = $entries | Where-Object Name -eq 'Remake.2024.mkv'
        $attempt = Invoke-Value $store 'Begin' @((New-Value 'Library.VideoTarget' @($currentEntry, 'omdb', $explicit)), $null, $cancel)
        $attempt.GetType().GetProperty('BlockedRequest', $flags).SetValue($attempt, $resourceKey)
        Invoke-Value $store 'Complete' @($attempt, $null, 'blocked', $cancel) | Out-Null
        $plan = @(Invoke-Value $videos 'Plan' @($entries, $option, $cancel))
        Require (($plan | Where-Object { $_.Entry.Name -eq 'Remake.2024.mkv' }).Answer.ToString() -eq 'NotFound' -and
            ($plan | Where-Object { $_.Entry.Name -eq 'Remake.2000.mkv' }).Answer.ToString() -eq 'Unasked') 'A known missing detail was not counted as not found, or marked another movie.'
        Invoke-Value $policy 'Record' @($resource, [Enum]::Parse($outcomeType, 'Answered')) | Out-Null
        $plan = @(Invoke-Value $videos 'Plan' @($entries, $option, $cancel))
        Require (($plan | Where-Object { $_.Entry.Name -eq 'Remake.2024.mkv' }).Answer.ToString() -eq 'Unasked') 'A resolved shared resource still counted the movie as not found.'
        $wikidataOption = $assembly.GetType('Syno.TinyTorrent.Library.Wikidata.Provider', $true).GetProperty('Option', $staticFlags).GetValue($null)
        $wikidata = $wikidataOption.Create.Invoke('')
        $raw = $searchRequest.Invoke($null, (Unwrap @((New-Value 'Library.VideoLookup+Context' @($wikidataOption, $wikidata, 'en', $cancel)), 'Spider-Man', $null)))
        $prepared = $searchRequest.Invoke($null, (Unwrap @((New-Value 'Library.VideoLookup+Context' @($wikidataOption, $wikidata, 'es', $cancel)),
            (Invoke-Value $wikidata 'Prepare' @('Spider-Man')), $null)))
        Require ($raw.Counter -eq $prepared.Counter) 'Preflight and execution charged different search counters for provider-normalized text.'
        $tmdbOption = $assembly.GetType('Syno.TinyTorrent.Library.Tmdb.Provider', $true).GetProperty('Option', $staticFlags).GetValue($null)
        $tmdb = $tmdbOption.Create.Invoke('offline-fixture')
        $tmdbContext = New-Value 'Library.VideoLookup+Context' @($tmdbOption, $tmdb, 'en', $cancel)
        $tmdbContext.GetType().GetProperty('Access', $flags).SetValue($tmdbContext,
            (New-Value 'Shared.Network.ProviderAccess' @('tmdb', 'offline-fixture', 'search')))
        $variants = [Collections.Generic.List[string]]::new()
        foreach ($variant in $videos.GetType().GetMethod('Queries', $staticFlags).Invoke($null,
            (Unwrap @($tmdbContext, 'Mission: Impossible - Rogue Nation', $null, $true)))) { $variants.Add($variant.Item1) }
        Require ($variants.Count -eq 2 -and $variants[0] -eq 'Mission: Impossible - Rogue Nation' -and
            $variants[1] -eq 'Mission Impossible Rogue Nation') 'An empty title search had no clean fallback.'
        $raw = $searchRequest.Invoke($null, (Unwrap @($tmdbContext, 'Mission: Impossible - Rogue Nation', $null)))
        $plain = $searchRequest.Invoke($null, (Unwrap @($tmdbContext, 'Mission Impossible Rogue Nation', $null)))
        Require ($raw.Key -ne $plain.Key -and $raw.Counter -eq $plain.Counter) 'A fallback search reused the wrong answer or evaded the movie limit.'
        $reasonType = $assembly.GetType('Syno.TinyTorrent.RequestBlockKind')
        $backoff = New-Value 'Shared.Network.RequestBlock' @([Enum]::Parse($reasonType, 'Backoff'), $null)
        $remote = New-Value 'Shared.Network.RequestBlock' @([Enum]::Parse($reasonType, 'RemoteWait'), [DateTimeOffset]::UtcNow.AddHours(1))
        $identity = $assembly.GetType('Syno.TinyTorrent.Shared.Network.RequestKey', $true).GetMethod('Digest', $staticFlags).Invoke($null, @('offline-fixture'))
        Invoke-Value $policy 'Fail' @((New-Value 'Shared.Network.ProviderAccess' @('omdb', $identity, 'search')), $backoff) | Out-Null
        Invoke-Value $policy 'Fail' @((New-Value 'Shared.Network.ProviderAccess' @('omdb', $identity, 'detail')), $remote) | Out-Null
        $plan = @(Invoke-Value $videos 'Plan' @($entries, $option, $cancel))
        Require (($plan | Where-Object { $_.Entry.Name -eq 'Remake.2000.mkv' }).Block.Kind.ToString() -eq 'NoMatch' -and
            $null -eq ($plan | Where-Object { $_.Entry.Name -eq 'Remake.2024.mkv' }).Block -and
            $plan[0].Wait.Kind.ToString() -eq 'RemoteWait') 'Provider availability changed New membership or concealed a hard hold behind forceable backoff.'
        $connection = $database.GetType().GetField('_connection', $flags).GetValue($database)
        $store.GetType().GetMethod('ResetProtection', $staticFlags).Invoke($null, @($connection, 'omdb')) | Out-Null
        Require ((Sql 'SELECT COUNT(*) FROM video_searches WHERE catalog_id=''omdb'';') -eq 0 -and
            (Sql 'SELECT COUNT(*) FROM video_choices WHERE catalog_id=''omdb'';') -eq 1) 'Reset kept a chooser-only no-match association or erased useful choices.'
        $answer = New-Value 'Library.VideoSearch' @((New-Values 'Library.VideoChoice' @()), $true)
        Invoke-Value $store 'SaveSearch' @($request, $answer, $cancel) | Out-Null
        $plan = @(Invoke-Value $videos 'Plan' @($entries, $option, $cancel))
        Require ($plan[0].Block.Kind.ToString() -eq 'Missing') 'An empty provider answer lost its Missing reason.'
        Sql 'DELETE FROM request_holds WHERE provider_id=''omdb'';' | Out-Null
    }
    finally { $videos.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null }

    $batch = New-Value 'Subtitles.Acquisition' @($database, $http, [Threading.Tasks.Task]::CompletedTask, $policy) @{ Work = $work }
    try {
        Invoke-Value $batch 'Load' @() | Out-Null
        $supplierType = $assembly.GetType('Syno.TinyTorrent.SubtitleSupplier')
        $subsource = [Enum]::Parse($supplierType, 'SubSource')
        Invoke-Value $batch 'SelectSupplier' @($subsource) | Out-Null
        $draft = Invoke-Value $batch 'Edit' @($subsource)
        $draft.GetType().GetProperty('Secret', $flags).SetValue($draft, 'offline-fixture')
        Invoke-Value $batch 'Save' @($draft) | Out-Null
        $releases = @('Remake.2024.1080p.WEB-DL.One.mkv', 'Remake.2024.1080p.WEB-DL.Two.mkv')
        $releaseFiles = 0..1 | ForEach-Object {
            $movie = Join-Path $directory $releases[$_]
            New-Value 'Shared.SourceFile' @($_, $movie, $movie, [long]100) @{ Complete = $true; Wanted = $true }
        }
        Invoke-Value $sources 'Replace' @($first, (New-Values 'Shared.SourceFile' @($releaseFiles)), $cancel) | Out-Null
        $targets = @(Invoke-Value $batch 'Targets' @($cancel) | Where-Object {
            $_.Language -eq 'en' -and $_.GetType().GetProperty('Rank', $flags).GetValue($_) -eq 1
        } | Sort-Object Path)
        $validate = [Linq.Expressions.Expression]::Lambda([Func[Threading.CancellationToken, Threading.Tasks.Task]],
            [Linq.Expressions.Expression]::Constant([Threading.Tasks.Task]::CompletedTask, [Threading.Tasks.Task]),
            [Linq.Expressions.ParameterExpression[]]@([Linq.Expressions.Expression]::Parameter([Threading.CancellationToken], 'cancel'))).Compile()
        $stage = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.SubtitleStage'), 'Complete')
        $questions = @($releases | ForEach-Object { New-Value 'Subtitles.SubtitleLookup' @($_, 'en', $validate) @{ Stage = $stage } })
        $keys = @($questions | ForEach-Object { Invoke-Value $batch 'SearchKey' @($_) })
        Require ($keys[0].QuestionId -eq $keys[1].QuestionId) 'The subtitle fixture did not share a remote title question.'
        $candidate = New-Value 'Subtitles.SubtitleCandidate' @('en', $releases[1], 'Two', 'fixture')
        $answer = New-Value 'Subtitles.SubtitleSearch' @((New-Values 'Subtitles.SubtitleCandidate' @($candidate)), $false)
        Sql 'INSERT INTO subtitle_searches(provider_id,question_id,answer,checked) VALUES($provider,$question,$answer,$checked);' @{
            '$provider' = $keys[0].ProviderId; '$question' = $keys[0].QuestionId
            '$answer' = [Text.Json.JsonSerializer]::Serialize($answer, $answer.GetType(), [Text.Json.JsonSerializerOptions]$null)
            '$checked' = [DateTimeOffset]::UtcNow.UtcTicks
        } | Out-Null
        $connection = $database.GetType().GetField('_connection', $flags).GetValue($database)
        Require (!(Invoke-Value $batch 'CanLookup' @($connection, $targets[0])) -and
            (Invoke-Value $batch 'CanLookup' @($connection, $targets[1]))) 'A subtitle rejection for one release hid a cached exact match for another release.'
        Require ((Invoke-Value $batch 'Protection' @($connection, $questions[0])).Kind.ToString() -eq 'NoMatch' -and
            $null -eq (Invoke-Value $batch 'Protection' @($connection, $questions[1]))) 'Cached subtitle candidates removed Force fetch or blocked a matching release.'
        $cachedCandidates = @(Invoke-Value $batch 'Lookup' @($questions[0], $cancel, $true))
        Require ($cachedCandidates.Count -eq 1 -and
            (Sql 'SELECT rejected FROM subtitle_searches WHERE provider_id=''subsource'';') -eq 1) 'A subtitle chooser-only no-match hid candidates or failed to make its query resettable.'
        $supplier = $batch.GetType().GetProperty('Supplier', $flags).GetValue($batch)
        $account = $batch.GetType().GetField('_account', $flags).GetValue($batch)
        Invoke-Value $policy 'Fail' @((Invoke-Value $supplier 'Scope' @($account, 'search')), $backoff) | Out-Null
        Invoke-Value $policy 'Fail' @((Invoke-Value $supplier 'Authentication' @($account)), $remote) | Out-Null
        Require ((Invoke-Value $batch 'Protection' @($connection, $questions[0])).Kind.ToString() -eq 'RemoteWait' -and
            !(Invoke-Value $batch 'CanLookup' @($connection, $targets[0])) -and
            (Invoke-Value $batch 'CanLookup' @($connection, $targets[1]))) 'Subtitle availability masked local matching or a hard hold.'
        Invoke-Value $batch 'ResetProtection' @($connection, 'subsource') | Out-Null
        Require ((Sql 'SELECT reusable FROM subtitle_searches WHERE provider_id=''subsource'';') -eq 0 -and
            (Sql 'SELECT length(answer) FROM subtitle_searches WHERE provider_id=''subsource'';') -gt 0 -and
            (Invoke-Value $batch 'CanLookup' @($connection, $targets[0]))) 'Reset left an unresolved subtitle answer reusable or discarded its candidate facts.'
        Sql 'DELETE FROM request_holds WHERE provider_id=''subsource'';' | Out-Null
        Invoke-Value $batch 'SelectSupplier' @([Enum]::Parse($supplierType, 'OpenSubtitles')) | Out-Null
        $hashTarget = $targets[0]
        $hashVideo = $hashTarget.Videos[0]
        $hashVideo.GetType().GetProperty('Size', $flags).SetValue($hashVideo, [long]1000000)
        $hashVideo.GetType().GetProperty('Readable', $flags).SetValue($hashVideo, $true)
        $hashKey = Invoke-Value $batch 'SearchKey' @($questions[0])
        Sql 'INSERT INTO subtitle_searches(provider_id,question_id,answer,checked,rejected) VALUES($provider,$question,''{"Candidates":[],"Empty":true}'',$checked,1);' @{
            '$provider' = $hashKey.ProviderId; '$question' = $hashKey.QuestionId; '$checked' = [DateTimeOffset]::UtcNow.UtcTicks
        } | Out-Null
        Invoke-Value $policy 'Record' @((New-Value 'Shared.Network.LookupRequest' @($hashKey, $null)),
            [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.RequestOutcome'), 'Missing')) | Out-Null
        Require (Invoke-Value $batch 'CanLookup' @($connection, $hashTarget)) 'A title-only miss suppressed a completed file before its first hash search.'
        Sql 'INSERT INTO subtitle_jobs(path,language,stage,outcome) VALUES($path,''en'',1,1);' @{ '$path' = $hashTarget.Path } | Out-Null
        Require (!(Invoke-Value $batch 'CanLookup' @($connection, $hashTarget))) 'A completed hash miss remained in New.'
        Invoke-Value $batch 'ResetProtection' @($connection, 'opensubtitles') | Out-Null
        $policy.GetType().GetMethod('Reset', $staticFlags).Invoke($null, @($connection, 'opensubtitles')) | Out-Null
        Require (Invoke-Value $batch 'CanLookup' @($connection, $hashTarget)) 'Reset did not release the completed hash miss.'
        Sql 'DELETE FROM subtitle_jobs WHERE path=$path;' @{ '$path' = $hashTarget.Path } | Out-Null
        $hashCount = Invoke-Value $batch 'SearchCount' @($releases[0], 'en')
        Sql 'INSERT INTO request_counts VALUES($supplier,$query,5);' @{
            '$supplier' = $hashCount.ProviderId; '$query' = $hashCount.QuestionId
        } | Out-Null
        Require (!(Invoke-Value $batch 'CanLookup' @($connection, $hashTarget))) 'A new hash attempt escaped the shared title search limit.'
        Sql 'DELETE FROM request_counts WHERE provider_id=''opensubtitles'';' | Out-Null
        $subdl = [Enum]::Parse($supplierType, 'SubDL')
        Invoke-Value $batch 'SelectSupplier' @($subdl) | Out-Null
        $draft = Invoke-Value $batch 'Edit' @($subdl)
        $draft.GetType().GetProperty('Secret', $flags).SetValue($draft, 'offline-fixture')
        Invoke-Value $batch 'Save' @($draft) | Out-Null
        $batchFiles = 0..3 | ForEach-Object {
            $movie = Join-Path $directory ("Batch." + $_ + ".2024.mkv")
            New-Value 'Shared.SourceFile' @($_, $movie, $movie, [long]100) @{ Complete = $true; Wanted = $true }
        }
        Invoke-Value $sources 'Replace' @($first, (New-Values 'Shared.SourceFile' @($batchFiles)), $cancel) | Out-Null
        $targets = Invoke-Value $batch 'Targets' @($cancel)
        $primary = @($targets | Where-Object { $_.GetType().GetProperty('Rank', $flags).GetValue($_) -eq 1 -and $_.Language -eq 'en' } | Sort-Object Path)
        Invoke-Value $batch 'Record' @($primary[0], $false, $cancel, $null) | Out-Null
        $matchType = $assembly.GetType('Syno.TinyTorrent.SubtitleMatch')
        foreach ($case in @(@(1, 'Exact'), @(2, 'None'), @(3, 'Likely'))) {
            $target = $primary[$case[0]]
            Sql 'INSERT INTO subtitle_jobs(path,language,stage,outcome,match) VALUES($path,''en'',1,1,$match); INSERT INTO subtitle_job_sources(path,language,origin,file_index) VALUES($path,''en'',''first'',$index);' @{
                '$path' = $target.Path; '$index' = $case[0]; '$movie' = $target.Videos[0].GetType().GetProperty('FinalPath', $flags).GetValue($target.Videos[0]); '$match' = [int][Enum]::Parse($matchType, $case[1])
            } | Out-Null
        }
        $scopeType = $assembly.GetType('Syno.TinyTorrent.SubtitleScope')
        $newScope = [Enum]::Parse($scopeType, 'New')
        $noExact = [Enum]::Parse($scopeType, 'NoExactMatch')
        $allScope = [Enum]::Parse($scopeType, 'All')
        Require ((Invoke-Value $batch 'Count' @($newScope, $null)) -eq 3 -and
            (Invoke-Value $batch 'Count' @($noExact, $null)) -eq 3 -and
            (Invoke-Value $batch 'Count' @($allScope, $null)) -eq 4) 'Batch scope confused new, exact, failed and likely matches.'
        Invoke-Value $policy 'SaveLimit' @(2, $cancel) | Out-Null
        $counter = Invoke-Value $batch 'SearchCount' @($primary[0].Videos[0].GetType().GetProperty('FinalPath', $flags).GetValue($primary[0].Videos[0]), 'en')
        Sql 'INSERT INTO request_counts VALUES($supplier,$query,2);' @{
            '$supplier' = $counter.ProviderId; '$query' = $counter.QuestionId
        } | Out-Null
        Require ((Invoke-Value $batch 'Count' @($newScope, $null)) -eq 2 -and
            (Invoke-Value $batch 'Count' @($allScope, $null)) -eq 4) 'The search cap did not distinguish New from an explicit All request.'
        Invoke-Value $policy 'SaveLimit' @(5, $cancel) | Out-Null
        $selected = [Collections.Generic.HashSet[long]]::new()
        $selected.Add($primary[2].Videos[0].GetType().GetProperty('EntryId', $flags).GetValue($primary[2].Videos[0])) | Out-Null
        Require ((Invoke-Value $batch 'Count' @($noExact, $selected)) -eq 1) 'Batch count escaped the Library selection.'
        $selected.Add($primary[1].Videos[0].GetType().GetProperty('EntryId', $flags).GetValue($primary[1].Videos[0])) | Out-Null
        Require ((Invoke-Value $batch 'Count' @($newScope, $selected)) -eq 1 -and
            (Invoke-Value $batch 'Count' @($allScope, $selected)) -eq 2) 'All movies excluded selected movies with earlier subtitle searches.'
        Invoke-Value $batch 'SetConnected' @($true) | Out-Null
        Invoke-Value $batch 'Find' @($noExact, 3, $selected) | Out-Null
        Require ((Sql 'SELECT COUNT(*) FROM subtitle_jobs WHERE manual=1 AND guesses=3;') -eq 1 -and
            (Sql 'SELECT enabled FROM subtitle_settings;') -eq 0) 'Manual batch required automatic downloads or changed its preference.'
        Require ((Sql 'SELECT COUNT(DISTINCT file_index) FROM subtitle_job_sources s JOIN subtitle_jobs j USING(path,language) WHERE j.manual=1;') -eq 1) 'Manual batch queued an unselected movie.'
        Sql 'UPDATE subtitle_jobs SET stage=0,outcome=1 WHERE manual=1;' | Out-Null
        Invoke-Value $batch 'Reconcile' @() | Out-Null
        Require ((Sql 'SELECT COUNT(*) FROM subtitle_jobs WHERE manual=1 AND stage=1 AND outcome=0;') -eq 1) 'A manual job could not advance while automatic downloading was off.'
        Add-Type @'
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
public sealed class SubtitleReply : HttpMessageHandler {
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<HttpResponseMessage> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) {
        Ready.TrySetResult();
        return Reply.Task.WaitAsync(cancellation);
    }
}
'@
        Invoke-Value $http 'Configure' @((New-Value 'Shared.Network.HttpRoute' @())) | Out-Null
        $chosenTarget = $primary[2]
        $chosenEntry = $chosenTarget.Videos[0].GetType().GetProperty('EntryId', $flags).GetValue($chosenTarget.Videos[0])
        $candidate = New-Value 'Subtitles.SubtitleCandidate' @('en', 'Fixture', 'Fixture', 'https://api.subdl.com/subtitle/fixture')
        foreach ($stale in @($true, $false)) {
            $reply = [SubtitleReply]::new()
            $client = [Net.Http.HttpClient]::new($reply)
            $http.GetType().GetField('_client', $flags).SetValue($http, $client)
            try {
                $pendingChoice = $batch.GetType().GetMethod('Choose', $flags).Invoke($batch, (Unwrap @($chosenEntry, $candidate, $cancel, $null)))
                $reply.Ready.Task.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() | Out-Null
                if ($stale) {
                    $unwantedChoice = New-Value 'Shared.SourceFile' @(2, $batchFiles[2].Path, $batchFiles[2].Path, [long]100) @{ Complete = $true; Wanted = $false }
                    Invoke-Value $sources 'Replace' @($first, (New-Values 'Shared.SourceFile' @($batchFiles[0], $batchFiles[1], $unwantedChoice, $batchFiles[3])), $cancel) | Out-Null
                }
                $response = [Net.Http.HttpResponseMessage]::new([Net.HttpStatusCode]::OK)
                $response.Content = [Net.Http.StringContent]::new("1`n00:00:00,000 --> 00:00:01,000`nChosen fixture`n")
                $reply.Reply.SetResult($response)
                $rejected = $false
                try { $pendingChoice.GetAwaiter().GetResult() | Out-Null }
                catch {
                    if ($_.Exception.GetBaseException() -is [OperationCanceledException]) { $rejected = $true }
                    else { throw }
                }
                Require ($rejected -eq $stale -and [IO.File]::Exists($chosenTarget.Path) -eq !$stale) 'A chosen subtitle was published after its movie became unwanted, or a current movie could not receive it.'
                if (!$stale) {
                    Require ((Sql 'SELECT reference FROM subtitle_outputs WHERE path=$path AND created=1;' @{ '$path' = $chosenTarget.Path }) -eq 'SubDL:https://api.subdl.com/subtitle/fixture') 'A chosen subtitle lost its supplier reference or created ownership.'
                }
            }
            finally {
                $client.Dispose()
                $http.GetType().GetField('_client', $flags).SetValue($http, $null)
                Invoke-Value $sources 'Replace' @($first, (New-Values 'Shared.SourceFile' @($batchFiles)), $cancel) | Out-Null
                [IO.File]::Delete($chosenTarget.Path)
            }
        }
        Invoke-Value $batch 'SetEnabled' @($true) | Out-Null
        Sql 'UPDATE subtitle_jobs SET reference=''SubDL:fixture'' WHERE manual=1;' | Out-Null
        Invoke-Value $batch 'SelectSupplier' @([Enum]::Parse($supplierType, 'SubSource')) | Out-Null
        Require ((Sql 'SELECT COUNT(*) FROM subtitle_jobs WHERE manual=1 AND reference IS NULL;') -eq 1) 'A supplier change cancelled a batch job waiting for its download.'
        Require ($batch.GetType().GetProperty('Enabled', $flags).GetValue($batch)) 'Choosing another supplier erased automatic intent.'
        $accountSupplier = [Enum]::Parse($supplierType, 'OpenSubtitles')
        Sql 'UPDATE subtitle_settings SET supplier=$supplier,username=''offline-fixture'',secret=NULL;' @{ '$supplier' = [int]$accountSupplier } | Out-Null
        Invoke-Value $batch 'Load' @() | Out-Null
        Require ($batch.GetType().GetProperty('Enabled', $flags).GetValue($batch) -and
            !$batch.GetType().GetProperty('CanCheck', $flags).GetValue($batch)) 'An incomplete account erased automatic intent or allowed Check.'
        Invoke-Value $batch 'SetEnabled' @($false) | Out-Null
        Require ((Sql 'SELECT COUNT(*) FROM subtitle_jobs WHERE manual=1;') -eq 1) 'Switching automatic downloads off cancelled a manual batch.'
        Sql 'DELETE FROM subtitle_jobs; DELETE FROM subtitle_output_sources; DELETE FROM subtitle_outputs; UPDATE subtitle_settings SET enabled=0,supplier=0,username='''',secret=NULL;' | Out-Null
    }
    finally { $batch.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null }
    Add-Type @'
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
public sealed class SubtitleSearchReply : HttpMessageHandler {
    public int Searches, Downloads;
    public bool Fail, Empty, Quota, PaymentRequired, Partial;
    public string OmdbError;
    public static Task Validate(CancellationToken cancellation) => Task.CompletedTask;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (request.RequestUri.AbsolutePath == "/api/v1/download") {
            response.StatusCode = PaymentRequired ? HttpStatusCode.PaymentRequired : HttpStatusCode.OK;
            response.Content = new StringContent("{\"remaining\":0,\"reset_time_utc\":\"" + System.DateTimeOffset.UtcNow.AddHours(5).ToString("O") + "\"}");
            return Task.FromResult(response);
        }
        if (request.RequestUri.AbsolutePath.Contains("/files/search")) {
            Searches++;
            if (Fail) throw new HttpRequestException("Offline connection failure");
            if (Quota) {
                response.StatusCode = HttpStatusCode.TooManyRequests;
                response.Headers.TryAddWithoutValidation("Retry-After", "18000");
                response.Content = new StringContent("unread oversized body");
                response.Content.Headers.ContentLength = 10000000;
                return Task.FromResult(response);
            }
            if (Empty) {
                response.Content = new StringContent("{\"match\":null,\"currentPage\":1,\"totalPages\":1,\"subtitles\":[]}");
                return Task.FromResult(response);
            }
            var json = """
                {"match":{"type":"movie","title":"Search Once","year":2024},"currentPage":1,"totalPages":1,"subtitles":[
                  {"language":"EN","unpack_files":[
                    {"language":"EN","format":"srt","release_name":"Search.Once.2024.Release1","url":"/subtitle/one"},
                    {"language":"EN","format":"srt","release_name":"Search.Once.2024.Release2","url":"/subtitle/two"},
                    {"language":"EN","format":"srt","release_name":"Search.Once.2024.Release3","url":"/subtitle/three"},
                    {"language":"EN","format":"srt","release_name":"Search.Once.2024.Release4","url":"/subtitle/four"}]}]}
                """;
            if (Partial) {
                json = json.Replace("\"totalPages\":1", "\"totalPages\":2");
                if (request.RequestUri.Query.Contains("page=2"))
                    json = json.Replace("Search Once", "Wrong Movie").Replace("\"currentPage\":1", "\"currentPage\":2");
            }
            response.Content = new StringContent(json);
        } else {
            Downloads++;
            response.Content = new StringContent(request.RequestUri.Host == "www.omdbapi.com" && OmdbError != null
                ? "{\"Response\":\"False\",\"Error\":\"" + OmdbError + "\"}"
                : "1\n00:00:00,000 --> 00:00:01,000\nFixture subtitle\n");
        }
        return Task.FromResult(response);
    }
}
'@
    $search = New-Value 'Subtitles.Acquisition' @($database, $http, [Threading.Tasks.Task]::CompletedTask, $policy) @{ Work = $work }
    $reply = [SubtitleSearchReply]::new()
    $client = [Net.Http.HttpClient]::new($reply)
    try {
        Sql 'DELETE FROM request_counts;' | Out-Null
        Invoke-Value $http 'Configure' @((New-Value 'Shared.Network.HttpRoute' @())) | Out-Null
        $http.GetType().GetField('_client', $flags).SetValue($http, $client)
        Invoke-Value $search 'Initialize' @('en') | Out-Null
        Invoke-Value $search 'SelectSupplier' @([Enum]::Parse($supplierType, 'SubDL')) | Out-Null
        $draft = Invoke-Value $search 'Edit' @([Enum]::Parse($supplierType, 'SubDL'))
        $draft.GetType().GetProperty('Secret', $flags).SetValue($draft, 'offline-fixture')
        Invoke-Value $search 'Save' @($draft) | Out-Null
        Invoke-Value $policy 'SaveLimit' @(1, $cancel) | Out-Null
        $movie = Join-Path $directory 'Search.Once.2024.mkv'
        [IO.File]::WriteAllBytes($movie, [byte[]]::new(100))
        $searchFile = New-Value 'Shared.SourceFile' @(0, $movie, $movie, [long]100) @{ Complete = $true; Wanted = $true }
        Invoke-Value $sources 'Replace' @($first, (New-Values 'Shared.SourceFile' @($searchFile)), $cancel) | Out-Null
        $operation = $search.GetType().GetField('_operation', $flags).GetValue($search)
        $operation.Wait()
        try {
            Invoke-Value $search 'SetConnected' @($true) | Out-Null
            $targets = @(Invoke-Value $search 'Targets' @($cancel) | Where-Object { $_.Language -eq 'en' } | Sort-Object Path)
            $target = @($targets | Where-Object { $_.GetType().GetProperty('Rank', $flags).GetValue($_) -eq 1 })[0]
            $connection = $database.GetType().GetField('_connection', $flags).GetValue($database)
            $counter = Invoke-Value $search 'SearchCount' @('Search.Once.2024.mkv', 'en')
            $intent = New-Value 'Subtitles.Acquisition+SearchIntent' @($counter, [DateTimeOffset]::UtcNow.UtcTicks)
            $target.GetType().GetProperty('Fresh', $flags).SetValue($target, $intent)
            $target.GetType().GetProperty('Manual', $flags).SetValue($target, $true)
            Invoke-Value $search 'Accept' @($connection, $target, $true, 4) | Out-Null
            $video = $target.Videos[0]
            $video.GetType().GetProperty('Complete', $flags).SetValue($video, $false)
            $video.GetType().GetProperty('Path', $flags).SetValue($video, $movie + '.moving')
            Invoke-Value $search 'Acquire' @($target, $cancel) | Out-Null
            Require ((Sql 'SELECT fresh IS NOT NULL AND outcome=0 FROM subtitle_jobs WHERE path=$path;' @{ '$path' = $target.Path }) -eq 1 -and
                $reply.Searches -eq 0) 'Waiting for a local movie file consumed the accepted fresh lookup before any provider request.'
            $video.GetType().GetProperty('Complete', $flags).SetValue($video, $true)
            $video.GetType().GetProperty('Path', $flags).SetValue($video, $movie)
            $target.GetType().GetProperty('Fresh', $flags).SetValue($target, $null)
            Invoke-Value $search 'Accept' @($connection, $target, $true, 4) | Out-Null
            foreach ($target in $targets | Sort-Object { $_.GetType().GetProperty('Rank', $flags).GetValue($_) }) {
                $target.GetType().GetProperty('Manual', $flags).SetValue($target, $true)
                $target.GetType().GetProperty('Guesses', $flags).SetValue($target, 4)
                $reference = Sql 'SELECT reference FROM subtitle_jobs WHERE path=$path;' @{ '$path' = $target.Path }
                if ($reference -is [string]) {
                    $target.GetType().GetProperty('Reference', $flags).SetValue($target, $reference)
                    $target.GetType().GetProperty('Match', $flags).SetValue($target, [Enum]::Parse($matchType, 'Likely'))
                }
                Invoke-Value $search 'Acquire' @($target, $cancel) | Out-Null
            }
            Require ($reply.Searches -eq 1 -and $reply.Downloads -eq 4 -and
                (Sql 'SELECT total FROM request_counts WHERE provider_id=''subdl'';') -eq 1) "Alternative subtitles did not reuse the saved candidates: $($reply.Searches) searches, $($reply.Downloads) downloads."
            $primary = @($targets | Where-Object { $_.GetType().GetProperty('Rank', $flags).GetValue($_) -eq 1 })[0]
            Invoke-Value $search 'Acquire' @($primary, $cancel) | Out-Null
            Require ($reply.Searches -eq 1) 'An already accepted batch lookup bypassed the limit.'
            $reference = 'https://api.subdl.com/subtitle/retry'
            $resource = $assembly.GetType('Syno.TinyTorrent.Shared.Network.RequestKey').GetMethod('Create', $staticFlags).Invoke($null,
                (Unwrap @('subdl', 'download', [string[]]@($reference))))
            Invoke-Value $policy 'Record' @((New-Value 'Shared.Network.LookupRequest' @($resource, $null)),
                [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.RequestOutcome'), 'Unusable')) | Out-Null
            $intent = New-Value 'Subtitles.Acquisition+SearchIntent' @($counter, [DateTimeOffset]::UtcNow.UtcTicks)
            $primary.GetType().GetProperty('Fresh', $flags).SetValue($primary, $intent)
            $primary.GetType().GetProperty('Reference', $flags).SetValue($primary, 'SubDL:' + $reference)
            $primary.GetType().GetProperty('Match', $flags).SetValue($primary, [Enum]::Parse($matchType, 'Exact'))
            Invoke-Value $search 'Acquire' @($primary, $cancel) | Out-Null
            Require ($reply.Downloads -eq 5 -and
                (Sql 'SELECT outcome FROM subtitle_jobs WHERE path=$path;' @{ '$path' = $primary.Path }) -eq 4) 'A resumed chosen subtitle lost its accepted force intent against an older unusable-resource hold.'
        }
        finally { $operation.Release() | Out-Null }
        $entryId = $primary.Videos[0].GetType().GetProperty('EntryId', $flags).GetValue($primary.Videos[0])
        $fresh = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.FetchScope'), 'Missing')
        Invoke-Value $search 'Candidates' @($entryId, 'en', $cancel, (New-Value 'Shared.Network.RetryCommand' @([DateTimeOffset]::UtcNow.UtcTicks))) | Out-Null
        Require ($reply.Searches -eq 2) 'The batch limit blocked an individual subtitle choice.'
        $validate = [SubtitleSearchReply].GetMethod('Validate').CreateDelegate([Func[Threading.CancellationToken,Threading.Tasks.Task]])
        $counter = Invoke-Value $search 'SearchCount' @('Search.Once.2024.mkv', 'en')
        $intent = New-Value 'Shared.Network.FreshRequest' @((New-Value 'Shared.Network.RetryCommand' @([DateTimeOffset]::UtcNow.UtcTicks)), $counter)
        $lookup = New-Value 'Subtitles.SubtitleLookup' @('Search.Once.2024.mkv', 'en', $validate) @{
            Fresh = $intent; Stage = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.SubtitleStage'), 'Complete')
        }
        1..2 | ForEach-Object { Invoke-Value $search 'Lookup' @($lookup, $cancel, $true) | Out-Null }
        Require ($reply.Searches -eq 3 -and (Sql 'SELECT total FROM request_counts WHERE provider_id=''subdl'';') -eq 3) 'Duplicate fresh work sent or charged the same completed question twice.'
        $reply.Fail = $true
        $failed = $false
        try { Invoke-Value $search 'Candidates' @($entryId, 'en', $cancel, (New-Value 'Shared.Network.RetryCommand' @([DateTimeOffset]::UtcNow.UtcTicks))) | Out-Null }
        catch { $failed = $_.Exception.GetBaseException().GetType().Name -eq 'SubtitleException' }
        Require ($failed -and (Sql 'SELECT total FROM request_counts WHERE provider_id=''subdl'';') -eq 3) 'A failed connection consumed the movie lookup allowance.'
        $reply.Fail = $false
        Sql 'DELETE FROM request_holds WHERE provider_id=''subdl'';' | Out-Null
        Invoke-Value $policy 'SaveLimit' @(100, $cancel) | Out-Null
        $reply.Empty = $true
        $lookup = New-Value 'Subtitles.SubtitleLookup' @('Completion.Evidence.2024.mkv', 'en', $validate)
        Invoke-Value $search 'Lookup' @($lookup, $cancel, $true) | Out-Null
        $lookup.GetType().GetProperty('Stage', $flags).SetValue($lookup, [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.SubtitleStage'), 'Complete'))
        Invoke-Value $search 'Lookup' @($lookup, $cancel, $true) | Out-Null
        Require ($reply.Searches -eq 6) 'Completed evidence reused an early subtitle miss instead of making its completion search.'
        $reply.Quota = $true
        $limited = $false
        try { Invoke-Value $search 'Candidates' @($entryId, 'en', $cancel, (New-Value 'Shared.Network.RetryCommand' @([DateTimeOffset]::UtcNow.UtcTicks))) | Out-Null }
        catch { $limited = $_.Exception.GetBaseException().GetType().Name -eq 'SubtitleException' }
        Require ($limited -and (Sql 'SELECT COUNT(*) FROM request_holds WHERE provider_id=''subdl'' AND reason=4;') -eq 1) 'A quota header was lost when its response body could not be read.'
        Sql 'DELETE FROM request_holds WHERE provider_id=''subdl'';' | Out-Null
        $reply.Quota = $reply.Empty = $false
        $reply.Partial = $true
        $checked = Sql 'SELECT MAX(checked) FROM subtitle_searches WHERE provider_id=''subdl'';'
        $failed = $false
        try { Invoke-Value $search 'Candidates' @($entryId, 'en', $cancel, (New-Value 'Shared.Network.RetryCommand' @([DateTimeOffset]::UtcNow.UtcTicks))) | Out-Null }
        catch { $failed = $_.Exception.GetBaseException().GetType().Name -eq 'SubtitleException' }
        Require ($failed -and (Sql 'SELECT MAX(checked) FROM subtitle_searches WHERE provider_id=''subdl'';') -eq $checked) 'A contradictory later subtitle page committed partial candidates as a complete answer.'
        $reply.Partial = $false
        Sql 'DELETE FROM request_holds WHERE provider_id=''subdl'';' | Out-Null
        $command = New-Value 'Shared.Network.RetryCommand' @([DateTimeOffset]::UtcNow.UtcTicks)
        $supplier = $search.GetType().GetProperty('Supplier', $flags).GetValue($search)
        $account = $search.GetType().GetField('_account', $flags).GetValue($search)
        Invoke-Value $policy 'Fail' @((Invoke-Value $supplier 'Scope' @($account, 'download')), $backoff) | Out-Null
        $before = $reply.Downloads
        $blocked = $false
        $candidate = New-Value 'Subtitles.SubtitleCandidate' @('en', 'Fixture', 'Fixture', 'https://api.subdl.com/subtitle/newer-hold')
        try { Invoke-Value $search 'Choose' @($entryId, $candidate, $cancel, $command) | Out-Null }
        catch { $blocked = $_.Exception.GetBaseException().GetType().Name -eq 'RequestBlockedException' }
        Require ($blocked -and $reply.Downloads -eq $before) 'Saving a subtitle renewed an old Force command and bypassed a newer download hold.'
        Sql 'DELETE FROM request_holds WHERE provider_id=''subdl'';' | Out-Null
        $supplier = New-Value 'Subtitles.Suppliers.OpenSubtitles' @($policy)
        $account = New-Value 'Subtitles.Account' @([Enum]::Parse($supplierType, 'OpenSubtitles'), '', '')
        $operation = New-Value 'Subtitles.Supplier+Operation' @($http, $account, $validate) @{ Purpose = 'download' }
        foreach ($payment in @($false, $true)) {
            $reply.PaymentRequired = $payment
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, 'https://api.opensubtitles.com/api/v1/download')
            $refused = $false
            try { Invoke-Value $supplier 'Send' @($operation, $request, $cancel, 2097152, $true, $false) | Out-Null }
            catch { $refused = $_.Exception.GetBaseException().GetType().Name -eq 'SubtitleException' }
            finally { $request.Dispose() }
            Require ($refused -eq $payment -and (Sql 'SELECT COUNT(*) FROM request_holds WHERE provider_id=''opensubtitles'' AND operation=''download'' AND reason=4 AND retry_at>$minimum;' @{ '$minimum' = [DateTimeOffset]::UtcNow.AddHours(4).UtcTicks }) -eq 1 -and
                (Sql 'SELECT COUNT(*) FROM request_holds WHERE provider_id=''opensubtitles'' AND operation='''';') -eq 0) 'A body-reported download allowance was lost or incorrectly blocked all supplier requests.'
            Sql 'DELETE FROM request_holds WHERE provider_id=''opensubtitles'';' | Out-Null
        }
        $before = $reply.Downloads
        $access = New-Value 'Shared.Network.ProviderAccess' @('broken-details', 'fixture', 'detail')
        foreach ($number in @(1, 2)) {
            $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, "https://provider.invalid/detail/$number")
            $call = New-Value 'Shared.Network.ProviderCall' @($request, $policy, (New-Value 'Shared.Network.OutboundRequest' @($access)))
            try { $assembly.GetType('Syno.TinyTorrent.Library.ProviderJson').GetMethod('Get', $staticFlags).Invoke($null, (Unwrap @($http, $call, $cancel, $false, $null))).GetAwaiter().GetResult() | Out-Null }
            catch {
                $failure = $_.Exception
                while ($failure.InnerException -and $failure.GetType().Name -notin @('ProviderException', 'RequestBlockedException')) { $failure = $failure.InnerException }
                Require ($failure.GetType().Name -in @('ProviderException', 'RequestBlockedException')) 'Malformed detail JSON reached the wrong failure boundary.'
            }
            finally { $request.Dispose() }
        }
        Require ($reply.Downloads -eq $before + 1) 'A malformed detail endpoint was requested again for another movie during backoff.'
        $waiting = [Threading.Tasks.TaskCompletionSource]::new()
        $videos = New-Value 'Library.VideoLookup' @($store, $waiting.Task, (New-Values 'Library.ProviderOption' @($option, $wikidataOption)), $policy, $http) @{ Work = $work }
        try {
            Invoke-Value $videos 'EnableAutomaticDownload' @('omdb') | Out-Null
            Invoke-Value $videos 'Refresh' @($true, 'en') | Out-Null
            Invoke-Value $videos 'Retry' @() | Out-Null
            $before = $reply.Downloads
            foreach ($scope in @($fetchNew, $fresh)) {
                $failed = $false
                try { Invoke-Value $videos 'Search' @('Retry Fresh', 'Retry.Fresh.2024.mkv', $option, $cancel, $scope, 2024, $false) | Out-Null }
                catch {
                    $failure = $_.Exception
                    while ($failure.InnerException -and $failure.GetType().Name -notin @('ProviderException', 'RequestBlockedException')) { $failure = $failure.InnerException }
                    $failed = $failure.GetType().Name -eq 'ProviderException'
                }
                Require $failed 'A new Force fetch inherited an older failed access recheck instead of reaching the provider.'
            }
            Require ($reply.Downloads -eq $before + 2) 'A failed access recheck swallowed the next explicit Force fetch.'
            Sql 'DELETE FROM request_holds WHERE provider_id=''omdb'';' | Out-Null
            $reply.OmdbError = 'Request limit reached!'
            $before = $reply.Downloads
            $blocks = Sql 'SELECT COUNT(*) FROM request_blocks WHERE provider_id=''omdb'';'
            $counts = Sql 'SELECT COUNT(*) FROM request_counts WHERE provider_id=''omdb'';'
            $entry = (Invoke-Value $store 'Search' @($query, $cancel)).Entries[0]
            foreach ($number in @(1, 2)) {
                $choice = New-Value 'Library.VideoChoice' @('omdb', "tt900000$number", $movieKind, "Quota Movie $number")
                $failure = $null
                try { Invoke-Value $videos 'Identify' @($entry, $choice, $option, $cancel, $fetchNew) | Out-Null }
                catch {
                    $failure = $_.Exception
                    while ($failure.InnerException -and $failure.GetType().Name -notin @('ProviderException', 'RequestBlockedException')) { $failure = $failure.InnerException }
                }
                $expected = if ($number -eq 1) { 'ProviderException' } else { 'RequestBlockedException' }
                Require ($null -ne $failure -and $failure.GetType().Name -eq $expected) 'An OMDb body-reported quota did not stop requests for other movies.'
            }
            Require ($reply.Downloads -eq $before + 1 -and
                (Sql 'SELECT COUNT(*) FROM request_blocks WHERE provider_id=''omdb'';') -eq $blocks -and
                (Sql 'SELECT COUNT(*) FROM request_counts WHERE provider_id=''omdb'';') -eq $counts) 'An OMDb quota response repeated HTTP or poisoned movie-specific request protection.'
            $reply.OmdbError = $null
            Invoke-Value $videos 'Refresh' @($false, 'en') | Out-Null
            Invoke-Value $videos 'Fetch' @((New-Values 'Library.Entry' @($entry)), $option, $fetchNew) | Out-Null
            $batchField = $videos.GetType().GetField('_batch', $flags)
            $accepted = $batchField.GetValue($videos)
            $queued = $accepted.GetType().GetProperty('Queue', $flags).GetValue($accepted)
            $observation = $accepted.GetType().GetProperty('Observation', $flags).GetValue($accepted)
            $progress = $observation.GetType().GetProperty('Progress', $flags).GetValue($observation)
            Require ($progress.GetType().GetProperty('Total', $flags).GetValue($progress) -eq 1) 'The metadata batch did not accept its file.'
            # Reload runs the replacement on a worker thread, where a script block has no runspace.
            $replace = [Linq.Expressions.Expression]::Lambda([Action], [Linq.Expressions.Expression]::Empty()).Compile()
            $enable = [Linq.Expressions.Expression]::Lambda([Func[bool]], [Linq.Expressions.Expression]::Constant($true)).Compile()
            Invoke-Value $videos 'Reload' @($option, $replace, $enable) | Out-Null
            Require ([object]::ReferenceEquals($accepted, $batchField.GetValue($videos)) -and $queued.Count -eq 1) 'Enabling a newly installed dataset discarded an accepted API batch.'
            Invoke-Value $videos 'DisableAutomaticDownload' @() | Out-Null
            Require ([object]::ReferenceEquals($accepted, $batchField.GetValue($videos)) -and $queued.Count -eq 1) 'Switching automatic downloads off discarded an accepted manual batch.'
            Invoke-Value $videos 'SelectProvider' @('wikidata') | Out-Null
            Require (!(Invoke-Value $store 'AutomaticDownload' @($cancel)) -and
                [object]::ReferenceEquals($accepted, $batchField.GetValue($videos)) -and $queued.Count -eq 1) 'Choosing a provider changed automatic intent or discarded an accepted manual batch.'
            Invoke-Value $videos 'EnableAutomaticDownload' @('omdb') | Out-Null
            Require ([object]::ReferenceEquals($accepted, $batchField.GetValue($videos)) -and $queued.Count -eq 1) 'Switching automatic downloads on discarded an accepted manual batch.'
        }
        finally { $videos.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null }
    }
    finally {
        $search.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
        $client.Dispose()
        $http.GetType().GetField('_client', $flags).SetValue($http, $null)
        Sql 'DELETE FROM subtitle_jobs; DELETE FROM subtitle_output_sources; DELETE FROM subtitle_outputs; DELETE FROM request_counts; UPDATE subtitle_settings SET enabled=0,supplier=0,username='''',secret=NULL;' | Out-Null
        Invoke-Value $policy 'SaveLimit' @(5, $cancel) | Out-Null
        Get-ChildItem -LiteralPath $directory -Filter 'Search.Once.2024*' | Remove-Item
    }
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    $inventory = New-Value 'Subtitles.Acquisition' @($database, $http, [Threading.Tasks.Task]::CompletedTask, $policy) @{ Work = $work }
    try {
        $french = [IO.Path]::ChangeExtension($path, '.fr.srt')
        $movieFolder = [IO.Path]::GetDirectoryName($path)
        $subsFolder = Join-Path $movieFolder 'Subs'
        [IO.Directory]::CreateDirectory($subsFolder) | Out-Null
        [IO.File]::WriteAllText($french, 'French fixture')
        [IO.File]::WriteAllText((Join-Path $subsFolder 'Spanish.srt'), 'Spanish fixture')
        [IO.File]::WriteAllText((Join-Path $movieFolder 'Other.Movie.de.srt'), 'Unrelated fixture')
        Invoke-Value $inventory 'Initialize' @('en') | Out-Null
        Invoke-Value $inventory 'SetConnected' @($true) | Out-Null
        Invoke-Value $inventory 'Reconcile' @() | Out-Null
        $languages = Sql "SELECT group_concat(language,',') FROM (SELECT language FROM subtitle_languages WHERE origin='first' AND file_index=0 ORDER BY language);"
        Require ($languages -eq 'es,fr') 'Folder discovery missed named or generic subtitle languages, or included another movie.'
        $matches = Invoke-Value $store 'Search' @($query, $cancel)
        Require (($matches.Entries[0].Subtitles -join ',') -eq 'es,fr') 'Library did not read the discovered languages from SQLite.'
        [IO.File]::WriteAllText([IO.Path]::ChangeExtension($path, '.de.srt'), 'Later fixture')
        Invoke-Value $inventory 'Reconcile' @() | Out-Null
        Require ((Sql "SELECT COUNT(*) FROM subtitle_languages WHERE origin='first' AND language='de';") -eq 0) 'A normal refresh rescanned an unchanged movie folder.'
        $nested = Join-Path $subsFolder 'Captions/English'
        [IO.Directory]::CreateDirectory($nested) | Out-Null
        $english = Join-Path $nested '2_English.srt'
        [IO.File]::WriteAllText($english, 'English fixture')
        $regional = [IO.Path]::ChangeExtension($path, '.en-US.srt')
        [IO.File]::WriteAllText($regional, 'English fixture')
        $unlabelled = Join-Path $subsFolder 'Unlabelled.srt'
        [IO.File]::WriteAllText($unlabelled, "1`n00:00:00,000 --> 00:00:05,000`nWe should go home before the weather gets worse. There is nobody waiting at the station, and the last train has already left. I thought you would remember the way back to the house. We have been walking for hours and everyone is tired, but there is still enough time to reach the village before midnight.`n")
        $spanishText = Join-Path $subsFolder 'Dialogue.srt'
        [IO.File]::WriteAllText($spanishText, "1`n00:00:00,000 --> 00:00:05,000`nTenemos que volver a casa antes de que empeore el tiempo. No queda nadie esperando en la estación y el último tren ya ha salido. Pensé que recordarías el camino de vuelta. Llevamos horas caminando y todos estamos cansados, pero todavía tenemos tiempo para llegar al pueblo antes de medianoche.`n")
        $forced = [IO.Path]::ChangeExtension($path, '.it.forced.srt')
        [IO.File]::WriteAllText($forced, 'Forced fixture')
        $hearing = [IO.Path]::ChangeExtension($path, '.ja.hi.srt')
        [IO.File]::WriteAllText($hearing, 'Hearing-impaired fixture')
        $bibliographic = [IO.Path]::ChangeExtension($path, '.dut.srt')
        [IO.File]::WriteAllText($bibliographic, 'Bibliographic fixture')
        $ansi = [Text.CodePagesEncodingProvider]::Instance.GetEncoding(0)
        $ansiText = Join-Path $subsFolder 'Ansi.srt'
        [IO.File]::WriteAllText($ansiText, "1`n00:00:00,000 --> 00:00:05,000`nNous devons rentrer à la maison avant que le temps ne se dégrade. Il n'y a personne à la gare, et le dernier train est déjà parti. Je pensais que tu te souviendrais du chemin du retour. Nous marchons depuis des heures et tout le monde est fatigué, mais nous avons encore le temps d'arriver au village avant minuit.`n", $ansi)
        Invoke-Value $inventory 'Rescan' @() | Out-Null
        Require ((Sql "SELECT COUNT(*) FROM subtitle_outputs WHERE path=`$path AND language='ja';" @{ '$path' = $hearing }) -eq 1) 'A hearing-impaired tag was read as a second language.'
        Require ((Sql "SELECT COUNT(*) FROM subtitle_outputs WHERE path=`$path AND language='nl';" @{ '$path' = $bibliographic }) -eq 1) 'A bibliographic language code was not recognised.'
        if ($ansi.CodePage -eq 1252) {
            Require ((Sql "SELECT COUNT(*) FROM subtitle_outputs WHERE path=`$path AND language='fr';" @{ '$path' = $ansiText }) -eq 1) 'Discovery could not identify dialogue saved in the system ANSI code page.'
        }
        Require ((Sql "SELECT COUNT(*) FROM subtitle_outputs WHERE path=`$path AND language='en' AND created=0;" @{ '$path' = $english }) -eq 1) 'Explicit rescan missed a subtitle in an arbitrary nested folder.'
        Require ((Sql "SELECT COUNT(*) FROM subtitle_outputs WHERE path=`$path AND language='en' AND created=0;" @{ '$path' = $unlabelled }) -eq 1) 'Discovery could not identify unlabelled subtitle dialogue locally.'
        Require ((Sql "SELECT COUNT(*) FROM subtitle_outputs WHERE path=`$path AND language='en';" @{ '$path' = $regional }) -eq 1) 'A filename language variant did not use the supplier catalogues canonical tag.'
        Require ((Sql "SELECT COUNT(*) FROM subtitle_outputs WHERE path=`$path AND language='es';" @{ '$path' = $spanishText }) -eq 1) 'Discovery could not identify Spanish dialogue locally.'
        Require ((Sql "SELECT COUNT(*) FROM subtitle_outputs WHERE path=`$path;" @{ '$path' = $forced }) -eq 0) 'A forced-only subtitle counted as a full subtitle.'
        $beforeUnreadable = Sql 'SELECT COUNT(*) FROM subtitle_languages;'
        $locked = [IO.File]::Open($unlabelled, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try {
            $scan = Invoke-Value $inventory 'Rescan' @()
            Require ($scan.Unreadable -gt 0 -and (Sql 'SELECT COUNT(*) FROM subtitle_languages;') -eq $beforeUnreadable) 'An unreadable scan discarded existing coverage.'
        }
        finally { $locked.Dispose() }
        $targets = Invoke-Value $inventory 'Targets' @($cancel)
        $englishTarget = @($targets | Where-Object { $_.Language -eq 'en' -and $_.GetType().GetProperty('Rank', $flags).GetValue($_) -eq 1 })[0]
        $connection = $database.GetType().GetField('_connection', $flags).GetValue($database)
        $recorded = $inventory.GetType().GetMethod('Recorded', [Reflection.BindingFlags]'Static,NonPublic')
        Require ($recorded.Invoke($null, (Unwrap @($connection, $englishTarget)))) 'A bundled subtitle did not satisfy acquisition at its recorded path.'
        $movedFolder = Join-Path $directory 'InventoryMoved'
        [IO.Directory]::CreateDirectory($movedFolder) | Out-Null
        $movedPath = Join-Path $movedFolder ([IO.Path]::GetFileName($path))
        [IO.File]::WriteAllText([IO.Path]::ChangeExtension($movedPath, '.de.srt'), 'Moved fixture')
        $movedFile = New-Value 'Shared.SourceFile' @(0, $movedPath, $movedPath, [long]100) @{ Complete = $true; Wanted = $true }
        Invoke-Value $sources 'Replace' @($first, (New-Values 'Shared.SourceFile' @($movedFile)), $cancel) | Out-Null
        Invoke-Value $inventory 'Reconcile' @() | Out-Null
        Require ((Sql "SELECT group_concat(language) FROM subtitle_languages WHERE origin='first';") -eq 'de') 'Moving a movie kept languages from the previous folder.'
        Require ([IO.File]::Exists($english)) 'Moving a movie moved or deleted a bundled subtitle.'
        $captions = Join-Path $movedFolder 'Captions/English'
        [IO.Directory]::CreateDirectory($captions) | Out-Null
        $nestedCaption = Join-Path $captions '2_English.srt'
        [IO.File]::WriteAllText($nestedCaption, 'English fixture')
        Invoke-Value $inventory 'Rescan' @() | Out-Null
        Require ((Sql "SELECT COUNT(*) FROM subtitle_outputs WHERE path=`$path AND language='en';" @{ '$path' = $nestedCaption }) -eq 1) 'Discovery did not walk arbitrary subfolders inside the mapped content folder.'
        $laterCaption = Join-Path $movedFolder 'French.srt'
        [IO.File]::WriteAllText($laterCaption, 'French fixture')
        $first = New-Value 'Shared.Contribution' @('first', 'First', $true, $true) @{ SavePath = $directory; Stamp = 'two' }
        Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($first)), $cancel) | Out-Null
        Invoke-Value $sources 'Replace' @($first, (New-Values 'Shared.SourceFile' @($movedFile)), $cancel) | Out-Null
        Invoke-Value $inventory 'Reconcile' @() | Out-Null
        Require ((Sql "SELECT COUNT(*) FROM subtitle_outputs WHERE path=`$path AND language='fr';" @{ '$path' = $laterCaption }) -eq 1) 'A changed torrent stamp did not discover a later bundled subtitle.'
        $samplePath = Join-Path $movedFolder 'Sample/Movie.sample.mkv'
        $sample = New-Value 'Shared.SourceFile' @(2, $samplePath, $samplePath, [long]100) @{ Complete = $true; Wanted = $true }
        Invoke-Value $sources 'Replace' @($first, (New-Values 'Shared.SourceFile' @($movedFile, $sample)), $cancel) | Out-Null
        Invoke-Value $inventory 'Rescan' @() | Out-Null
        Require ((Sql "SELECT COUNT(*) FROM subtitle_outputs WHERE path=`$path AND language='fr';" @{ '$path' = $laterCaption }) -eq 1) 'A sample video stopped a generic subtitle from belonging to its movie.'
        $otherEpisode = New-Value 'Shared.SourceFile' @(1, (Join-Path $movedFolder 'Other.Episode.mkv'), (Join-Path $movedFolder 'Other.Episode.mkv'), [long]100) @{ Complete = $true; Wanted = $true }
        Invoke-Value $sources 'Replace' @($first, (New-Values 'Shared.SourceFile' @($movedFile, $otherEpisode)), $cancel) | Out-Null
        Invoke-Value $inventory 'Rescan' @() | Out-Null
        Require ((Sql "SELECT COUNT(*) FROM subtitle_languages WHERE origin='first' AND language IN ('en','fr');") -eq 0) 'Generic season-folder subtitles were assigned to an arbitrary episode.'
        Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    }
    finally { $inventory.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null }
    $subtitles = New-Value 'Subtitles.Acquisition' @($database, $http, [Threading.Tasks.Task]::CompletedTask, $policy) @{ Work = $work }
    $caption = [IO.Path]::ChangeExtension($path, '.en.srt')
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($caption)) | Out-Null
    [IO.File]::WriteAllText($caption, "1`n00:00:00,000 --> 00:00:01,000`nFixture`n")
    Sql 'INSERT INTO subtitle_outputs(path,language,created) VALUES($path,$language,1); INSERT INTO subtitle_output_sources(path,language,origin,file_index,movie_path) VALUES($path,$language,$origin,0,$movie);' @{
        '$path' = $caption; '$language' = 'en'; '$origin' = 'first'; '$movie' = $path
    } | Out-Null
    $deletion = Invoke-Value $subtitles 'CaptureDeletion' (,[string[]]@('first'))
    $permanent = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.DeletionMode'), 'Permanent')
    Invoke-Value $subtitles 'Delete' @($deletion, $permanent) | Out-Null
    Require ([IO.File]::Exists($caption)) 'A deletion ran before the refreshed source was ready.'
    Sql 'UPDATE file_reads SET collected=0;' | Out-Null
    Invoke-Value $subtitles 'SetConnected' @($true) | Out-Null
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Require ([IO.File]::Exists($caption)) 'An unknown current payload mapping permitted deletion.'
    Sql 'UPDATE file_reads SET collected=1;' | Out-Null
    Add-Type @'
using System;
using System.Threading;
public static class DeletionGate {
    public static readonly ManualResetEventSlim Entered = new(false);
    public static readonly ManualResetEventSlim Continue = new(false);
    public static readonly Comparison<string> Compare = StringComparer.OrdinalIgnoreCase.Compare;
    private static int armed = 1;
    public static readonly Comparison<string> Callback = (left, right) => {
        if (Interlocked.Exchange(ref armed, 0) == 1) {
            Entered.Set();
            if (!Continue.Wait(10000)) throw new TimeoutException("Deletion gate was not released");
        }
        return Compare(left, right);
    };
}
'@
    $connection = $database.GetType().GetField('_connection', $flags).GetValue($database)
    $connection.CreateCollation('PATH', [DeletionGate]::Callback)
    try {
        # Withdraw the source between the deletion's path and membership reads.
        $reconciling = $subtitles.GetType().GetMethod('Reconcile', $flags).Invoke($subtitles, @())
        Require ([DeletionGate]::Entered.Wait(10000)) 'Deletion did not inspect its path.'
        $empty = New-Values 'Shared.Contribution' @()
        $withdrawing = $sources.GetType().GetMethod('Reconcile', $flags).Invoke($sources, (Unwrap @($empty, $cancel)))
        [DeletionGate]::Continue.Set()
        $withdrawing.GetAwaiter().GetResult() | Out-Null
        $reconciling.GetAwaiter().GetResult() | Out-Null
    }
    finally {
        [DeletionGate]::Continue.Set()
        if ($null -ne $reconciling) { $reconciling.GetAwaiter().GetResult() | Out-Null }
        $connection.CreateCollation('PATH', [DeletionGate]::Compare)
    }
    Require (![IO.File]::Exists($caption)) ('The accepted deletion was lost during source invalidation. Queued={0}; pending={1}; sources={2}.' -f
        $subtitles.GetType().GetField('_deletions', $flags).GetValue($subtitles).Count,
        (Sql 'SELECT COUNT(*) FROM pending_origins;'), (Sql 'SELECT COUNT(*) FROM file_reads;'))
    Invoke-Value $subtitles 'Initialize' @('en') | Out-Null

    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($first)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    [IO.File]::WriteAllText($caption, "1`n00:00:00,000 --> 00:00:01,000`nShared fixture`n")
    Sql 'INSERT OR IGNORE INTO subtitle_outputs(path,language,created) VALUES($path,$language,1); INSERT OR IGNORE INTO subtitle_output_sources(path,language,origin,file_index,movie_path) VALUES($path,$language,$origin,0,$movie);' @{
        '$path' = $caption; '$language' = 'en'; '$origin' = 'first'; '$movie' = $path
    } | Out-Null
    $deletion = Invoke-Value $subtitles 'CaptureDeletion' (,[string[]]@('first'))
    Invoke-Value $subtitles 'SetConnected' @($false) | Out-Null
    Invoke-Value $subtitles 'Delete' @($deletion, $permanent) | Out-Null
    $payload = New-Value 'Shared.SourceFile' @(0, $caption, $caption, [long]100) @{ Complete = $true; Wanted = $true }
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($second)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($second, (New-Values 'Shared.SourceFile' @($payload)), $cancel) | Out-Null
    Invoke-Value $subtitles 'SetConnected' @($true) | Out-Null
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Require ([IO.File]::Exists($caption)) "Subtitle deletion reached another torrent's payload."
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @()), $cancel) | Out-Null
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Require ([IO.File]::Exists($caption)) 'A protected deletion was replayed after its protecting torrent disappeared.'

    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($first)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    Sql 'INSERT INTO subtitle_outputs(path,language,created) VALUES($path,$language,1); INSERT INTO subtitle_output_sources(path,language,origin,file_index,movie_path) VALUES($path,$language,$origin,0,$movie);' @{
        '$path' = $caption; '$language' = 'en'; '$origin' = 'first'; '$movie' = $path
    } | Out-Null
    $deletion = Invoke-Value $subtitles 'CaptureDeletion' (,[string[]]@('first'))
    Invoke-Value $subtitles 'SetConnected' @($false) | Out-Null
    Invoke-Value $subtitles 'Delete' @($deletion, $permanent) | Out-Null
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($second)), $cancel) | Out-Null
    $collisionPath = [IO.Path]::ChangeExtension($path, '.mp4')
    $collision = New-Value 'Shared.SourceFile' @(1, $collisionPath, $collisionPath, [long]100) @{ Complete = $true; Wanted = $true }
    Invoke-Value $sources 'Replace' @($second, (New-Values 'Shared.SourceFile' @($file, $collision)), $cancel) | Out-Null
    Invoke-Value $subtitles 'SetConnected' @($true) | Out-Null
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Require ([IO.File]::Exists($caption)) 'Deletion reached a newly shared movie whose caption association had not been recorded.'
    Invoke-Value $subtitles 'SetLanguages' (,[string[]]@('en', 'es')) | Out-Null
    $missingCaption = [IO.Path]::ChangeExtension($path, '.es.srt')
    Sql @'
INSERT INTO subtitle_outputs(path,language,created) VALUES($present,'en',1),($missing,'es',1);
INSERT INTO subtitle_output_sources(path,language,origin,file_index,movie_path) VALUES($present,'en','second',0,$movie),($missing,'es','second',0,$movie);
'@ @{ '$present' = $caption; '$missing' = $missingCaption; '$movie' = $path } | Out-Null
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Require ([IO.File]::ReadAllText($caption) -ceq "1`n00:00:00,000 --> 00:00:01,000`nShared fixture`n" -and
        ![IO.File]::Exists($missingCaption)) 'Reconciliation changed files at ambiguous caption destinations.'
    Require ((Sql 'SELECT COUNT(*) FROM subtitle_outputs;') -eq 2 -and
        (Sql 'SELECT COUNT(*) FROM subtitle_outputs WHERE created=1 AND ((path=$present AND language=''en'') OR (path=$missing AND language=''es''));' @{
            '$present' = $caption; '$missing' = $missingCaption
        }) -eq 2) 'Reconciliation discarded or relabeled recorded ownership at an ambiguous caption destination.'
    Require ((Sql 'SELECT COUNT(*) FROM subtitle_output_sources;') -eq 2 -and
        (Sql 'SELECT COUNT(*) FROM subtitle_output_sources WHERE origin=''second'' AND file_index=0 AND ((path=$present AND language=''en'') OR (path=$missing AND language=''es''));' @{
            '$present' = $caption; '$missing' = $missingCaption
        }) -eq 2) 'Reconciliation changed existing associations or satisfied the other ambiguous movie.'
    Invoke-Value $subtitles 'SetLanguages' (,[string[]]@()) | Out-Null
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($first)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    Sql 'INSERT OR IGNORE INTO subtitle_outputs(path,language,created) VALUES($path,$language,1); INSERT OR IGNORE INTO subtitle_output_sources(path,language,origin,file_index,movie_path) VALUES($path,$language,$origin,0,$movie);' @{
        '$path' = $caption; '$language' = 'en'; '$origin' = 'first'; '$movie' = $path
    } | Out-Null

    $targets = Invoke-Value $subtitles 'Targets' @($cancel)
    $target = $targets | Where-Object { $_.Path -eq $caption } | Select-Object -First 1
    Sql 'DELETE FROM subtitle_outputs WHERE path=$path;' @{ '$path' = $caption } | Out-Null
    Sql 'UPDATE file_reads SET collected=0;' | Out-Null
    Invoke-Value $subtitles 'Record' @($target, $true, $cancel, 'fixture:chosen') | Out-Null
    Require ((Sql 'SELECT created FROM subtitle_outputs WHERE path=$path;' @{ '$path' = $caption }) -eq 1) 'A committed creation lost its ownership during recollection.'
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null

    Invoke-Value $subtitles 'Record' @($target, $false, $cancel, $null) | Out-Null
    Require ((Sql 'SELECT reference FROM subtitle_outputs WHERE path=$path;' @{ '$path' = $caption }) -eq 'fixture:chosen' -and
        (Sql 'SELECT created FROM subtitle_outputs WHERE path=$path;' @{ '$path' = $caption }) -eq 1) 'Recognizing an existing caption discarded its supplier reference or created ownership.'
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Invoke-Value $subtitles 'SetLanguages' (,[string[]]@('es')) | Out-Null
    $movedMovie = Join-Path $directory 'Followed/Example.2024.mkv'
    $movedCaption = [IO.Path]::ChangeExtension($movedMovie, '.en.srt')
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($movedMovie)) | Out-Null
    $unwanted = New-Value 'Shared.SourceFile' @(0, $movedMovie, $movedMovie, [long]100) @{ Complete = $true; Wanted = $false }
    Invoke-Value $sources 'Replace' @($first, (New-Values 'Shared.SourceFile' @($unwanted)), $cancel) | Out-Null
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Require (![IO.File]::Exists($caption) -and [IO.File]::Exists($movedCaption)) 'A recorded caption stopped following its movie after language removal or priority zero.'
    Require ((Sql 'SELECT created FROM subtitle_outputs WHERE path=$path;' @{ '$path' = $movedCaption }) -eq 1) 'A followed caption lost its created ownership.'
    Invoke-Value $sources 'Reconcile' @((New-Values 'Shared.Contribution' @($first, $second)), $cancel) | Out-Null
    $sharedMovie = New-Value 'Shared.SourceFile' @(0, $movedMovie, $movedMovie, [long]100) @{ Complete = $true; Wanted = $true }
    Invoke-Value $sources 'Replace' @($second, (New-Values 'Shared.SourceFile' @($sharedMovie)), $cancel) | Out-Null
    $newMovie = Join-Path $directory 'Retained/Example.2024.mkv'
    $newCaption = [IO.Path]::ChangeExtension($newMovie, '.en.srt')
    $movingMovie = New-Value 'Shared.SourceFile' @(0, $newMovie, $newMovie, [long]100) @{ Complete = $true; Wanted = $false }
    Invoke-Value $sources 'Replace' @($first, (New-Values 'Shared.SourceFile' @($movingMovie)), $cancel) | Out-Null
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Require ([IO.File]::Exists($movedCaption) -and ![IO.File]::Exists($newCaption)) 'A movie move took a caption from a newly shared movie at its old location.'
    Require ((Sql 'SELECT COUNT(*) FROM subtitle_output_sources WHERE path=$path AND origin=$origin;' @{
        '$path' = $movedCaption; '$origin' = 'second'
    }) -eq 1) 'A retained caption lost its new current association.'
    $requestReset = [DateTimeOffset]::UtcNow.AddMinutes(5)
    $downloadReset = $requestReset.AddMinutes(10)
    $remote = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.RequestBlockKind'), 'RemoteWait')
    $requestAccess = New-Value 'Shared.Network.ProviderAccess' @('opensubtitles', 'saved-account', '')
    $downloadAccess = New-Value 'Shared.Network.ProviderAccess' @('opensubtitles', 'saved-account', 'download')
    Invoke-Value $policy 'Fail' @($requestAccess, (New-Value 'Shared.Network.RequestBlock' @($remote, $requestReset))) | Out-Null
    Invoke-Value $policy 'Fail' @($downloadAccess, (New-Value 'Shared.Network.RequestBlock' @($remote, $downloadReset))) | Out-Null
    $savedOutputs = Sql 'SELECT COUNT(*) FROM subtitle_outputs;'
    $savedSources = Sql 'SELECT COUNT(*) FROM subtitle_output_sources;'
    Sql "INSERT INTO request_counts VALUES('opensubtitles','persistent-fixture',3);" | Out-Null
    Invoke-Value $policy 'SaveLimit' @(7, $cancel) | Out-Null
    $missingKey = New-Value 'Shared.Network.RequestKey' @('tmdb', 'persistent-miss')
    $missingLookup = New-Value 'Shared.Network.LookupRequest' @($missingKey, $null)
    $missingOutcome = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.RequestOutcome'), 'Missing')
    Invoke-Value $policy 'Record' @($missingLookup, $missingOutcome) | Out-Null
    Require ($savedOutputs -gt 0 -and $savedSources -gt 0) 'The recovery fixture has no healthy subtitle records.'
    Sql "UPDATE subtitle_settings SET username='Restored user',secret=x'00',languages='{';" | Out-Null
    $subtitles.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $subtitles = $null
    $database.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $database = New-Value 'Shared.Storage.Database' @($directory)
    $policy = New-Value 'Shared.Network.RequestPolicy' @($database)
    $subtitles = New-Value 'Subtitles.Acquisition' @($database, $http, [Threading.Tasks.Task]::CompletedTask, $policy) @{ Work = $work }
    Invoke-Value $subtitles 'Initialize' @('en') | Out-Null
    Require (!$subtitles.GetType().GetProperty('HasSecret', $flags).GetValue($subtitles)) 'An unreadable protected secret was retained.'
    Require ($subtitles.GetType().GetProperty('Username', $flags).GetValue($subtitles) -eq 'Restored user') 'Secret recovery discarded the saved account name.'
    Require ($subtitles.GetType().GetProperty('Languages', $flags).GetValue($subtitles).Length -eq 0) 'Malformed subtitle settings did not recover to defaults.'
    Require ((Sql 'SELECT secret IS NULL AND languages=''[]'' FROM subtitle_settings;') -eq 1) 'Repaired subtitle settings were not saved.'
    Require ((Sql 'SELECT COUNT(*) FROM subtitle_outputs;') -eq $savedOutputs -and
        (Sql 'SELECT COUNT(*) FROM subtitle_output_sources;') -eq $savedSources) 'Settings recovery discarded healthy subtitle outputs or ownership.'
    Require ((Sql 'SELECT lookups FROM request_settings;') -eq 7 -and
        (Sql "SELECT total FROM request_counts WHERE provider_id='opensubtitles' AND question_id='persistent-fixture';") -eq 3) 'Lookup counts or their configured limit did not survive reopening.'
    $remembered = Invoke-Value $policy 'Read' @($missingLookup, $cancel, [long]0)
    Require ($null -ne $remembered -and $remembered.Kind.ToString() -eq 'Missing') 'Reopening forgot a known remote miss and allowed another request.'
    Require ((Sql 'SELECT retry_at FROM request_holds WHERE provider_id=''opensubtitles'' AND identity=''saved-account'' AND operation='''';') -eq $requestReset.UtcTicks -and
        (Sql 'SELECT retry_at FROM request_holds WHERE provider_id=''opensubtitles'' AND identity=''saved-account'' AND operation=''download'';') -eq $downloadReset.UtcTicks) 'Scoped supplier deadlines did not survive reopening.'

    $backoff = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.RequestBlockKind'), 'Backoff')
    $access = New-Value 'Shared.Network.ProviderAccess' @('fixture', 'account', 'search')
    Invoke-Value $policy 'Fail' @($access, (New-Value 'Shared.Network.RequestBlock' @($backoff, $null))) | Out-Null
    $command = New-Value 'Shared.Network.RetryCommand' @([DateTimeOffset]::UtcNow.UtcTicks)
    $question = New-Value 'Shared.Network.RequestKey' @('fixture', 'movie')
    $fresh = New-Value 'Shared.Network.FreshRequest' @($command, $question)
    $outbound = New-Value 'Shared.Network.OutboundRequest' @($access)
    Invoke-Value $policy 'Reserve' @($outbound, $fresh, $cancel) | Out-Null
    Invoke-Value $policy 'Fail' @($access, (New-Value 'Shared.Network.RequestBlock' @($backoff, $null))) | Out-Null
    $blocked = $false
    try { Invoke-Value $policy 'Reserve' @($outbound, $fresh, $cancel) | Out-Null }
    catch { $blocked = $_.Exception.GetBaseException().GetType().Name -eq 'RequestBlockedException' }
    Require $blocked 'An accepted force command bypassed the new hold created by its own failed attempt.'
    $connection = $database.GetType().GetField('_connection', $flags).GetValue($database)
    $assembly.GetType('Syno.TinyTorrent.Shared.Network.RequestPolicy').GetMethod('ClearBackoff', $staticFlags).Invoke($null, @($connection, $null)) | Out-Null
    Require ((Sql 'SELECT COUNT(*) FROM request_holds WHERE reason=3;') -eq 0 -and
        (Sql 'SELECT COUNT(*) FROM request_holds WHERE provider_id=''opensubtitles'' AND identity=''saved-account'' AND reason=4;') -eq 2 -and
        (Sql 'SELECT total FROM request_counts WHERE provider_id=''opensubtitles'' AND question_id=''persistent-fixture'';') -eq 3 -and
        $null -ne (Invoke-Value $policy 'Read' @($missingLookup, $cancel, [long]0))) 'Changing route cleared remote holds or content history instead of only local backoff.'
    $assembly.GetType('Syno.TinyTorrent.Shared.Network.RequestPolicy').GetMethod('Reset', $staticFlags).Invoke($null, @($connection, $null)) | Out-Null
    Require ((Sql 'SELECT COUNT(*) FROM request_blocks;') -eq 0 -and
        (Sql 'SELECT COUNT(*) FROM request_counts;') -eq 0 -and
        (Sql 'SELECT COUNT(*) FROM request_holds WHERE reason=3;') -eq 0 -and
        (Sql 'SELECT COUNT(*) FROM request_holds WHERE provider_id=''opensubtitles'' AND identity=''saved-account'' AND reason=4;') -eq 2 -and
        (Sql 'SELECT COUNT(*) FROM request_usage WHERE provider_id=''fixture'';') -gt 0 -and
        (Sql 'SELECT COUNT(*) FROM subtitle_outputs;') -eq $savedOutputs) 'Reset erased remote protection, pacing or subtitle files, or retained local holds.'
    $subtitles.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $subtitles = $null
    Sql 'DROP TABLE subtitle_output_sources; PRAGMA user_version=1;' | Out-Null
    $database.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $database = New-Value 'Shared.Storage.Database' @($directory)
    $rejected = $false
    try { Invoke-Value $database 'ReadStatistics' @() | Out-Null }
    catch {
        $failure = $_.Exception
        while ($failure.InnerException) { $failure = $failure.InnerException }
        Require ($failure.GetType().Name -eq 'DatabaseVersionException') 'An obsolete database reached feature SQL instead of incompatible-storage recovery.'
        $rejected = $database.GetType().GetProperty('IsIncompatible', $flags).GetValue($database)
    }
    Require $rejected 'An obsolete database was accepted.'
    Invoke-Value $database 'DeleteIncompatible' @() | Out-Null
    Require (![IO.File]::Exists((Join-Path $directory 'library.db'))) 'Incompatible-storage recovery did not delete the old database.'
    $database.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $database = New-Value 'Shared.Storage.Database' @($directory)
    Invoke-Value $database 'ReadStatistics' @() | Out-Null
    Require ((Sql 'SELECT COUNT(*) FROM subtitle_output_sources;') -eq 0) 'Incompatible-storage recovery did not recreate subtitle ownership.'
    Write-Output 'PASS: source membership, cached payload, stale replies, deletion recovery, subtitle ownership, one-search alternatives, saved lookup limits, failure exclusion, recursive inventory, local language detection, scan markers, moves and allowance restoration.'
}
finally {
    try {
        if ($null -ne $subtitles) { $subtitles.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null }
        if ($null -ne $http) { $http.Dispose() }
        $database.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    }
    finally {
        # A queue on its own thread is shut down from another thread only
        # asynchronously.
        try {
            if ($null -ne $dispatcher) {
                $sdk = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'Microsoft.Windows.SDK.NET.dll'))
                $asTask = $sdk.GetType('System.WindowsRuntimeSystemExtensions', $true).GetMethod(
                    'AsTask', [type[]]@($sdk.GetType('Windows.Foundation.IAsyncAction', $true)))
                $asTask.Invoke($null, @($dispatcher.ShutdownQueueAsync())).GetAwaiter().GetResult() | Out-Null
            }
        }
        finally { if ($null -ne $work) { $work.Dispose() } }
    }
}
