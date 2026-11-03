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
    private IdentityMap<object> _membership = null!;
    private IdentityMap<object> _candidate = null!;
    private IdentityMap<object> _entering = null!;
    private IdentityMap<object> _leaving = null!;
    private bool _suspended = true;

    internal Source(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    internal sealed class Capture(IReadOnlyList<object> rows, long assignmentVersion)
    {
        internal IReadOnlyList<object> Rows { get; } = rows;
        internal long AssignmentVersion { get; } = assignmentVersion;
        internal bool Validated { get; set; }
    }

    internal event EventHandler<Change>? SnapshotChanged;

    /// <summary>A row a notification added, with its index in the source.</summary>
    internal readonly record struct Arrival(object Item, int Index);

    /// <summary>How a notification moved the source indices of the rows it did not touch.</summary>
    internal readonly record struct Shift(int From, int Removed, int To, int Added)
    {
        internal int Apply(int index)
        {
            if (index >= From && index < From + Removed)
            {
                // A row the notification replaced with a new instance of itself takes the new
                // instance's index; the other rows in this range left.
                return To + (index - From);
            }
            if (Removed > 0 && index >= From + Removed)
            {
                index -= Removed;
            }
            if (Added > 0 && index >= To)
            {
                index += Added;
            }
            return index;
        }
    }

    /// <summary>
    /// One change to the source, classified once for every step of the publish. A whole change
    /// affects every row: an assignment, a <c>RefreshView()</c>, a Reset or a Move, or any change
    /// without a validated predecessor. Any other change names its rows: those that leave, those
    /// that arrive, and those to read again, which a same-identity Replace and a row named by
    /// <c>RefreshView(changed)</c> are.
    /// </summary>
    internal sealed class Preparation(
        Capture capture,
        IdentityMap<object> membership,
        IdentityMap<object> entering,
        IdentityMap<object> leaving,
        IReadOnlyList<object>? leavers = null,
        IReadOnlyList<Arrival>? arrivals = null,
        IReadOnlyList<object>? reread = null,
        Shift shift = default,
        bool movesRows = false)
    {
        internal Capture Capture { get; } = capture;
        internal IdentityMap<object> Membership { get; } = membership;
        internal bool IsWhole => leavers is null;
        /// <summary>The accepted instances of the rows that leave.</summary>
        internal IReadOnlyList<object> Leavers { get; } = leavers ?? Array.Empty<object>();
        internal IReadOnlyList<Arrival> Arrivals { get; } = arrivals ?? Array.Empty<Arrival>();
        /// <summary>The current instances of the rows that stay and are read again.</summary>
        internal IReadOnlyList<object> Reread { get; } = reread ?? Array.Empty<object>();
        internal Shift Shift { get; } = shift;
        /// <summary>Whether a row the change removed arrives again at another index.</summary>
        internal bool MovesRows { get; } = movesRows;

        /// <summary>
        /// The source's instance with <paramref name="item"/>'s identity after the change, or
        /// null.
        /// </summary>
        internal object? Resolve(object item) =>
            entering.Resolve(item) ?? (leaving.ContainsKey(item) ? null : Membership.Resolve(item));
    }

    internal void SetIdentity(IIdentity identity)
    {
        _membership = identity.CreateMap<object>();
        _candidate = identity.CreateMap<object>();
        _entering = identity.CreateMap<object>();
        _leaving = identity.CreateMap<object>();
        _snapshot.Validated = false;
    }

    internal Preparation Prepare(Change change, Action<object> validate)
    {
        Capture capture = change switch
        {
            Change.Whole whole => whole.Capture,
            Change.Collection collection => collection.Capture,
            _ => _snapshot,
        };
        _entering.Clear();
        _leaving.Clear();
        bool accepted = ReferenceEquals(capture, _snapshot) && capture.Validated;
        if (change is Change.Values values && accepted)
        {
            List<object> named = new();
            foreach (object item in values.Rows)
            {
                if (item is not null && _membership.Resolve(item) is { } row && _entering.TryAdd(row, row))
                {
                    named.Add(row);
                }
            }
            _entering.Clear();
            return new(capture, _membership, _entering, _leaving, Array.Empty<object>(), null, named);
        }
        if (change is Change.Collection { HasPredecessor: true, Notification: var notification }
            && notification.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove
                or NotifyCollectionChangedAction.Replace
            && (notification.NewItems is not { Count: > 0 } || notification.NewStartingIndex >= 0)
            && (notification.OldItems is not { Count: > 0 } || notification.OldStartingIndex >= 0))
        {
            return PrepareRows(capture, notification, validate);
        }
        if (!accepted)
        {
            _candidate.Clear();
            foreach (object item in capture.Rows)
            {
                validate(item);
                if (!_candidate.TryAdd(item, item))
                {
                    throw new InvalidOperationException("The source contains duplicate row identities.");
                }
            }
        }
        return new(capture, accepted ? _membership : _candidate, _entering, _leaving);
    }

    private Preparation PrepareRows(Capture capture, NotifyCollectionChangedEventArgs notification,
        Action<object> validate)
    {
        List<object> removed = new();
        foreach (object item in notification.OldItems ?? Array.Empty<object>())
        {
            object row = _membership.Resolve(item)
                ?? throw new InvalidOperationException("The source removed an unavailable row.");
            _leaving.TryAdd(row, row);
            removed.Add(row);
        }
        List<Arrival> arrivals = new();
        List<object> reread = new();
        bool movesRows = false;
        IList added = notification.NewItems ?? Array.Empty<object>();
        for (int offset = 0; offset < added.Count; offset++)
        {
            object item = added[offset]!;
            validate(item);
            if (!_entering.TryAdd(item, item)
                || _membership.ContainsKey(item) && !_leaving.ContainsKey(item))
            {
                throw new InvalidOperationException("The source contains duplicate row identities.");
            }
            // The same identity at the same place is a new instance of a row that stays.
            if (offset < removed.Count && ReferenceEquals(_leaving.Resolve(item), removed[offset]))
            {
                _leaving.Remove(item);
                reread.Add(item);
            }
            else
            {
                movesRows |= _leaving.Count > 0 && _leaving.ContainsKey(item);
                arrivals.Add(new(item, notification.NewStartingIndex + offset));
            }
        }
        List<object> leavers = new(removed.Count);
        foreach (object row in removed)
        {
            if (ReferenceEquals(_leaving.Resolve(row), row))
            {
                leavers.Add(row);
            }
        }
        return new(capture, _membership, _entering, _leaving, leavers, arrivals, reread,
            new(notification.OldStartingIndex, removed.Count, notification.NewStartingIndex, added.Count),
            movesRows);
    }

    internal void Accept(Preparation preparation)
    {
        if (ReferenceEquals(preparation.Membership, _candidate))
        {
            (_membership, _candidate) = (_candidate, _membership);
        }
        foreach (object row in preparation.Leavers)
        {
            _membership.Remove(row);
        }
        foreach (var entry in _entering)
        {
            _membership.Set(entry.Key, entry.Key);
        }
        preparation.Capture.Validated = true;
        _snapshot = preparation.Capture;
    }

    /// <summary>
    /// Keep <paramref name="capture"/> without preparing it: until the schema is captured, or as
    /// the roots of a hierarchy, which validates its rows itself.
    /// </summary>
    internal void Store(Capture capture, bool validated)
    {
        capture.Validated = validated;
        _snapshot = capture;
    }

    internal Capture Current => _snapshot;
    internal IdentityMap<object> Membership => _membership;
    internal void Complete()
    {
        _entering.Clear();
        _leaving.Clear();
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
