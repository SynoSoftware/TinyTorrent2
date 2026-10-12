using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Models;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Dictionary<string, long> _batch = new(StringComparer.OrdinalIgnoreCase);
    private bool CanAcquire => _connected && Ready && !NeedsSetup(Failure);
    internal bool CanFind => _connected && Ready && !Finding;

    // Reconcile decides which automatic jobs exist, so the worker only waits
    // for the switch and the start delay before running them.
    private const string Due = "outcome=$pending AND (manual=1 OR $automatic)";

    // These failures persist until the person changes the supplier setup, so
    // work waits instead of retrying.
    private static bool NeedsSetup(SubtitleFailure reason) =>
        reason
            is SubtitleFailure.Authentication
                or SubtitleFailure.Unconfigured
                or SubtitleFailure.Unavailable;

    private void Wake()
    {
        if (!_disposed && _signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
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
                if (!CanAcquire && !HasDeletions)
                    continue;
                await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    await DeleteAccepted().ConfigureAwait(false);
                    while (CanAcquire)
                    {
                        var now = DateTimeOffset.UtcNow;
                        if (RetryAt is { } allowance && allowance > now)
                        {
                            delay = TimeSpan.FromSeconds(
                                Math.Min((allowance - now).TotalSeconds, int.MaxValue / 1000)
                            );
                            break;
                        }
                        CancellationToken cancellation;
                        lock (_requestSync)
                            cancellation = _requests.Token;
                        var retry = await Run(
                                connection =>
                                {
                                    using var command = connection.CreateCommand();
                                    command.CommandText =
                                        $"SELECT MIN(retry_at) FROM subtitle_jobs WHERE {Due};";
                                    command.Parameters.AddWithValue(
                                        "$automatic",
                                        Enabled && settled.IsCompleted
                                    );
                                    command.Parameters.AddWithValue(
                                        "$pending",
                                        (int)SubtitleOutcome.Pending
                                    );
                                    return command.ExecuteScalar() is long value
                                        ? (long?)value
                                        : null;
                                },
                                cancellation
                            )
                            .ConfigureAwait(false);
                        if (retry is null)
                            break;
                        var remaining = retry.Value - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        if (remaining > 0)
                        {
                            delay = TimeSpan.FromSeconds(Math.Min(remaining, int.MaxValue / 1000));
                            break;
                        }
                        var targets = (await Targets(cancellation).ConfigureAwait(false))
                            .Where(Eligible)
                            .ToArray();
                        var target = await Run(
                                connection =>
                                {
                                    using var command = connection.CreateCommand();
                                    command.CommandText = $"""
                                        SELECT path,language,manual,guesses,reference,match FROM subtitle_jobs
                                        WHERE {Due} AND retry_at<=$now ORDER BY retry_at,path;
                                        """;
                                    command.Parameters.AddWithValue(
                                        "$pending",
                                        (int)SubtitleOutcome.Pending
                                    );
                                    command.Parameters.AddWithValue(
                                        "$automatic",
                                        Enabled && settled.IsCompleted
                                    );
                                    command.Parameters.AddWithValue(
                                        "$now",
                                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                    );
                                    using var reader = command.ExecuteReader();
                                    var due = new List<Target>();
                                    while (reader.Read())
                                    {
                                        var found = targets.FirstOrDefault(value =>
                                            value.Language == reader.GetString(1)
                                            && StringComparer.OrdinalIgnoreCase.Equals(
                                                value.Path,
                                                reader.GetString(0)
                                            )
                                        );
                                        if (found is not null)
                                            due.Add(
                                                found with
                                                {
                                                    Manual = reader.GetBoolean(2),
                                                    Guesses = reader.GetInt32(3),
                                                    Reference = reader.IsDBNull(4)
                                                        ? null
                                                        : reader.GetString(4),
                                                    Match = reader.IsDBNull(5)
                                                        ? null
                                                        : (SubtitleMatch)reader.GetInt32(5),
                                                }
                                            );
                                    }
                                    // Extra guesses spend the supplier's allowance only
                                    // after every due rank 1 subtitle.
                                    return due.MinBy(value => value.Rank);
                                },
                                cancellation
                            )
                            .ConfigureAwait(false);
                        if (target is null)
                            break;
                        await Acquire(target, cancellation).ConfigureAwait(false);
                        await RefreshCounts().ConfigureAwait(false);
                        delay = TimeSpan.Zero;
                        break;
                    }
                }
                catch (OperationCanceledException) { }
                catch (SqliteException) { }
                finally
                {
                    _operation.Release();
                    Notify();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task Validate(Target target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!_connected || !Enabled && !target.Manual)
            throw new OperationCanceledException(cancellation);
        var current = await Run(
                connection => Current(connection, target.Videos.Where(video => video.Eligible)),
                cancellation
            )
            .ConfigureAwait(false);
        if (!current)
            throw new OperationCanceledException(cancellation);
    }

    private async Task Acquire(Target target, CancellationToken cancellation)
    {
        try
        {
            await Validate(target, cancellation).ConfigureAwait(false);
            if (
                !target.Manual
                && target.Reference is null
                && await Run(connection => Recorded(connection, target), cancellation)
                    .ConfigureAwait(false)
            )
            {
                await Complete(target).ConfigureAwait(false);
                return;
            }
            if (!target.Manual && SubtitleFile.Exists(target.Path))
            {
                await Record(target, false, cancellation).ConfigureAwait(false);
                await Complete(target).ConfigureAwait(false);
                return;
            }
            if (target.Reference is { } reference)
            {
                // SaveAccount clears or deletes references on a supplier change,
                // so a stored reference always belongs to the current supplier.
                var prefix = SupplierId + ":";
                var cached = new SubtitleCandidate(
                    target.Language,
                    string.Empty,
                    string.Empty,
                    reference[prefix.Length..]
                )
                {
                    Exact = target.Match == SubtitleMatch.Exact,
                };
                var cachedLookup = new SubtitleLookup(
                    Path.GetFileName(target.Videos[0].FinalPath),
                    target.Language,
                    token => Validate(target, token)
                )
                {
                    Guesses = target.Guesses,
                };
                await SaveMatch(target, cached, cachedLookup, cancellation).ConfigureAwait(false);
                return;
            }
            if (
                !await Run(connection => CanLookup(connection, target), cancellation)
                    .ConfigureAwait(false)
            )
            {
                await Complete(target).ConfigureAwait(false);
                return;
            }
            var stage = await Run(
                    connection =>
                    {
                        if (Supplier?.SupportsHash != true)
                            Execute(
                                connection,
                                "UPDATE subtitle_jobs SET stage=$stage WHERE path=$path AND language=$language;",
                                ("$stage", (int)Stage(target)),
                                ("$path", target.Path),
                                ("$language", target.Language)
                            );
                        using var command = connection.CreateCommand();
                        command.CommandText =
                            "SELECT stage FROM subtitle_jobs WHERE path=$path AND language=$language;";
                        command.Parameters.AddWithValue("$path", target.Path);
                        command.Parameters.AddWithValue("$language", target.Language);
                        return (SubtitleStage)Convert.ToInt32(command.ExecuteScalar());
                    },
                    cancellation
                )
                .ConfigureAwait(false);
            var hashing = Supplier?.SupportsHash == true && stage == SubtitleStage.Complete;
            var video = target.Videos.FirstOrDefault(value =>
                value.Eligible && (!hashing || value.CanHash) && SubtitleFile.Exists(value.Path)
            );
            if (video is null)
            {
                // An unfinished or moving torrent may not have its file in
                // place yet; a finished movie at rest that is missing from its
                // folder gets no subtitle.
                var eligible = target.Videos.Where(value => value.Eligible).ToArray();
                if (
                    target.Complete
                    && !eligible.Any(value => SubtitleFile.Exists(value.Path))
                    && await Run(
                            connection => Current(connection, eligible, readable: true),
                            cancellation
                        )
                        .ConfigureAwait(false)
                )
                {
                    ReportFile(target.Path, SubtitleFailure.Missing);
                    await Defer(target, SubtitleOutcome.Failed, null, cancellation)
                        .ConfigureAwait(false);
                    return;
                }
                await Defer(
                        target,
                        SubtitleOutcome.Pending,
                        DateTimeOffset.UtcNow.AddMinutes(1),
                        cancellation
                    )
                    .ConfigureAwait(false);
                return;
            }
            async Task ValidateLookup(CancellationToken token)
            {
                await Validate(target, token).ConfigureAwait(false);
                if (!hashing)
                    return;
                var current = await Run(
                        connection => Current(connection, [video], readable: true),
                        token
                    )
                    .ConfigureAwait(false);
                if (!current)
                    throw new OperationCanceledException(token);
            }
            string? hash = null;
            if (hashing)
            {
                await ValidateLookup(cancellation).ConfigureAwait(false);
                hash = await SubtitleFile
                    .Hash(video.Path, video.Size, cancellation)
                    .ConfigureAwait(false);
                await ValidateLookup(cancellation).ConfigureAwait(false);
                if (hash is null)
                {
                    await Defer(target, SubtitleOutcome.Pending, null, cancellation)
                        .ConfigureAwait(false);
                    return;
                }
            }
            var lookup = new SubtitleLookup(
                Path.GetFileName(video.FinalPath),
                target.Language,
                ValidateLookup
            )
            {
                Hash = hash,
                Size = video.Size,
                Guesses = target.Guesses,
            };
            var matches = await Lookup(lookup, cancellation).ConfigureAwait(false);
            await ValidateLookup(cancellation).ConfigureAwait(false);
            Answered(SubtitleFailure.None);
            if (matches.Count == 0)
            {
                await Defer(target, SubtitleOutcome.NoMatch, null, cancellation)
                    .ConfigureAwait(false);
                return;
            }
            await Run(
                    connection =>
                    {
                        for (var index = 0; index < matches.Count; index++)
                        {
                            var candidate = matches[index];
                            var pending = target with
                            {
                                Rank = index + 1,
                                Path = SubtitleFile.Destination(
                                    video.FinalPath,
                                    target.Language,
                                    index + 1
                                ),
                            };
                            if (
                                index > 0
                                && !Accept(connection, pending, target.Manual, target.Guesses)
                            )
                                continue;
                            Execute(
                                connection,
                                "UPDATE subtitle_jobs SET reference=$reference,match=$match,outcome=$pending,retry_at=0 WHERE path=$path AND language=$language;",
                                ("$reference", SupplierId + ":" + candidate.Reference),
                                (
                                    "$match",
                                    (int)(
                                        candidate.Exact ? SubtitleMatch.Exact : SubtitleMatch.Likely
                                    )
                                ),
                                ("$pending", (int)SubtitleOutcome.Pending),
                                ("$path", pending.Path),
                                ("$language", pending.Language)
                            );
                            if (_batch.TryGetValue(target.Path, out var entry))
                                _batch[pending.Path] = entry;
                        }
                        return true;
                    },
                    cancellation
                )
                .ConfigureAwait(false);
            await SaveMatch(target, matches[0], lookup, cancellation).ConfigureAwait(false);
        }
        catch (SubtitleException error)
        {
            Answered(error.Reason);
            if (error.Reason == SubtitleFailure.Format)
                ReportFile(target.Path, error.Reason);
            var outcome =
                error.Reason == SubtitleFailure.Format ? SubtitleOutcome.Failed
                : NeedsSetup(error.Reason) ? SubtitleOutcome.Unavailable
                : SubtitleOutcome.Pending;
            await Defer(
                    target,
                    outcome,
                    error.Reason == SubtitleFailure.Quota ? DateTimeOffset.UtcNow : error.RetryAt,
                    cancellation
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            await Defer(target, SubtitleOutcome.Pending, null, cancellation).ConfigureAwait(false);
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or ArgumentException
            )
        {
            var reason = error is InvalidDataException or ArgumentException
                ? SubtitleFailure.Format
                : SubtitleFailure.Save;
            ReportFile(target.Path, reason);
            // A folder that refuses the file refuses it again, so the job waits
            // for Reconcile or Find instead of retrying on a timer.
            if (reason == SubtitleFailure.Save)
                _saveFailed = true;
            await Defer(
                    target,
                    reason == SubtitleFailure.Format
                        ? SubtitleOutcome.Failed
                        : SubtitleOutcome.Unavailable,
                    null,
                    cancellation
                )
                .ConfigureAwait(false);
        }
        finally
        {
            await SaveAllowance(RequireSupplier(SupplierId).Allowance(_account))
                .ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<SubtitleCandidate>> Lookup(
        SubtitleLookup lookup,
        CancellationToken cancellation,
        bool choices = false
    )
    {
        await lookup.Validate(cancellation).ConfigureAwait(false);
        var supplier = RequireSupplier(SupplierId);
        var candidates = choices
            ? await supplier.Candidates(http, _account, lookup, cancellation).ConfigureAwait(false)
            : await supplier.Match(http, _account, lookup, cancellation).ConfigureAwait(false);
        await Run(
                connection =>
                {
                    Execute(
                        connection,
                        """
                        INSERT INTO subtitle_lookups(supplier,query,language,total) VALUES($supplier,$query,$language,1)
                        ON CONFLICT(supplier,query,language) DO UPDATE SET total=total+1;
                        """,
                        ("$supplier", (int)supplier.SupplierId),
                        ("$query", Query(lookup.FileName)),
                        ("$language", lookup.Language)
                    );
                    return true;
                },
                CancellationToken.None
            )
            .ConfigureAwait(false);
        return candidates;
    }

    private Task Defer(
        Target target,
        SubtitleOutcome outcome,
        DateTimeOffset? retry,
        CancellationToken cancellation
    ) =>
        Run(
            connection =>
            {
                if (outcome == SubtitleOutcome.Pending && retry is null)
                {
                    using var attempts = connection.CreateCommand();
                    attempts.CommandText =
                        "SELECT attempts FROM subtitle_jobs WHERE path=$path AND language=$language;";
                    attempts.Parameters.AddWithValue("$path", target.Path);
                    attempts.Parameters.AddWithValue("$language", target.Language);
                    var count = Convert.ToInt32(attempts.ExecuteScalar());
                    retry = DateTimeOffset.UtcNow.AddSeconds(
                        Math.Min(3600, 30 * Math.Pow(2, Math.Min(count, 7)))
                    );
                }
                Execute(
                    connection,
                    """
                    UPDATE subtitle_jobs SET outcome=$outcome,retry_at=$retry,attempts=attempts+1,
                        match=CASE WHEN $outcome=$nomatch THEN $none ELSE match END
                    WHERE path=$path AND language=$language;
                    """,
                    ("$outcome", (int)outcome),
                    ("$retry", retry?.ToUnixTimeSeconds() ?? 0),
                    ("$path", target.Path),
                    ("$language", target.Language),
                    ("$nomatch", (int)SubtitleOutcome.NoMatch),
                    ("$none", (int)SubtitleMatch.None)
                );
                if (outcome == SubtitleOutcome.NoMatch)
                    Advance(connection, target);
                return true;
            },
            cancellation
        );

    private async Task SaveMatch(
        Target target,
        SubtitleCandidate match,
        SubtitleLookup lookup,
        CancellationToken cancellation
    )
    {
        var reference = SupplierId + ":" + match.Reference;
        var saved = await Run(
                connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT DISTINCT o.path,o.reference FROM subtitle_outputs o
                        JOIN subtitle_output_sources s USING(path,language)
                        JOIN collected_files f ON f.origin=s.origin AND f.file_index=s.file_index
                        JOIN locations l ON l.entry_id=f.entry_id
                        WHERE f.entry_id=$entry AND o.language=$language AND s.movie_path=l.path COLLATE PATH;
                        """;
                    command.Parameters.AddWithValue("$entry", target.Videos[0].EntryId);
                    command.Parameters.AddWithValue("$language", target.Language);
                    using var reader = command.ExecuteReader();
                    var outputs = new List<(string Path, string? Reference)>();
                    while (reader.Read())
                        outputs.Add(
                            (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1))
                        );
                    return outputs;
                },
                cancellation
            )
            .ConfigureAwait(false);
        var present = saved.Where(output => SubtitleFile.Exists(output.Path)).ToArray();
        if (
            !present.Any(output => output.Reference == reference)
            && (match.Exact || present.Length < target.Guesses)
        )
            await Download(Vacant(target), match, lookup, cancellation).ConfigureAwait(false);
        await Complete(target, match.Exact ? SubtitleMatch.Exact : SubtitleMatch.Likely)
            .ConfigureAwait(false);
    }

    // The first rank without a file beside the video, so a new subtitle never
    // replaces one already there.
    private static Target Vacant(Target target)
    {
        var rank = 1;
        var path = SubtitleFile.Destination(target.Videos[0].FinalPath, target.Language, rank);
        while (SubtitleFile.Exists(path))
            path = SubtitleFile.Destination(target.Videos[0].FinalPath, target.Language, ++rank);
        return target with { Path = path, Rank = rank };
    }

    private async Task<bool> Download(
        Target target,
        SubtitleCandidate candidate,
        SubtitleLookup lookup,
        CancellationToken cancellation
    )
    {
        await lookup.Validate(cancellation).ConfigureAwait(false);
        // A folder that refuses the file fails here, before the download
        // spends the supplier's allowance.
        SubtitleFile.Probe(target.Path);
        var bytes = await RequireSupplier(SupplierId)
            .Download(http, _account, lookup, candidate, cancellation)
            .ConfigureAwait(false);
        await lookup.Validate(cancellation).ConfigureAwait(false);
        var created = await SubtitleFile
            .Publish(target.Path, bytes, cancellation)
            .ConfigureAwait(false);
        // Publication has committed; cancellation must not discard ownership of its file.
        await Record(
                target,
                created,
                CancellationToken.None,
                created ? SupplierId + ":" + candidate.Reference : null
            )
            .ConfigureAwait(false);
        return created;
    }

    private Task Complete(Target target, SubtitleMatch? match = null) =>
        Run(
            connection =>
            {
                Execute(
                    connection,
                    "UPDATE subtitle_jobs SET outcome=$complete,reference=NULL,match=COALESCE($match,match) WHERE path=$path AND language=$language;",
                    ("$complete", (int)SubtitleOutcome.Complete),
                    ("$match", match is null ? null : (int)match),
                    ("$path", target.Path),
                    ("$language", target.Language)
                );
                return true;
            },
            CancellationToken.None
        );

    internal async Task Find(SubtitleScope scope, int guesses, IReadOnlySet<long>? entries = null)
    {
        if (!CanFind)
            return;
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!CanFind)
                return;
            CancellationToken cancellation;
            lock (_requestSync)
                cancellation = _requests.Token;
            var targets = (await Targets(cancellation).ConfigureAwait(false))
                .Where(Eligible)
                .ToArray();
            var accepted = await Run(
                    connection =>
                    {
                        var paths = new List<string>();
                        foreach (var first in Searchable(connection, targets, scope, entries))
                            if (Accept(connection, first, true, guesses))
                                paths.Add(first.Path);
                        return paths;
                    },
                    cancellation
                )
                .ConfigureAwait(false);
            _batch.Clear();
            foreach (var target in targets.Where(target => accepted.Contains(target.Path)))
                _batch[target.Path] = target.Videos[0].EntryId;
            BatchId++;
            Found = 0;
            FindTotal = _batch.Values.Distinct().Count();
            await RefreshCounts().ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
        Wake();
    }

    // Ends the batch and any manual jobs kept from an earlier session. Saved
    // subtitles stay, and Reconcile restores the automatic jobs Find took over.
    internal async Task StopFinding()
    {
        Invalidate();
        await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await Run(
                    connection =>
                    {
                        Cancel(connection, manual: true);
                        return true;
                    },
                    _lifetime.Token
                )
                .ConfigureAwait(false);
            await RefreshCounts().ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
        Notify();
        await Reconcile().ConfigureAwait(false);
    }
}
