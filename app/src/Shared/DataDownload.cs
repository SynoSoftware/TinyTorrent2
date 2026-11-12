using System.ComponentModel;
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Shared;

/// <summary>
/// The copy of one dataset on this computer and the work that changes it:
/// the download, with its cancel and its interruption, the replacement of
/// the installed file and its deletion. Settings and the status bar observe
/// it. Its instance members are called on the window thread, and it raises
/// its events there.
/// </summary>
internal sealed class DataDownload : INotifyPropertyChanged
{
    private readonly IDataset _dataset;
    private readonly BackgroundWork _work;
    private readonly Func<Action, Task> _replace;
    private Run? _run;
    private bool _suspended;

    /// <param name="replace">
    /// Runs a replacement off the window thread when the owner must stop
    /// other work around it; by default <see cref="Task.Run(Action)"/>.
    /// </param>
    internal DataDownload(IDataset dataset, BackgroundWork work, Func<Action, Task>? replace = null)
    {
        _dataset = dataset;
        _work = work;
        _replace = replace ?? (change => Task.Run(change));
    }

    /// <summary>Raised for <see cref="Installed"/>, <see cref="Work"/> and <see cref="CanStart"/>.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>A failed download or deletion, with the data's name set.</summary>
    internal event EventHandler<Exception>? Failed;

    internal string Section => _dataset.Section;

    /// <summary>The installed data's file, or null when it is missing.</summary>
    internal FileInfo? Installed =>
        _dataset.FilePath is { } path && new FileInfo(path) is { Exists: true } file ? file : null;

    /// <summary>Whether downloading again would most likely transfer the same file.</summary>
    internal bool IsCurrent =>
        Installed is { LastWriteTimeUtc: var written } && _dataset.IsCurrent(written);

    internal Task<long>? Count => _dataset.Count;

    /// <summary>The running download, or null.</summary>
    internal BackgroundWork.Operation? Work => _run?.Operation;

    internal bool CanStart => _run is null && !_suspended && _dataset.FilePath is not null;

    /// <summary>
    /// Downloads the data and replaces the installed copy. Returns whether
    /// the new data is installed. Failure or cancellation before installation
    /// keeps the previous copy and deletes the build; a later failure leaves
    /// the replacement installed. A stop the person did not ask for is
    /// reported as an interruption, because they did not cause it. Completion
    /// and the person's own cancellation are reported through the
    /// background-work owner.
    /// </summary>
    internal async Task<bool> Start()
    {
        if (!CanStart || _dataset.FilePath is not { } path)
            return false;
        var run = new Run(_work, _dataset.Downloading);
        _run = run;
        ShowWork();
        try
        {
            await Execute(run, path);
        }
        finally
        {
            _run = null;
            ShowWork();
        }
        return run.Installed;
    }

    /// <summary>Stops the running download at the person's request, and completes when it has ended.</summary>
    internal Task Cancel() => Stop(interrupted: false);

    /// <summary>Stops the running download as one the person did not ask for, which reports it, and completes when it has ended.</summary>
    internal Task Interrupt() => Stop(interrupted: true);

    /// <summary>Interrupts the running download and starts no other until <paramref name="apply"/> has ended.</summary>
    internal async Task Suspend(Func<Task> apply)
    {
        _suspended = true;
        Changed(nameof(CanStart));
        try
        {
            await Interrupt();
            await apply();
        }
        finally
        {
            _suspended = false;
            Changed(nameof(CanStart));
        }
    }

    /// <summary>Deletes the installed data and its leftovers. A failure is reported through <see cref="Failed"/>.</summary>
    internal async Task Delete()
    {
        if (_run is not null || _dataset.FilePath is not { } path)
            return;
        using var operation = _work.Begin(
            new(
                new WorkDescription.Maintenance(MaintenanceWork.DeleteDataset),
                new WorkProgress.Unknown(),
                new WorkExecution.Finishing()
            )
        );
        try
        {
            await _replace(() =>
                Change(() =>
                {
                    foreach (var leftover in _dataset.Leftovers)
                        File.Delete(leftover);
                    File.Delete(path);
                })
            );
            operation.Outcome = WorkOutcome.Completed;
            // The Settings row shows that the data is gone.
            operation.ReportsSuccess = false;
        }
        catch (Exception error)
        {
            operation.Error = error;
            Report(error);
        }
        Changed(nameof(Installed));
    }

