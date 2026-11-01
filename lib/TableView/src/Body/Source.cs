using System.Collections;
using System.Collections.Specialized;
using Microsoft.UI.Dispatching;

namespace Syno.TableView.Body;

/// <summary>
/// The source pipeline of specification section 5.3. It subscribes to
/// <see cref="INotifyCollectionChanged"/>, presents the source as one ordered list per
/// notification, and enforces UI-thread affinity. It never filters, dispatches, or reorders.
/// </summary>
internal sealed class Source
{
    private readonly DispatcherQueue _dispatcher;
    private IEnumerable? _source;
    private INotifyCollectionChanged? _notifier;
    private IReadOnlyList<object> _snapshot = Array.Empty<object>();
    private bool _suspended = true;

    internal Hierarchy? Hierarchy { get; set; }

    internal Source(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    internal event EventHandler<IReadOnlyList<object>>? SnapshotChanged;

    internal void Accept(IReadOnlyList<object> snapshot) => _snapshot = snapshot;

    /// <summary>The last accepted source snapshot.</summary>
    internal IReadOnlyList<object> Snapshot => _snapshot;

    internal void Set(IEnumerable? source)
    {
        RequireUiThread();

        IReadOnlyList<object> next = Capture(source);
        Hierarchy?.Invalidate();
        Detach();
        _source = source;
        if (!_suspended)
            Attach();
        SnapshotChanged?.Invoke(this, next);
    }

    internal void Suspend()
    {
        _suspended = true;
        Hierarchy?.Suspend();
        Detach();
    }

    internal void Resume()
    {
        if (!_suspended)
            return;
        _suspended = false;
        Hierarchy?.Resume();
        Attach();
        if (_source is INotifyCollectionChanged)
            SnapshotChanged?.Invoke(this, Capture(_source));
        else if (Hierarchy is not null)
            SnapshotChanged?.Invoke(this, _snapshot);
    }

    private void Attach()
    {
        _notifier = _source as INotifyCollectionChanged;
        if (_notifier is not null)
            _notifier.CollectionChanged += OnSourceCollectionChanged;
    }

    private void Detach()
    {
        if (_notifier is not null)
            _notifier.CollectionChanged -= OnSourceCollectionChanged;
        _notifier = null;
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RequireUiThread();
        if (!_suspended)
            SnapshotChanged?.Invoke(this, Capture(_source));
    }

    /// <summary>
    /// The source as an ordered list. An indexable source is read in place, so a large source costs
    /// no second list: section 5.3 has the host notify or assign again after every change, so a
    /// later read gives the rows a copy taken now would hold. Any other enumerable is read once,
    /// so a one-shot iterator is treated as a snapshot; the host supplies a new iterator for a
    /// later source update.
    /// </summary>
    private static IReadOnlyList<object> Capture(IEnumerable? source)
    {
        if (source is IList list)
            return new Rows(list);

        List<object> next = new();
        if (source is not null)
        {
            foreach (object? item in source)
                next.Add(item ?? throw NullRow());
        }

        return next;
    }

    private static InvalidOperationException NullRow() =>
        new("ItemsSource contains a null row.");

    /// <summary>An indexable source read in place.</summary>
    private sealed class Rows(IList list) : IReadOnlyList<object>
    {
        public int Count => list.Count;

        public object this[int index] => list[index] ?? throw NullRow();

        public IEnumerator<object> GetEnumerator()
        {
            for (int index = 0; index < list.Count; index++)
                yield return this[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private void RequireUiThread()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            throw new InvalidOperationException(
                "Table is UI-thread-affine. ItemsSource assignment and collection notifications "
                    + "must occur on the control's DispatcherQueue."
            );
        }
    }
}
