using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI.Dispatching;

namespace Syno.TableView.Body;

/// <summary>
/// The source pipeline of specification section 5.3. It subscribes to
/// <see cref="INotifyCollectionChanged"/>, captures one ordered snapshot per notification, and
/// enforces UI-thread affinity. It never filters, dispatches, or reorders.
/// </summary>
internal sealed class Source
{
    private readonly DispatcherQueue _dispatcher;
    private IEnumerable? _source;
    private INotifyCollectionChanged? _notifier;
    private IReadOnlyList<object> _snapshot = Array.Empty<object>();
    private bool _suspended = true;
    private long _revision;

    internal Source(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    internal event EventHandler<IReadOnlyList<object>>? SnapshotChanged;

    internal void Accept(IReadOnlyList<object> snapshot)
    {
        _snapshot = snapshot;
        _revision++;
    }

    /// <summary>The last accepted source snapshot.</summary>
    internal IReadOnlyList<object> Snapshot => _snapshot;
    internal IEnumerable? Input => _source;

    internal void SetSource(IEnumerable? source)
    {
        RequireUiThread();

        IReadOnlyList<object> next = Capture(source);
        IEnumerable? previous = _source;
        // A later nested publication must not make an accepted assignment look rejected.
        long revision = _revision;
        Detach();
        _source = source;
        if (!_suspended) Attach();
        try
        {
            SnapshotChanged?.Invoke(this, next);
        }
        catch
        {
            if (ReferenceEquals(_source, source) && _revision == revision)
            {
                Detach();
                _source = previous;
                if (!_suspended) Attach();
            }
            throw;
        }
    }

    internal void Suspend()
    {
        _suspended = true;
        Detach();
    }

    internal void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        Attach();
        if (_source is INotifyCollectionChanged)
            SnapshotChanged?.Invoke(this, Capture(_source));
    }

    private void Attach()
    {
        _notifier = _source as INotifyCollectionChanged;
        if (_notifier is not null) _notifier.CollectionChanged += OnSourceCollectionChanged;
    }

    private void Detach()
    {
        if (_notifier is not null) _notifier.CollectionChanged -= OnSourceCollectionChanged;
        _notifier = null;
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RequireUiThread();
        if (!_suspended) SnapshotChanged?.Invoke(this, Capture(_source));
    }

    /// <summary>
    /// One ordered enumeration. A one-shot iterator is consumed here and is therefore treated as a
    /// snapshot; the host supplies a new iterator for a later source update.
    /// </summary>
    private static IReadOnlyList<object> Capture(IEnumerable? source)
    {
        List<object> next = new();
        if (source is not null)
        {
            foreach (object? item in source)
            {
                if (item is null) throw new InvalidOperationException("ItemsSource contains a null row.");
                next.Add(item);
            }
        }

        return next;
    }

    private void RequireUiThread()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            throw new InvalidOperationException(
                "Table is UI-thread-affine. ItemsSource assignment and collection notifications " +
                "must occur on the control's DispatcherQueue.");
        }
    }
}