    /// <summary>Raises a change of every property, after the dataset's <see cref="IDataset.FilePath"/> changed.</summary>
    internal void Refresh() => Changed(string.Empty);

    /// <summary>
    /// Downloads the reply of <paramref name="call"/> to a file beside
    /// <paramref name="path"/> and renames it when it is complete, so a file
    /// at <paramref name="path"/> is whole.
    /// </summary>
    internal static async Task Fetch(
        ProviderHttp http,
        ProviderCall call,
        string path,
        Action<WorkProgress> progress,
        CancellationToken cancellation
    )
    {
        var part = path + ".part";
        try
        {
            await http.Download(call, part, progress, cancellation).ConfigureAwait(false);
            File.Move(part, path, overwrite: true);
        }
        finally
        {
            File.Delete(part);
        }
    }

    // Builds the data beside the installed file and replaces the installed
    // copy with it, then deletes a build that is not installed, whatever
    // ended the run.
    private async Task Execute(Run run, string path)
    {
        var building = path + ".tmp";
        var cancellation = run.Cancellation;
        try
        {
            await Build(building, run);
            cancellation.ThrowIfCancellationRequested();
            try
            {
                await _replace(() =>
                {
                    run.Install(
                        _dataset.Installing,
                        () => Change(() => File.Move(building, path, overwrite: true))
                    );
                    // The data is installed, so a leftover that another
                    // program holds waits for the next installation.
                    try
                    {
                        foreach (var leftover in _dataset.Leftovers)
                            File.Delete(leftover);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                });
            }
            finally
            {
                Changed(nameof(Installed));
            }
            run.Operation.Outcome = WorkOutcome.Completed;
        }
        // A stop can end the build with any error, such as the one a changed
        // network route causes.
        catch (Exception) when (run.Interrupted && !run.Installed)
        {
            System.Diagnostics.Debug.WriteLine($"Data download interrupted: {run.Description}.");
            var error = new DownloadException(DownloadFailure.Interrupted);
            run.Operation.Error = error;
            Report(error);
        }
        catch (Exception) when (cancellation.IsCancellationRequested && !run.Installed)
        {
            run.Operation.Outcome = WorkOutcome.Cancelled;
            System.Diagnostics.Debug.WriteLine($"Data download cancelled: {run.Description}.");
        }
        catch (Exception error)
        {
            run.Operation.Error = error;
            Report(error);
        }
        finally
        {
            try
            {
                if (!run.Installed)
                    await Task.Run(() => Discard(building));
            }
            catch (Exception error)
            {
                run.Operation.Outcome = WorkOutcome.Failed;
                run.Operation.Error = error;
                Report(error);
            }
            finally
            {
                await run.End();
            }
        }
    }

    // A dataset translates the exceptions of its own libraries; the failures
    // that every download shares are translated here.
    private async Task Build(string building, Run run)
    {
        try
        {
            await _dataset.Build(building, run.Report, run.Cancellation);
        }
        catch (ReplyException error)
        {
            throw Failure(error);
        }
        catch (InvalidDataException error)
        {
            throw new DownloadException(DownloadFailure.Damaged, error);
        }
        catch (IOException error) when (IsDiskFull(error))
        {
            throw new DownloadException(DownloadFailure.DiskFull, error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DownloadException(DownloadFailure.Failed, error);
        }
    }

    // Another program that holds the installed file keeps it from changing.
    private void Change(Action change)
    {
        try
        {
            _dataset.Replace(change);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DownloadException(DownloadFailure.InUse, error);
        }
    }

    // A build that another program holds is deleted by the next build.
    private static void Discard(string building)
    {
        try
        {
            File.Delete(building);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private Task Stop(bool interrupted)
    {
        if (_run is not { } run)
            return Task.CompletedTask;
        var stopped = run.Stop(interrupted);
        ShowWork();
        return stopped;
    }

    // Windows reports a full disk as ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL.
    private static bool IsDiskFull(IOException error) =>
        error.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027);

    /// <summary>
    /// The failure that a download reports for a reply that failed: a route
    /// failure points to Network settings, a remote wait names its time, and
    /// any other reply fails the download.
    /// </summary>
    private static Exception Failure(ReplyException error) =>
        error switch
        {
            ReplyException.Route => new DownloadException(DownloadFailure.Route, error),
            ReplyException.Held { Failure: ReplyFailure.Wait } wait => new RequestBlockedException(
                wait.Hold
            ),
            _ => new DownloadException(DownloadFailure.Failed, error),
        };

    private void Report(Exception error)
    {
        if (error is DownloadException failure)
            failure.Subject ??= _dataset.Name;
        Failed?.Invoke(this, error);
    }

    private void ShowWork()
    {
        Changed(nameof(Work));
        Changed(nameof(CanStart));
    }

    private void Changed(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    // One download: its work in the work list, its stop, and whether a stop
    // interrupted it or it installed the data. Start makes one for each
    // download, so the next one starts clean.
    private sealed class Run
    {
        private readonly object _gate = new();
        private readonly CancellableWork _work;

        internal Run(BackgroundWork work, WorkDescription starting)
        {
            _work = new(
                work,
                new(starting, new WorkProgress.Unknown(), new WorkExecution.Running()),
                _ => Stop(false)
            );
        }

        internal BackgroundWork.Operation Operation => _work.Operation;

        /// <summary>Set by a stop, so the build ends at it.</summary>
        internal CancellationToken Cancellation => _work.Token;

        internal WorkDescription Description => _work.Observation.Description;

        internal bool Interrupted { get; private set; }

        internal bool Installed { get; private set; }

        internal void Report(WorkDescription description, WorkProgress progress)
        {
            lock (_gate)
            {
                if (_work.Observation.Execution is not WorkExecution.Running)
                    return;
                _work.Report(
                    _work.Observation with
                    {
                        Description = description,
                        Progress = progress,
                    }
                );
            }
        }

        /// <summary>
        /// Runs <paramref name="install"/> as the finishing step, which a stop no
        /// longer ends, so the installed data is never half replaced.
        /// </summary>
        internal void Install(WorkDescription description, Action install)
        {
            lock (_gate)
            {
                _work.Token.ThrowIfCancellationRequested();
                _work.Report(
                    new(description, new WorkProgress.Unknown(), new WorkExecution.Finishing())
                );
            }
            install();
            Installed = true;
        }

        /// <summary>
        /// Stops the download as the person's own cancel, or as one they did not
        /// ask for when <paramref name="interrupted"/>, and completes when it has ended.
        /// </summary>
        internal Task Stop(bool interrupted)
        {
            lock (_gate)
            {
                // Finishing cannot be cancelled, so a stop then is no interruption.
                // The interruption is recorded before the token fires, because
                // Execute's failure filter reads it as soon as the build throws;
                // Report and Install change the work only under _gate, so the
                // check holds.
                if (_work.CanCancel || _work.IsCancelling)
                    Interrupted |= interrupted;
                _work.TryCancel(out _);
                return _work.Completion;
            }
        }

        internal ValueTask End() => _work.DisposeAsync();
    }
}

/// <summary>A data download, or the replacement or removal of its installed data, failed.</summary>
internal sealed class DownloadException(DownloadFailure failure, Exception? inner = null)
    : Exception(failure.ToString(), inner), IFailure
{
    internal DownloadFailure Failure { get; } = failure;

    /// <summary>The name of the data, which its owner sets so the text can name it.</summary>
    internal string? Subject { get; set; }

    public string Describe(Strings text) =>
        text.Format(
            "work",
            "download_" + Failure.ToString().ToLowerInvariant(),
            Subject ?? string.Empty
        );
}
