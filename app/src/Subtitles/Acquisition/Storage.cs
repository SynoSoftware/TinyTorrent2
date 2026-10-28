using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;
using Syno.TinyTorrent.Shared.Storage;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition
{
    private async Task Load()
    {
        var settings = await Run(
                connection =>
                {
                    // Finished torrents never restart automatic searches; unfinished
                    // torrents still qualify when they complete while the window is closed.
                    Cancel(connection, manual: false);
                    Execute(
                        connection,
                        """
                        UPDATE subtitle_jobs SET outcome=$pending WHERE manual=0 AND outcome=$cancelled
                            AND (reference IS NOT NULL OR lower(path) LIKE '%.'||lower(language)||'.srt')
                            AND EXISTS (SELECT 1 FROM subtitle_job_sources s WHERE s.path=subtitle_jobs.path
                                AND s.language=subtitle_jobs.language
                                AND (s.origin NOT IN (SELECT origin FROM known_torrents)
                                    OR s.origin IN (SELECT origin FROM new_origins)));
                        """,
                        ("$pending", (int)SubtitleOutcome.Pending),
                        ("$cancelled", (int)SubtitleOutcome.Cancelled)
                    );
                    Resume(connection);
                    using var command = connection.CreateCommand();
                    command.CommandText =
                        "SELECT enabled,supplier,username,secret,languages,extras FROM subtitle_settings WHERE singleton=1;";
                    using var reader = command.ExecuteReader();
                    if (!reader.Read())
                        return null;
                    var enabled = reader[0] is long flag && flag == 1;
                    var repairEnabled = !Equals(reader[0], enabled ? 1L : 0L);
                    var extras = reader[5] is long count
                        ? (int)Math.Clamp(count, 0, MaximumExtras)
                        : 1;
                    var repairExtras = !Equals(reader[5], (long)extras);
                    var secret = string.Empty;
                    var repairSecret = false;
                    if (!reader.IsDBNull(3))
                    {
                        if (reader[3] is byte[] encrypted)
                        {
                            try
                            {
                                secret = Credentials.Unprotect(encrypted);
                            }
                            catch (CryptographicException)
                            {
                                repairSecret = true;
                            }
                        }
                        else
                            repairSecret = true;
                    }
                    // A user name without a readable password keeps the user
                    // name and waits for the password, because a failed
                    // sign-in never falls back to anonymous use. A key
                    // supplier has no user name, so a saved one is ignored
                    // and the key kept.
                    var supplierId = (SubtitleSupplier)reader.GetInt32(1);
                    var username = reader.GetString(2);
                    var password = GetSupplier(supplierId)?.Access == Access.Password;
                    Account account =
                        password && username.Length > 0
                            ? secret.Length > 0
                                ? new Account.SignedIn(supplierId, username, secret)
                                : new Account.PasswordNeeded(supplierId, username)
                        : secret.Length > 0 ? new Account.OwnKey(supplierId, secret)
                        : new Account.BuiltIn(supplierId);
                    var repairLanguages = false;
                    string[] languages;
                    try
                    {
                        var saved = JsonSerializer.Deserialize<string?[]>(reader.GetString(4));
                        languages =
                            saved
                                ?.OfType<string>()
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray()
                            ?? [];
                        repairLanguages = saved is null || !saved.SequenceEqual(languages);
                    }
                    catch (JsonException)
                    {
                        languages = [];
                        repairLanguages = true;
                    }
                    reader.Close();
                    if (repairEnabled || repairSecret || repairLanguages || repairExtras)
                        Execute(
                            connection,
                            """
                            UPDATE subtitle_settings SET enabled=$enabled,secret=CASE WHEN $secret THEN NULL ELSE secret END,
                                languages=CASE WHEN $languages THEN $values ELSE languages END,
                                extras=$extras WHERE singleton=1;
                            """,
                            ("$enabled", enabled),
                            ("$secret", repairSecret),
                            ("$languages", repairLanguages),
                            ("$values", JsonSerializer.Serialize(languages)),
                            ("$extras", extras)
                        );
                    return new
                    {
                        Enabled = enabled,
                        Extras = extras,
                        Account = account,
                        Languages = languages,
                    };
                },
                _lifetime.Token
            )
            .ConfigureAwait(false);
        if (settings is null)
            return;
        Enabled = settings.Enabled;
        Extras = settings.Extras;
        _account = settings.Account;
        Languages = settings.Languages;
        await RefreshAllowance().ConfigureAwait(false);
    }

    private async Task RefreshAllowance() =>
        _allowance = Supplier is { } supplier
            ? await supplier.Allowance(_account).ConfigureAwait(false)
            : new();

    private Task<Target[]> Targets(CancellationToken cancellation) =>
        Run(
            connection =>
            {
                var languages = new HashSet<string>(
                    EffectiveLanguages,
                    StringComparer.OrdinalIgnoreCase
                );
                lock (_deletions)
                    foreach (var deletion in _deletions)
                        languages.Add(deletion.Language);
                // Explicit downloads can create more alternatives than the automatic limit.
                var ranks = 1 + MaximumExtras;
                var files = new List<Video>();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT path,language FROM subtitle_output_sources UNION SELECT path,language FROM subtitle_jobs;";
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        languages.Add(reader.GetString(1));
                        ranks = Math.Max(
                            ranks,
                            SubtitleFile.Rank(reader.GetString(0), reader.GetString(1))
                        );
                    }
                command.CommandText = """
                    SELECT f.origin,f.file_index,f.path,l.path,f.size,f.complete,current_files.readable,f.entry_id,f.wanted,
                        f.origin IN (SELECT origin FROM new_origins)
                    FROM collected_files f JOIN locations l USING(entry_id)
                    JOIN current_files USING(entry_id)
                    WHERE f.kind=$kind;
                    """;
                command.Parameters.AddWithValue("$kind", (int)FileKind.Video);
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
                            IsNew = reader.GetBoolean(9),
                        };
                        files.Add(video);
                    }
                var targets = new List<Target>();
                foreach (var language in languages)
                    for (var rank = 1; rank <= ranks; rank++)
                        foreach (
                            var group in files.GroupBy(
                                video => SubtitleFile.Destination(video.FinalPath, language, rank),
                                StringComparer.OrdinalIgnoreCase
                            )
                        )
                            targets.Add(
                                new Target(group.Key, language, group.ToArray()) { Rank = rank }
                            );
                return targets.ToArray();
            },
            cancellation
        );

    private Task? _reconciling;
    private bool _reconcilePending;

    internal Task Reconcile()
    {
        lock (_requestSync)
        {
            if (_disposed || !_connected)
                return Task.CompletedTask;
            _reconcilePending = true;
            return _reconciling ??= Task.Run(ReconcileChanges);
        }
    }

    private async Task ReconcileChanges()
    {
        try
        {
            while (true)
            {
                lock (_requestSync)
                {
                    if (!_reconcilePending || _disposed || !_connected)
                    {
                        _reconciling = null;
                        return;
                    }
                    _reconcilePending = false;
                }
                await ReconcileOnce().ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_requestSync)
                _reconciling = null;
            throw;
        }
    }

    private async Task ReconcileOnce()
    {
        if (_disposed || !_connected)
            return;
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!_connected)
                return;
            CancellationToken cancellation;
            lock (_requestSync)
                cancellation = _requests.Token;
            await DeleteAccepted().ConfigureAwait(false);
            var targets = await Targets(cancellation).ConfigureAwait(false);
            var index = new TargetIndex(targets);
            await ReconcileJobs(index, cancellation).ConfigureAwait(false);
            await ReconcileFiles(index, cancellation).ConfigureAwait(false);
            try
            {
                await Discover(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine("Automatic subtitle file discovery cancelled.");
            }
            var eligible = targets
                .Where(Eligible)
                .SelectMany(target =>
                    target.Videos.Select(video => (target.Language, video.Origin, video.Index))
                )
                .ToHashSet();
            var reconciled = await Run(
                    connection =>
                    {
                        if (!_connected)
                            return false;
                        Execute(connection, Database.Cleanup);
                        // Work stopped by a folder that refused its file tries again.
                        if (_saveFailed)
                            Resume(connection);
                        foreach (var target in targets)
                        {
                            if (target.Rank != 1 || !Eligible(target))
                                continue;
                            if (!Current(connection, target.Videos))
                                continue;
                            // A manual job advances to its hash stage whatever the
                            // automatic switch says; an automatic job only while its
                            // torrent is new, because a known torrent's earlier miss
                            // waits for Find.
                            Advance(connection, target);
                            if (Enabled && target.IsNew && !Recorded(connection, target))
                                Accept(connection, target, false, 1 + Extras);
                        }
                        using var jobs = connection.CreateCommand();
                        jobs.CommandText = """
                            SELECT j.path,j.language,s.origin,s.file_index FROM subtitle_jobs j
                            JOIN subtitle_job_sources s USING(path,language) WHERE NOT EXISTS (
                                SELECT 1 FROM subtitle_job_sources s JOIN pending_origins p USING(origin)
                                WHERE s.path=j.path AND s.language=j.language);
                            """;
                        var associations =
                            new List<(string Path, string Language, string Origin, int Index)>();
                        using (var reader = jobs.ExecuteReader())
                            while (reader.Read())
                                associations.Add(
                                    (
                                        reader.GetString(0),
                                        reader.GetString(1),
                                        reader.GetString(2),
                                        reader.GetInt32(3)
                                    )
                                );
                        foreach (
                            var group in associations.GroupBy(item => (item.Path, item.Language))
                        )
                            if (
                                !group.Any(source =>
                                    eligible.Contains(
                                        (source.Language, source.Origin, source.Index)
                                    )
                                )
                            )
                                Cancel(
                                    connection,
                                    language: group.Key.Language,
                                    path: group.Key.Path
                                );
                        return true;
                    },
                    cancellation
                )
                .ConfigureAwait(false);
            await RefreshCounts().ConfigureAwait(false);
            _databaseFailure = null;
            if (reconciled)
                _saveFailed = false;
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Subtitle membership reconciliation cancelled.");
        }
        catch (SqliteException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _saveFailed = true;
        }
        finally
        {
            _operation.Release();
            Notify();
        }
        Wake();
    }

    private static void AssociateOutput(SqliteConnection connection, Target target)
    {
        if (target.IsAmbiguous)
            return;
        foreach (var video in target.Videos)
            Execute(
                connection,
                """
                INSERT INTO subtitle_output_sources(path,language,origin,file_index,movie_path)
                SELECT $path,$language,f.origin,f.file_index,l.path FROM collected_files f
                JOIN locations l USING(entry_id)
                WHERE f.origin=$origin AND f.file_index=$index AND f.entry_id=$entry
                    AND l.path=$movie COLLATE PATH
                ON CONFLICT(path,language,origin,file_index) DO UPDATE SET movie_path=excluded.movie_path;
                """,
                ("$path", target.Path),
                ("$language", target.Language),
                ("$origin", video.Origin),
                ("$index", video.Index),
                ("$movie", video.FinalPath),
                ("$entry", video.EntryId)
            );
    }

    private static void Unblock(SqliteConnection connection) =>
        Execute(
            connection,
            "UPDATE subtitle_jobs SET outcome=$pending,retry_at=0 WHERE outcome=$blocked;",
            ("$pending", (int)SubtitleOutcome.Pending),
            ("$blocked", (int)SubtitleOutcome.Blocked)
        );

    private static void Resume(SqliteConnection connection) =>
        Execute(
            connection,
            "UPDATE subtitle_jobs SET outcome=$pending,retry_at=0 WHERE outcome=$unavailable;",
            ("$pending", (int)SubtitleOutcome.Pending),
            ("$unavailable", (int)SubtitleOutcome.Unavailable)
        );

    // Cancellation retains search history and the decision not to revive automatic
    // work; Stop can return manual work to the automatic queue while its torrent is new.
    private static void Cancel(
        SqliteConnection connection,
        bool? manual = null,
        string? language = null,
        string? path = null
    ) =>
        Execute(
            connection,
            """
            UPDATE subtitle_jobs SET outcome=$cancelled,retry_at=0
            WHERE outcome IN ($pending,$unavailable,$blocked) AND ($manual IS NULL OR manual=$manual)
                AND ($language IS NULL OR language=$language) AND ($path IS NULL OR path=$path);
            """,
            ("$pending", (int)SubtitleOutcome.Pending),
            ("$unavailable", (int)SubtitleOutcome.Unavailable),
            ("$cancelled", (int)SubtitleOutcome.Cancelled),
            ("$blocked", (int)SubtitleOutcome.Blocked),
            ("$manual", manual),
            ("$language", language),
            ("$path", path)
        );

    private bool Eligible(Target target) =>
        !target.IsAmbiguous
        && target.Videos.Any(video => video.Eligible)
        && EffectiveLanguages.Contains(target.Language, StringComparer.OrdinalIgnoreCase)
        && Supplier?.Supports(target.Language) == true;

    private SubtitleStage Stage(Target target)
    {
        var complete =
            Supplier?.SupportsHash == true
                ? target.Videos.Any(video => video.CanHash)
                : target.Complete;
        return complete ? SubtitleStage.Complete : SubtitleStage.Release;
    }

    private void Advance(SqliteConnection connection, Target target)
    {
        if (Stage(target) == SubtitleStage.Release)
            return;
        Execute(
            connection,
            """
            UPDATE subtitle_jobs SET stage=$next,outcome=$pending,retry_at=0,attempts=0
            WHERE path=$path AND language=$language AND stage=$previous AND outcome=$nomatch
                AND (manual=1 OR $new);
            """,
            ("$pending", (int)SubtitleOutcome.Pending),
            ("$nomatch", (int)SubtitleOutcome.NoMatch),
            ("$path", target.Path),
            ("$language", target.Language),
            ("$new", target.IsNew),
            ("$next", (int)SubtitleStage.Complete),
            ("$previous", (int)SubtitleStage.Release)
        );
    }

    private bool Accept(SqliteConnection connection, Target target, bool manual, int guesses)
    {
        if (!Eligible(target) || !Current(connection, target.Videos.Where(video => video.Eligible)))
            return false;
        Execute(
            connection,
            """
            INSERT INTO subtitle_jobs(path,language,stage,outcome,manual,guesses,fresh)
            VALUES($path,$language,$stage,$pending,$manual,$guesses,$fresh)
            ON CONFLICT(path,language) DO UPDATE SET stage=excluded.stage,outcome=excluded.outcome,
                retry_at=0,attempts=0,manual=excluded.manual,guesses=excluded.guesses,fresh=excluded.fresh,
                reference=CASE WHEN $manual OR subtitle_jobs.manual=1 THEN NULL ELSE subtitle_jobs.reference END
                WHERE $manual OR (subtitle_jobs.outcome=$cancelled
                    AND subtitle_jobs.manual=1);
            """,
            ("$path", target.Path),
            ("$language", target.Language),
            (
                "$stage",
                (int)(
                    Supplier?.SupportsHash == true && !manual
                        ? SubtitleStage.Release
                        : Stage(target)
                )
            ),
            ("$pending", (int)SubtitleOutcome.Pending),
            ("$manual", manual),
            ("$guesses", guesses),
            ("$fresh", target.Fresh is null ? null : JsonSerializer.Serialize(target.Fresh)),
            ("$cancelled", (int)SubtitleOutcome.Cancelled)
        );
        AssociateJob(connection, target);
        return true;
    }

    private async Task<bool> Record(
        Target target,
        bool created,
        CancellationToken cancellation,
        string? reference = null
    )
    {
        var recorded = await Run(
                connection =>
                {
                    if (
                        !created
                        && !Current(connection, target.Videos.Where(video => video.Eligible))
                    )
                        return false;
                    Execute(
                        connection,
                        """
                        INSERT INTO subtitle_outputs(path,language,created,reference) VALUES($path,$language,$created,$reference)
                        ON CONFLICT(path,language) DO UPDATE SET created=MAX(created,excluded.created),
                            reference=CASE WHEN excluded.created THEN COALESCE(excluded.reference,reference) ELSE reference END;
                        """,
                        ("$path", target.Path),
                        ("$language", target.Language),
                        ("$created", created),
                        ("$reference", reference)
                    );
                    if (created)
                        foreach (var video in target.Videos)
                            Execute(
                                connection,
                                """
                                INSERT INTO subtitle_output_sources(path,language,origin,file_index,movie_path)
                                VALUES($path,$language,$origin,$index,$movie)
                                ON CONFLICT(path,language,origin,file_index) DO UPDATE SET movie_path=excluded.movie_path;
                                """,
                                ("$path", target.Path),
                                ("$language", target.Language),
                                ("$origin", video.Origin),
                                ("$index", video.Index),
                                ("$movie", video.FinalPath)
                            );
                    else
                        AssociateOutput(connection, target);
                    Execute(connection, Database.Cleanup + Database.Invalidate);
                    return true;
                },
                cancellation
            )
            .ConfigureAwait(false);
        if (recorded)
            LanguagesChanged?.Invoke(this, EventArgs.Empty);
        return recorded;
    }

    private static bool Recorded(SqliteConnection connection, Target target)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM subtitle_outputs o JOIN subtitle_output_sources s USING(path,language)
            WHERE o.language=$language AND ($first OR o.path=$path COLLATE PATH)
                AND s.origin=$origin AND s.file_index=$index AND s.movie_path=$movie COLLATE PATH LIMIT 1;
            """;
        command.Parameters.AddWithValue("$path", target.Path);
        command.Parameters.AddWithValue("$language", target.Language);
        command.Parameters.AddWithValue("$first", target.Rank == 1);
        command.Parameters.Add("$origin", SqliteType.Text);
        command.Parameters.Add("$index", SqliteType.Integer);
        command.Parameters.Add("$movie", SqliteType.Text);
        foreach (var video in target.Videos)
        {
            command.Parameters["$origin"].Value = video.Origin;
            command.Parameters["$index"].Value = video.Index;
            command.Parameters["$movie"].Value = video.FinalPath;
            if (command.ExecuteScalar() is not null)
                return true;
        }
        return false;
    }

    private static bool Current(
        SqliteConnection connection,
        IEnumerable<Video> videos,
        bool readable = false
    )
    {
        foreach (var video in videos)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT 1 FROM collected_files f JOIN locations l USING(entry_id)
                WHERE f.origin=$origin AND f.file_index=$index AND l.path=$path COLLATE PATH
                    AND f.entry_id=$entry AND f.wanted=$wanted
                """;
            if (readable)
                command.CommandText += """
                     AND f.path=l.path COLLATE PATH AND f.size=$size AND f.complete=1
                        AND EXISTS(SELECT 1 FROM current_files c WHERE c.entry_id=f.entry_id AND c.readable=1)
                    """;
            command.Parameters.AddWithValue("$origin", video.Origin);
            command.Parameters.AddWithValue("$index", video.Index);
            command.Parameters.AddWithValue("$path", video.FinalPath);
            command.Parameters.AddWithValue("$entry", video.EntryId);
            command.Parameters.AddWithValue("$wanted", video.Wanted);
            command.Parameters.AddWithValue("$size", video.Size);
            if (command.ExecuteScalar() is not null)
                return true;
        }
        return false;
    }

    // Search evidence follows the movie even when moving its saved subtitle failed.
    private static Dictionary<string, SubtitleMatch> History(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.path,j.language,j.match,l.path FROM subtitle_jobs j
            JOIN subtitle_job_sources s USING(path,language)
            JOIN collected_files f ON f.origin=s.origin AND f.file_index=s.file_index
            JOIN locations l ON l.entry_id=f.entry_id
            WHERE j.match IS NOT NULL ORDER BY j.path COLLATE PATH;
            """;
        using var reader = command.ExecuteReader();
        var history = new Dictionary<string, SubtitleMatch>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var path = reader.GetString(0);
            var language = reader.GetString(1);
            if (SubtitleFile.Rank(path, language) != 1)
                continue;
            var current = SubtitleFile.Destination(reader.GetString(3), language);
            if (
                !history.ContainsKey(current)
                || StringComparer.OrdinalIgnoreCase.Equals(path, current)
            )
                history[current] = (SubtitleMatch)reader.GetInt32(2);
        }
        return history;
    }

    // The finished movies Find searches, which Count also counts.
    private IEnumerable<Target> Searchable(
        SqliteConnection connection,
        IEnumerable<Target> targets,
        SubtitleScope scope,
        IReadOnlySet<long>? entries
    )
    {
        var history = History(connection);
        return targets.Where(target =>
            target.Rank == 1
            && target.Complete
            && (entries is null || target.Videos.Any(video => entries.Contains(video.EntryId)))
            && Includes(history, target, scope)
            && (scope != SubtitleScope.New || CanLookup(connection, target))
        );
    }

    private static bool Includes(
        IReadOnlyDictionary<string, SubtitleMatch> history,
        Target target,
        SubtitleScope scope
    ) =>
        scope == SubtitleScope.All
        || (
            history.TryGetValue(target.Path, out var outcome)
                ? outcome != SubtitleMatch.Exact
                : true
        );

    private bool CanLookup(SqliteConnection connection, Target target)
    {
        var lookup = new SubtitleLookup(
            Path.GetFileName(target.Videos[0].FinalPath),
            target.Language,
            _ => Task.CompletedTask
        )
        {
            Stage = Stage(target),
        };
        var counter = SearchCount(lookup.FileName, lookup.Language);
        if (SearchProtection(connection, lookup) is null)
            return true;
        if (Supplier?.SupportsHash == true && lookup.Stage == SubtitleStage.Complete)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT EXISTS(SELECT 1 FROM subtitle_jobs WHERE path=$path AND language=$language AND stage=$stage AND outcome IN ($nomatch,$complete));";
            command.Parameters.AddWithValue("$path", target.Path);
            command.Parameters.AddWithValue("$language", target.Language);
            command.Parameters.AddWithValue("$stage", (int)SubtitleStage.Complete);
            command.Parameters.AddWithValue("$nomatch", (int)SubtitleOutcome.NoMatch);
            command.Parameters.AddWithValue("$complete", (int)SubtitleOutcome.Complete);
            if (Convert.ToInt32(command.ExecuteScalar()) == 0)
                return policy.Read(connection, new LookupRequest(counter, counter))?.CanForce
                    != true;
        }
        return false;
    }

    private RequestBlock? SearchProtection(SqliteConnection connection, SubtitleLookup lookup)
    {
        var key = SearchKey(lookup);
        var saved = SavedSearch(connection, key);
        if (saved is null || saved.Candidates.Count == 0)
            return policy.Read(
                connection,
                new LookupRequest(key, SearchCount(lookup.FileName, lookup.Language))
            );
        return Exact(FileName.Interpret(lookup.FileName), saved.Candidates) is null
            ? new(RequestBlockKind.NoMatch)
            : null;
    }

    private RequestBlock? Protection(SqliteConnection connection, SubtitleLookup lookup)
    {
        var supplier = RequireSupplier(SupplierId);
        return policy.Read(
                connection,
                [
                    supplier.QuotaScope(_account),
                    supplier.Scope(_account, "search"),
                    supplier.Authentication(_account),
                ]
            ) ?? SearchProtection(connection, lookup);
    }

    // What the search limit counts for a video: its title and year, with the
    // season and episodes, so another copy or release of the same movie
    // shares the count.
    private static string Query(string path)
    {
        var release = FileName.Interpret(path);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{FileName.Normalize(release.Title)} {release.Year} {release.Season} {string.Join(' ', release.Episodes)}"
        );
    }

    private async Task RefreshCounts()
    {
        var targets = (await Targets(_lifetime.Token).ConfigureAwait(false))
            .Where(Eligible)
            .ToArray();
        var counts = await Run(
                connection =>
                {
                    var history = History(connection);
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT path,outcome,manual FROM subtitle_jobs;";
                    var finding = false;
                    var blocked = 0;
                    var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            var outcome = (SubtitleOutcome)reader.GetInt32(1);
                            if (
                                outcome
                                is SubtitleOutcome.Pending
                                    or SubtitleOutcome.Unavailable
                                    or SubtitleOutcome.Blocked
                            )
                                finding |= reader.GetBoolean(2);
                            else if (outcome == SubtitleOutcome.Complete)
                                completed.Add(reader.GetString(0));
                            if (outcome == SubtitleOutcome.Blocked && reader.GetBoolean(2))
                                blocked++;
                        }
                    var found = targets
                        .Where(target =>
                            _batch.ContainsKey(target.Path)
                            && history.TryGetValue(target.Path, out var outcome)
                            && outcome is SubtitleMatch.Exact or SubtitleMatch.Likely
                            && completed.Contains(target.Path)
                        )
                        .Select(target => target.Videos[0].EntryId)
                        .Distinct()
                        .Count();
                    command.CommandText = """
                        SELECT COUNT(*) FROM current_files f WHERE f.kind=$video AND f.complete=1 AND EXISTS (
                            SELECT 1 FROM collected_files c JOIN subtitle_languages s ON s.origin=c.origin AND s.file_index=c.file_index
                            WHERE c.entry_id=f.entry_id);
                        """;
                    command.Parameters.AddWithValue("$video", (int)FileKind.Video);
                    return (
                        Finding: finding,
                        Blocked: blocked,
                        Found: found,
                        Subtitled: Convert.ToInt32(command.ExecuteScalar())
                    );
                },
                _lifetime.Token
            )
            .ConfigureAwait(false);
        var changed = WithSubtitles != counts.Subtitled;
        WithSubtitles = counts.Subtitled;
        if (changed)
            LanguagesChanged?.Invoke(this, EventArgs.Empty);
        Finding = counts.Finding;
        Blocked = counts.Blocked;
        if (_batch.Count > 0)
            Found = counts.Found;
        await RefreshWork().ConfigureAwait(false);
        Notify();
    }

    private static void Execute(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] values
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values)
            command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void ReportFile(string path, SubtitleFailure reason) =>
        FileFailed?.Invoke(this, new(path, reason));

    private sealed class TargetIndex
    {
        private readonly Dictionary<
            (string Language, int Rank, string Origin, int Index),
            Target
        > _sources = [];
        private readonly Dictionary<string, Dictionary<string, Target>> _languages = new(
            StringComparer.Ordinal
        );
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Target, int> _order = [];

        internal TargetIndex(Target[] targets)
        {
            foreach (var target in targets)
            {
                _order.Add(target, _order.Count);
                _paths.Add(target.Path);
                if (!_languages.TryGetValue(target.Language, out var paths))
                    _languages.Add(target.Language, paths = new(StringComparer.OrdinalIgnoreCase));
                paths.TryAdd(target.Path, target);
                foreach (var video in target.Videos)
                    _sources.TryAdd(
                        (target.Language, target.Rank, video.Origin, video.Index),
                        target
                    );
            }
        }

        internal Target? Find(string language, int rank, string origin, int index) =>
            _sources.GetValueOrDefault((language, rank, origin, index));

        internal Target? Find(string language, string path) =>
            _languages.TryGetValue(language, out var paths) ? paths.GetValueOrDefault(path) : null;

        internal bool Contains(string path) => _paths.Contains(path);

        internal Target[] Order(IEnumerable<Target> targets) =>
            targets.Distinct().OrderBy(target => _order[target]).ToArray();
    }

    private sealed record Video(string Origin, int Index, string FinalPath)
    {
        private bool IsExtra { get; } = FileName.Interpret(FinalPath).IsExtra;
        internal required string Path { get; init; }
        internal long EntryId { get; init; }
        internal long Size { get; init; }
        internal bool Complete { get; init; }
        internal bool Readable { get; init; }
        internal bool Wanted { get; init; }
        internal bool IsNew { get; init; }
        internal bool Eligible => Wanted && !IsExtra;
        internal bool CanHash =>
            Eligible && Complete && Readable && Size >= SubtitleFile.HashMinimum;
    }

    // Rank 1 is the subtitle the person gets; higher ranks are the extra
    // guesses downloaded only when no subtitle matches the exact release.
    private sealed record Target(string Path, string Language, Video[] Videos)
    {
        internal SearchIntent? Fresh { get; init; }
        internal int Rank { get; init; } = 1;
        internal bool Manual { get; init; }
        internal int Guesses { get; init; } = 2;
        internal string? Reference { get; init; }
        internal SubtitleMatch? Match { get; init; }
        internal bool Complete => Videos.Any(video => video.Eligible && video.Complete);
        internal bool IsNew => Videos.Any(video => video.Eligible && video.IsNew);
        internal bool IsAmbiguous { get; } =
            Videos
                .Select(video => video.FinalPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any();
    }
}
