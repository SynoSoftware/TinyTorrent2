using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition
{
    internal const string Cleanup = """
        DELETE FROM subtitle_sources WHERE NOT EXISTS (
            SELECT 1 FROM pending_origins p WHERE p.origin=subtitle_sources.origin)
        AND NOT EXISTS (SELECT 1 FROM collected_files f
            WHERE f.origin=subtitle_sources.origin AND f.file_index=subtitle_sources.file_index);
        DELETE FROM subtitle_outputs WHERE NOT EXISTS (SELECT 1 FROM subtitle_sources s
            WHERE s.path=subtitle_outputs.path AND s.language=subtitle_outputs.language);
        DELETE FROM subtitle_jobs WHERE NOT EXISTS (SELECT 1 FROM subtitle_sources s
            WHERE s.path=subtitle_jobs.path AND s.language=subtitle_jobs.language);
        """;

    private Task Load() => database.Run(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT enabled,supplier,username,secret,languages,checked_at,request_reset,download_reset FROM subtitle_settings WHERE singleton=1;";
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            Enabled = reader.GetBoolean(0);
            var secret = string.Empty;
            var repairSecret = false;
            if (!reader.IsDBNull(3))
            {
                if (reader[3] is byte[] encrypted)
                {
                    try { secret = Unprotect(encrypted); }
                    catch (CryptographicException) { repairSecret = true; }
                }
                else
                    repairSecret = true;
            }
            _account = new((SubtitleSupplier)reader.GetInt32(1), reader.GetString(2), secret);
            var repairLanguages = false;
            try
            {
                var languages = JsonSerializer.Deserialize<string?[]>(reader.GetString(4));
                Languages = languages?.OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
                repairLanguages = languages is null || !languages.SequenceEqual(Languages);
            }
            catch (JsonException)
            {
                Languages = [];
                repairLanguages = true;
            }
            var repairDate = false;
            LastRecheck = null;
            if (!reader.IsDBNull(5))
            {
                if (DateTimeOffset.TryParse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    LastRecheck = date;
                else
                    repairDate = true;
            }
            var repairResets = false;
            _allowance = new Allowance(
                ReadReset(reader, 6, ref repairResets), ReadReset(reader, 7, ref repairResets));
            reader.Close();
            if (repairSecret || repairLanguages || repairDate || repairResets)
                Execute(connection, """
                    UPDATE subtitle_settings SET secret=CASE WHEN $secret THEN NULL ELSE secret END,
                        languages=CASE WHEN $languages THEN $values ELSE languages END,
                        checked_at=CASE WHEN $date THEN NULL ELSE checked_at END,
                        request_reset=CASE WHEN $resets THEN $request ELSE request_reset END,
                        download_reset=CASE WHEN $resets THEN $download ELSE download_reset END WHERE singleton=1;
                    """, ("$secret", repairSecret), ("$languages", repairLanguages),
                    ("$values", JsonSerializer.Serialize(Languages)), ("$date", repairDate), ("$resets", repairResets),
                    ("$request", _allowance.RequestReset?.ToUnixTimeMilliseconds()),
                    ("$download", _allowance.DownloadReset?.ToUnixTimeMilliseconds()));
            Supplier?.Restore(_account, _allowance);
            if (RetryAt > DateTimeOffset.UtcNow)
                Failure = SubtitleFailure.Quota;
        }
        return true;
    }, _lifetime.Token);

    private static DateTimeOffset? ReadReset(SqliteDataReader reader, int ordinal, ref bool repaired)
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
        await database.Run(connection =>
        {
            Execute(connection, "UPDATE subtitle_settings SET request_reset=$request,download_reset=$download WHERE singleton=1;",
                ("$request", allowance.RequestReset?.ToUnixTimeMilliseconds()), ("$download", allowance.DownloadReset?.ToUnixTimeMilliseconds()));
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
        _allowance = allowance;
    }

    private Task<Target[]> Targets(CancellationToken cancellation) => database.Run(connection =>
    {
        var languages = new HashSet<string>(EffectiveLanguages, StringComparer.OrdinalIgnoreCase);
        foreach (var deletion in _deletions)
            languages.Add(deletion.Language);
        var files = new List<Video>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT language FROM subtitle_sources;";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                languages.Add(reader.GetString(0));
        command.CommandText = """
            SELECT f.origin,f.file_index,f.path,l.path,f.size,f.complete,current_files.readable,f.entry_id,f.wanted
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
                    Path = reader.GetString(2), FinalPath = reader.GetString(3), Size = reader.GetInt64(4),
                    Complete = reader.GetBoolean(5), Readable = reader.GetBoolean(6),
                    EntryId = reader.GetInt64(7), Wanted = reader.GetBoolean(8),
                };
                files.Add(video);
            }
        var targets = new List<Target>();
        foreach (var language in languages)
            foreach (var group in files.GroupBy(video => SubtitleFile.Destination(video.FinalPath, language), StringComparer.OrdinalIgnoreCase))
            {
                targets.Add(new Target(group.Key, language, group.ToArray()));
            }
        return targets.ToArray();
    }, cancellation);

    internal async Task Reconcile()
    {
        if (_disposed || !_connected)
            return;
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            var revision = Interlocked.Read(ref _revision);
            await DeleteAccepted().ConfigureAwait(false);
            var targets = await Targets(_lifetime.Token).ConfigureAwait(false);
            await ReconcileFiles(targets, _lifetime.Token).ConfigureAwait(false);
            await database.Run(connection =>
            {
                if (revision != Interlocked.Read(ref _revision) || !_connected)
                    return false;
                Execute(connection, Cleanup);
                foreach (var target in targets)
                {
                    if (!Current(connection, target.Videos))
                        continue;
                    using var saved = connection.CreateCommand();
                    saved.CommandText = "SELECT 1 FROM subtitle_outputs WHERE path=$path AND language=$language;";
                    saved.Parameters.AddWithValue("$path", target.Path);
                    saved.Parameters.AddWithValue("$language", target.Language);
                    var recorded = saved.ExecuteScalar() is not null;
                    if (recorded)
                        Associate(connection, target);
                    if (!Enabled || !Eligible(target) || recorded)
                        continue;
                    if (target.Videos.Any(video => video.Eligible && !video.Complete))
                        Accept(connection, target, false);
                    Advance(connection, target);
                }
                using var jobs = connection.CreateCommand();
                jobs.CommandText = """
                    SELECT path,language FROM subtitle_jobs j WHERE NOT EXISTS (
                        SELECT 1 FROM subtitle_sources s JOIN pending_origins p USING(origin)
                        WHERE s.path=j.path AND s.language=j.language);
                    """;
                var obsolete = new List<(string Path, string Language)>();
                using (var reader = jobs.ExecuteReader())
                    while (reader.Read())
                        if (!targets.Any(target => Eligible(target) && target.Language == reader.GetString(1) &&
                            StringComparer.OrdinalIgnoreCase.Equals(target.Path, reader.GetString(0))))
                            obsolete.Add((reader.GetString(0), reader.GetString(1)));
                foreach (var item in obsolete)
                    Execute(connection, "DELETE FROM subtitle_jobs WHERE path=$path AND language=$language;",
                        ("$path", item.Path), ("$language", item.Language));
                return true;
            }, _lifetime.Token).ConfigureAwait(false);
            await RefreshCounts().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            Failure = error is SqliteException ? SubtitleFailure.Database : SubtitleFailure.Save;
        }
        finally
        {
            _operation.Release();
            Notify();
        }
        Wake();
    }

    private static void Associate(SqliteConnection connection, Target target)
    {
        if (target.IsAmbiguous)
            return;
        foreach (var video in target.Videos)
            Execute(connection, """
                INSERT OR IGNORE INTO subtitle_sources(path,language,origin,file_index)
                SELECT $path,$language,f.origin,f.file_index FROM collected_files f
                JOIN locations l USING(entry_id)
                WHERE f.origin=$origin AND f.file_index=$index AND f.entry_id=$entry
                    AND l.path=$movie COLLATE PATH;
                """, ("$path", target.Path), ("$language", target.Language), ("$origin", video.Origin),
                ("$index", video.Index), ("$movie", video.FinalPath), ("$entry", video.EntryId));
    }

    private static void Resume(SqliteConnection connection) =>
        Execute(connection, "UPDATE subtitle_jobs SET outcome=$pending,retry_at=0 WHERE outcome=$unavailable;",
            ("$pending", (int)SubtitleOutcome.Pending), ("$unavailable", (int)SubtitleOutcome.Unavailable));

    private bool Eligible(Target target) => !target.IsAmbiguous && target.Videos.Any(video => video.Eligible) &&
        EffectiveLanguages.Contains(target.Language, StringComparer.OrdinalIgnoreCase) &&
        Supplier?.Supports(target.Language) == true;

    private SubtitleStage Stage(Target target)
    {
        var complete = Supplier?.SupportsHash == true
            ? target.Videos.Any(video => video.CanHash)
            : target.Complete;
        return complete ? SubtitleStage.Complete : SubtitleStage.Release;
    }

    private void Advance(SqliteConnection connection, Target target)
    {
        if (Stage(target) == SubtitleStage.Release)
            return;
        Execute(connection, """
            UPDATE subtitle_jobs SET stage=$next,outcome=$pending,retry_at=0,attempts=0
            WHERE path=$path AND language=$language AND stage=$previous AND outcome=$nomatch;
            """, ("$pending", (int)SubtitleOutcome.Pending), ("$nomatch", (int)SubtitleOutcome.NoMatch),
            ("$path", target.Path), ("$language", target.Language),
            ("$next", (int)SubtitleStage.Complete), ("$previous", (int)SubtitleStage.Release));
    }

    private bool Accept(SqliteConnection connection, Target target, bool retry)
    {
        if (!Eligible(target) || !Current(connection, target.Videos.Where(video => video.Eligible)))
            return false;
        Associate(connection, target);
        Execute(connection, """
            INSERT INTO subtitle_jobs(path,language,stage,outcome) VALUES($path,$language,$stage,$pending)
            ON CONFLICT(path,language) DO UPDATE SET stage=excluded.stage,outcome=excluded.outcome,retry_at=0,attempts=0
                WHERE $retry AND subtitle_jobs.outcome IN ($nomatch,$failed);
            """, ("$path", target.Path), ("$language", target.Language),
            ("$stage", (int)(Supplier?.SupportsHash == true && !retry ? SubtitleStage.Release : Stage(target))),
            ("$pending", (int)SubtitleOutcome.Pending), ("$retry", retry), ("$nomatch", (int)SubtitleOutcome.NoMatch),
            ("$failed", (int)SubtitleOutcome.Failed));
        if (!retry)
            return true;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT outcome IN ($pending,$unavailable) FROM subtitle_jobs WHERE path=$path AND language=$language;";
        command.Parameters.AddWithValue("$pending", (int)SubtitleOutcome.Pending);
        command.Parameters.AddWithValue("$unavailable", (int)SubtitleOutcome.Unavailable);
        command.Parameters.AddWithValue("$path", target.Path);
        command.Parameters.AddWithValue("$language", target.Language);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private Task<bool> Record(Target target, bool created, CancellationToken cancellation) => database.Run(connection =>
    {
        if (!created && !Current(connection, target.Videos.Where(video => video.Eligible)))
            return false;
        Associate(connection, target);
        Execute(connection, """
            INSERT INTO subtitle_outputs(path,language,created) VALUES($path,$language,$created)
            ON CONFLICT(path,language) DO NOTHING;
            DELETE FROM subtitle_jobs WHERE path=$path AND language=$language;
            DELETE FROM subtitle_failures WHERE path=$path;
            """, ("$path", target.Path), ("$language", target.Language), ("$created", created));
        return true;
    }, cancellation);

    private static bool Current(SqliteConnection connection, IEnumerable<Video> videos)
    {
        foreach (var video in videos)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT 1 FROM collected_files f JOIN locations l USING(entry_id)
                WHERE f.origin=$origin AND f.file_index=$index AND l.path=$path COLLATE PATH
                    AND f.entry_id=$entry AND f.wanted=$wanted;
                """;
            command.Parameters.AddWithValue("$origin", video.Origin);
            command.Parameters.AddWithValue("$index", video.Index);
            command.Parameters.AddWithValue("$path", video.FinalPath);
            command.Parameters.AddWithValue("$entry", video.EntryId);
            command.Parameters.AddWithValue("$wanted", video.Wanted);
            if (command.ExecuteScalar() is not null)
                return true;
        }
        return false;
    }

    private async Task RefreshCounts()
    {
        var targets = (await Targets(_lifetime.Token).ConfigureAwait(false)).Where(Eligible).ToArray();
        var counts = await database.Run(connection =>
        {
            var missing = new Dictionary<string, int>();
            foreach (var target in targets.Where(target => target.Complete))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1 FROM subtitle_outputs WHERE path=$path AND language=$language;";
                command.Parameters.AddWithValue("$path", target.Path);
                command.Parameters.AddWithValue("$language", target.Language);
                if (command.ExecuteScalar() is null)
                    missing[target.Language] = missing.GetValueOrDefault(target.Language) + 1;
            }
            using var pending = connection.CreateCommand();
            pending.CommandText = "SELECT path FROM subtitle_jobs WHERE outcome IN ($pending,$unavailable);";
            pending.Parameters.AddWithValue("$pending", (int)SubtitleOutcome.Pending);
            pending.Parameters.AddWithValue("$unavailable", (int)SubtitleOutcome.Unavailable);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = pending.ExecuteReader())
                while (reader.Read())
                    paths.Add(reader.GetString(0));
            var finished = new List<(string Path, bool Found)>();
            foreach (var path in _finding.Where(path => !paths.Contains(path)))
            {
                using var saved = connection.CreateCommand();
                saved.CommandText = "SELECT 1 FROM subtitle_outputs WHERE path=$path;";
                saved.Parameters.AddWithValue("$path", path);
                finished.Add((path, targets.Any(target => StringComparer.OrdinalIgnoreCase.Equals(target.Path, path)) &&
                    saved.ExecuteScalar() is not null));
            }
            var failures = new Dictionary<string, SubtitleFailure>(StringComparer.OrdinalIgnoreCase);
            using var failed = connection.CreateCommand();
            failed.CommandText = "SELECT path,reason FROM subtitle_failures ORDER BY path;";
            using (var reader = failed.ExecuteReader())
                while (reader.Read())
                    failures.Add(reader.GetString(0), (SubtitleFailure)reader.GetInt32(1));
            return (Missing: missing, Pending: paths.Count, Finished: finished, Failures: failures);
        }, _lifetime.Token).ConfigureAwait(false);
        Missing = counts.Missing;
        Pending = counts.Pending;
        FileFailures = counts.Failures;
        foreach (var item in counts.Finished)
            if (_finding.Remove(item.Path) && item.Found)
                Found = (Found ?? 0) + 1;
        Notify();
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values)
            command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private Task ReportFile(string path, SubtitleFailure reason) => database.Run(connection =>
    {
        Execute(connection, """
            INSERT INTO subtitle_failures(path,reason) VALUES($path,$reason)
            ON CONFLICT(path) DO UPDATE SET reason=excluded.reason;
            """, ("$path", path), ("$reason", (int)reason));
        return true;
    }, CancellationToken.None);

    private sealed record Video(string Origin, int Index)
    {
        internal required string Path { get; init; }
        internal long EntryId { get; init; }
        internal required string FinalPath { get; init; }
        internal long Size { get; init; }
        internal bool Complete { get; init; }
        internal bool Readable { get; init; }
        internal bool Wanted { get; init; }
        internal bool Eligible => Wanted && !FileName.Interpret(FinalPath).IsExtra;
        internal bool CanHash => Eligible && Complete && Readable && Size >= SubtitleFile.HashMinimum;
    }
    private sealed record Target(string Path, string Language, Video[] Videos)
    {
        internal bool Complete => Videos.Any(video => video.Eligible && video.Complete);
        internal bool IsAmbiguous => Videos.Select(video => video.FinalPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
    }
}
