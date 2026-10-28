using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Subtitles;

internal sealed partial class Acquisition
{
    private WorkRun? _manualWork;
    private WorkRun? _automaticWork;

    private ref WorkRun? Slot(bool manual) =>
        ref manual ? ref _manualWork : ref _automaticWork;

    private async Task RefreshWork()
    {
        var pending = await Run(
                connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT path,language,manual,retry_at FROM subtitle_jobs
                        WHERE outcome IN ($pending,$unavailable,$blocked);
                        """;
                    command.Parameters.AddWithValue("$pending", (int)SubtitleOutcome.Pending);
                    command.Parameters.AddWithValue(
                        "$unavailable",
                        (int)SubtitleOutcome.Unavailable
                    );
                    command.Parameters.AddWithValue("$blocked", (int)SubtitleOutcome.Blocked);
                    using var reader = command.ExecuteReader();
                    var jobs = new List<PendingJob>();
                    while (reader.Read())
                        jobs.Add(
                            new(
                                new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)),
                                reader.GetInt64(3)
                            )
                        );
                    return jobs;
                },
                _lifetime.Token
            )
            .ConfigureAwait(false);
        foreach (var manual in new[] { true, false })
        {
            var run = Slot(manual);
            if (run is { Ended: true })
                run = null;
            var jobs = pending.Where(job => job.Job.Manual == manual).ToArray();
            if (run is { Stopping: true })
                continue;
            if (run is not null && !jobs.Any(job => run.Jobs.Contains(job.Job)))
            {
                try
                {
                    await CompleteWork(run).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    run.Operation.Outcome = error is OperationCanceledException
                        ? WorkOutcome.Cancelled : WorkOutcome.Failed;
                    run.Operation.Error = error;
                    throw;
                }
                finally
                {
                    await run.Finish().ConfigureAwait(false);
                }
                run = null;
            }
            if (run is null && jobs.Length > 0 && (manual || Enabled))
                run = new(
                    this,
                    manual ? SubtitleWork.Manual : SubtitleWork.Automatic,
                    jobs.Select(job => job.Job)
                );
            lock (_requestSync)
            {
                Slot(manual) = run;
                if (run is null || run.Stopping)
                    continue;
                run.Jobs.UnionWith(jobs.Select(job => job.Job));
                var due = jobs.Length == 0 ? 0 : jobs.Min(job => job.RetryAt);
                run.Report(
                    new WorkProgress.Fraction(
                        run.Jobs.Count - jobs.Length,
                        run.Jobs.Count,
                        WorkUnit.Files
                    ),
                    new WorkExecution.Waiting
                    {
                        RetryAt =
                            due > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                ? DateTimeOffset.FromUnixTimeSeconds(due)
                                : null,
                    }
                );
            }
        }
    }

    private WorkRun? Accepted(string path, string language, bool manual)
    {
        lock (_requestSync)
        {
            var run = Slot(manual);
            return
                run is { Stopping: false, Ended: false }
                && run.Jobs.Contains(new(path, language, manual))
                ? run
                : null;
        }
    }

    private async Task CompleteWork(WorkRun run)
    {
        Job[] jobs;
        lock (_requestSync)
            jobs = run.Jobs.ToArray();
        var result = await Run(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT outcome FROM subtitle_jobs WHERE path=$path AND language=$language AND manual=$manual;";
            var path = command.Parameters.AddWithValue("$path", string.Empty);
            var language = command.Parameters.AddWithValue("$language", string.Empty);
            var manual = command.Parameters.AddWithValue("$manual", false);
            var saved = 0;
            var missing = 0;
            var failed = 0;
            var cancelled = 0;
            foreach (var job in jobs)
            {
                path.Value = job.Path;
                language.Value = job.Language;
                manual.Value = job.Manual;
                var value = command.ExecuteScalar();
                switch (value is long outcome ? (SubtitleOutcome)outcome : SubtitleOutcome.Cancelled)
                {
                    case SubtitleOutcome.Complete:
                        saved++;
                        break;
                    case SubtitleOutcome.NoMatch:
                        missing++;
                        break;
                    case SubtitleOutcome.Cancelled:
                        cancelled++;
                        break;
                    default:
                        failed++;
                        break;
                }
            }
            return (saved, missing, failed, cancelled);
        }, _lifetime.Token).ConfigureAwait(false);
        run.Operation.Summarize("subtitles_result", result.saved, result.missing, result.failed);
        run.Operation.Outcome = result.failed > 0 ? WorkOutcome.Failed
            : result.cancelled > 0 ? WorkOutcome.Cancelled : WorkOutcome.Completed;
    }

    private async Task StopWork(WorkRun run, Task signalling)
    {
        await signalling.ConfigureAwait(false);
        if (run.Kind == SubtitleWork.Discovery)
            return;
        var entered = false;
        try
        {
            await _operation.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            entered = true;
            await Run(
                    connection =>
                    {
                        foreach (var job in run.Jobs)
                            Cancel(connection, job.Manual, job.Language, job.Path);
                        return true;
                    },
                    _lifetime.Token
                )
                .ConfigureAwait(false);
            // RefreshCounts may register pending jobs as a successor to this run.
            await run.Finish().ConfigureAwait(false);
            await RefreshCounts().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine(
                "Saving cancelled subtitle jobs interrupted: worker is stopping."
            );
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            run.Operation.Outcome = WorkOutcome.Failed;
            run.Operation.Error = error;
            _databaseFailure = error;
        }
        finally
        {
            await run.Finish().ConfigureAwait(false);
            if (entered)
                _operation.Release();
            Notify();
            Wake();
        }
        if (run.Kind == SubtitleWork.Manual && !_disposed)
        {
            try
            {
                await Reconcile().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }
    }

    private sealed record Job(string Path, string Language, bool Manual);

    private sealed record PendingJob(Job Job, long RetryAt);

    private sealed class WorkRun
    {
        private readonly Acquisition _owner;
        private readonly CancellableWork _work;
        internal Task Stopped { get; private set; } = Task.CompletedTask;
        internal WorkObservation Observation => _work.Observation;
        internal BackgroundWork.Operation Operation => _work.Operation;
        internal CancellationToken Token => _work.Token;
        internal HashSet<Job> Jobs { get; }
        internal SubtitleWork Kind { get; }
        internal bool Stopping => _work.IsCancelling;
        internal bool Ended => _work.IsEnded;
        internal IReadOnlyList<Discovery> Scans { get; set; } = [];

        internal WorkRun(Acquisition owner, SubtitleWork kind, IEnumerable<Job>? jobs = null)
        {
            _owner = owner;
            Kind = kind;
            Jobs = jobs?.ToHashSet() ?? [];
            _work = new(
                owner.Work,
                new(
                    new WorkDescription.Subtitles(kind),
                    new WorkProgress.Unknown(),
                    new WorkExecution.Waiting()
                ),
                _ => Stop()
            );
            // Only a manual search is something the person waits for; automatic
            // downloads and file scans show their results in place.
            Operation.ReportsSuccess = kind == SubtitleWork.Manual;
        }

        internal Task Stop()
        {
            lock (_owner._requestSync)
            {
                if (!_work.TryCancel(out var signalling))
                    return Stopped;
                _owner._skippedScans.UnionWith(
                    Scans.Where(scan => !scan.Scanned).Select(scan => scan.Key)
                );
                Stopped = _owner.StopWork(this, signalling);
                return Stopped;
            }
        }

        internal void Report(WorkProgress progress, WorkExecution execution)
        {
            lock (_owner._requestSync)
                _work.Report(
                    _work.Observation with
                    {
                        Progress = progress,
                        Execution = execution,
                    }
                );
        }

        internal Task Finish()
        {
            lock (_owner._requestSync)
            {
                if (!Ended && Operation.Error is null && (Stopping || _owner._disposed))
                    Operation.Outcome = WorkOutcome.Cancelled;
                return _work.DisposeAsync().AsTask();
            }
        }
    }
}
