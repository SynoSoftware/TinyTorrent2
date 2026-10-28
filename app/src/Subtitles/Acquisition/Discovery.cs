using System.Text.Json;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Storage;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition
{
    // Movies whose scan the person stopped or whose folder could not be read,
    // by scan marker key. They are not scanned automatically again this
    // session until their path or stamp changes.
    private readonly HashSet<(string Origin, int Index, string Path, string Stamp)> _skippedScans = [];
    private IReadOnlySet<long> _displayed = new HashSet<long>();
    private Task? _inspecting;
    private bool _inspectPending;

    /// <summary>
    /// Sets the Library entries on screen. Their folders are checked for
    /// subtitle files once per path and torrent stamp, in the background, as
    /// the Library contract's no-scan ruling allows.
    /// </summary>
    internal void SetDisplayed(IReadOnlySet<long> entries)
    {
        lock (_requestSync)
        {
            var same = _displayed.SetEquals(entries);
            _displayed = entries;
            if (!same)
                QueueInspect();
        }
    }

    // Callers hold _requestSync. The check runs on its own task, so a folder
    // walk that hangs on a sleeping drive never delays a reconciliation.
    private void QueueInspect()
    {
        if (_disposed || !_connected || _displayed.Count == 0)
            return;
        _inspectPending = true;
        _inspecting ??= Task.Run(InspectChanges);
    }

    private async Task InspectChanges()
    {
        try
        {
            while (true)
            {
                lock (_requestSync)
                {
                    if (!_inspectPending || _disposed || !_connected)
                    {
                        _inspecting = null;
                        return;
                    }
                    _inspectPending = false;
                }
                await Inspect().ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_requestSync)
                _inspecting = null;
            throw;
        }
    }

    // Checks the folders of the displayed movies that are not scanned at their
    // path and stamp yet. The person did not start this check, so it shows no
    // work item and no failure, and it holds _operation only to record what it
    // found, so other subtitle work never waits for its folder walk.
    private async Task Inspect()
    {
        try
        {
            CancellationToken cancellation;
            IReadOnlySet<long> displayed;
            lock (_requestSync)
            {
                if (_disposed || !_connected)
                    return;
                cancellation = _requests.Token;
                displayed = _displayed;
            }
            if (displayed.Count == 0)
                return;
            bool Pending(Discovery movie) =>
                displayed.Contains(movie.Video.EntryId) && Unscanned(movie);
            // The displayed movies are usually scanned already, so only one that
            // is not reads the whole library, whose other movies in the same
            // folder decide which movie a subtitle file belongs to.
            var shown = await Movies(cancellation, displayed).ConfigureAwait(false);
            lock (_requestSync)
                if (!shown.Any(Pending))
                    return;
            var movies = await Movies(cancellation).ConfigureAwait(false);
            HashSet<Discovery> accepted;
            lock (_requestSync)
                accepted = movies.Where(Pending).ToHashSet();
            if (accepted.Count == 0)
                return;
            await Scan(null, movies, accepted, cancellation).ConfigureAwait(false);
            await _operation.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                await RefreshCounts().ConfigureAwait(false);
            }
            finally
            {
                _operation.Release();
            }
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Subtitle file check of displayed movies cancelled.");
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
    }

    // Whether an automatic scan still has to check the movie. Callers hold
    // _requestSync.
    private bool Unscanned(Discovery movie) =>
        movie.Video.Eligible
        && movie.Video.Readable
        && !movie.Scanned
        && !_skippedScans.Contains(movie.Key);

    internal async Task<ScanResult> Rescan()
    {
        var run = new WorkRun(this, SubtitleWork.Discovery);
        CancellationToken cancellation;
        lock (_requestSync)
            cancellation = _requests.Token;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(
            cancellation,
            run.Token
        );
        cancellation = request.Token;
        var entered = false;
        try
        {
            await _operation.WaitAsync(cancellation).ConfigureAwait(false);
            entered = true;
            run.Report(new WorkProgress.Unknown(), new WorkExecution.Running());
            var paths = await Run(
                    connection =>
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText =
                            "SELECT DISTINCT path FROM subtitle_outputs WHERE created=1;";
                        using var reader = command.ExecuteReader();
                        var paths = new List<string>();
                        while (reader.Read())
                            paths.Add(reader.GetString(0));
                        return paths;
                    },
                    cancellation
                )
                .ConfigureAwait(false);
            var missing = new List<string>();
            var unreadable = await Task.Run(
                    () =>
                    {
                        var failures = 0;
                        foreach (var path in paths)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            try
                            {
                                if (!SubtitleFile.Exists(path))
                                    missing.Add(path);
                            }
                            catch (Exception error)
                                when (error is IOException or UnauthorizedAccessException)
                            {
                                failures++;
                            }
                        }
                        return failures;
                    },
                    cancellation
                )
                .ConfigureAwait(false);
            if (missing.Count > 0)
            {
                await Run(
                        connection =>
                        {
                            foreach (var path in missing)
                                Execute(
                                    connection,
                                    "DELETE FROM subtitle_outputs WHERE path=$path AND created=1;",
                                    ("$path", path)
                                );
                            Execute(connection, Database.Invalidate);
                            return true;
                        },
                        cancellation
                    )
                    .ConfigureAwait(false);
                LanguagesChanged?.Invoke(this, EventArgs.Empty);
            }
            var movies = await Movies(cancellation).ConfigureAwait(false);
            var accepted = movies
                .Where(movie => movie.Video.Eligible && movie.Video.Readable)
                .ToHashSet();
            var result = await Scan(run, movies, accepted, cancellation).ConfigureAwait(false);
            await RefreshCounts().ConfigureAwait(false);
            run.Operation.Summarize("scan_result", result.Found, result.Unreadable + unreadable);
            run.Operation.Outcome = result.Unreadable + unreadable > 0
                ? WorkOutcome.Failed : WorkOutcome.Completed;
            return result with { Unreadable = result.Unreadable + unreadable };
        }
        catch (OperationCanceledException)
        {
            run.Operation.Outcome = WorkOutcome.Cancelled;
            throw;
        }
        catch (Exception error)
        {
            run.Operation.Error = error;
            throw;
        }
        finally
        {
            try
            {
                await run.Finish().ConfigureAwait(false);
            }
            finally
            {
                if (entered)
                    _operation.Release();
            }
            Notify();
        }
    }

    // Scans the folders of torrents that just finished, before their
    // completion search, so a bundled subtitle stops a download. It shows in
    // the work list only when it has movies to scan.
    private async Task Discover(CancellationToken cancellation)
    {
        var movies = await Movies(cancellation).ConfigureAwait(false);
        HashSet<Discovery> accepted;
        lock (_requestSync)
            accepted = movies.Where(movie => movie.Video.JustFinished && Unscanned(movie)).ToHashSet();
        if (accepted.Count == 0)
            return;
        var run = new WorkRun(this, SubtitleWork.Discovery);
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(
                cancellation,
                run.Token
            );
            var result = await Scan(run, movies, accepted, request.Token).ConfigureAwait(false);
            run.Operation.Summarize("scan_result", result.Found, result.Unreadable);
            run.Operation.Outcome = result.Unreadable > 0
                ? WorkOutcome.Failed : WorkOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            run.Operation.Outcome = WorkOutcome.Cancelled;
            throw;
        }
        catch (Exception error)
        {
            run.Operation.Error = error;
            throw;
        }
        finally
        {
            await run.Finish().ConfigureAwait(false);
        }
    }

    // Reads every movie, or with entries only those Library entries.
    private async Task<List<Discovery>> Movies(
        CancellationToken cancellation,
        IReadOnlySet<long>? entries = null
    )
    {
        var movies = await Run(
                connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT f.origin,l.path FROM collected_files f JOIN locations l USING(entry_id)
                        WHERE $entries IS NULL OR f.origin IN (SELECT origin FROM collected_files
                            WHERE entry_id IN (SELECT value FROM json_each($entries)));
                        """;
                    command.Parameters.AddWithValue(
                        "$entries",
                        entries is null ? DBNull.Value : JsonSerializer.Serialize(entries)
                    );
                    var folders = new Dictionary<string, string?>(StringComparer.Ordinal);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            var origin = reader.GetString(0);
                            var path = reader.GetString(1);
                            var parent = Path.GetDirectoryName(path);
                            if (folders.TryGetValue(origin, out var folder))
                            {
                                while (folder is not null && !Within(path, folder))
                                    folder = Path.GetDirectoryName(folder);
                                parent = folder;
                            }
                            folders[origin] = parent;
                        }
                    command.CommandText = $"""
                        SELECT s.origin,s.file_index,s.path,l.path,s.size,s.complete,f.readable,s.entry_id,s.wanted,
                            c.save_path,c.stamp,EXISTS(SELECT 1 FROM subtitle_scans a WHERE a.origin=s.origin
                                AND a.file_index=s.file_index AND a.path=l.path COLLATE PATH AND a.stamp=c.stamp),
                            s.origin IN ({Finished})
                        FROM collected_files s JOIN locations l USING(entry_id)
                        JOIN current_files f USING(entry_id) JOIN file_reads c USING(origin)
                        WHERE s.kind=$kind AND ($entries IS NULL
                            OR f.entry_id IN (SELECT value FROM json_each($entries)));
                        """;
                    command.Parameters.AddWithValue("$kind", (int)FileKind.Video);
                    var result = new List<Discovery>();
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            var video = new Video(
                                reader.GetString(0),
                                reader.GetInt32(1),
                                reader.GetString(3)
                            )
                            {
                                Path = reader.GetString(2),
                                Size = reader.GetInt64(4),
                                Complete = reader.GetBoolean(5),
                                Readable = reader.GetBoolean(6),
                                EntryId = reader.GetInt64(7),
                                Wanted = reader.GetBoolean(8),
                                JustFinished = reader.GetBoolean(12),
                            };
                            var folder = folders[video.Origin];
                            var savePath = reader.GetString(9);
                            var recursive =
                                folder is not null
                                && savePath.Length > 0
                                && Within(folder, savePath);
                            result.Add(
                                new(
                                    video,
                                    recursive ? folder! : Path.GetDirectoryName(video.FinalPath)!,
                                    recursive,
                                    reader.GetString(10),
                                    reader.GetBoolean(11)
                                )
                            );
                        }
                    return result;
                },
                cancellation
            )
            .ConfigureAwait(false);
        // A full read forgets the skipped scans of movies whose path or stamp
        // changed.
        if (entries is null)
            lock (_requestSync)
                _skippedScans.IntersectWith(
                    movies.Where(movie => !movie.Scanned).Select(movie => movie.Key)
                );
        return movies;
    }

    // Finds the subtitles beside the accepted movies and records them. Without
    // a run the scan shows nothing in the work list.
    private async Task<ScanResult> Scan(
        WorkRun? run,
        List<Discovery> movies,
        HashSet<Discovery> accepted,
        CancellationToken cancellation
    )
    {
        if (run is not null)
            lock (_requestSync)
                run.Scans = accepted.ToArray();
        var changed = false;
        try
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tags = Suppliers
                .SelectMany(supplier => supplier.Languages)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var failures = 0;
            var folders = movies
                .GroupBy(movie => (movie.Folder, movie.Recursive))
                .Where(folder => folder.Any(accepted.Contains))
                .ToArray();
            var entries = folders
                .Select(folder => folder.Key.Folder)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    folder => folder,
                    _ => new HashSet<long>(),
                    StringComparer.OrdinalIgnoreCase
                );
            foreach (var movie in movies.Where(movie => movie.Video.Eligible))
            {
                cancellation.ThrowIfCancellationRequested();
                for (
                    var parent = Path.GetDirectoryName(movie.Video.FinalPath);
                    parent is not null;
                    parent = Path.GetDirectoryName(parent)
                )
                    if (entries.TryGetValue(parent, out var contained) && contained.Count < 2)
                        contained.Add(movie.Video.EntryId);
            }
            var done = 0;
            foreach (var folder in folders)
            {
                cancellation.ThrowIfCancellationRequested();
                run?.Report(
                    new WorkProgress.Fraction(done++, folders.Length, WorkUnit.Folders),
                    new WorkExecution.Running()
                );
                var pending = folder.Where(accepted.Contains).ToArray();
                // Samples and unselected files are not movies, so a Sample folder
                // leaves Subs/English.srt to the one movie beside it.
                var sole = entries[folder.Key.Folder].Count == 1;
                List<(Discovery Movie, string Path, string Language)> records;
                try
                {
                    records = await Task.Run(
                            async () =>
                            {
                                var result = new List<(Discovery, string, string)>();
                                foreach (
                                    var file in SubtitleFiles(
                                        folder.Key.Folder,
                                        folder.Key.Recursive
                                    )
                                )
                                {
                                    cancellation.ThrowIfCancellationRequested();
                                    var matches = folder
                                        .Where(movie =>
                                            movie.Video.Eligible
                                            && SubtitleFile.BelongsTo(
                                                file,
                                                movie.Video.FinalPath,
                                                folder.Key.Folder,
                                                sole
                                            )
                                        )
                                        .ToArray();
                                    if (
                                        matches
                                            .Select(movie => movie.Video.EntryId)
                                            .Distinct()
                                            .Count() != 1
                                    )
                                        continue;
                                    if (!matches.Any(pending.Contains))
                                        continue;
                                    var language = await SubtitleFile
                                        .Identify(
                                            file,
                                            matches[0].Video.FinalPath,
                                            folder.Key.Folder,
                                            tags,
                                            cancellation
                                        )
                                        .ConfigureAwait(false);
                                    if (language is not null)
                                        foreach (var movie in matches.Where(pending.Contains))
                                            result.Add((movie, file, language));
                                }
                                return result;
                            },
                            cancellation
                        )
                        .ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    failures++;
                    // A displayed movie in this folder then fails once, not on
                    // every scroll.
                    lock (_requestSync)
                        _skippedScans.UnionWith(pending.Select(movie => movie.Key));
                    continue;
                }
                // The quiet check takes _operation only for this step; the
                // other scans hold it already.
                if (run is null)
                    await _operation.WaitAsync(cancellation).ConfigureAwait(false);
                var outdated = new List<Discovery>();
                try
                {
                    changed |= await Run(
                        connection =>
                        {
                            var recorded = false;
                            foreach (var movie in pending)
                            {
                                var video = movie.Video;
                                using var stamp = connection.CreateCommand();
                                stamp.CommandText =
                                    "SELECT stamp FROM file_reads WHERE origin=$origin;";
                                stamp.Parameters.AddWithValue("$origin", video.Origin);
                                if (
                                    !Current(connection, [video], readable: true)
                                    || !Equals(stamp.ExecuteScalar(), movie.Stamp)
                                )
                                {
                                    outdated.Add(movie);
                                    continue;
                                }
                                Execute(
                                    connection,
                                    """
                                    DELETE FROM subtitle_output_sources WHERE origin=$origin AND file_index=$index AND EXISTS (
                                        SELECT 1 FROM subtitle_outputs o WHERE o.path=subtitle_output_sources.path COLLATE PATH
                                            AND o.language=subtitle_output_sources.language AND o.created=0);
                                    """,
                                    ("$origin", video.Origin),
                                    ("$index", video.Index)
                                );
                                foreach (
                                    var record in records.Where(record => record.Movie == movie)
                                )
                                {
                                    Execute(
                                        connection,
                                        "INSERT OR IGNORE INTO subtitle_outputs(path,language,created) VALUES($path,$language,0);",
                                        ("$path", record.Path),
                                        ("$language", record.Language)
                                    );
                                    Execute(
                                        connection,
                                        "INSERT INTO subtitle_output_sources(path,language,origin,file_index,movie_path) VALUES($path,$language,$origin,$index,$movie) ON CONFLICT(path,language,origin,file_index) DO UPDATE SET movie_path=excluded.movie_path;",
                                        ("$path", record.Path),
                                        ("$language", record.Language),
                                        ("$origin", video.Origin),
                                        ("$index", video.Index),
                                        ("$movie", video.FinalPath)
                                    );
                                    found.Add(record.Path);
                                }
                                Execute(
                                    connection,
                                    "INSERT OR REPLACE INTO subtitle_scans(origin,file_index,path,stamp) VALUES($origin,$index,$path,$stamp);",
                                    ("$origin", video.Origin),
                                    ("$index", video.Index),
                                    ("$path", video.FinalPath),
                                    ("$stamp", movie.Stamp)
                                );
                                recorded = true;
                            }
                            if (recorded)
                            {
                                Execute(connection, Database.Cleanup);
                                Execute(connection, Database.Invalidate);
                            }
                            return recorded;
                        },
                        cancellation
                    )
                    .ConfigureAwait(false);
                }
                finally
                {
                    if (run is null)
                        _operation.Release();
                }
                // A movie that is not current at its path and stamp gets no
                // marker, so it is skipped instead of staying due.
                lock (_requestSync)
                    _skippedScans.UnionWith(outdated.Select(movie => movie.Key));
            }
            return new(found.Count, failures);
        }
        finally
        {
            if (changed)
                LanguagesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool Within(string path, string folder) =>
        path.StartsWith(
            Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase
        );

    private static IEnumerable<string> SubtitleFiles(string folder, bool recursive)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var path in Directory.EnumerateFiles(folder, "*", options))
            if (SubtitleFile.IsSubtitle(path))
                yield return path;
        if (!recursive)
            foreach (var name in new[] { "Subs", "Subtitles" })
            {
                var path = Path.Combine(folder, name);
                if (
                    Directory.Exists(path)
                    && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0
                )
                    foreach (var subtitle in SubtitleFiles(path, recursive: true))
                        yield return subtitle;
            }
    }

    private sealed record Discovery(
        Video Video,
        string Folder,
        bool Recursive,
        string Stamp,
        bool Scanned
    )
    {
        // The key of its subtitle_scans marker.
        internal (string Origin, int Index, string Path, string Stamp) Key =>
            (Video.Origin, Video.Index, Video.FinalPath, Stamp);
    }
}

internal sealed record ScanResult(int Found, int Unreadable);
