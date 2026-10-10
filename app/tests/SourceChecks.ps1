param(
    [string] $BinaryDirectory = "$PSScriptRoot/../../artifacts/bin/TinyTorrent/debug_win-x64"
)

$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $BinaryDirectory).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $binary 'TinyTorrentUI.dll'))
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

function Require([bool] $Condition, [string] $Failure) {
    if (!$Condition) { throw $Failure }
}

function Constant([string] $Name, [string] $Member) {
    $type = $assembly.GetType('Syno.TinyTorrent.' + $Name, $true)
    return $type.GetField($Member, [Reflection.BindingFlags]'Static,Public,NonPublic').GetRawConstantValue()
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

$schema = (Constant 'Library.Store' 'Schema') + (Constant 'Subtitles.Acquisition' 'Schema')
$projection = (Constant 'Services.FileSources' 'Schema') + (Constant 'Library.FileFacts' 'Schema') + (Constant 'Library.Store' 'Projection')
$cleanup = (Constant 'Library.Store' 'Cleanup') + (Constant 'Subtitles.Acquisition' 'Cleanup')
$reconcile = (Constant 'Library.Store' 'Reconcile') + (Constant 'Library.FileFacts' 'Reconcile')
$invalidate = Constant 'Library.Store' 'Invalidate'
$database = New-Value 'Services.Database' @($directory, $schema, $projection)
$subtitles = $null
$http = $null
try {
    $sources = New-Value 'Services.FileSources' @($database, $cleanup, $reconcile, $invalidate)
    $store = New-Value 'Library.Store' @($database)
    Invoke-Value $store 'Recover' @($cancel) | Out-Null
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
    $lateTarget = New-Value 'Library.VideoTarget' @($entry, 'tmdb', $false)
    $late = Invoke-Value $store 'Begin' @($lateTarget, $null, $cancel)
    Require ($null -ne $late) 'An unattempted movie could not start enrichment.'
    $video = New-Value 'Library.VideoInformation' @('tmdb', 'movie/1', 'Chosen title', $movieKind)
    $chosen = Invoke-Value $store 'Begin' @((New-Value 'Library.VideoTarget' @($entry, 'tmdb', $true)), $null, $cancel)
    Require (Invoke-Value $store 'Complete' @($chosen, $video, 'success', $cancel)) 'An explicit identification was not saved.'
    $mixed = New-Value 'Library.Query' @('2024 chosen', $configuration)
    $matches = Invoke-Value $store 'Search' @($mixed, $cancel)
    Require ($matches.Entries.Count -eq 1 -and $matches.Counts[$configuration] -eq 1 -and $matches.Entries[0].Title -eq 'Chosen title') 'Search and its row summary did not reflect the saved identification.'
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
    $mixed = New-Value 'Library.Query' @('2024 nonexistentfilm', $configuration)
    $matches = Invoke-Value $store 'Search' @($mixed, $cancel)
    Require ($matches.Entries.Count -eq 0 -and $matches.Counts[$configuration] -eq 0) 'An unmatched term reused an earlier video match or behaved as OR across terms.'
    $connection = $database.GetType().GetField('_connection', $flags).GetValue($database)
    $command = $connection.CreateCommand()
    try {
        $command.CommandText = "INSERT INTO file_facts VALUES ('first',0,1700000000,1700000001,'','','',NULL,NULL,'',NULL,'');"
        $command.ExecuteNonQuery() | Out-Null
    }
    finally { $command.Dispose() }

    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first, $second)), $cancel) | Out-Null
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
    $database = New-Value 'Services.Database' @($directory, $schema, $projection)
    $sources = New-Value 'Services.FileSources' @($database, $cleanup, $reconcile, $invalidate)
    $store = New-Value 'Library.Store' @($database)
    Invoke-Value $store 'Recover' @($cancel) | Out-Null
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($second)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($second, $files, $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries[0].Title -eq 'Chosen title' -and $matches.Entries[0].Created -eq 1700000000) 'Saved identification or properties did not survive reopening.'

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
    $missTarget = New-Value 'Library.VideoTarget' @($entry, 'tmdb', $false)
    $miss = Invoke-Value $store 'Begin' @($missTarget, $null, $cancel)
    Require ($null -ne $miss -and (Invoke-Value $store 'Complete' @($miss, $null, 'no_match', $cancel))) 'An ordinary miss was not recorded.'
    $target = New-Value 'Library.VideoTarget' @($entry, $catalog, $false)
    $request = Invoke-Value $store 'Begin' @($target, $null, $cancel)
    Require ($null -ne $request) 'An unattempted empty movie could not start its website lookup.'
    Invoke-Value $store 'Complete' @($request, $null, 'failed', $cancel) | Out-Null
    $settings = [Collections.Generic.Dictionary[string,string]]::new()
    $settings.Add('website', 'rottentomatoes')
    $settings.Add('delay', '7')
    Invoke-Value $store 'SaveSettings' @('public', $settings, $cancel) | Out-Null
    Invoke-Value $store 'SetProvider' @('public', $cancel) | Out-Null
    $database.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $database = New-Value 'Services.Database' @($directory, $schema, $projection)
    $sources = New-Value 'Services.FileSources' @($database, $cleanup, $reconcile, $invalidate)
    $store = New-Value 'Library.Store' @($database)
    Invoke-Value $store 'Recover' @($cancel) | Out-Null
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    $entry = $matches.Entries[0]
    $target = New-Value 'Library.VideoTarget' @($entry, $catalog, $false)
    Require (!(Invoke-Value $store 'CanLookup' @($target, $cancel))) 'Reopening retried a refused website lookup automatically.'
    $missTarget = New-Value 'Library.VideoTarget' @($entry, 'tmdb', $false)
    Require (!(Invoke-Value $store 'CanLookup' @($missTarget, $cancel)) -and $null -eq (Invoke-Value $store 'Next' @('tmdb', $cancel))) 'Reopening retried an ordinary no-match result.'
    $restoredSettings = Invoke-Value $store 'Settings' @('public', $cancel)
    Require ((Invoke-Value $store 'Provider' @($cancel)) -eq 'public' -and $restoredSettings['website'] -eq 'rottentomatoes' -and $restoredSettings['delay'] -eq '7') 'Source selection or provider-owned settings did not survive reopening.'

    $http = New-Value 'Services.ProviderHttp' @()
    $websites = $assembly.GetType('Syno.TinyTorrent.Library.Public.Provider').GetProperty('Websites', [Reflection.BindingFlags]'Static,Public,NonPublic').GetValue($null)
    foreach ($site in $websites) {
        $adapter = New-Value 'Library.Public.Provider' @($http, (New-Value 'Library.Public.Settings' @($site, 5)))
        $address = if ($site.WebsiteId -eq 'imdb') { 'https://www.imdb.com/title/tt1234567/' } else { 'https://www.rottentomatoes.com/m/example' }
        $html = '<script type="application/ld+json">' + (@{
            '@type' = 'Movie'; url = $address; name = 'Example'; datePublished = '2024-01-01'
            description = 'A fictional movie used by checks.'; genre = @('Drama'); actor = @(@{ name = 'Example actor' })
            aggregateRating = @{ ratingValue = 8.5; ratingCount = 25 }; keywords = 'fixture'
        } | ConvertTo-Json -Depth 5 -Compress) + '</script>'
        if ($site.WebsiteId -eq 'rottentomatoes') { $html += '<rt-text data-qa="synopsis-value">A fictional movie used by checks.</rt-text>' }
        $describe = $adapter.GetType().GetMethod('Describe', $flags, $null, [Type[]]@([string], [Uri]), $null)
        $websiteVideo = $describe.Invoke($adapter, @($html, [Uri]$address))
        Require ($websiteVideo.CatalogId -eq ('public/' + $site.WebsiteId) -and $websiteVideo.Title -eq 'Example' -and $websiteVideo.GetType().GetProperty('Year', $flags).GetValue($websiteVideo) -eq 2024) 'A website fixture lost its catalogue or movie identity.'
        $rejected = $false
        try { $describe.Invoke($adapter, @($html, [Uri]($address.TrimEnd('/') + '_other'))) | Out-Null }
        catch { $rejected = $true }
        Require $rejected 'Movie data from a different canonical page was accepted.'
        if ($site.WebsiteId -eq 'rottentomatoes') { $savedVideo = $websiteVideo }
    }
    $target = New-Value 'Library.VideoTarget' @($entry, $catalog, $true)
    $request = Invoke-Value $store 'Begin' @($target, $null, $cancel)
    Require (Invoke-Value $store 'Complete' @($request, $savedVideo, 'complete', $cancel)) 'A requested website result was not saved.'
    $cached = Invoke-Value $store 'Saved' @($target, 'es', $cancel)
    $records = $cached.GetType().GetProperty('Records', $flags).GetValue($cached)
    Require ($cached.Title -eq 'Example' -and $records.Count -eq 1 -and $records[0].Content.GetProperty('aggregateRating').GetProperty('ratingCount').GetInt32() -eq 25) 'Caching discarded normalized facts or movie data not yet shown by the UI.'
    Require (!(Invoke-Value $store 'CanLookup' @($target, $cancel))) 'A saved movie remained eligible for automatic website traffic.'
    $request = Invoke-Value $store 'Begin' @($target, $null, $cancel)
    Invoke-Value $store 'Complete' @($request, $null, 'failed', $cancel) | Out-Null
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    Require ($matches.Entries[0].Title -eq 'Example') 'A failed refresh discarded the saved movie.'
    $request = Invoke-Value $store 'Begin' @($target, $null, $cancel)
    Invoke-Value $store 'ClearIdentification' @($entry, $cancel) | Out-Null
    Require (!(Invoke-Value $store 'Complete' @($request, $savedVideo, 'complete', $cancel))) 'A late website reply overwrote a newer Clear identification.'

    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first, $second)), $cancel) | Out-Null
    $otherPath = Join-Path $directory 'Other/Example.2024.mkv'
    $otherFile = New-Value 'Services.SourceFile' @(0, $otherPath, $otherPath, [long]100) @{ Complete = $true; Wanted = $true }
    Invoke-Value $sources 'Replace' @($second, (New-Values 'Services.SourceFile' @($otherFile)), $cancel) | Out-Null
    $request = Invoke-Value $store 'Begin' @($target, $null, $cancel)
    $matches = Invoke-Value $store 'Search' @($query, $cancel)
    $otherEntry = $matches.Entries | Where-Object Path -eq $otherPath
    $chosen = Invoke-Value $store 'Begin' @((New-Value 'Library.VideoTarget' @($otherEntry, 'tmdb', $true)), $null, $cancel)
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
        $isolatedTarget = New-Value 'Library.VideoTarget' @($isolatedEntry, $case.Catalog, $true)
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
        $isolatedTarget = New-Value 'Library.VideoTarget' @($matches.Entries[0], $case.Catalog, $true)
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
        $renameTarget = New-Value 'Library.VideoTarget' @($renameEntry, $catalog, $case.Manual)
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
    $detail = Invoke-Value $store 'Detail' @($afterEntry.EntryId, $cancel)
    Require ($afterEntry.Title -eq 'After' -and $afterEntry.Type -eq '' -and $detail.Entry.Title -eq 'After' -and
        $detail.Cast -eq '' -and $detail.Synopsis -eq '') 'A renamed file displayed stale automatic movie details before lookup.'
    $oldTitle = New-Value 'Library.Query' @('Before', $configuration)
    Require ((Invoke-Value $store 'Search' @($oldTitle, $cancel)).Entries.Count -eq 0) 'A renamed file remained searchable by its stale automatic movie title.'
    $renameTarget = New-Value 'Library.VideoTarget' @($afterEntry, $catalog, $false)
    $attempt = Invoke-Value $store 'Begin' @($renameTarget, $null, $cancel)
    Require ($null -ne $attempt -and $null -eq $attempt.Choice) 'A renamed file reused its old automatic choice instead of matching again.'
    Require (Invoke-Value $store 'Complete' @($attempt, $null, 'no_match', $cancel)) 'A renamed file could not record an ordinary miss.'
    Require ((Sql "SELECT COUNT(*) FROM identifications WHERE origin='renames' AND file_index=0 AND video_id IS NULL AND explicit=0 AND evidence='After.2025.mkv';") -eq 1) 'A renamed-file miss retained its stale automatic identification.'
    $deliberateEntry = $matches.Entries | Where-Object Path -eq $renamedPath
    $detail = Invoke-Value $store 'Detail' @($deliberateEntry.EntryId, $cancel)
    Require ($deliberateEntry.Title -eq 'Kept choice' -and $detail.Synopsis -eq 'Saved synopsis') 'Renaming discarded an explicit movie choice.'
    $renameTarget = New-Value 'Library.VideoTarget' @($deliberateEntry, $catalog, $true)
    $attempt = Invoke-Value $store 'Begin' @($renameTarget, $null, $cancel)
    Require ($null -ne $attempt -and $attempt.Choice.VideoId -eq 'deliberate') 'An explicit refresh lost the accepted choice after rename.'
    Require (Invoke-Value $store 'Complete' @($attempt, $null, 'failed', $cancel)) 'A renamed file could not record its failed explicit refresh.'
    $detail = Invoke-Value $store 'Detail' @($deliberateEntry.EntryId, $cancel)
    Require ($detail.Entry.Title -eq 'Kept choice' -and $detail.Synopsis -eq 'Saved synopsis') 'A failed refresh discarded the explicit choice of a renamed file.'
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first)), $cancel) | Out-Null

    $subtitles = New-Value 'Subtitles.Acquisition' @($database, $http)
    Invoke-Value $subtitles 'Initialize' @('en') | Out-Null
    $caption = [IO.Path]::ChangeExtension($path, '.en.srt')
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($caption)) | Out-Null
    [IO.File]::WriteAllText($caption, "1`n00:00:00,000 --> 00:00:01,000`nFixture`n")
    Sql 'INSERT INTO subtitle_sources VALUES($path,$language,$origin,0); INSERT INTO subtitle_outputs VALUES($path,$language,1);' @{
        '$path' = $caption; '$language' = 'en'; '$origin' = 'first'
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
    Sql 'INSERT OR IGNORE INTO subtitle_sources VALUES($path,$language,$origin,0); INSERT OR IGNORE INTO subtitle_outputs VALUES($path,$language,1);' @{
        '$path' = $caption; '$language' = 'en'; '$origin' = 'first'
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
    Sql 'INSERT INTO subtitle_sources VALUES($path,$language,$origin,0); INSERT INTO subtitle_outputs VALUES($path,$language,1);' @{
        '$path' = $caption; '$language' = 'en'; '$origin' = 'first'
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
INSERT INTO subtitle_sources VALUES($present,'en','second',0),($missing,'es','second',0);
INSERT INTO subtitle_outputs VALUES($present,'en',1),($missing,'es',1);
'@ @{ '$present' = $caption; '$missing' = $missingCaption } | Out-Null
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Invoke-Value $subtitles 'Recheck' @() | Out-Null
    Require ($null -ne $subtitles.GetType().GetProperty('LastRecheck', $flags).GetValue($subtitles)) 'Recheck did not complete for the ambiguous destinations.'
    Require ($subtitles.GetType().GetProperty('RecheckTotal', $flags).GetValue($subtitles) -eq 0 -and
        $subtitles.GetType().GetProperty('Rechecked', $flags).GetValue($subtitles) -eq 0) 'Recheck included ambiguous caption destinations.'
    Require ([IO.File]::ReadAllText($caption) -ceq "1`n00:00:00,000 --> 00:00:01,000`nShared fixture`n" -and
        ![IO.File]::Exists($missingCaption)) 'Recheck changed files at ambiguous caption destinations.'
    Require ((Sql 'SELECT COUNT(*) FROM subtitle_outputs;') -eq 2 -and
        (Sql 'SELECT COUNT(*) FROM subtitle_outputs WHERE created=1 AND ((path=$present AND language=''en'') OR (path=$missing AND language=''es''));' @{
            '$present' = $caption; '$missing' = $missingCaption
        }) -eq 2) 'Reconciliation or Recheck discarded or relabeled recorded ownership at an ambiguous caption destination.'
    Require ((Sql 'SELECT COUNT(*) FROM subtitle_sources;') -eq 2 -and
        (Sql 'SELECT COUNT(*) FROM subtitle_sources WHERE origin=''second'' AND file_index=0 AND ((path=$present AND language=''en'') OR (path=$missing AND language=''es''));' @{
            '$present' = $caption; '$missing' = $missingCaption
        }) -eq 2) 'Reconciliation or Recheck changed existing associations or satisfied the other ambiguous movie.'
    Invoke-Value $subtitles 'SetLanguages' (,[string[]]@()) | Out-Null
    Invoke-Value $sources 'Reconcile' @((New-Values 'Services.Contribution' @($first)), $cancel) | Out-Null
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null
    Sql 'INSERT OR IGNORE INTO subtitle_sources VALUES($path,$language,$origin,0);' @{
        '$path' = $caption; '$language' = 'en'; '$origin' = 'first'
    } | Out-Null

    $targets = Invoke-Value $subtitles 'Targets' @($cancel)
    $target = $targets | Where-Object { $_.Path -eq $caption } | Select-Object -First 1
    Sql 'DELETE FROM subtitle_outputs WHERE path=$path;' @{ '$path' = $caption } | Out-Null
    Sql 'UPDATE contributions SET collected=0;' | Out-Null
    Invoke-Value $subtitles 'Record' @($target, $true, $cancel) | Out-Null
    Require ((Sql 'SELECT created FROM subtitle_outputs WHERE path=$path;' @{ '$path' = $caption }) -eq 1) 'A committed creation lost its ownership during recollection.'
    Invoke-Value $sources 'Replace' @($first, $files, $cancel) | Out-Null

    $saveFailure = [Enum]::Parse($assembly.GetType('Syno.TinyTorrent.Models.SubtitleFailure'), 'Save')
    $failedPath = Join-Path $directory 'Unwritable.en.srt'
    Invoke-Value $subtitles 'ReportFile' @($failedPath, $saveFailure) | Out-Null
    Invoke-Value $subtitles 'Record' @($target, $true, $cancel) | Out-Null
    Invoke-Value $subtitles 'Reconcile' @() | Out-Null
    Require ($subtitles.GetType().GetProperty('FileFailures', $flags).GetValue($subtitles).ContainsKey($failedPath)) 'A successful caption hid a different file failure.'
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
    Require ((Sql 'SELECT COUNT(*) FROM subtitle_sources WHERE path=$path AND origin=$origin;' @{
        '$path' = $movedCaption; '$origin' = 'second'
    }) -eq 1) 'A retained caption lost its new current association.'
    $requestReset = [DateTimeOffset]::UtcNow.AddMinutes(5)
    $downloadReset = $requestReset.AddMinutes(10)
    $allowance = New-Value 'Subtitles.Allowance' @($requestReset, $downloadReset)
    Invoke-Value $subtitles 'SaveAllowance' @($allowance) | Out-Null
    $savedOutputs = Sql 'SELECT COUNT(*) FROM subtitle_outputs;'
    $savedSources = Sql 'SELECT COUNT(*) FROM subtitle_sources;'
    Require ($savedOutputs -gt 0 -and $savedSources -gt 0) 'The recovery fixture has no healthy subtitle records.'
    Sql "UPDATE subtitle_settings SET username='Restored user',secret=x'00',languages='{',checked_at='not-a-date';" | Out-Null
    $subtitles.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $subtitles = $null
    $database.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $database = New-Value 'Services.Database' @($directory, $schema, $projection)
    $subtitles = New-Value 'Subtitles.Acquisition' @($database, $http)
    Invoke-Value $subtitles 'Initialize' @('en') | Out-Null
    Require (!$subtitles.GetType().GetProperty('HasSecret', $flags).GetValue($subtitles)) 'An unreadable protected secret was retained.'
    Require ($subtitles.GetType().GetProperty('Username', $flags).GetValue($subtitles) -eq 'Restored user') 'Secret recovery discarded the saved account name.'
    Require ($subtitles.GetType().GetProperty('Languages', $flags).GetValue($subtitles).Length -eq 0 -and
        $null -eq $subtitles.GetType().GetProperty('LastRecheck', $flags).GetValue($subtitles)) 'Malformed subtitle settings did not recover to defaults.'
    Require ((Sql 'SELECT secret IS NULL AND languages=''[]'' AND checked_at IS NULL FROM subtitle_settings;') -eq 1) 'Repaired subtitle settings were not saved.'
    Require ((Sql 'SELECT COUNT(*) FROM subtitle_outputs;') -eq $savedOutputs -and
        (Sql 'SELECT COUNT(*) FROM subtitle_sources;') -eq $savedSources) 'Settings recovery discarded healthy subtitle outputs or ownership.'
    $restored = $subtitles.GetType().GetField('_allowance', $flags).GetValue($subtitles)
    Require ($restored.RequestReset.ToUnixTimeMilliseconds() -eq $requestReset.ToUnixTimeMilliseconds() -and
        $restored.DownloadReset.ToUnixTimeMilliseconds() -eq $downloadReset.ToUnixTimeMilliseconds()) 'Scoped supplier deadlines did not survive reopening.'
    Require ((Sql 'SELECT reason FROM subtitle_failures WHERE path=$path;' @{ '$path' = $failedPath }) -eq [int]$saveFailure) 'The affected file failure did not survive reopening.'
    Require ($subtitles.GetType().GetProperty('FileFailures', $flags).GetValue($subtitles).ContainsKey($failedPath)) 'An offline reopened window hid a persisted file failure.'
    $subtitles.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
    $subtitles = $null
    Write-Output 'PASS: search/count filters, shared identity, stale enrichment, durable misses and failures, catalogue isolation, complete cached payload, failed refresh preservation, stale reply rejection, accepted deletion recovery, payload protection, ambiguous Recheck, committed ownership, persistent file failures and scoped allowance restoration.'
}
finally {
    if ($null -ne $subtitles) { $subtitles.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null }
    if ($null -ne $http) { $http.Dispose() }
    $database.DisposeAsync().AsTask().GetAwaiter().GetResult() | Out-Null
}
