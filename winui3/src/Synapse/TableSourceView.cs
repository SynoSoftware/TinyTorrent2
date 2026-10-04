using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI.Dispatching;

namespace Synapse;

/// <summary>
/// The source pipeline of specification section 5.3. It subscribes to
/// <see cref="INotifyCollectionChanged"/>, captures one ordered snapshot per notification, and
/// enforces UI-thread affinity. It never filters, dispatches, or reorders.
/// </summary>
internal sealed class TableSourceView
{
    private readonly DispatcherQueue _dispatcher;
    private IEnumerable? _source;
    private INotifyCollectionChanged? _notifier;
    private List<object> _snapshot = new();

    internal TableSourceView(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    internal event EventHandler? SnapshotChanged;

    /// <summary>The latest ordered source snapshot. In Phase A this is also the private view.</summary>
    internal IReadOnlyList<object> Snapshot => _snapshot;

    internal void SetSource(IEnumerable? source)
    {
        RequireUiThread();

        if (_notifier is not null)
        {
            _notifier.CollectionChanged -= OnSourceCollectionChanged;
            _notifier = null;
        }

        _source = source;
        _notifier = source as INotifyCollectionChanged;

        if (_notifier is not null)
        {
            _notifier.CollectionChanged += OnSourceCollectionChanged;
        }

        Capture();
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RequireUiThread();
        Capture();
    }

    /// <summary>
    /// One ordered enumeration. A one-shot iterator is consumed here and is therefore treated as a
    /// snapshot; the host supplies a new iterator for a later source update.
    /// </summary>
    private void Capture()
    {
        List<object> next = new();
        if (_source is not null)
        {
            foreach (object? item in _source)
            {
                if (item is not null)
                {
                    next.Add(item);
                }
            }
        }

        _snapshot = next;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RequireUiThread()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            throw new InvalidOperationException(
                "TableView is UI-thread-affine. ItemsSource assignment and collection notifications " +
                "must occur on the control's DispatcherQueue.");
        }
    }
}
