
using Syno.TinyTorrent.Shared.Network;

namespace Syno.TinyTorrent.Shared;

/// <summary>
/// One running data download as Settings and the status bar observe it:
/// its phases, its cancellation and the interruption that a route change or
/// a protection reset causes. The owner reports progress and installs under
/// <see cref="Install"/>; <see cref="Run"/> ends the download, whatever
/// ended it.
/// </summary>
internal sealed class DataDownload
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _finished = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private WorkObservation _observation;
    private Task _signalling = Task.CompletedTask;
    private bool _ended;
    private bool _interrupted;
    private bool _installed;
    internal BackgroundWork.Operation Operation { get; }

    /// <summary>Set by a stop, so the build ends at it.</summary>
    internal CancellationToken Cancellation => _cancellation.Token;

    internal DataDownload(BackgroundWork work, WorkDescription starting)
    {
        _observation = new(starting, new WorkProgress.Unknown(), new WorkExecution.Running());
        Operation = work.Begin(_observation, () => Stop(false));
    }

    // Windows reports a full disk as ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL.
    internal static bool IsDiskFull(IOException error) =>
        error.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027);

    /// <summary>
    /// The failure that a download reports for a reply that failed: a route
    /// failure points to Network settings, a remote wait names its time, and
    /// any other reply fails the download.
    /// </summary>
    internal static Exception Failure(ReplyException error) =>
        error switch
        {
            ReplyException.Route => new Library.RouteException(error),
            ReplyException.Held { Failure: ReplyFailure.Wait } wait => new RequestBlockedException(
                wait.Hold
            ),
            _ => new Library.VideoException(VideoFailure.DownloadFailed, error),
        };

    /// <summary>
    /// Runs the download: <paramref name="build"/> fetches the data, then
    /// <paramref name="install"/> replaces the installed copy through
    /// <see cref="Install"/>. Returns whether the new data is installed.
    /// Failure or cancellation before installation keeps the previous copy
    /// and runs <paramref name="discard"/>; a later failure leaves the
    /// replacement installed. A stop the person did not ask for is reported
    /// as an interruption, because they did not cause it. Completion and the
    /// person's own cancellation are reported through the background-work owner.
    /// </summary>
    internal async Task<bool> Run(
        Func<Task> build,
        Func<Task> install,
        Action discard,
        Action<Exception> report
    )
    {
        var cancellation = _cancellation.Token;
        try
        {
            await build();
            cancellation.ThrowIfCancellationRequested();
            await install();
            Operation.Outcome = WorkOutcome.Completed;
        }
        // A stop can end the build with any error, such as the one a changed
        // network route causes.
        catch (Exception) when (_interrupted && !_installed)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Data download interrupted: {_observation.Description}."
            );
            var error = new Library.VideoException(VideoFailure.Interrupted);
            Operation.Error = error;
            report(error);
        }
        catch (Exception) when (cancellation.IsCancellationRequested && !_installed)
        {
            Operation.Outcome = WorkOutcome.Cancelled;
            System.Diagnostics.Debug.WriteLine(
                $"Data download cancelled: {_observation.Description}."
            );
        }
        catch (Exception error)
        {
            Operation.Error = error;
            report(error);
        }
        finally
        {
            try
            {
                if (!_installed)
                    await Task.Run(discard);
            }
            catch (Exception error)
            {
                Operation.Outcome = WorkOutcome.Failed;
                Operation.Error = error;
                report(error);
            }
            finally
            {
                await Finish();
            }
        }
        return _installed;
    }

    internal void Report(WorkDescription description, WorkProgress progress)
    {
        lock (_gate)
        {
            if (_ended || _observation.Execution is not WorkExecution.Running)
                return;
            _observation = _observation with { Description = description, Progress = progress };
            Operation.Report(_observation);
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
            _cancellation.Token.ThrowIfCancellationRequested();
            _observation = new(
                description,
                new WorkProgress.Unknown(),
                new WorkExecution.Finishing()
            );
            Operation.Report(_observation);
        }
        install();
        _installed = true;
    }

    /// <summary>
    /// Stops the download as the person's own cancel, or as one they did not
    /// ask for when <paramref name="interrupted"/>, and completes when it has ended.
    /// </summary>
    internal Task Stop(bool interrupted)
    {
        lock (_gate)
        {
            if (_ended || _observation.Execution is WorkExecution.Finishing)
                return _finished.Task;
            _interrupted |= interrupted;
            if (_observation.Execution is not WorkExecution.Cancelling)
            {
                _observation = _observation with { Execution = new WorkExecution.Cancelling() };
                Operation.Report(_observation);
                // CancelAsync sets the token immediately and runs callbacks off this gate.
                _signalling = _cancellation.CancelAsync();
            }
            return _finished.Task;
        }
    }

    private async Task Finish()
    {
        Task signalling;
        lock (_gate)
        {
            _ended = true;
            signalling = _signalling;
        }
        try
        {
            await signalling.ConfigureAwait(false);
        }
        finally
        {
            _cancellation.Dispose();
            Operation.Dispose();
            _finished.TrySetResult();
        }
    }
}
