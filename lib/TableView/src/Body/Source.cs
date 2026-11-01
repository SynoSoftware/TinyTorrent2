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
    private Capture _snapshot = new(Array.Empty<object>(), 0);
    private long _assignmentVersion;
    private IdentityMap<object> _membership;
    private IdentityMap<object> _candidate;
    private bool _suspended = true;

    internal Source(DispatcherQueue dispatcher, IIdentity identity)
    {
        _dispatcher = dispatcher;
        _membership = identity.CreateMap<object>(keepsKeys: true);
        _candidate = identity.CreateMap<object>(keepsKeys: true);
    }

    internal sealed class Capture(IReadOnlyList<object> rows, long assignmentVersion)
    {
        internal IReadOnlyList<object> Rows { get; } = rows;
        internal long AssignmentVersion { get; } = assignmentVersion;
        internal bool Validated { get; set; }
    }

    internal event EventHandler<Capture>? SnapshotChanged;

    internal void SetIdentity(IIdentity identity)
    {
        _membership = identity.CreateMap<object>(keepsKeys: true);
        _candidate = identity.CreateMap<object>(keepsKeys: true);
        _snapshot.Validated = false;
    }

    internal IdentityMap<object> Prepare(Capture capture, Action<object> validate)
    {
        if (ReferenceEquals(capture, _snapshot) && capture.Validated)
        {
            return _membership;
        }
        _candidate.Clear();
        foreach (object item in capture.Rows)
        {
            validate(item);
            if (!_candidate.TryAdd(item, item))
            {
                throw new InvalidOperationException("The source contains duplicate row identities.");
            }
        }
        return _candidate;
    }

    internal void Accept(Capture snapshot, bool validated, IdentityMap<object>? membership = null)
    {
        if (membership is not null && !ReferenceEquals(membership, _membership))
        {
            (_membership, _candidate) = (_candidate, _membership);
        }
        snapshot.Validated = validated;
        _snapshot = snapshot;
    }

    internal Capture Current => _snapshot;
    internal IdentityMap<object> Membership => _membership;

    /// <summary>The last accepted source snapshot.</summary>
    internal IReadOnlyList<object> Snapshot => _snapshot.Rows;

    internal void Set(IEnumerable? source)
    {
        RequireUiThread();

        Capture next = new(CaptureRows(source), ++_assignmentVersion);
        Detach();
        _source = source;
        if (!_suspended)
            Attach();
        SnapshotChanged?.Invoke(this, next);
    }

    internal void Suspend()
    {
        _suspended = true;
        Detach();
    }

    internal void Resume()
    {
        if (!_suspended)
            return;
        _suspended = false;
        Attach();
        if (_source is INotifyCollectionChanged)
        {
            _snapshot.Validated = false;
            SnapshotChanged?.Invoke(this, new(CaptureRows(_source), _assignmentVersion));
        }
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
        {
            _snapshot.Validated = false;
            SnapshotChanged?.Invoke(this, new(CaptureRows(_source), _assignmentVersion));
        }
    }

    /// <summary>
    /// The source as an ordered list. An indexable source is read in place, so a large source costs
    /// no second list: section 5.3 has the host notify or assign again after every change, so a
    /// later read gives the rows a copy taken now would hold. Any other enumerable is read once,
    /// so a one-shot iterator is treated as a snapshot; the host supplies a new iterator for a
    /// later source update.
    /// </summary>
    private static IReadOnlyList<object> CaptureRows(IEnumerable? source)
    {
        if (source is IReadOnlyList<object> rows)
        {
            return rows;
        }
        if (source is IList list)
            return new Rows(list);

        List<object> next = new();
        if (source is not null)
        {
            foreach (object? item in source)
                next.Add(item!);
        }

        return next;
    }

    /// <summary>An indexable source read in place.</summary>
    private sealed class Rows(IList list) : IReadOnlyList<object>
    {
        public int Count => list.Count;

        public object this[int index] => list[index]!;

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
