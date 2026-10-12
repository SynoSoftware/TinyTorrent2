param(
    [string] $BinaryDirectory = "$PSScriptRoot/../../artifacts/bin/TinyTorrent/debug_win-x64"
)

$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'TinyTorrentUI.dll'))
$lookupKind = $assembly.GetType('Syno.TinyTorrent.Library.LookupKind', $true)
$automatic = [Enum]::Parse($lookupKind, 'Automatic')
$explicit = [Enum]::Parse($lookupKind, 'Explicit')
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$cancel = [Threading.CancellationToken]::None
$movieKind = $assembly.GetType('Syno.TinyTorrent.Models.VideoKind', $true).GetField('Movie').GetValue($null)
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
    $result = $Target.GetType().GetMethod($Name, $flags).Invoke($Target, (Unwrap $Arguments))
    if ($result -is [Threading.Tasks.Task]) { return $result.GetAwaiter().GetResult() }
    return $result
}

# The key the subtitle search limit counts a movie's searches under.
function LookupKey([string] $Path) {
    return $assembly.GetType('Syno.TinyTorrent.Subtitles.Acquisition', $true).GetMethod('Query', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @($Path))
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

$database = New-Value 'Services.Database' @($directory)
$subtitles = $null
# Library supplies the shared search limit to subtitles as a function they call
# on worker threads, so the checks pass a compiled delegate, not a script block.
$lookupLimit = [Runtime.CompilerServices.StrongBox[int]]::new(5)
$limit = [Linq.Expressions.Expression]::Lambda([Func[int]],
    [Linq.Expressions.Expression]::Field([Linq.Expressions.Expression]::Constant($lookupLimit), 'Value')).Compile()
$http = $null
try {
    $staticFlags = [Reflection.BindingFlags]'Static,NonPublic'
    $confidence = $assembly.GetType('Syno.TinyTorrent.Library.VideoLookup').GetMethod('Confidence', $staticFlags)
    foreach ($case in @(
        @('The Long Road Home', 'Long Road Home', 2024, $true),
        @('The Long Road', 'Long Road', 2024, $false),
        @('The Long Road', 'The Long Road', 2023, $false),
        @('The Long Road 1', 'The Long Road 2', 2024, $false),
        @('The Long Road', 'The Long Road', 2024, $true)
    )) {
        $release = New-Value 'Models.Release' @($case[0], $case[0]) @{ Year = 2024 }
        $choice = New-Value 'Library.VideoChoice' @('fixture', 'movie', $movieKind, $case[1]) @{ Year = $case[2] }
        $score = $confidence.Invoke($null, (Unwrap @($release, $choice)))
        Require (($score -gt 0.8) -eq $case[3]) ('Unsafe automatic metadata confidence for ' + $case[0] + ' / ' + $case[1])
    }
    $store = New-Value 'Library.Store' @($database)
    $sources = New-Value 'Services.FileSources' @($database)
    $configuration = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.Models.LibraryConfiguration'), 'Videos')
    $query = New-Value 'Library.Query' @('', $configuration)
    $path = Join-Path $directory 'Example.2024.mkv'
    $file = New-Value 'Services.SourceFile' @(0, $path, $path, [long]100) @{ Complete = $true; Wanted = $true }
    $files = New-Values 'Services.SourceFile' @($file)
    $first = New-Value 'Services.Contribution' @('first', 'First', $true, $true) @{ SavePath = $directory; Stamp = 'one' }
    $second = New-Value 'Services.Contribution' @('second', 'Second', $true, $true) @{ SavePath = $directory; Stamp = 'one' }
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries.Count -eq 1) 'A finished file was not listed.'
    $entry = $matches.Entries[0]
    $lateTarget = New-Value 'Library.VideoTarget' @($entry, 'tmdb', $automatic)
    $late = Invoke-Value $store 'Begin' @($lateTarget, $null, $cancel)
    Require ($null -ne $late) 'An unattempted movie could not start enrichment.'
    $video = New-Value 'Library.VideoInformation' @('tmdb', 'movie/1', 'Chosen title', $movieKind) @{
        TmdbRating = (New-Value 'Library.Rating' @([double]8.25, $movieKind))
    }
    $chosen = Invoke-Value $store 'Begin' @((New-Value 'Library.VideoTarget' @($entry, 'tmdb', $explicit)), $null, $cancel)
    Require (Invoke-Value $store 'Complete' @($chosen, $video, 'success', $cancel)) 'An explicit identification was not saved.'
    $mixed = New-Value 'Library.Query' @('2024 chosen', $configuration)
    $matches = Invoke-Value $store 'Search' @($mixed, $cancel)
    Require ($matches.Entries.Count -eq 1 -and $matches.Counts[$configuration] -eq 1 -and $matches.Entries[0].Title -eq 'Chosen title') 'Search and its row summary did not reflect the saved identification.'
    Require ($matches.Entries[0].TmdbRating.Value -eq 8.25 -and $matches.Entries[0].TmdbRating.Kind -eq $movieKind -and
        $null -eq $matches.Entries[0].ImdbRating) 'An identified movie lost its rating scope or acquired another provider rating.'
    $detail = Invoke-Value $store 'Detail' @($entry.EntryId, $null, $cancel)
    Require ($detail.Entry.TmdbRating.Value -eq 8.25) 'The Inspector read did not carry the saved rating.'
    $filters = $mixed.GetType().GetProperty('Configurations', $flags).GetValue($mixed)
    $videoFilters = [Collections.Generic.Dictionary[string,string]]::new()
    $videoFilters.Add('type', 'episode')
    $filters.Add($configuration, $videoFilters)
    $fileConfiguration = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.Models.LibraryConfiguration'), 'Files')
    $fileFilters = [Collections.Generic.Dictionary[string,string]]::new()
    $fileFilters.Add('kind', '0')
    $filters.Add($fileConfiguration, $fileFilters)
    $matches = Invoke-Value $store 'Search' @($mixed, $cancel)
    Require ($matches.Entries.Count -eq 0 -and $matches.Counts[$configuration] -eq 0 -and $matches.Counts[$fileConfiguration] -eq 1) 'Rows and configuration counts did not use their own saved filters.'
    $fileQuery = New-Value 'Library.Query' @('', $fileConfiguration)
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

    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first, $second)), $cancel) | Out-Null
    Require ((Sql 'SELECT group_concat(origin) FROM new_origins;') -eq 'second') 'A new database searched its first torrents, or a torrent added later was not new.'
    Invoke-Value $sources 'Replace' @($second, $files, $cancel) | Out-Null
    $path = Join-Path $directory 'Moved/Example.2024.mkv'
    $file = New-Value 'Services.SourceFile' @(0, $path, $path, [long]100) @{ Complete = $true; Wanted = $true }
    $files = New-Values 'Services.SourceFile' @($file)
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($second, $files, $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries.Count -eq 1 -and $matches.Entries[0].EntryId -eq $entry.EntryId -and $matches.Entries[0].Path -eq $path) 'Moving shared contributors lost their identity or retained their old path.'
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($second)), $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries.Count -eq 1 -and $matches.Entries[0].Title -eq 'Chosen title') 'Removing one contributor lost the shared identification.'
    Require ($matches.Entries[0].Created -eq 1700000000) 'Removing the original contributor lost shared file properties.'

    $database.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $database = New-Value 'Services.Database' @($directory)
    $sources = New-Value 'Services.FileSources' @($database)
    $store = New-Value 'Library.Store' @($database)
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($second)), $cancel) | Out-Null
    Require ((Sql 'SELECT COUNT(*) FROM new_origins;') -eq 0) 'Reopening treated a known torrent as new.'
    Invoke-Value $sources 'Replace' @($second, $files, $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries[0].Title -eq 'Chosen title' -and $matches.Entries[0].Created -eq 1700000000) 'Saved identification or properties did not survive reopening.'
    Require ($matches.Entries[0].TmdbRating.Value -eq 8.25 -and $matches.Entries[0].TmdbRating.Kind -eq $movieKind -and
        $null -eq $matches.Entries[0].ImdbRating) 'A saved rating or its scope did not survive move, withdrawal and reopening.'

    $pending = New-Value 'Services.Contribution' @('second', 'Second', $true, $false) @{ SavePath = $directory; Stamp = 'moving' }
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($pending)), $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries.Count -eq 0) 'A pending path was still presented as confirmed.'
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($second)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($second, $files, $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries[0].Title -eq 'Chosen title') 'Pending metadata/path confirmation deleted a saved decision.'

    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @()), $cancel) | Out-Null
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    Require (!(Invoke-Value $store 'Complete' @($late, $video, 'complete', $cancel))) 'A withdrawn request was accepted after its file was re-added.'
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries.Count -eq 1 -and $matches.Entries[0].Title -ne 'Chosen title') 'A late identification resurrected a removed decision after re-add.'

    $catalog = 'public/rottentomatoes'
    $entry = $matches.Entries[0]
    $missTarget = New-Value 'Library.VideoTarget' @($entry, 'tmdb', $automatic)
    $miss = Invoke-Value $store 'Begin' @($missTarget, $null, $cancel)
    Require ($null -ne $miss -and (Invoke-Value $store 'Complete' @($miss, $null, 'no_match', $cancel))) 'An ordinary miss was not recorded.'
    Require ($null -eq (Invoke-Value $store 'Next' @('tmdb', $cancel))) 'A miss was tried again in the same session.'
    $target = New-Value 'Library.VideoTarget' @($entry, $catalog, $automatic)
    $request = Invoke-Value $store 'Begin' @($target, $null, $cancel)
    Require ($null -ne $request) 'An unattempted empty movie could not start its website lookup.'
    Invoke-Value $store 'Complete' @($request, $null, 'failed', $cancel) | Out-Null
    Invoke-Value $store 'EnableProvider' @('rottentomatoes', $cancel) | Out-Null
    $database.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $database = New-Value 'Services.Database' @($directory)
    $sources = New-Value 'Services.FileSources' @($database)
    $store = New-Value 'Library.Store' @($database)
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    $entry = $matches.Entries[0]
    Require ($null -ne (Invoke-Value $store 'Next' @('tmdb', $cancel))) 'Reopening did not try a file without a match again.'
    1..2 | ForEach-Object { Invoke-Value $store 'SaveSearch' @('tmdb', 'Example', (New-Values 'Library.VideoChoice' @()), $cancel) | Out-Null }
    Require ((Invoke-Value $store 'Searches' @('tmdb', 'example', $cancel)) -eq 2 -and
        (Invoke-Value $store 'Searches' @('public/imdb', 'Example', $cancel)) -eq 0) 'Searches were not counted per provider and search text.'
    Require ((Invoke-Value $store 'Provider' @($cancel)) -eq 'rottentomatoes') 'Source selection did not survive reopening.'
    Require (Invoke-Value $store 'Enabled' @($cancel)) 'The selected provider reopened disabled.'

    $http = New-Value 'Services.ProviderHttp' @()
    $options = $assembly.GetType('Syno.TinyTorrent.Library.Public.Provider').GetMethod('Options', [Reflection.BindingFlags]'Static,NonPublic').Invoke($null, @($http))
    foreach ($option in $options) {
        $adapter = $option.Create.Invoke()
        $source = $adapter.CatalogId
        $address = if ($source -eq 'public/imdb') { 'https://www.imdb.com/title/tt1234567/' } else { 'https://www.rottentomatoes.com/m/example' }
        $html = '<script type="application/ld+json">' + (@{
            '@type' = 'Movie'; url = $address; name = 'Example'; datePublished = '2024-01-01'
            description = 'A fictional movie used by checks.'; genre = @('Drama'); actor = @(@{ name = 'Example actor' })
            aggregateRating = @{ ratingValue = 8.5; ratingCount = 25 }; keywords = 'fixture'
        } | ConvertTo-Json -Depth 5 -Compress) + '</script>'
        if ($source -eq 'public/rottentomatoes') { $html += '<rt-text data-qa="synopsis-value">A fictional movie used by checks.</rt-text>' }
        $describe = $adapter.GetType().GetMethod('Describe', $flags, $null, [Type[]]@([string], [Uri]), $null)
        $websiteVideo = $describe.Invoke($adapter, @($html, [Uri]$address))
        Require ($websiteVideo.CatalogId -eq $source -and $websiteVideo.Title -eq 'Example' -and $websiteVideo.GetType().GetProperty('Year', $flags).GetValue($websiteVideo) -eq 2024) 'A website fixture lost its catalogue or movie identity.'
        $rejected = $false
        try { $describe.Invoke($adapter, @($html, [Uri]($address.TrimEnd('/') + '_other'))) | Out-Null }
        catch { $rejected = $true }
        Require $rejected 'Movie data from a different canonical page was accepted.'
        if ($source -eq 'public/rottentomatoes') { $savedVideo = $websiteVideo }
    }
    $target = New-Value 'Library.VideoTarget' @($entry, $catalog, $explicit)
    $request = Invoke-Value $store 'Begin' @($target, $null, $cancel)
    Require (Invoke-Value $store 'Complete' @($request, $savedVideo, 'complete', $cancel)) 'A requested website result was not saved.'
    $cached = Invoke-Value $store 'Saved' @($target, 'es', $cancel)
    $records = $cached.GetType().GetProperty('Records', $flags).GetValue($cached)
    Require ($cached.Title -eq 'Example' -and $records.Count -eq 1 -and $records[0].Content.GetProperty('aggregateRating').GetProperty('ratingCount').GetInt32() -eq 25) 'Caching discarded normalized facts or movie data not yet shown by the UI.'
    $automaticTarget = New-Value 'Library.VideoTarget' @($entry, $catalog, $automatic)
    Require ($null -eq (Invoke-Value $store 'Begin' @($automaticTarget, $null, $cancel))) 'A saved movie remained eligible for automatic website traffic.'
    $request = Invoke-Value $store 'Begin' @($target, $null, $cancel)
    Invoke-Value $store 'Complete' @($request, $null, 'failed', $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries[0].Title -eq 'Example') 'A failed refresh discarded the saved movie.'
    $request = Invoke-Value $store 'Begin' @($target, $null, $cancel)
    Invoke-Value $store 'EraseMetadata' @($entry, $cancel) | Out-Null
    Require (!(Invoke-Value $store 'Complete' @($request, $savedVideo, 'complete', $cancel))) 'A late website reply overwrote a newer Clear identification.'

    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first, $second)), $cancel) | Out-Null
    $otherPath = Join-Path $directory 'Other/Example.2024.mkv'
    $otherFile = New-Value 'Services.SourceFile' @(0, $otherPath, $otherPath, [long]100) @{ Complete = $true; Wanted = $true }
    Invoke-Value $sources 'Replace' @($second, (New-Values 'Services.SourceFile' @($otherFile)), $cancel) | Out-Null
    $request = Invoke-Value $store 'Begin' @($target, $null, $cancel)
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    $otherEntry = $matches.Entries | Where-Object Path -eq $otherPath
    $chosen = Invoke-Value $store 'Begin' @((New-Value 'Library.VideoTarget' @($otherEntry, 'tmdb', $explicit)), $null, $cancel)
    Require (Invoke-Value $store 'Complete' @($chosen, $video, 'success', $cancel)) 'An explicit identification was not saved.'
    Invoke-Value $sources 'Replace' @($second, $files, $cancel) | Out-Null
    Require (!(Invoke-Value $store 'Complete' @($request, $savedVideo, 'complete', $cancel))) 'A late website reply overwrote a newer identification merged by source reconciliation.'
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries.Count -eq 1 -and $matches.Entries[0].Title -eq 'Chosen title') 'Reconciliation lost the newer explicit identification.'
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first)), $cancel) | Out-Null

    $isolated = New-Value 'Services.Contribution' @('catalogues', 'Catalogues', $true, $true) @{ SavePath = $directory; Stamp = 'one' }
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first, $isolated)), $cancel) | Out-Null
    $alphaPath = Join-Path $directory 'Alpha.2024.mkv'
    $betaPath = Join-Path $directory 'Beta.2024.mkv'
    $alphaFile = New-Value 'Services.SourceFile' @(0, $alphaPath, $alphaPath, [long]100) @{ Complete = $true; Wanted = $true }
    $betaFile = New-Value 'Services.SourceFile' @(1, $betaPath, $betaPath, [long]100) @{ Complete = $true; Wanted = $true }
    Invoke-Value $sources 'Replace' @($isolated, (New-Values 'Services.SourceFile' @($alphaFile, $betaFile)), $cancel) | Out-Null
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
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first)), $cancel) | Out-Null

    $renames = New-Value 'Services.Contribution' @('renames', 'Renames', $true, $true) @{ SavePath = $directory; Stamp = 'one' }
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first, $renames)), $cancel) | Out-Null
    $beforePath = Join-Path $directory 'Before.2024.mkv'
    $deliberatePath = Join-Path $directory 'Deliberate.2024.mkv'
    $before = New-Value 'Services.SourceFile' @(0, $beforePath, $beforePath, [long]100) @{ Complete = $true; Wanted = $true }
    $deliberate = New-Value 'Services.SourceFile' @(1, $deliberatePath, $deliberatePath, [long]100) @{ Complete = $true; Wanted = $true }
    Invoke-Value $sources 'Replace' @($renames, (New-Values 'Services.SourceFile' @($before, $deliberate)), $cancel) | Out-Null
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
    $after = New-Value 'Services.SourceFile' @(0, $afterPath, $afterPath, [long]100) @{ Complete = $true; Wanted = $true }
    $renamed = New-Value 'Services.SourceFile' @(1, $renamedPath, $renamedPath, [long]100) @{ Complete = $true; Wanted = $true }
    Invoke-Value $sources 'Replace' @($renames, (New-Values 'Services.SourceFile' @($after, $renamed)), $cancel) | Out-Null
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
    $seriesKind = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.Models.VideoKind'), 'Series')
    $episodeKind = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.Models.VideoKind'), 'Episode')
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
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first)), $cancel) | Out-Null

    $batch = New-Value 'Subtitles.Acquisition' @($database, $http, [Threading.Tasks.Task]::CompletedTask, $limit)
    try {
        Invoke-Value $batch 'Load' @() | Out-Null
        $supplierType = $assembly.GetType('Syno.TinyTorrent.Models.SubtitleSupplier')
        $subdl = [Enum]::Parse($supplierType, 'SubDL')
        Invoke-Value $batch 'SelectSupplier' @($subdl) | Out-Null
        $draft = Invoke-Value $batch 'Edit' @($subdl)
        $draft.GetType().GetProperty('Secret', $flags).SetValue($draft, 'offline-fixture')
        Invoke-Value $batch 'Save' @($draft) | Out-Null
        $batchFiles = 0..3 | ForEach-Object {
            $movie = Join-Path $directory ("Batch." + $_ + ".2024.mkv")
            New-Value 'Services.SourceFile' @($_, $movie, $movie, [long]100) @{ Complete = $true; Wanted = $true }
        }
        Invoke-Value $sources 'Replace' @($first, (New-Values 'Services.SourceFile' @($batchFiles)), $cancel) | Out-Null
        $targets = Invoke-Value $batch 'Targets' @($cancel)
        $primary = @($targets | Where-Object { $_.GetType().GetProperty('Rank', $flags).GetValue($_) -eq 1 -and $_.Language -eq 'en' } | Sort-Object Path)
        Invoke-Value $batch 'Record' @($primary[0], $false, $cancel, $null) | Out-Null
        $matchType = $assembly.GetType('Syno.TinyTorrent.Models.SubtitleMatch')
        foreach ($case in @(@(1, 'Exact'), @(2, 'None'), @(3, 'Likely'))) {
            $target = $primary[$case[0]]
            Sql 'INSERT INTO subtitle_jobs(path,language,stage,outcome,match) VALUES($path,''en'',1,1,$match); INSERT INTO subtitle_job_sources(path,language,origin,file_index) VALUES($path,''en'',''first'',$index);' @{
                '$path' = $target.Path; '$index' = $case[0]; '$movie' = $target.Videos[0].GetType().GetProperty('FinalPath', $flags).GetValue($target.Videos[0]); '$match' = [int][Enum]::Parse($matchType, $case[1])
            } | Out-Null
        }
        $scopeType = $assembly.GetType('Syno.TinyTorrent.Models.SubtitleScope')
        $newScope = [Enum]::Parse($scopeType, 'New')
        $noExact = [Enum]::Parse($scopeType, 'NoExactMatch')
        $allScope = [Enum]::Parse($scopeType, 'All')
        Require ((Invoke-Value $batch 'Count' @($newScope, $null)) -eq 1 -and
            (Invoke-Value $batch 'Count' @($noExact, $null)) -eq 2 -and
            (Invoke-Value $batch 'Count' @($allScope, $null)) -eq 4) 'Batch scope confused new, exact, failed and likely matches.'
        $lookupLimit.Value = 2
        Sql 'INSERT INTO subtitle_lookups VALUES($supplier,$query,''en'',2);' @{
            '$supplier' = [int]$subdl; '$query' = (LookupKey $primary[0].Path)
        } | Out-Null
        Require ((Invoke-Value $batch 'Count' @($newScope, $null)) -eq 0 -and
            (Invoke-Value $batch 'Count' @($allScope, $null)) -eq 3) 'A batch scope bypassed the saved lookup limit.'
        $lookupLimit.Value = 5
        $selected = [Collections.Generic.HashSet[long]]::new()
        $selected.Add($primary[2].Videos[0].GetType().GetProperty('EntryId', $flags).GetValue($primary[2].Videos[0])) | Out-Null
        Require ((Invoke-Value $batch 'Count' @($noExact, $selected)) -eq 1) 'Batch count escaped the Library selection.'
        $selected.Add($primary[1].Videos[0].GetType().GetProperty('EntryId', $flags).GetValue($primary[1].Videos[0])) | Out-Null
        Require ((Invoke-Value $batch 'Count' @($newScope, $selected)) -eq 0 -and
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
        Invoke-Value $http 'Configure' @((New-Value 'Services.HttpRoute' @())) | Out-Null
        $chosenTarget = $primary[2]
        $chosenEntry = $chosenTarget.Videos[0].GetType().GetProperty('EntryId', $flags).GetValue($chosenTarget.Videos[0])
        $candidate = New-Value 'Subtitles.SubtitleCandidate' @('en', 'Fixture', 'Fixture', 'https://api.subdl.com/subtitle/fixture')
        foreach ($stale in @($true, $false)) {
            $reply = [SubtitleReply]::new()
            $client = [Net.Http.HttpClient]::new($reply)
            $http.GetType().GetField('_client', $flags).SetValue($http, $client)
            try {
                $pendingChoice = $batch.GetType().GetMethod('Choose', $flags).Invoke($batch, (Unwrap @($chosenEntry, $candidate, $cancel)))
                $reply.Ready.Task.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() | Out-Null
                if ($stale) {
                    $unwantedChoice = New-Value 'Services.SourceFile' @(2, $batchFiles[2].Path, $batchFiles[2].Path, [long]100) @{ Complete = $true; Wanted = $false }
                    Invoke-Value $sources 'Replace' @($first, (New-Values 'Services.SourceFile' @($batchFiles[0], $batchFiles[1], $unwantedChoice, $batchFiles[3])), $cancel) | Out-Null
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
                Invoke-Value $sources 'Replace' @($first, (New-Values 'Services.SourceFile' @($batchFiles)), $cancel) | Out-Null
                [IO.File]::Delete($chosenTarget.Path)
            }
        }
        Invoke-Value $batch 'SetEnabled' @($true) | Out-Null
        Sql 'UPDATE subtitle_jobs SET reference=''SubDL:fixture'' WHERE manual=1;' | Out-Null
        Invoke-Value $batch 'SelectSupplier' @([Enum]::Parse($supplierType, 'SubSource')) | Out-Null
        Require ((Sql 'SELECT COUNT(*) FROM subtitle_jobs WHERE manual=1 AND reference IS NULL;') -eq 1) 'A supplier change cancelled a batch job waiting for its download.'
        Require ($batch.GetType().GetProperty('Enabled', $flags).GetValue($batch) -and
            !$batch.GetType().GetProperty('CanCheck', $flags).GetValue($batch)) 'Choosing an unconfigured supplier erased automatic intent or allowed Check.'
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
    public bool Fail;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (request.RequestUri.AbsolutePath.Contains("/files/search")) {
            Searches++;
            if (Fail) throw new HttpRequestException("Offline connection failure");
            response.Content = new StringContent("""
                {"match":{"type":"movie","title":"Search Once","year":2024},"currentPage":1,"totalPages":1,"subtitles":[
                  {"language":"EN","unpack_files":[
                    {"language":"EN","format":"srt","release_name":"Search.Once.2024.Release1","url":"/subtitle/one"},
                    {"language":"EN","format":"srt","release_name":"Search.Once.2024.Release2","url":"/subtitle/two"},
                    {"language":"EN","format":"srt","release_name":"Search.Once.2024.Release3","url":"/subtitle/three"},
                    {"language":"EN","format":"srt","release_name":"Search.Once.2024.Release4","url":"/subtitle/four"}]}]}
                """);
        } else {
            Downloads++;
            response.Content = new StringContent("1\n00:00:00,000 --> 00:00:01,000\nFixture subtitle\n");
        }
        return Task.FromResult(response);
    }
}
'@
    $search = New-Value 'Subtitles.Acquisition' @($database, $http, [Threading.Tasks.Task]::CompletedTask, $limit)
    $reply = [SubtitleSearchReply]::new()
    $client = [Net.Http.HttpClient]::new($reply)
    try {
        Sql 'DELETE FROM subtitle_lookups;' | Out-Null
        Invoke-Value $http 'Configure' @((New-Value 'Services.HttpRoute' @())) | Out-Null
        $http.GetType().GetField('_client', $flags).SetValue($http, $client)
        Invoke-Value $search 'Initialize' @('en') | Out-Null
        Invoke-Value $search 'SelectSupplier' @([Enum]::Parse($supplierType, 'SubDL')) | Out-Null
        $draft = Invoke-Value $search 'Edit' @([Enum]::Parse($supplierType, 'SubDL'))
        $draft.GetType().GetProperty('Secret', $flags).SetValue($draft, 'offline-fixture')
        Invoke-Value $search 'Save' @($draft) | Out-Null
        $lookupLimit.Value = 1
        $movie = Join-Path $directory 'Search.Once.2024.mkv'
        [IO.File]::WriteAllBytes($movie, [byte[]]::new(100))
        $searchFile = New-Value 'Services.SourceFile' @(0, $movie, $movie, [long]100) @{ Complete = $true; Wanted = $true }
        Invoke-Value $sources 'Replace' @($first, (New-Values 'Services.SourceFile' @($searchFile)), $cancel) | Out-Null
        $operation = $search.GetType().GetField('_operation', $flags).GetValue($search)
        $operation.Wait()
        try {
            Invoke-Value $search 'SetConnected' @($true) | Out-Null
            $targets = @(Invoke-Value $search 'Targets' @($cancel) | Where-Object { $_.Language -eq 'en' } | Sort-Object Path)
            $target = @($targets | Where-Object { $_.GetType().GetProperty('Rank', $flags).GetValue($_) -eq 1 })[0]
            $connection = $database.GetType().GetField('_connection', $flags).GetValue($database)
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
                (Sql 'SELECT total FROM subtitle_lookups WHERE query=$query AND language=''en'';' @{ '$query' = (LookupKey $movie) }) -eq 1) "Alternative subtitles did not reuse the saved candidates: $($reply.Searches) searches, $($reply.Downloads) downloads."
            $primary = @($targets | Where-Object { $_.GetType().GetProperty('Rank', $flags).GetValue($_) -eq 1 })[0]
            Invoke-Value $search 'Acquire' @($primary, $cancel) | Out-Null
            Require ($reply.Searches -eq 1) 'An already accepted batch lookup bypassed the limit.'
        }
        finally { $operation.Release() | Out-Null }
        $entryId = $primary.Videos[0].GetType().GetProperty('EntryId', $flags).GetValue($primary.Videos[0])
        Invoke-Value $search 'Candidates' @($entryId, 'en', $cancel) | Out-Null
        Require ($reply.Searches -eq 2) 'The batch limit blocked an individual subtitle choice.'
        $reply.Fail = $true
        $failed = $false
        try { Invoke-Value $search 'Candidates' @($entryId, 'en', $cancel) | Out-Null }
        catch { $failed = $_.Exception.GetBaseException().GetType().Name -eq 'SubtitleException' }
        Require ($failed -and (Sql 'SELECT total FROM subtitle_lookups WHERE query=$query AND language=''en'';' @{ '$query' = (LookupKey $movie) }) -eq 2) 'A failed connection consumed the movie lookup allowance.'
    }
    finally {
        $search.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
        $client.Dispose()
        $http.GetType().GetField('_client', $flags).SetValue($http, $null)
        Sql 'DELETE FROM subtitle_jobs; DELETE FROM subtitle_output_sources; DELETE FROM subtitle_outputs; DELETE FROM subtitle_lookups; UPDATE subtitle_settings SET enabled=0,supplier=0,username='''',secret=NULL;' | Out-Null
        $lookupLimit.Value = 5
        Get-ChildItem -LiteralPath $directory -Filter 'Search.Once.2024*' | Remove-Item
    }
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    $inventory = New-Value 'Subtitles.Acquisition' @($database, $http, [Threading.Tasks.Task]::CompletedTask, $limit)
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
        $movedFile = New-Value 'Services.SourceFile' @(0, $movedPath, $movedPath, [long]100) @{ Complete = $true; Wanted = $true }
        Invoke-Value $sources 'Replace' @($first, (New-Values 'Services.SourceFile' @($movedFile)), $cancel) | Out-Null
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
        $first = New-Value 'Services.Contribution' @('first', 'First', $true, $true) @{ SavePath = $directory; Stamp = 'two' }
        Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first)), $cancel) | Out-Null
        Invoke-Value $sources 'Replace' @($first, (New-Values 'Services.SourceFile' @($movedFile)), $cancel) | Out-Null
        Invoke-Value $inventory 'Reconcile' @() | Out-Null
        Require ((Sql "SELECT COUNT(*) FROM subtitle_outputs WHERE path=`$path AND language='fr';" @{ '$path' = $laterCaption }) -eq 1) 'A changed torrent stamp did not discover a later bundled subtitle.'
        $samplePath = Join-Path $movedFolder 'Sample/Movie.sample.mkv'
        $sample = New-Value 'Services.SourceFile' @(2, $samplePath, $samplePath, [long]100) @{ Complete = $true; Wanted = $true }
        Invoke-Value $sources 'Replace' @($first, (New-Values 'Services.SourceFile' @($movedFile, $sample)), $cancel) | Out-Null
        Invoke-Value $inventory 'Rescan' @() | Out-Null
        Require ((Sql "SELECT COUNT(*) FROM subtitle_outputs WHERE path=`$path AND language='fr';" @{ '$path' = $laterCaption }) -eq 1) 'A sample video stopped a generic subtitle from belonging to its movie.'
        $otherEpisode = New-Value 'Services.SourceFile' @(1, (Join-Path $movedFolder 'Other.Episode.mkv'), (Join-Path $movedFolder 'Other.Episode.mkv'), [long]100) @{ Complete = $true; Wanted = $true }
        Invoke-Value $sources 'Replace' @($first, (New-Values 'Services.SourceFile' @($movedFile, $otherEpisode)), $cancel) | Out-Null
        Invoke-Value $inventory 'Rescan' @() | Out-Null
        Require ((Sql "SELECT COUNT(*) FROM subtitle_languages WHERE origin='first' AND language IN ('en','fr');") -eq 0) 'Generic season-folder subtitles were assigned to an arbitrary episode.'
        Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    }
    finally { $inventory.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null }
    $subtitles = New-Value 'Subtitles.Acquisition' @($database, $http, [Threading.Tasks.Task]::CompletedTask, $limit)
    Invoke-Value $subtitles 'Initialize' @('en') | Out-Null
    $caption = [IO.Path]::ChangeExtension($path, '.en.srt')
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($caption)) | Out-Null
    [IO.File]::WriteAllText($caption, "1`n00:00:00,000 --> 00:00:01,000`nFixture`n")
    Sql 'INSERT INTO subtitle_outputs(path,language,created) VALUES($path,$language,1); INSERT INTO subtitle_output_sources(path,language,origin,file_index,movie_path) VALUES($path,$language,$origin,0,$movie);' @{
        '$path' = $caption; '$language' = 'en'; '$origin' = 'first'; '$movie' = $path
    } | Out-Null
    $deletion = Invoke-Value $subtitles 'CaptureDeletion' (,[string[]]@('first'))
    $permanent = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.Models.DeletionMode'), 'Permanent')
    Invoke-Value $subtitles 'Delete' @($deletion, $permanent) | Out-Null
    Require ([IO.File]::Exists($caption)) 'A deletion ran before the refreshed source was ready.'
    Sql 'UPDATE contributions SET collected=0;' | Out-Null
    Invoke-Value $subtitles 'SetConnected' @($true) | Out-Null
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Require ([IO.File]::Exists($caption)) 'An unknown current payload mapping permitted deletion.'
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @()), $cancel) | Out-Null
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Require (![IO.File]::Exists($caption)) 'The accepted deletion was lost during source invalidation.'

    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    [IO.File]::WriteAllText($caption, "1`n00:00:00,000 --> 00:00:01,000`nShared fixture`n")
    Sql 'INSERT OR IGNORE INTO subtitle_outputs(path,language,created) VALUES($path,$language,1); INSERT OR IGNORE INTO subtitle_output_sources(path,language,origin,file_index,movie_path) VALUES($path,$language,$origin,0,$movie);' @{
        '$path' = $caption; '$language' = 'en'; '$origin' = 'first'; '$movie' = $path
    } | Out-Null
    $deletion = Invoke-Value $subtitles 'CaptureDeletion' (,[string[]]@('first'))
    Invoke-Value $subtitles 'SetConnected' @($false) | Out-Null
    Invoke-Value $subtitles 'Delete' @($deletion, $permanent) | Out-Null
    $payload = New-Value 'Services.SourceFile' @(0, $caption, $caption, [long]100) @{ Complete = $true; Wanted = $true }
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($second)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($second, (New-Values 'Services.SourceFile' @($payload)), $cancel) | Out-Null
    Invoke-Value $subtitles 'SetConnected' @($true) | Out-Null
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Require ([IO.File]::Exists($caption)) "Subtitle deletion reached another torrent's payload."
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @()), $cancel) | Out-Null
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Require ([IO.File]::Exists($caption)) 'A protected deletion was replayed after its protecting torrent disappeared.'

    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    Sql 'INSERT INTO subtitle_outputs(path,language,created) VALUES($path,$language,1); INSERT INTO subtitle_output_sources(path,language,origin,file_index,movie_path) VALUES($path,$language,$origin,0,$movie);' @{
        '$path' = $caption; '$language' = 'en'; '$origin' = 'first'; '$movie' = $path
    } | Out-Null
    $deletion = Invoke-Value $subtitles 'CaptureDeletion' (,[string[]]@('first'))
    Invoke-Value $subtitles 'SetConnected' @($false) | Out-Null
    Invoke-Value $subtitles 'Delete' @($deletion, $permanent) | Out-Null
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($second)), $cancel) | Out-Null
    $collisionPath = [IO.Path]::ChangeExtension($path, '.mp4')
    $collision = New-Value 'Services.SourceFile' @(1, $collisionPath, $collisionPath, [long]100) @{ Complete = $true; Wanted = $true }
    Invoke-Value $sources 'Replace' @($second, (New-Values 'Services.SourceFile' @($file, $collision)), $cancel) | Out-Null
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
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    Sql 'INSERT OR IGNORE INTO subtitle_outputs(path,language,created) VALUES($path,$language,1); INSERT OR IGNORE INTO subtitle_output_sources(path,language,origin,file_index,movie_path) VALUES($path,$language,$origin,0,$movie);' @{
        '$path' = $caption; '$language' = 'en'; '$origin' = 'first'; '$movie' = $path
    } | Out-Null

    $targets = Invoke-Value $subtitles 'Targets' @($cancel)
    $target = $targets | Where-Object { $_.Path -eq $caption } | Select-Object -First 1
    Sql 'DELETE FROM subtitle_outputs WHERE path=$path;' @{ '$path' = $caption } | Out-Null
    Sql 'UPDATE contributions SET collected=0;' | Out-Null
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
    $unwanted = New-Value 'Services.SourceFile' @(0, $movedMovie, $movedMovie, [long]100) @{ Complete = $true; Wanted = $false }
    Invoke-Value $sources 'Replace' @($first, (New-Values 'Services.SourceFile' @($unwanted)), $cancel) | Out-Null
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Require (![IO.File]::Exists($caption) -and [IO.File]::Exists($movedCaption)) 'A recorded caption stopped following its movie after language removal or priority zero.'
    Require ((Sql 'SELECT created FROM subtitle_outputs WHERE path=$path;' @{ '$path' = $movedCaption }) -eq 1) 'A followed caption lost its created ownership.'
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first, $second)), $cancel) | Out-Null
    $sharedMovie = New-Value 'Services.SourceFile' @(0, $movedMovie, $movedMovie, [long]100) @{ Complete = $true; Wanted = $true }
    Invoke-Value $sources 'Replace' @($second, (New-Values 'Services.SourceFile' @($sharedMovie)), $cancel) | Out-Null
    $newMovie = Join-Path $directory 'Retained/Example.2024.mkv'
    $newCaption = [IO.Path]::ChangeExtension($newMovie, '.en.srt')
    $movingMovie = New-Value 'Services.SourceFile' @(0, $newMovie, $newMovie, [long]100) @{ Complete = $true; Wanted = $false }
    Invoke-Value $sources 'Replace' @($first, (New-Values 'Services.SourceFile' @($movingMovie)), $cancel) | Out-Null
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Require ([IO.File]::Exists($movedCaption) -and ![IO.File]::Exists($newCaption)) 'A movie move took a caption from a newly shared movie at its old location.'
    Require ((Sql 'SELECT COUNT(*) FROM subtitle_output_sources WHERE path=$path AND origin=$origin;' @{
        '$path' = $movedCaption; '$origin' = 'second'
    }) -eq 1) 'A retained caption lost its new current association.'
    $requestReset = [DateTimeOffset]::UtcNow.AddMinutes(5)
    $downloadReset = $requestReset.AddMinutes(10)
    $allowance = New-Value 'Subtitles.Allowance' @($requestReset, $downloadReset)
    Invoke-Value $subtitles 'SaveAllowance' @($allowance) | Out-Null
    $savedOutputs = Sql 'SELECT COUNT(*) FROM subtitle_outputs;'
    $savedSources = Sql 'SELECT COUNT(*) FROM subtitle_output_sources;'
    Sql "INSERT INTO subtitle_lookups VALUES(0,'first 2024','en',3);" | Out-Null
    Invoke-Value (New-Value 'Library.Store' @($database)) 'SaveLookupLimit' @(7, $cancel) | Out-Null
    Require ($savedOutputs -gt 0 -and $savedSources -gt 0) 'The recovery fixture has no healthy subtitle records.'
    Sql "UPDATE subtitle_settings SET username='Restored user',secret=x'00',languages='{';" | Out-Null
    $subtitles.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $subtitles = $null
    $database.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $database = New-Value 'Services.Database' @($directory)
    $subtitles = New-Value 'Subtitles.Acquisition' @($database, $http, [Threading.Tasks.Task]::CompletedTask, $limit)
    Invoke-Value $subtitles 'Initialize' @('en') | Out-Null
    Require (!$subtitles.GetType().GetProperty('HasSecret', $flags).GetValue($subtitles)) 'An unreadable protected secret was retained.'
    Require ($subtitles.GetType().GetProperty('Username', $flags).GetValue($subtitles) -eq 'Restored user') 'Secret recovery discarded the saved account name.'
    Require ($subtitles.GetType().GetProperty('Languages', $flags).GetValue($subtitles).Length -eq 0) 'Malformed subtitle settings did not recover to defaults.'
    Require ((Sql 'SELECT secret IS NULL AND languages=''[]'' FROM subtitle_settings;') -eq 1) 'Repaired subtitle settings were not saved.'
    Require ((Sql 'SELECT COUNT(*) FROM subtitle_outputs;') -eq $savedOutputs -and
        (Sql 'SELECT COUNT(*) FROM subtitle_output_sources;') -eq $savedSources) 'Settings recovery discarded healthy subtitle outputs or ownership.'
    Require ((Invoke-Value (New-Value 'Library.Store' @($database)) 'LookupLimit' @($cancel)) -eq 7 -and
        (Sql "SELECT total FROM subtitle_lookups WHERE supplier=0 AND query='first 2024' AND language='en';") -eq 3) 'Lookup counts or their configured limit did not survive reopening.'
    $restored = $subtitles.GetType().GetField('_allowance', $flags).GetValue($subtitles)
    Require ($restored.RequestReset.ToUnixTimeMilliseconds() -eq $requestReset.ToUnixTimeMilliseconds() -and
        $restored.DownloadReset.ToUnixTimeMilliseconds() -eq $downloadReset.ToUnixTimeMilliseconds()) 'Scoped supplier deadlines did not survive reopening.'
    $subtitles.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $subtitles = $null
    Sql 'DROP TABLE subtitle_output_sources; PRAGMA user_version=1;' | Out-Null
    $database.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $database = New-Value 'Services.Database' @($directory)
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
    $database = New-Value 'Services.Database' @($directory)
    Invoke-Value $database 'ReadStatistics' @() | Out-Null
    Require ((Sql 'SELECT COUNT(*) FROM subtitle_output_sources;') -eq 0) 'Incompatible-storage recovery did not recreate subtitle ownership.'
    Write-Output 'PASS: source membership, cached payload, stale replies, deletion recovery, subtitle ownership, one-search alternatives, saved lookup limits, failure exclusion, recursive inventory, local language detection, scan markers, moves and allowance restoration.'
}
finally {
    if ($null -ne $subtitles) { $subtitles.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null }
    if ($null -ne $http) { $http.Dispose() }
    $database.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
}
