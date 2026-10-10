using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly HashSet<string> _finding = new(StringComparer.OrdinalIgnoreCase);
    private bool CanAcquire => _connected && Enabled && Ready && !Rechecking &&
        Failure is not SubtitleFailure.Authentication and not SubtitleFailure.Unconfigured and not SubtitleFailure.Unavailable;

    private void Wake()
    {
        if (!_disposed && _signal.CurrentCount == 0)
        {
            try { _signal.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    private async Task Pump()
    {
        var delay = Timeout.InfiniteTimeSpan;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await _signal.WaitAsync(delay, _lifetime.Token).ConfigureAwait(false);
                delay = Timeout.InfiniteTimeSpan;
                if (!CanAcquire)
                    continue;
                await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    while (CanAcquire)
                    {
                        var now = DateTimeOffset.UtcNow;
                        if (RetryAt is { } allowance && allowance > now)
                        {
                            delay = TimeSpan.FromSeconds(Math.Min((allowance - now).TotalSeconds, int.MaxValue / 1000));
                            break;
                        }
                        CancellationToken cancellation;
                        long revision;
                        lock (_requestSync)
                        {
                            cancellation = _requests.Token;
                            revision = _revision;
                        }
                        var retry = await database.Run(connection =>
                        {
                            using var command = connection.CreateCommand();
                            command.CommandText = "SELECT MIN(retry_at) FROM subtitle_jobs WHERE outcome=$pending;";
                            command.Parameters.AddWithValue("$pending", (int)SubtitleOutcome.Pending);
                            return command.ExecuteScalar() is long value ? (long?)value : null;
                        }, cancellation).ConfigureAwait(false);
                        if (retry is null)
                            break;
                        var remaining = retry.Value - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        if (remaining > 0)
                        {
                            delay = TimeSpan.FromSeconds(Math.Min(remaining, int.MaxValue / 1000));
                            break;
                        }
                        var targets = (await Targets(cancellation).ConfigureAwait(false)).Where(Eligible).ToArray();
                        var target = await database.Run(connection =>
                        {
                            using var command = connection.CreateCommand();
                            command.CommandText = """
                                SELECT path,language FROM subtitle_jobs
                                WHERE outcome=$pending AND retry_at<=$now ORDER BY retry_at,path;
                                """;
                            command.Parameters.AddWithValue("$pending", (int)SubtitleOutcome.Pending);
                            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                            using var reader = command.ExecuteReader();
                            while (reader.Read())
                            {
                                var found = targets.FirstOrDefault(value => value.Language == reader.GetString(1) &&
                                    StringComparer.OrdinalIgnoreCase.Equals(value.Path, reader.GetString(0)));
                                if (found is not null)
                                    return found;
                            }
                            return null;
                        }, cancellation).ConfigureAwait(false);
                        if (target is null)
                            break;
                        await Acquire(target, revision, cancellation).ConfigureAwait(false);
                        await RefreshCounts().ConfigureAwait(false);
                        delay = TimeSpan.Zero;
                        break;
                    }
                }
                catch (OperationCanceledException) { }
                catch (SqliteException) { Failure = SubtitleFailure.Database; }
                finally
                {
                    _operation.Release();
                    Notify();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task Validate(Target target, long revision, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!_connected || !Enabled || revision != Interlocked.Read(ref _revision))
            throw new OperationCanceledException(cancellation);
        var current = await database.Run(connection => Current(connection, target.Videos.Where(video => video.Eligible)), cancellation).ConfigureAwait(false);
        if (!current)
            throw new OperationCanceledException(cancellation);
    }

    private async Task Acquire(Target target, long revision, CancellationToken cancellation)
    {
        try
        {
            await Validate(target, revision, cancellation).ConfigureAwait(false);
            if (SubtitleFile.Exists(target.Path))
            {
                await Record(target, false, cancellation).ConfigureAwait(false);
                return;
            }
            var stage = await database.Run(connection =>
            {
                if (Supplier?.SupportsHash != true)
                    Execute(connection, "UPDATE subtitle_jobs SET stage=$stage WHERE path=$path AND language=$language;",
                        ("$stage", (int)Stage(target)), ("$path", target.Path), ("$language", target.Language));
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT stage FROM subtitle_jobs WHERE path=$path AND language=$language;";
                command.Parameters.AddWithValue("$path", target.Path);
                command.Parameters.AddWithValue("$language", target.Language);
                return (SubtitleStage)Convert.ToInt32(command.ExecuteScalar());
            }, cancellation).ConfigureAwait(false);
            var hashing = Supplier?.SupportsHash == true && stage == SubtitleStage.Complete;
            var video = target.Videos.FirstOrDefault(value =>
                value.Eligible && (!hashing || value.CanHash) && SubtitleFile.Exists(value.Path));
            if (video is null)
            {
                await Defer(target, SubtitleOutcome.Pending, DateTimeOffset.UtcNow.AddMinutes(1), cancellation).ConfigureAwait(false);
                return;
            }
            async Task ValidateLookup(CancellationToken token)
            {
                await Validate(target, revision, token).ConfigureAwait(false);
                if (!hashing)
                    return;
                var current = await database.Run(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT 1 FROM collected_files f JOIN locations l USING(entry_id)
                        JOIN current_files USING(entry_id)
                        WHERE f.origin=$origin AND f.file_index=$index AND f.entry_id=$entry
                            AND f.path=$path COLLATE PATH AND l.path=$path COLLATE PATH AND f.size=$size
                            AND f.wanted=1 AND f.complete=1 AND current_files.readable=1;
                        """;
                    command.Parameters.AddWithValue("$origin", video.Origin);
                    command.Parameters.AddWithValue("$index", video.Index);
                    command.Parameters.AddWithValue("$entry", video.EntryId);
                    command.Parameters.AddWithValue("$path", video.Path);
                    command.Parameters.AddWithValue("$size", video.Size);
                    return command.ExecuteScalar() is not null;
                }, token).ConfigureAwait(false);
                if (!current)
                    throw new OperationCanceledException(token);
            }
            string? hash = null;
            if (hashing)
            {
                await ValidateLookup(cancellation).ConfigureAwait(false);
                hash = await SubtitleFile.Hash(video.Path, video.Size, cancellation).ConfigureAwait(false);
                await ValidateLookup(cancellation).ConfigureAwait(false);
                if (hash is null)
                {
                    await Defer(target, SubtitleOutcome.Pending, null, cancellation).ConfigureAwait(false);
                    return;
                }
            }
            var lookup = new SubtitleLookup(Path.GetFileName(video.FinalPath), target.Language,
                ValidateLookup) { Hash = hash, Size = video.Size };
            var bytes = await RequireSupplier(SupplierId).Acquire(http, _account, lookup, cancellation).ConfigureAwait(false);
            await ValidateLookup(cancellation).ConfigureAwait(false);
            Checked = true;
            Failure = SubtitleFailure.None;
            if (bytes is null)
            {
                await Defer(target, SubtitleOutcome.NoMatch, null, cancellation).ConfigureAwait(false);
                return;
            }
            var created = await SubtitleFile.Publish(target.Path, bytes, cancellation).ConfigureAwait(false);
            // Publication has committed; cancellation must not discard ownership of its file.
            await Record(target, created, CancellationToken.None).ConfigureAwait(false);
        }
        catch (SubtitleException error)
        {
            Failure = error.Reason == SubtitleFailure.Format ? SubtitleFailure.None : error.Reason;
            if (error.Reason == SubtitleFailure.Format)
                await ReportFile(target.Path, error.Reason).ConfigureAwait(false);
            var outcome = error.Reason == SubtitleFailure.Format ? SubtitleOutcome.Failed :
                error.Reason is SubtitleFailure.Authentication or SubtitleFailure.Unconfigured or SubtitleFailure.Unavailable
                    ? SubtitleOutcome.Unavailable : SubtitleOutcome.Pending;
            await Defer(target, outcome,
                error.Reason == SubtitleFailure.Quota ? DateTimeOffset.UtcNow : error.RetryAt, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && revision == Interlocked.Read(ref _revision))
        {
            await Defer(target, SubtitleOutcome.Pending, null, cancellation).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            var reason = error is InvalidDataException or ArgumentException ? SubtitleFailure.Format : SubtitleFailure.Save;
            await ReportFile(target.Path, reason).ConfigureAwait(false);
            await Defer(target, reason == SubtitleFailure.Format ? SubtitleOutcome.Failed : SubtitleOutcome.Pending,
                null, cancellation).ConfigureAwait(false);
        }
        finally
        {
            var allowance = RequireSupplier(SupplierId).Allowance(_account);
            await SaveAllowance(allowance).ConfigureAwait(false);
            if (allowance.RetryAt > DateTimeOffset.UtcNow && Failure == SubtitleFailure.None)
                Failure = SubtitleFailure.Quota;
        }
    }

    private Task Defer(Target target, SubtitleOutcome outcome, DateTimeOffset? retry, CancellationToken cancellation) =>
        database.Run(connection =>
        {
            if (outcome == SubtitleOutcome.Pending && retry is null)
            {
                using var attempts = connection.CreateCommand();
                attempts.CommandText = "SELECT attempts FROM subtitle_jobs WHERE path=$path AND language=$language;";
                attempts.Parameters.AddWithValue("$path", target.Path);
                attempts.Parameters.AddWithValue("$language", target.Language);
                var count = Convert.ToInt32(attempts.ExecuteScalar());
                retry = DateTimeOffset.UtcNow.AddSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(count, 7))));
            }
            Execute(connection, """
                UPDATE subtitle_jobs SET outcome=$outcome,retry_at=$retry,attempts=attempts+1
                WHERE path=$path AND language=$language;
                """, ("$outcome", (int)outcome), ("$retry", retry?.ToUnixTimeSeconds() ?? 0),
                ("$path", target.Path), ("$language", target.Language));
            if (outcome == SubtitleOutcome.NoMatch)
                Advance(connection, target);
            return true;
        }, cancellation);

    internal async Task Find()
    {
        if (!Enabled || !Ready || Rechecking || _finding.Count > 0 || !_connected)
            return;
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!Enabled || !Ready || Rechecking || _finding.Count > 0 || !_connected)
                return;
            CancellationToken cancellation;
            lock (_requestSync)
                cancellation = _requests.Token;
            var targets = (await Targets(cancellation).ConfigureAwait(false)).Where(Eligible).ToArray();
            var accepted = await database.Run(connection =>
            {
                var paths = new List<string>();
                foreach (var target in targets.Where(target => target.Complete))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT 1 FROM subtitle_outputs WHERE path=$path AND language=$language;";
                    command.Parameters.AddWithValue("$path", target.Path);
                    command.Parameters.AddWithValue("$language", target.Language);
                    if (command.ExecuteScalar() is not null)
                        continue;
                    if (Accept(connection, target, true))
                        paths.Add(target.Path);
                }
                return paths;
            }, cancellation).ConfigureAwait(false);
            foreach (var path in accepted)
                _finding.Add(path);
            FindTotal = _finding.Count;
            Found = 0;
            await RefreshCounts().ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
        Wake();
    }

    internal async Task Recheck()
    {
        if (Rechecking || _finding.Count > 0 || !_connected)
            return;
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (Rechecking || _finding.Count > 0 || !_connected)
                return;
            Rechecking = true;
            Rechecked = 0;
            CancellationToken cancellation;
            lock (_requestSync)
                cancellation = _requests.Token;
            Notify();
            var targets = (await Targets(cancellation).ConfigureAwait(false))
                .Where(target => !target.IsAmbiguous && target.Complete &&
                    EffectiveLanguages.Contains(target.Language, StringComparer.OrdinalIgnoreCase)).ToArray();
            RecheckTotal = targets.Length;
            foreach (var target in targets)
            {
                cancellation.ThrowIfCancellationRequested();
                var present = await Task.Run(() => SubtitleFile.Exists(target.Path), cancellation).ConfigureAwait(false);
                var recorded = present ? await Record(target, false, cancellation).ConfigureAwait(false) :
                    await database.Run(connection =>
                    {
                        if (!Current(connection, target.Videos.Where(video => video.Eligible)))
                            return false;
                        Execute(connection, "DELETE FROM subtitle_outputs WHERE path=$path AND language=$language;",
                            ("$path", target.Path), ("$language", target.Language));
                        return true;
                    }, cancellation).ConfigureAwait(false);
                if (!recorded)
                    throw new OperationCanceledException(cancellation);
                Rechecked++;
                Notify();
            }
            var now = DateTimeOffset.Now;
            await database.Run(connection =>
            {
                Execute(connection, "UPDATE subtitle_settings SET checked_at=$time WHERE singleton=1;",
                    ("$time", now.ToString("O")));
                Execute(connection, "DELETE FROM subtitle_failures WHERE path NOT IN (SELECT path FROM subtitle_jobs);");
                return true;
            }, cancellation).ConfigureAwait(false);
            LastRecheck = now;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            Failure = error is SqliteException ? SubtitleFailure.Database : SubtitleFailure.Save;
        }
        finally
        {
            Rechecking = false;
            try
            {
                if (!_disposed)
                    await RefreshCounts().ConfigureAwait(false);
            }
            finally
            {
                _operation.Release();
                Notify();
                Wake();
            }
        }
    }
}
