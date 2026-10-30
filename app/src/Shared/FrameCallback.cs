using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace Syno.TinyTorrent.Shared;

// Posting from Rendering lets the current render turn finish before this work runs.
internal sealed class FrameCallback : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private Action? _callback;

    internal FrameCallback(DispatcherQueue dispatcher, Action callback)
    {
        _dispatcher = dispatcher;
        _callback = callback;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, object args)
    {
        Detach();
        if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, Invoke))
            Dispose();
    }

    private void Invoke()
    {
        var callback = _callback;
        Dispose();
        callback?.Invoke();
    }

    private void Detach()
    {
        CompositionTarget.Rendering -= OnRendering;
    }

    public void Dispose()
    {
        Detach();
        _callback = null;
    }
}
