using System.Text.Json;
using Microsoft.Data.Sqlite;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Shared.Network;
using SupplierType = Syno.TinyTorrent.Subtitles.Supplier;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Dictionary<string, long> _batch = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, RetryCommand> _retryCommands = [];
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
                        CancellationToken cancellation;
                        lock (_requestSync)
                            cancellation = _requests.Token;
                        var retry = await Run(
                                connection =>
                                {
                                    using var command = connection.CreateCommand();
                                    command.CommandText =
                                        $"SELECT path,language,manual,retry_at FROM subtitle_jobs WHERE {Due};";
                                    command.Parameters.AddWithValue(
                                        "$automatic",
                                        Enabled && settled.IsCompleted
                                    );
                                    command.Parameters.AddWithValue(
                                        "$pending",
                                        (int)SubtitleOutcome.Pending
                                    );
                                    using var reader = command.ExecuteReader();
                                    long? earliest = null;
                                    while (reader.Read())
                                        if (
                                            Accepted(
                                                reader.GetString(0),
                                                reader.GetString(1),
                                                reader.GetBoolean(2)
                                            )
                                            is not null
                                        )
                                        {
                                            var value = reader.GetInt64(3);
                                            earliest = earliest is null
                                                ? value
                                                : Math.Min(earliest.Value, value);
                                        }
                                    return earliest;
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
                        var targets = new TargetIndex(
                            (await Targets(cancellation).ConfigureAwait(false))
                                .Where(Eligible)
                                .ToArray()
                        );
                        var target = await Run(
                                connection =>
                                {
                                    using var command = connection.CreateCommand();
                                    command.CommandText = $"""
                                        SELECT path,language,manual,guesses,reference,match,fresh FROM subtitle_jobs
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
                                        var path = reader.GetString(0);
                                        var language = reader.GetString(1);
                                        var found = targets.Find(language, path);
                                        if (
                                            found is not null
                                            && Accepted(path, language, reader.GetBoolean(2))
                                                is not null
                                        )
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
                                                    Fresh = reader.IsDBNull(6)
                                                        ? null
                                                        : JsonSerializer.Deserialize<SearchIntent>(
                                                            reader.GetString(6)
                                                        ),
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
                        if (Accepted(target.Path, target.Language, target.Manual) is not { } run)
                            continue;
                        using (
                            var request = CancellationTokenSource.CreateLinkedTokenSource(
                                cancellation,
                                run.Token
                            )
                        )
                        {
                            run.Report(run.Observation.Progress, new WorkExecution.Running());
                            await Acquire(target, request.Token).ConfigureAwait(false);
                        }
                        await RefreshCounts().ConfigureAwait(false);
                        delay = TimeSpan.Zero;
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine("Subtitle acquisition cancelled.");
                    lock (_requestSync)
                        foreach (var run in new[] { _manualWork, _automaticWork })
                            if (run is not null)
                                run.Report(run.Observation.Progress, new WorkExecution.Waiting());
                }
                catch (SqliteException error)
                {
                    _databaseFailure = error;
                }
                finally
                {
                    _operation.Release();
                    Notify();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine(
                "Subtitle worker wait cancelled: worker is stopping."
            );
        }
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
                    Fresh = Fresh(target),
                };
                await SaveMatch(target, cached, cachedLookup, cancellation).ConfigureAwait(false);
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
                    await SetOutcome(target, SubtitleOutcome.Failed, null, cancellation)
                        .ConfigureAwait(false);
                    return;
                }
                await SetOutcome(
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
                    await SetOutcome(target, SubtitleOutcome.Pending, null, cancellation)
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
                Stage = stage,
                Fresh = Fresh(target),
            };
            var matches = await Lookup(lookup, cancellation).ConfigureAwait(false);
            await ValidateLookup(cancellation).ConfigureAwait(false);
            Answered(SubtitleFailure.None);
            if (matches.Count == 0)
            {
                await RecordNoMatch(target, cancellation).ConfigureAwait(false);
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
                            lock (_requestSync)
                                Accepted(target.Path, target.Language, target.Manual)
                                    ?.Jobs.Add(new(pending.Path, pending.Language, pending.Manual));
                        }
                        return true;
                    },
                    cancellation
                )
                .ConfigureAwait(false);
            await SaveMatch(target, matches[0], lookup, cancellation).ConfigureAwait(false);
        }
        catch (RequestBlockedException error)
        {
            if (error.Block.Kind == RequestBlockKind.Attention)
                Answered(SubtitleFailure.Authentication);
            await SetOutcome(
                    target,
                    error.Block.Kind == RequestBlockKind.Attention ? SubtitleOutcome.Unavailable
                        : error.Block.RetryAt is null ? SubtitleOutcome.Blocked
                        : SubtitleOutcome.Pending,
                    error.Block.RetryAt,
                    cancellation
                )
                .ConfigureAwait(false);
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
            await SetOutcome(target, outcome, error.RetryAt, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Subtitle lookup cancelled ({target.Language}): target changed."
            );
            await SetOutcome(target, SubtitleOutcome.Pending, null, cancellation)
                .ConfigureAwait(false);
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
            await SetOutcome(
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
            await RefreshAllowance().ConfigureAwait(false);
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
        var counter = SearchCount(lookup.FileName, lookup.Language);
        var downloaded = false;
        var questions = new List<RequestKey>();
        async Task<IReadOnlyList<SubtitleCandidate>> Read(SubtitleLookup question)
        {
            var key = SearchKey(question);
            questions.Add(key);
            var saved = await Run(
                    connection =>
                        SavedSearch(connection, key, question.Fresh?.Command.Accepted ?? 0),
                    cancellation
                )
                .ConfigureAwait(false);
            if (saved is not null)
                return saved.Candidates;
            if (question.Fresh is null)
            {
                if (
                    await policy
                        .Read(new LookupRequest(key, counter), cancellation)
                        .ConfigureAwait(false) is
                    { } block
                )
                    throw new RequestBlockedException(block);
            }
            var answer = await supplier
                .Candidates(http, _account, question, cancellation)
                .ConfigureAwait(false);
            await Run(
                    connection =>
                    {
                        Execute(
                            connection,
                            """
                            INSERT INTO subtitle_searches(provider_id,question_id,answer,checked) VALUES($provider,$question,$answer,$checked)
                            ON CONFLICT(provider_id,question_id) DO UPDATE SET answer=excluded.answer,reusable=1,rejected=0,checked=excluded.checked;
                            """,
                            ("$provider", key.ProviderId),
                            ("$question", key.QuestionId),
                            ("$checked", DateTimeOffset.UtcNow.UtcTicks),
                            ("$answer", JsonSerializer.Serialize(answer))
                        );
                        RequestPolicy.Record(
                            connection,
                            new(key),
                            answer.Empty ? RequestOutcome.Missing
                                : answer.Candidates.Count == 0 ? RequestOutcome.NoMatch
                                : RequestOutcome.Answered
                        );
                        return true;
                    },
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            downloaded = true;
            return answer.Candidates;
        }
        var release = FileName.Interpret(lookup.FileName);
        if (release.IsExtra || release.Title.Length == 0)
            return [];
        var candidates = await Read(lookup).ConfigureAwait(false);
        var exact = Exact(release, candidates);
        if (!choices && exact is null && lookup.Hash is not null)
        {
            candidates = await Read(lookup with { Hash = null }).ConfigureAwait(false);
            exact = Exact(release, candidates);
        }
        if (downloaded)
            await policy
                .Record(new(counter, counter), RequestOutcome.Answered)
                .ConfigureAwait(false);
        if (exact is null)
            await Run(
                    connection =>
                    {
                        foreach (var key in questions)
                            Execute(
                                connection,
                                "UPDATE subtitle_searches SET rejected=1 WHERE provider_id=$provider AND question_id=$question;",
                                ("$provider", key.ProviderId),
                                ("$question", key.QuestionId)
                            );
                        return true;
                    },
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        if (choices)
            return candidates;
        if (exact is not null)
            return [exact with { Exact = true }];
        if (supplier.SupportsHash && lookup.Hash is null)
            return [];
        return candidates
            .OrderByDescending(candidate => SupplierType.Likeness(release, candidate.Release))
            .DistinctBy(candidate => candidate.Reference)
            .DistinctBy(candidate => FileName.Normalize(candidate.Release))
            .Take(lookup.Guesses)
            .ToArray();
    }

    private static SubtitleCandidate? Exact(
        Release release,
        IEnumerable<SubtitleCandidate> candidates
    ) =>
        candidates.FirstOrDefault(candidate =>
            candidate.HashMatch || SupplierType.SameRelease(release, candidate.Release)
        );

    private RequestKey SearchCount(string fileName, string language) =>
        RequestKey.Create(
            RequireSupplier(SupplierId).ProviderId,
            "search-count",
            [Query(fileName), language]
        );

    private RequestKey SearchKey(SubtitleLookup lookup) =>
        RequestKey.Create(
            RequireSupplier(SupplierId).ProviderId,
            "search",
            [
                RequireSupplier(SupplierId).Question(lookup),
                lookup.Language,
                lookup.Hash ?? string.Empty,
                lookup.Hash is null
                    ? string.Empty
                    : lookup.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                RequireSupplier(SupplierId).Scope(_account, "search").Identity,
                lookup.Stage.ToString(),
            ]
        );

    private static SubtitleSearch? SavedSearch(
        SqliteConnection connection,
        RequestKey key,
        long since = 0
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT answer FROM subtitle_searches WHERE provider_id=$provider AND question_id=$question AND reusable=1 AND checked>=$since;";
        command.Parameters.AddWithValue("$since", since);
        command.Parameters.AddWithValue("$provider", key.ProviderId);
        command.Parameters.AddWithValue("$question", key.QuestionId);
        return command.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<SubtitleSearch>(json)
            : null;
    }

    private Task RecordNoMatch(Target target, CancellationToken cancellation) =>
        Run(
            connection =>
            {
                Execute(
                    connection,
                    """
                    UPDATE subtitle_jobs SET outcome=$outcome,retry_at=0,attempts=attempts+1,
                        fresh=NULL,match=$none WHERE path=$path AND language=$language;
                    """,
                    ("$outcome", (int)SubtitleOutcome.NoMatch),
                    ("$none", (int)SubtitleMatch.None),
                    ("$path", target.Path),
                    ("$language", target.Language)
                );
                Advance(connection, target);
                return true;
            },
            cancellation
        );

    private Task SetOutcome(
        Target target,
        SubtitleOutcome outcome,
        DateTimeOffset? retry,
        CancellationToken cancellation
    ) =>
        Run(
            connection =>
            {
                if (outcome == SubtitleOutcome.Pending && retry is null)
                    retry = DateTimeOffset.UtcNow.AddMinutes(1);
                var intent = outcome
                    is SubtitleOutcome.Pending
                        or SubtitleOutcome.Unavailable
                        or SubtitleOutcome.Blocked
                    ? target.Fresh
                    : null;
                Execute(
                    connection,
                    """
                    UPDATE subtitle_jobs SET outcome=$outcome,retry_at=$retry,attempts=attempts+1,
                        fresh=$fresh
                    WHERE path=$path AND language=$language;
                    """,
                    ("$outcome", (int)outcome),
                    ("$retry", retry?.ToUnixTimeSeconds() ?? 0),
                    ("$fresh", intent is null ? null : JsonSerializer.Serialize(intent)),
                    ("$path", target.Path),
                    ("$language", target.Language)
                );
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
        var request = new LookupRequest(
            RequestKey.Create(
                RequireSupplier(SupplierId).ProviderId,
                "download",
                [candidate.Reference]
            )
        );
        if (
            await policy
                .Read(request, cancellation, lookup.Fresh?.Command.Accepted ?? 0)
                .ConfigureAwait(false) is
            { } block
        )
            throw new RequestBlockedException(block);
        byte[] bytes;
        try
        {
            bytes = await RequireSupplier(SupplierId)
                .Download(http, _account, lookup, candidate, cancellation)
                .ConfigureAwait(false);
        }
        catch (SubtitleException error) when (error.Reason == SubtitleFailure.Format)
        {
            await policy.Record(request, RequestOutcome.Unusable).ConfigureAwait(false);
            throw;
        }
        await policy.Record(request, RequestOutcome.Answered).ConfigureAwait(false);
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
                    "UPDATE subtitle_jobs SET outcome=$complete,reference=NULL,fresh=NULL,match=COALESCE($match,match) WHERE path=$path AND language=$language;",
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
            var command = DateTimeOffset.UtcNow.UtcTicks;
            var accepted = await Run(
                    connection =>
                    {
                        var paths = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var first in Searchable(connection, targets, scope, entries))
                            if (
                                Accept(
                                    connection,
                                    first with
                                    {
                                        Fresh =
                                            scope == SubtitleScope.New
                                                ? null
                                                : new(
                                                    SearchCount(
                                                        first.Videos[0].FinalPath,
                                                        first.Language
                                                    ),
                                                    command
                                                ),
                                    },
                                    true,
                                    guesses
                                )
                            )
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

    internal Task StopFinding()
    {
        lock (_requestSync)
            return _manualWork?.Stop() ?? Task.CompletedTask;
    }

    private FreshRequest? Fresh(Target target)
    {
        if (
            target.Fresh is not { } intent
            || intent.Key != SearchCount(target.Videos[0].FinalPath, target.Language)
        )
            return null;
        if (!_retryCommands.TryGetValue(intent.Accepted, out var command))
            _retryCommands.Add(intent.Accepted, command = new(intent.Accepted));
        return new(command, intent.Key);
    }

    private sealed record SearchIntent(RequestKey Key, long Accepted);
}
