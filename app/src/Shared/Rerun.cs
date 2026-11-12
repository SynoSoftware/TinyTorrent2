namespace Syno.TinyTorrent.Shared;

/// <summary>
/// A background pass that runs again when it is requested during its run.
/// Requests made during one pass cause one more pass after it, so a burst
/// of requests starts no pass for each and never loses the last one.
/// </summary>
internal sealed class Rerun(Func<Task> pass)
{
    private readonly object _gate = new();
    private Task? _running;
    private bool _requested;

    /// <summary>
    /// Requests a pass on the thread pool. The task completes when no
    /// requested pass is left, and fails when a pass throws; the next
    /// request then starts again. A caller may hold its own lock, because
    /// the pass never runs on the calling thread.
    /// </summary>
    internal Task Request()
    {
        lock (_gate)
        {
            _requested = true;
            return _running ??= Task.Run(Run);
        }
    }

    /// <summary>The task of the passes requested so far; completed while none runs.</summary>
    internal Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _running ?? Task.CompletedTask;
            }
        }
    }

    private async Task Run()
    {
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (!_requested)
                    {
                        _running = null;
                        return;
                    }
                    _requested = false;
                }
                await pass().ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_gate)
            {
                _running = null;
            }
            throw;
        }
    }
}
