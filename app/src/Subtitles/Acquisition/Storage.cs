using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition
{
    private Task Load() =>
        Run(
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
                    "SELECT enabled,supplier,username,secret,languages,request_reset,download_reset,extras FROM subtitle_settings WHERE singleton=1;";
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    Enabled = reader[0] is long enabled && enabled == 1;
                    var repairEnabled = !Equals(reader[0], Enabled ? 1L : 0L);
                    Extras = reader[7] is long extras ? (int)Math.Clamp(extras, 0, MaximumExtras) : 1;
                    var repairExtras = !Equals(reader[7], (long)Extras);
                    var secret = string.Empty;
                    var repairSecret = false;
                    if (!reader.IsDBNull(3))
                    {
                        if (reader[3] is byte[] encrypted)
                        {
                            try
                            {
                                secret = Unprotect(encrypted);
                            }
                            catch (CryptographicException)
                            {
                                repairSecret = true;
                            }
                        }
                        else
                            repairSecret = true;
                    }
                    _account = new(
                        (SubtitleSupplier)reader.GetInt32(1),
                        reader.GetString(2),
                        secret
                    );
                    var repairLanguages = false;
                    try
                    {
                        var languages = JsonSerializer.Deserialize<string?[]>(reader.GetString(4));
                        Languages =
                            languages
                                ?.OfType<string>()
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray()
                            ?? [];
                        repairLanguages = languages is null || !languages.SequenceEqual(Languages);
                    }
                    catch (JsonException)
                    {
                        Languages = [];
                        repairLanguages = true;
                    }
                    var repairResets = false;
                    _allowance = new Allowance(
                        ReadReset(reader, 5, ref repairResets),
                        ReadReset(reader, 6, ref repairResets)
                    );
                    reader.Close();
                    if (repairEnabled || repairSecret || repairLanguages || repairResets || repairExtras)
                        Execute(
                            connection,
                            """
                            UPDATE subtitle_settings SET enabled=$enabled,secret=CASE WHEN $secret THEN NULL ELSE secret END,
                                languages=CASE WHEN $languages THEN $values ELSE languages END,
                                request_reset=CASE WHEN $resets THEN $request ELSE request_reset END,
                                download_reset=CASE WHEN $resets THEN $download ELSE download_reset END,
                                extras=$extras WHERE singleton=1;
                            """,
                            ("$enabled", Enabled),
                            ("$secret", repairSecret),
                            ("$languages", repairLanguages),
                            ("$values", JsonSerializer.Serialize(Languages)),
                            ("$resets", repairResets),
                            ("$request", _allowance.RequestReset?.ToUnixTimeMilliseconds()),
                            ("$download", _allowance.DownloadReset?.ToUnixTimeMilliseconds()),
                            ("$extras", Extras)
                        );
                    Supplier?.Restore(_account, _allowance);
                }
                return true;
            },
            _lifetime.Token
        );

    private static DateTimeOffset? ReadReset(
        SqliteDataReader reader,
        int ordinal,
        ref bool repaired
    )
    {
        if (reader.IsDBNull(ordinal))
            return null;
        if (reader[ordinal] is long value && value is >= -62135596800000 and <= 253402300799999)
            return DateTimeOffset.FromUnixTimeMilliseconds(value);
        repaired = true;
        return null;
    }

    private async Task SaveAllowance(Allowance allowance)
    {
        if (_allowance == allowance)
            return;
        await Run(
                connection =>
                {
                    Execute(
                        connection,
                        "UPDATE subtitle_settings SET request_reset=$request,download_reset=$download WHERE singleton=1;",
                        ("$request", allowance.RequestReset?.ToUnixTimeMilliseconds()),
                        ("$download", allowance.DownloadReset?.ToUnixTimeMilliseconds())
                    );
                    return true;
                },
                CancellationToken.None
            )
            .ConfigureAwait(false);
        _allowance = allowance;
    }

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
                command.CommandText = "SELECT path,language FROM subtitle_output_sources UNION SELECT path,language FROM subtitle_jobs;";
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
                        var video = new Video(reader.GetString(0), reader.GetInt32(1))
                        {
                            Path = reader.GetString(2),
                            FinalPath = reader.GetString(3),
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

    internal async Task Reconcile()
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
            await ReconcileJobs(targets, cancellation).ConfigureAwait(false);
            await ReconcileFiles(targets, cancellation).ConfigureAwait(false);
            await Discover(cancellation).ConfigureAwait(false);
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
                            if (!Current(connection, target.Videos))
                                continue;
                            var recorded = Recorded(connection, target);
                            if (target.Rank != 1 || !Eligible(target))
                                continue;
                            // A manual job advances to its hash stage whatever the
                            // automatic switch says; an automatic job only while its
                            // torrent is new, because a known torrent's earlier miss
                            // waits for Find.
                            Advance(connection, target);
                            if (Enabled && !recorded && target.IsNew)
                                Accept(connection, target, false, 1 + Extras);
                        }
                        using var jobs = connection.CreateCommand();
                        jobs.CommandText = """
                            SELECT j.path,j.language,s.origin,s.file_index FROM subtitle_jobs j
                            JOIN subtitle_job_sources s USING(path,language) WHERE NOT EXISTS (
                                SELECT 1 FROM subtitle_job_sources s JOIN pending_origins p USING(origin)
                                WHERE s.path=j.path AND s.language=j.language);
                            """;
                        var associations = new List<(
                            string Path,
                            string Language,
                            string Origin,
                            int Index
                        )>();
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
                        foreach (var group in associations.GroupBy(item => (item.Path, item.Language)))
                            if (
                                !targets.Any(target =>
                                    Eligible(target)
                                    && target.Language == group.Key.Language
                                    && target.Videos.Any(video =>
                                        group.Any(source =>
                                            source.Origin == video.Origin
                                            && source.Index == video.Index
                                        )
                                    )
                                )
                            )
                                Cancel(connection, language: group.Key.Language, path: group.Key.Path);
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
        catch (OperationCanceledException) { }
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
            WHERE outcome IN ($pending,$unavailable) AND ($manual IS NULL OR manual=$manual)
                AND ($language IS NULL OR language=$language) AND ($path IS NULL OR path=$path);
            """,
            ("$pending", (int)SubtitleOutcome.Pending),
            ("$unavailable", (int)SubtitleOutcome.Unavailable),
            ("$cancelled", (int)SubtitleOutcome.Cancelled),
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
            INSERT INTO subtitle_jobs(path,language,stage,outcome,manual,guesses)
            VALUES($path,$language,$stage,$pending,$manual,$guesses)
            ON CONFLICT(path,language) DO UPDATE SET stage=excluded.stage,outcome=excluded.outcome,
                retry_at=0,attempts=0,manual=excluded.manual,guesses=excluded.guesses,
                reference=CASE WHEN $manual OR subtitle_jobs.manual=1 THEN NULL ELSE subtitle_jobs.reference END
                WHERE $manual OR (subtitle_jobs.outcome=$cancelled
                    AND (subtitle_jobs.manual=1 OR $unfinished));
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
            ("$unfinished", target.Videos.Any(video => video.Eligible && video.IsNew && !video.Complete)),
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
            if (!history.ContainsKey(current) || StringComparer.OrdinalIgnoreCase.Equals(path, current))
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
            && CanLookup(connection, target)
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
                ? scope == SubtitleScope.NoExactMatch && outcome != SubtitleMatch.Exact
                : scope == SubtitleScope.New
        );

    // The shared search limit, which automatic and batch searches respect;
    // a search in the choice dialog counts but is never refused.
    private bool CanLookup(SqliteConnection connection, Target target)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT total FROM subtitle_lookups WHERE supplier=$supplier AND query=$query AND language=$language;";
        command.Parameters.AddWithValue("$supplier", (int)SupplierId);
        command.Parameters.AddWithValue("$query", Query(target.Videos[0].FinalPath));
        command.Parameters.AddWithValue("$language", target.Language);
        return Convert.ToInt64(command.ExecuteScalar()) < lookupLimit();
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
                    var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                        {
                            var outcome = (SubtitleOutcome)reader.GetInt32(1);
                            if (outcome is SubtitleOutcome.Pending or SubtitleOutcome.Unavailable)
                                finding |= reader.GetBoolean(2);
                            else if (outcome == SubtitleOutcome.Complete)
                                completed.Add(reader.GetString(0));
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
                        Found: found,
                        Subtitled: Convert.ToInt32(command.ExecuteScalar())
                    );
                },
                _lifetime.Token
            )
            .ConfigureAwait(false);
        WithSubtitles = counts.Subtitled;
        Finding = counts.Finding;
        if (_batch.Count > 0)
            Found = counts.Found;
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

    private sealed record Video(string Origin, int Index)
    {
        internal required string Path { get; init; }
        internal long EntryId { get; init; }
        internal required string FinalPath { get; init; }
        internal long Size { get; init; }
        internal bool Complete { get; init; }
        internal bool Readable { get; init; }
        internal bool Wanted { get; init; }
        internal bool IsNew { get; init; }
        internal bool Eligible => Wanted && !FileName.Interpret(FinalPath).IsExtra;
        internal bool CanHash =>
            Eligible && Complete && Readable && Size >= SubtitleFile.HashMinimum;
    }

    // Rank 1 is the subtitle the person gets; higher ranks are the extra
    // guesses downloaded only when no subtitle matches the exact release.
    private sealed record Target(string Path, string Language, Video[] Videos)
    {
        internal int Rank { get; init; } = 1;
        internal bool Manual { get; init; }
        internal int Guesses { get; init; } = 2;
        internal string? Reference { get; init; }
        internal SubtitleMatch? Match { get; init; }
        internal bool Complete => Videos.Any(video => video.Eligible && video.Complete);
        internal bool IsNew => Videos.Any(video => video.Eligible && video.IsNew);
        internal bool IsAmbiguous =>
            Videos
                .Select(video => video.FinalPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any();
    }
}
