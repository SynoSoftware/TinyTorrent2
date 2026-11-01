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
    private NotifyCollectionChangedEventHandler? _handler;
    private Capture _snapshot = new(Array.Empty<object>(), 0);
    private long _assignmentVersion;
    private IdentityMap<object> _membership;
    private IdentityMap<object> _candidate;
    private IdentityMap<object> _additions;
    private IdentityMap<object> _leavers;
    private bool _suspended = true;

    internal Source(DispatcherQueue dispatcher, IIdentity identity)
    {
        _dispatcher = dispatcher;
        _membership = identity.CreateMap<object>(keepsKeys: true);
        _candidate = identity.CreateMap<object>(keepsKeys: true);
        _additions = identity.CreateMap<object>(keepsKeys: true);
        _leavers = identity.CreateMap<object>(keepsKeys: true);
    }

    internal sealed class Capture(IReadOnlyList<object> rows, long assignmentVersion)
    {
        internal IReadOnlyList<object> Rows { get; } = rows;
        internal long AssignmentVersion { get; } = assignmentVersion;
        internal bool Validated { get; set; }
    }

    internal event EventHandler<Change>? SnapshotChanged;

    internal sealed class Preparation(
        Capture capture,
        IdentityMap<object> membership,
        IdentityMap<object> additions,
        IdentityMap<object> leavers,
        IReadOnlyList<object> added,
        IReadOnlyList<object> removed,
        bool incremental,
        Change change) : IIdentityKeys
    {
        internal Capture Capture { get; } = capture;
        internal IdentityMap<object> Membership { get; } = membership;
        internal IdentityMap<object> Additions { get; } = additions;
        internal IdentityMap<object> Leavers { get; } = leavers;
        internal IReadOnlyList<object> Added { get; } = added;
        internal IReadOnlyList<object> Removed { get; } = removed;
        internal bool IsIncremental { get; } = incremental;
        internal Change Change { get; } = change;
        public IIdentityKeys CapturedFor(object item) => Additions.ContainsReference(item) ? Additions
            : Leavers.ContainsReference(item) ? Leavers : Membership;
        public bool ContainsReference(object item) => Additions.ContainsReference(item)
            || Leavers.ContainsReference(item) || Membership.ContainsReference(item);
        internal object? Resolve(object item, IIdentityKeys keys)
        {
            if (Additions.TryGetValue(item, keys, out object next))
            {
                return next;
            }
            return Leavers.ContainsKey(item, keys) ? null : Membership.Resolve(item, keys);
        }
        internal bool Affects(object item, IIdentityKeys keys) => Additions.ContainsKey(item, keys)
            || Leavers.ContainsKey(item, keys);
    }

    internal void SetIdentity(IIdentity identity)
    {
        _membership = identity.CreateMap<object>(keepsKeys: true);
        _candidate = identity.CreateMap<object>(keepsKeys: true);
        _additions = identity.CreateMap<object>(keepsKeys: true);
        _leavers = identity.CreateMap<object>(keepsKeys: true);
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

    internal Preparation Prepare(Change change, Action<object> validate)
    {
        Capture capture = change switch
        {
            Change.Whole whole => whole.Capture,
            Change.Collection collection => collection.Capture,
            _ => _snapshot,
        };
        _additions.Clear();
        _leavers.Clear();
        if (change is Change.Values values)
        {
            bool incremental = capture.Validated;
            IdentityMap<object> membership = Prepare(capture, validate);
            foreach (object item in values.Rows)
            {
                object? current = membership.Resolve(item);
                if (current is not null)
                {
                    _additions.TryAdd(current, current, membership);
                }
            }
            return new(capture, membership, _additions, _leavers,
                _additions.Select(entry => entry.Key).ToArray(), Array.Empty<object>(), incremental, change);
        }
        if (change is not Change.Collection { HasPredecessor: true } delta
            || delta.Notification.Action == NotifyCollectionChangedAction.Reset
            || delta.Notification.NewStartingIndex < 0 && delta.Notification.NewItems is { Count: > 0 }
            || delta.Notification.OldStartingIndex < 0 && delta.Notification.OldItems is { Count: > 0 })
        {
            return new(capture, Prepare(capture, validate), _additions, _leavers,
                Array.Empty<object>(), Array.Empty<object>(), false, change);
        }
        List<object> removed = new();
        if (delta.Notification.OldItems is { } old)
        {
            foreach (object item in old)
            {
                object current = _membership.Resolve(item)
                    ?? throw new InvalidOperationException("The source removed an unavailable row.");
                _leavers.TryAdd(current, current, _membership);
                removed.Add(current);
            }
        }
        List<object> added = new();
        if (delta.Notification.NewItems is { } next)
        {
            foreach (object item in next)
            {
                bool entered;
                if (delta.Notification.Action == NotifyCollectionChangedAction.Move)
                {
                    entered = _additions.TryAdd(item, item, _leavers);
                }
                else
                {
                    validate(item);
                    entered = _additions.TryAdd(item, item);
                }
                if (!entered
                    || _membership.ContainsKey(item, _additions) && !_leavers.ContainsKey(item, _additions))
                {
                    throw new InvalidOperationException("The source contains duplicate row identities.");
                }
                added.Add(item);
            }
        }
        return new(capture, _membership, _additions, _leavers, added, removed, true, change);
    }

    internal void Accept(Preparation preparation)
    {
        if (preparation.IsIncremental)
        {
            if (preparation.Change is not Change.Values)
            {
                foreach (object item in preparation.Removed)
                {
                    _membership.Remove(item, preparation.Leavers);
                }
                foreach (object item in preparation.Added)
                {
                    _membership.Set(item, item, preparation.Additions);
                }
            }
            Accept(preparation.Capture, validated: true);
        }
        else
        {
            Accept(preparation.Capture, validated: true, preparation.Membership);
        }
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
    internal void Complete()
    {
        _additions.Clear();
        _leavers.Clear();
    }

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
        SnapshotChanged?.Invoke(this, new Change.Whole(next));
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
            SnapshotChanged?.Invoke(this, new Change.Whole(new(CaptureRows(_source), _assignmentVersion)));
        }
    }

    private void Attach()
    {
        _notifier = _source as INotifyCollectionChanged;
        if (_notifier is not null)
        {
            NotifyCollectionChangedEventHandler? handler = null;
            handler = (sender, args) =>
            {
                if (ReferenceEquals(_handler, handler))
                {
                    OnSourceCollectionChanged(sender, args);
                }
            };
            _handler = handler;
            _notifier.CollectionChanged += handler;
        }
    }

    private void Detach()
    {
        INotifyCollectionChanged? notifier = _notifier;
        NotifyCollectionChangedEventHandler? handler = _handler;
        _notifier = null;
        _handler = null;
        if (notifier is not null)
        {
            notifier.CollectionChanged -= handler;
        }
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RequireUiThread();
        if (!_suspended)
        {
            bool predecessor = _snapshot.Validated && _snapshot.AssignmentVersion == _assignmentVersion;
            _snapshot.Validated = false;
            SnapshotChanged?.Invoke(this, new Change.Collection(
                new(CaptureRows(_source), _assignmentVersion), e, predecessor));
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
