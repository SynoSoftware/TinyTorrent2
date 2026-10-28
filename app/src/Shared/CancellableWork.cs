namespace Syno.TinyTorrent.Shared;

/// <summary>
/// The lifecycle that cancellable background work shares: its operation in
/// <see cref="BackgroundWork"/>, one cancellation and its end. The owner keeps
/// its own queues and counts and sets the operation's outcome; this type
/// ignores reports once the work is cancelling or ended, starts the
/// cancellation once, and ends the work only after the cancellation's
/// callbacks have run, because the token's source is disposed then.
/// </summary>
internal sealed class CancellableWork : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private WorkPhase _phase;
    private Task _signalling = Task.CompletedTask;

    /// <param name="stop">
    /// What the work list's Cancel runs, when the owner has more to stop than
    /// the token; by default <see cref="TryCancel"/>.
    /// </param>
    /// <param name="cancellation">
    /// The source this work cancels and disposes; by default a new one. An
    /// owner that cancels it directly stops the work without showing
    /// Cancelling, for a stop the person did not ask for.
    /// </param>
    internal CancellableWork(
        BackgroundWork work,
        WorkObservation initial,
        Func<CancellableWork, Task>? stop = null,
        CancellationTokenSource? cancellation = null
    )
    {
        _cancellation = cancellation ?? new();
        Token = _cancellation.Token;
        Observation = initial;
        Operation = work.Begin(
            initial,
            () =>
            {
                if (stop is not null)
                    return stop(this);
                TryCancel(out var signalling);
                return signalling;
            }
        );
    }

    internal BackgroundWork.Operation Operation { get; }

    internal CancellationToken Token { get; }

    internal WorkObservation Observation { get; private set; }

    /// <summary>
    /// Whether <see cref="TryCancel"/> would start the cancellation now. The
    /// answer can be stale when the caller acts on it, so a stop acts on what
    /// <see cref="TryCancel"/> returns instead.
    /// </summary>
    internal bool CanCancel
    {
        get
        {
            lock (_gate)
                return IsRunning && Observation.Execution.CanCancel;
        }
    }

    internal bool IsCancelling
    {
        get
        {
            lock (_gate)
                return _phase == WorkPhase.Cancelling;
        }
    }

    internal bool IsEnded
    {
        get
        {
            lock (_gate)
                return _phase == WorkPhase.Ended;
        }
    }

    /// <summary>Completes when the work has ended and released its resources.</summary>
    internal Task Completion => _completion.Task;

    // Callers hold _gate.
    private bool IsRunning => _phase == WorkPhase.Running && !_cancellation.IsCancellationRequested;

    /// <summary>Shows <paramref name="observation"/>, unless the work is cancelling or has ended.</summary>
    internal void Report(WorkObservation observation)
    {
        lock (_gate)
        {
            if (!IsRunning)
                return;
            Observation = observation;
            Operation.Report(observation);
        }
    }

    /// <summary>
    /// Shows Cancelling and cancels the token, unless the work has ended, is
    /// already stopping or cannot be cancelled in its current execution.
    /// Returns whether this call started the cancellation, so the owner's own
    /// stop work runs once; <paramref name="signalling"/> runs the token's
    /// callbacks.
    /// </summary>
    internal bool TryCancel(out Task signalling)
    {
        lock (_gate)
        {
            var starts = IsRunning && Observation.Execution.CanCancel;
            if (starts)
            {
                _phase = WorkPhase.Cancelling;
                Observation = Observation with { Execution = new WorkExecution.Cancelling() };
                Operation.Report(Observation);
                // CancelAsync sets the token at once and runs its callbacks
                // off this gate and off the caller's.
                _signalling = _cancellation.CancelAsync();
            }
            signalling = _signalling;
            return starts;
        }
    }

    /// <summary>
    /// Ends the work: no report or cancellation is accepted from now on. When
    /// the cancellation's callbacks have run, the operation ends with the
    /// outcome the owner set, and the token's source is disposed. A second
    /// call returns <see cref="Completion"/>.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Task signalling;
        lock (_gate)
        {
            if (_phase == WorkPhase.Ended)
                return new(_completion.Task);
            _phase = WorkPhase.Ended;
            signalling = _signalling;
        }
        return new(End(signalling));
    }

    private async Task End(Task signalling)
    {
        try
        {
            await signalling.ConfigureAwait(false);
        }
        finally
        {
            Operation.Dispose();
            _cancellation.Dispose();
            _completion.TrySetResult();
        }
    }
}
