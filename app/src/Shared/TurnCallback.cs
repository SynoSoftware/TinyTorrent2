using Microsoft.UI.Dispatching;

namespace Syno.TinyTorrent.Shared;

// Runs an action once on the dispatcher's thread after the current turn,
// however many times it is requested before then, so a burst of changes
// costs one refresh. Any thread may request it. A request while the action
// runs queues another run, because the flag clears first.
internal sealed class TurnCallback
{
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueuePriority _priority;
    private readonly Action _action;
    private readonly DispatcherQueueHandler _run;
    private int _queued;

    internal TurnCallback(DispatcherQueue dispatcher, DispatcherQueuePriority priority, Action action)
    {
        _dispatcher = dispatcher;
        _priority = priority;
        _action = action;
        _run = Run;
    }

    internal void Request()
    {
        if (Interlocked.Exchange(ref _queued, 1) != 0)
        {
            return;
        }
        if (!_dispatcher.TryEnqueue(_priority, _run))
        {
            Interlocked.Exchange(ref _queued, 0);
        }
    }

    // A request that found the flag set made its writes before this
    // exchange, which is a full fence, so the action reads them.
    private void Run()
    {
        Interlocked.Exchange(ref _queued, 0);
        _action();
    }
}
