using System.Collections;
using System.Collections.Specialized;

namespace Syno.TableView;

/// <summary>
/// A table's hierarchy: a node for every row at every depth, the child lists they were read
/// from, and the order each expanded parent's children were last shown in.
/// </summary>
internal sealed class Hierarchy(
    Column column,
    Func<object, IEnumerable> children,
    Func<object, bool> isExpanded,
    Action<object, bool> setExpanded,
    Func<object, bool>? hasUnrealizedChildren
)
{
    internal sealed record Node(
        object Item,
        object? Parent,
        int Depth,
        ChildSource Source
    )
    {
        internal IReadOnlyList<object> Children => Source.Items;
        internal int Position { get; set; }
        internal bool Expandable { get; set; }
        internal bool Expanded { get; set; }
    }

    internal sealed class ChildSource(IEnumerable source, IReadOnlyList<object> items)
    {
        internal IEnumerable Source { get; } = source;
        internal IReadOnlyList<object> Items { get; set; } = items;
        internal HashSet<Node> Parents { get; } = new(ReferenceEqualityComparer.Instance);
        internal NotifyCollectionChangedEventHandler? Handler { get; set; }
    }

    /// <summary>A child list, or the roots, whose items a publish reads again.</summary>
    /// <param name="Parent">The parent row, or null for the roots.</param>
    /// <param name="Source">
    /// The child list whose items become <paramref name="After"/>, if it stays.
    /// </param>
    private sealed record ListChange(object? Parent, int Depth, IReadOnlyList<object> Before,
        IReadOnlyList<object> After, ChildSource? Source);

    private IIdentity _identity = null!;
    private IdentityMap<Node> _nodes = null!;
    private readonly Dictionary<IEnumerable, ChildSource> _sources = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyList<object> _roots = Array.Empty<object>();
    private long _assignmentVersion = -1;
    // Notifying lists whose accepted items may differ from the host's: a notification no accepted
    // publish has applied, or a time the table did not observe them. Every change reads them.
    private readonly HashSet<ChildSource> _stale = new();
    private bool _suspended;
    // A node arrived or left since sibling positions were computed: a row that changes parent
    // can leave the shown order as it was. A node that only takes a new instance keeps its
    // position.
    private bool _nodesChanged;
    private int _expandableRows;
    private IdentityMap<int> _counts = null!;
    private Preparation _prepared = null!;
    // The last projection's row count, so the next one rarely grows its list.
    private int _projectedRows;

    /// <summary>
    /// The order each expanded parent's children were shown in by the last publish, keyed by the
    /// parent, and the roots' order. Stored derived state, so a group sorts again when it may be
    /// out of date: after a change of sort column or direction (<see cref="ForgetOrder"/>), when
    /// its parent was not expanded and shown at the last publish (no entry), when its list
    /// changed which rows it holds or their order, when moves are allowed and it holds a row
    /// <c>RefreshView(changed)</c> names or a new instance of a row, and whenever moves are
    /// allowed for a whole change or a pending settle.
    /// </summary>
    private IdentityMap<object[]> _kept = null!;
    private IdentityMap<object[]> _candidateKept = null!;
    private object[]? _keptRoots;
    private object[]? _candidateKeptRoots;

    internal long Version { get; private set; }

    internal Column Column => column;
    internal event EventHandler<Change.Children>? CollectionChanged;

    internal bool IsExpanded(object item) => isExpanded(item);

    internal void SetExpanded(object item, bool value) => setExpanded(item, value);

    internal Node? Find(object item) => _nodes.TryGetValue(item, out Node node) ? node : null;

    internal int RootCount => _roots.Count;

    /// <summary>Whether any shown row is expandable, so the expander slot is reserved.</summary>
    internal bool HasChildren => _expandableRows > 0;

    /// <summary>
    /// Whether the person can expand the row: it has children, or the host has some it has
    /// not supplied yet. Supplied children take precedence over the host's flag.
    /// </summary>
    internal bool IsExpandable(Node node) =>
        node.Children.Count > 0 || hasUnrealizedChildren?.Invoke(node.Item) == true;

    /// <summary>
    /// Whether the accepted hierarchy shows <paramref name="item"/>: every ancestor is
    /// expanded.
    /// </summary>
    internal bool IsShown(object item)
    {
        if (Find(item) is not { } node)
        {
            return false;
        }
        while (node.Parent is { } parent)
        {
            if (Find(parent) is not { } ancestor || ancestor.Children.Count == 0 || !isExpanded(parent))
            {
                return false;
            }
            node = ancestor;
        }
        return true;
    }

    /// <summary>The kept sibling orders were made under another sort.</summary>
    internal void ForgetOrder()
    {
        _kept.Clear();
        _keptRoots = null;
    }

    /// <summary>
    /// Whether a child list's accepted items still match the host's list. A plain list raises no
    /// changes, so it matches only within the root assignment it was read in. A notifying list
    /// matches unless it is stale.
    /// </summary>
    private bool Trusts(ChildSource source, long assignmentVersion) =>
        source.Source is INotifyCollectionChanged
            ? !_stale.Contains(source)
            : _assignmentVersion == assignmentVersion;

    /// <summary>
    /// Start again with <paramref name="identity"/>: nothing is accepted yet, so the next
    /// <see cref="Prepare"/> reads every list.
    /// </summary>
    internal void SetIdentity(IIdentity identity)
    {
        _identity = identity;
        _nodes = identity.CreateMap<Node>();
        _counts = identity.CreateMap<int>();
        _kept = identity.CreateMap<object[]>();
        _candidateKept = identity.CreateMap<object[]>();
        _prepared = new(this, identity);
        _roots = Array.Empty<object>();
        _assignmentVersion = -1;
    }

    /// <summary>
    /// The hierarchy after <paramref name="change"/>, kept apart from the accepted one until
    /// <see cref="Preparation.Accept"/>. Only the lists the change can have altered are read
    /// again: every list for a new root assignment, the roots for a root change, the notified
    /// list for a child notification, and any list the hierarchy cannot trust. Each changed
    /// list is compared with its accepted items by identity: rows that leave take their
    /// subtrees with them, rows that arrive bring theirs, and a new instance of a row replaces
    /// the old one in place.
    /// </summary>
    internal Preparation Prepare(Change change, Body.Source.Capture capture, Action<object> validate)
    {
        _prepared.Prepare(change, capture, validate);
        return _prepared;
    }

    internal sealed class Preparation
    {
        private readonly Hierarchy _owner;
        private readonly List<ListChange> _changes = new();
        private readonly IdentityMap<Node> _removed;
        private readonly IdentityMap<Node> _added;
        private readonly IdentitySet _after;
        private readonly IdentitySet _seen;
        private bool _readAll;
        private readonly Dictionary<ChildSource, IReadOnlyList<object>> _lists = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IEnumerable, ChildSource> _created = new(ReferenceEqualityComparer.Instance);
        private long _assignmentVersion;

        internal Preparation(Hierarchy owner, IIdentity identity)
        {
            _owner = owner;
            _removed = identity.CreateMap<Node>();
            _added = identity.CreateMap<Node>();
            _after = new(identity);
            _seen = new(identity);
            ChangedGroups = new(identity);
            NamedGroups = new(identity);
        }

        internal IReadOnlyList<object> Roots { get; private set; } = Array.Empty<object>();
        /// <summary>
        /// Every group may be read again: the change was not a child notification or named
        /// rows.
        /// </summary>
        internal bool IsWhole { get; private set; }
        private bool RootsChanged { get; set; }
        private bool NamedRoots { get; set; }
        /// <summary>Parents whose children changed which rows they hold, or their order.</summary>
        private IdentitySet ChangedGroups { get; }
        /// <summary>
        /// Parents of rows named by <c>RefreshView(changed)</c> or replaced by a new instance.
        /// </summary>
        private IdentitySet NamedGroups { get; }
        /// <summary>
        /// Whether any row has a new node: it arrived, or a new instance replaced it.
        /// </summary>
        internal bool HasNewNodes => _added.Count > 0;

        /// <summary>
        /// Whether <paramref name="parent"/>'s children, or the roots for null, sort again rather
        /// than keep their last order, under <paramref name="siblings"/>.
        /// </summary>
        internal bool SortsAgain(object? parent, SiblingSort siblings) => siblings == SiblingSort.All
            || (parent is null
                ? RootsChanged || siblings == SiblingSort.Named && NamedRoots
                : ChangedGroups.Contains(parent) || siblings == SiblingSort.Named && NamedGroups.Contains(parent));

        /// <summary>
        /// Whether the change changes no shown row: the roots did not change, and every list it
        /// changed or named a row of belongs to a parent that is collapsed or not shown.
        /// </summary>
        /// <param name="shown">Whether the view shows a row.</param>
        internal bool ChangesNoShownRow(Func<object, bool> shown)
        {
            return !IsWhole && !RootsChanged && !NamedRoots && !ShowsChildren(ChangedGroups)
                && !ShowsChildren(NamedGroups);

            // A parent the view shows expanded, or that the host has expanded since.
            bool ShowsChildren(IdentitySet parents)
            {
                foreach (object parent in parents)
                {
                    if (shown(parent) && (_owner.IsExpanded(parent) || _owner.Find(parent)?.Expanded == true))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>See <see cref="Hierarchy.Prepare"/>.</summary>
        internal void Prepare(Change change, Body.Source.Capture capture, Action<object> validate)
        {
            Roots = _owner._roots;
            _assignmentVersion = capture.AssignmentVersion;
            IsWhole = change is not (Change.Children or Change.Values);
            if (change is Change.Children notified)
            {
                // The list stays stale unless this change is accepted.
                _owner._stale.Add(notified.Source);
            }
            try
            {
                if (!capture.Validated || capture.AssignmentVersion != _owner._assignmentVersion)
                {
                    ReadAll(capture.Rows);
                }
                else
                {
                    if (change is Change.Whole or Change.Collection)
                    {
                        RecordList(null, 0, _owner._roots, capture.Rows, null);
                    }
                    ReadUntrusted(change as Change.Children);
                    if (change is Change.Values values)
                    {
                        Name(values.Rows);
                    }
                }
                Apply(validate);
            }
            catch
            {
                Complete();
                throw;
            }
        }

        internal Node? Find(object item)
        {
            if (_added.Count > 0 && _added.TryGetValue(item, out Node added))
            {
                return added;
            }
            if (_removed.Count > 0 && _removed.ContainsKey(item))
            {
                return null;
            }
            return _owner.Find(item);
        }

        internal IReadOnlyList<object> ItemsOf(Node node) =>
            _lists.Count > 0 && _lists.TryGetValue(node.Source, out IReadOnlyList<object>? items) ? items : node.Source.Items;

        /// <summary>
        /// A new root assignment: every list may have changed, so each is read again.
        /// </summary>
        private void ReadAll(IReadOnlyList<object> roots)
        {
            _readAll = true;
            RecordList(null, 0, _owner._roots, roots, null);
            Stack<object> pending = new();
            Push(roots);
            while (pending.TryPop(out object? item))
            {
                // An accepted row reached twice is repeated, or its own ancestor.
                if (!_seen.Add(item))
                {
                    throw Duplicate();
                }
                Node node = _owner._nodes[item];
                ChildSource source = node.Source;
                IEnumerable enumerable = _owner.Children(item);
                IReadOnlyList<object> items;
                if (!ReferenceEquals(enumerable, source.Source))
                {
                    Node relinked = node with { Source = SourceFor(enumerable) };
                    _added.Set(item, relinked);
                    items = ItemsOf(relinked);
                    RecordList(item, node.Depth + 1, source.Items, items, null);
                }
                else if (_owner.Trusts(source, _assignmentVersion))
                {
                    items = source.Items;
                }
                else
                {
                    items = enumerable is IReadOnlyList<object> list && SameReferences(list, source.Items)
                        ? source.Items
                        : Read(enumerable);
                    RecordList(item, node.Depth + 1, source.Items, items, source);
                }
                Push(items);
            }
            _seen.Clear();

            void Push(IReadOnlyList<object> items)
            {
                for (int index = items.Count - 1; index >= 0; index--)
                {
                    if (items[index] is { } item && _owner._nodes.ContainsKey(item))
                    {
                        pending.Push(item);
                    }
                }
            }
        }

        /// <summary>
        /// The lists the hierarchy cannot trust, read again; the notified list takes its
        /// notification when the list was trusted before it and the notification gives indices.
        /// </summary>
        private void ReadUntrusted(Change.Children? notified)
        {
            // Within one root assignment only a stale list can differ from what the table holds.
            foreach (ChildSource source in _owner._stale)
            {
                NotifyCollectionChangedEventArgs? args = notified?.Notification;
                IReadOnlyList<object> items = ReferenceEquals(source, notified?.Source) && notified!.HasPredecessor
                    && args!.Action != NotifyCollectionChangedAction.Reset
                    && (args.OldItems is not { Count: > 0 } || args.OldStartingIndex >= 0)
                    && (args.NewItems is not { Count: > 0 } || args.NewStartingIndex >= 0)
                        ? ApplyNotification(source.Items, args)
                        : Read(source.Source);
                foreach (Node parent in source.Parents)
                {
                    RecordList(parent.Item, parent.Depth + 1, source.Items, items, source);
                }
            }
        }

        private void Name(IEnumerable<object> rows)
        {
            foreach (object row in rows)
            {
                if (row is not null && _owner.Find(row) is { } node)
                {
                    if (node.Parent is { } parent)
                    {
                        NamedGroups.Add(parent);
                    }
                    else
                    {
                        NamedRoots = true;
                    }
                }
            }
        }

        /// <summary>
        /// Record that a list's items become <paramref name="after"/>, and the change when they
        /// differ from <paramref name="before"/>.
        /// </summary>
        private void RecordList(object? parent, int depth, IReadOnlyList<object> before,
            IReadOnlyList<object> after, ChildSource? source)
        {
            bool same = ReferenceEquals(before, after) || SameReferences(before, after);
            if (source is not null)
            {
                _lists[source] = after;
            }
            else if (parent is null && !same)
            {
                // A copy: a notifying root list changes in place, and the next publish compares
                // with this.
                Roots = after.ToArray();
            }
            if (!same)
            {
                _changes.Add(new(parent, depth, before, after, source));
            }
        }

        /// <summary>
        /// Apply the recorded list changes to the candidate: first every row that leaves, with its
        /// subtree, and every new instance, whose own child list can let rows leave, so a row that
        /// moves between lists arrives as a new row; then every row that arrives.
        /// </summary>
        private void Apply(Action<object> validate)
        {
            // Replace can append a change, which leaves its rows in this loop too.
            for (int index = 0; index < _changes.Count; index++)
            {
                ListChange change = _changes[index];
                Leave(change);
                foreach (object item in change.After)
                {
                    if (item is not null && _owner.Find(item) is { } existing && !_removed.ContainsKey(item)
                        && !ReferenceEquals(existing.Item, item))
                    {
                        Replace(existing, item, change.Parent, change.Depth);
                    }
                }
            }
            for (int index = 0; index < _changes.Count; index++)
            {
                ListChange change = _changes[index];
                // A parent that left took its subtree; one that also arrived brought its own, so
                // the order its children were last shown in no longer holds.
                if (change.Parent is { } parent && _removed.ContainsKey(parent))
                {
                    ChangedGroups.Add(parent);
                    continue;
                }
                bool structural = change.Before.Count != change.After.Count;
                for (int at = 0; at < change.After.Count; at++)
                {
                    object item = change.After[at];
                    validate(item);
                    structural |= !structural && !_owner._identity.IsSame(change.Before[at], item);
                    Node? existing = _owner.Find(item) is { } accepted && !_removed.ContainsKey(item) ? accepted : null;
                    if (existing is null)
                    {
                        Arrive(item, change.Parent, change.Depth, validate);
                    }
                    else if (!SameParent(existing.Parent, change.Parent) || !_seen.Add(item))
                    {
                        throw Duplicate();
                    }
                }
                if (!structural)
                {
                    continue;
                }
                if (change.Parent is { } changed)
                {
                    ChangedGroups.Add(changed);
                }
                else
                {
                    RootsChanged = true;
                }
            }
        }

        private void Leave(ListChange change)
        {
            _after.Clear();
            foreach (object item in change.After)
            {
                if (item is not null)
                {
                    _after.Add(item);
                }
            }
            foreach (object item in change.Before)
            {
                if (!_after.Contains(item) && _owner.Find(item) is { } node)
                {
                    LeaveSubtree(node);
                }
            }
        }

        private void LeaveSubtree(Node node)
        {
            Stack<Node> pending = new();
            pending.Push(node);
            while (pending.TryPop(out Node? next))
            {
                if (!_removed.TryAdd(next.Item, next))
                {
                    continue;
                }
                // A row that leaves arrives again as a new node, if it arrives.
                _added.Remove(next.Item);
                foreach (object child in next.Source.Items)
                {
                    if (_owner.Find(child) is { } descendant)
                    {
                        pending.Push(descendant);
                    }
                }
            }
        }

        private void Arrive(object item, object? parent, int depth, Action<object> validate)
        {
            Stack<(object Item, object? Parent, int Depth)> pending = new();
            pending.Push((item, parent, depth));
            while (pending.TryPop(out var next))
            {
                validate(next.Item);
                if (_owner.Find(next.Item) is not null && !_removed.ContainsKey(next.Item)
                    || !_added.TryAdd(next.Item, new(next.Item, next.Parent, next.Depth, SourceFor(_owner.Children(next.Item)))))
                {
                    throw Duplicate();
                }
                IReadOnlyList<object> items = ItemsOf(_added[next.Item]);
                for (int index = items.Count - 1; index >= 0; index--)
                {
                    pending.Push((items[index], next.Item, next.Depth + 1));
                }
            }
        }

        /// <summary>
        /// A new instance of a row: the same node, with the instance's own child list. A new root
        /// assignment has compared that list already.
        /// </summary>
        private void Replace(Node existing, object item, object? parent, int depth)
        {
            IEnumerable enumerable = _owner.Children(item);
            ChildSource source = ReferenceEquals(enumerable, existing.Source.Source) ? existing.Source : SourceFor(enumerable);
            Node replacement = existing with { Item = item, Parent = parent, Depth = depth, Source = source };
            _added.Set(item, replacement);
            // The new instance may carry a new sort key, so its group is read again as a row
            // RefreshView(changed) names, as a flat table reads a same-identity Replace.
            if (parent is null)
            {
                NamedRoots = true;
            }
            else
            {
                NamedGroups.Add(parent);
            }
            if (!ReferenceEquals(source, existing.Source) && !_readAll)
            {
                _changes.Add(new(item, depth + 1, existing.Source.Items, ItemsOf(replacement), null));
            }
        }

        /// <summary>
        /// The child list for <paramref name="enumerable"/>: the one already read, or a new
        /// one.
        /// </summary>
        private ChildSource SourceFor(IEnumerable enumerable)
        {
            if (_owner._sources.TryGetValue(enumerable, out ChildSource? source)
                || _created.TryGetValue(enumerable, out source))
            {
                if (!_lists.ContainsKey(source) && !_owner.Trusts(source, _assignmentVersion))
                {
                    _lists[source] = Read(enumerable);
                }
                return source;
            }
            source = new(enumerable, Read(enumerable));
            _created.Add(enumerable, source);
            return source;
        }

        private bool SameParent(object? left, object? right) =>
            left is null ? right is null : right is not null && _owner._identity.IsSame(left, right);

        private static InvalidOperationException Duplicate() =>
            new("The hierarchy contains duplicate row identities or a cycle.");

        /// <summary>Accept the change, with the sibling orders the projection kept.</summary>
        internal void Accept()
        {
            Hierarchy owner = _owner;
            ApplyNodes();
            (owner._kept, owner._candidateKept) = (owner._candidateKept, owner._kept);
            owner._keptRoots = owner._candidateKeptRoots;
            owner.Version++;
            Complete();
        }

        /// <summary>
        /// Accept a change that changes no shown row (<see cref="ChangesNoShownRow"/>), with no
        /// projection: every kept sibling order stays, and only the expanders of shown parents
        /// whose children changed can change.
        /// </summary>
        /// <param name="shown">Whether the view shows a row.</param>
        /// <returns>Whether an expander changed.</returns>
        internal bool AcceptHidden(Func<object, bool> shown)
        {
            List<object> parents = new();
            foreach (object parent in ChangedGroups)
            {
                if (shown(parent))
                {
                    parents.Add(parent);
                }
            }
            ApplyNodes();
            _owner.Version++;
            Complete();
            return _owner.UpdateExpanders(parents);
        }

        private void ApplyNodes()
        {
            Hierarchy owner = _owner;
            List<Node> replaced = new();
            foreach (var entry in _added)
            {
                if (owner._nodes.TryGetValue(entry.Key, out Node old) && !_removed.ContainsKey(entry.Key))
                {
                    replaced.Add(old);
                }
            }
            foreach (var entry in _added)
            {
                owner.Link(entry.Value);
            }
            // Before the removals, which stop observing a list that loses every parent.
            foreach (var (source, items) in _lists)
            {
                source.Items = items;
                owner._stale.Remove(source);
                owner.Attach(source);
            }
            foreach (var entry in _removed)
            {
                owner.Unlink(entry.Value);
                owner._nodes.Remove(entry.Key);
            }
            foreach (Node old in replaced)
            {
                owner.Unlink(old);
            }
            foreach (var entry in _added)
            {
                owner._nodes.Set(entry.Key, entry.Value);
            }
            owner._nodesChanged |= _added.Count > replaced.Count || _removed.Count > 0;
            owner._roots = Roots;
            owner._assignmentVersion = _assignmentVersion;
        }

        internal void Complete()
        {
            _changes.Clear();
            _readAll = false;
            _seen.Clear();
            _removed.Clear();
            _added.Clear();
            _after.Clear();
            _lists.Clear();
            _created.Clear();
            ChangedGroups.Clear();
            NamedGroups.Clear();
            RootsChanged = false;
            NamedRoots = false;
            _owner._candidateKept.Clear();
            _owner._candidateKeptRoots = null;
        }
    }

    private void Link(Node node)
    {
        _sources.TryAdd(node.Source.Source, node.Source);
        node.Source.Parents.Add(node);
        Attach(node.Source);
    }

    private void Unlink(Node node)
    {
        ChildSource source = node.Source;
        source.Parents.Remove(node);
        if (source.Parents.Count == 0)
        {
            Detach(source);
            _sources.Remove(source.Source);
            _stale.Remove(source);
        }
    }

    private static bool SameReferences(IReadOnlyList<object> next, IReadOnlyList<object> before)
    {
        if (next.Count != before.Count)
        {
            return false;
        }
        for (int index = 0; index < next.Count; index++)
        {
            if (!ReferenceEquals(next[index], before[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static IReadOnlyList<object> Read(IEnumerable source)
    {
        List<object> items = new();
        if (source is IList indexed)
        {
            for (int index = 0; index < indexed.Count; index++)
            {
                items.Add(indexed[index]!);
            }
        }
        else
        {
            foreach (object? item in source)
            {
                items.Add(item!);
            }
        }
        return items;
    }

    private static IReadOnlyList<object> ApplyNotification(IReadOnlyList<object> before,
        NotifyCollectionChangedEventArgs args)
    {
        List<object> next = new(before);
        if (args.OldItems is { Count: > 0 } old)
        {
            next.RemoveRange(args.OldStartingIndex, old.Count);
        }
        if (args.NewItems is { Count: > 0 } added)
        {
            next.InsertRange(args.NewStartingIndex, added.Cast<object>());
        }
        return next;
    }

    /// <summary>
    /// Bring the expanders of <paramref name="parents"/>, shown rows, up to date.
    /// </summary>
    /// <returns>Whether one changed.</returns>
    private bool UpdateExpanders(IReadOnlyList<object> parents)
    {
        bool changed = false;
        foreach (object parent in parents)
        {
            Node node = _nodes[parent];
            bool expandable = IsExpandable(node);
            if (node.Expandable != expandable)
            {
                _expandableRows += expandable ? 1 : -1;
                node.Expandable = expandable;
                changed = true;
            }
        }
        if (changed)
        {
            Version++;
        }
        return changed;
    }

    internal void UpdatePresentation(IReadOnlyList<object> order, bool moved)
    {
        moved |= _nodesChanged;
        _nodesChanged = false;
        if (moved)
        {
            _counts.Clear();
        }
        int roots = 0;
        _expandableRows = 0;
        foreach (object item in order)
        {
            Node node = _nodes[item];
            bool hasChildren = IsExpandable(node);
            bool expanded = IsExpanded(item);
            if (node.Expandable != hasChildren || node.Expanded != expanded)
            {
                Version++;
            }
            node.Expandable = hasChildren;
            node.Expanded = expanded;
            _expandableRows += hasChildren ? 1 : 0;
            if (!moved)
            {
                continue;
            }
            if (node.Parent is not { } parent)
            {
                node.Position = ++roots;
            }
            else
            {
                _counts.TryGetValue(parent, out int count);
                node.Position = count + 1;
                _counts.Set(parent, node.Position);
            }
        }
    }

    private IEnumerable Children(object item) => children(item)
        ?? throw new InvalidOperationException("The children selector returned null.");

    internal void Suspend()
    {
        _suspended = true;
        foreach (ChildSource source in _sources.Values)
        {
            Detach(source);
        }
    }

    internal void Resume()
    {
        _suspended = false;
    }

    private void Detach(ChildSource source)
    {
        NotifyCollectionChangedEventHandler? handler = source.Handler;
        source.Handler = null;
        if (handler is not null && source.Source is INotifyCollectionChanged notifier)
        {
            notifier.CollectionChanged -= handler;
            _stale.Add(source);
        }
    }

    private void Attach(ChildSource source)
    {
        if (source.Handler is not null || source.Source is not INotifyCollectionChanged notifier)
        {
            return;
        }
        if (_suspended)
        {
            _stale.Add(source);
            return;
        }
        NotifyCollectionChangedEventHandler? handler = null;
        handler = (_, args) =>
        {
            if (!ReferenceEquals(source.Handler, handler))
            {
                return;
            }
            // Read only: the table checks the thread, and the publish marks the list stale.
            CollectionChanged?.Invoke(this, new(source, args, !_stale.Contains(source)));
        };
        source.Handler = handler;
        notifier.CollectionChanged += handler;
    }

    internal sealed class Work(IdentityMap<int> parents)
    {
        internal int GroupFor(object? parent) => parent is null ? 0
            : parents.TryGetValue(parent, out int group) ? group : -1;
    }

    /// <summary>
    /// The prepared hierarchy as rows in view order: each shown row followed by its children when
    /// it is expanded, each sibling group in the order <paramref name="sort"/> gives. A group keeps
    /// the order it was last shown in unless <paramref name="siblings"/> sorts it again.
    /// </summary>
    /// <returns>The rows, and whether any group kept its last order.</returns>
    internal (IReadOnlyList<object> Order, bool Kept) Project(Preparation prepared, SortedOrder? sort,
        SortDirection direction, SiblingSort siblings)
    {
        bool kept = false;
        IReadOnlyList<object> order = Flatten(prepared, (parent, items) =>
        {
            if (sort is null)
            {
                return items;
            }
            object[]? previous = parent is null ? _keptRoots
                : _kept.TryGetValue(parent, out object[]? group) ? group : null;
            object[] result;
            if (prepared.SortsAgain(parent, siblings) || previous is null || previous.Length != items.Count)
            {
                result = (object[])sort.SortSiblings(items, direction);
            }
            else
            {
                result = prepared.HasNewNodes ? Current(previous) : previous;
                kept = true;
            }
            Keep(parent, result);
            return result;
        });
        return (order, kept);

        object[] Current(object[] previous)
        {
            object[]? current = null;
            for (int index = 0; index < previous.Length; index++)
            {
                object item = prepared.Find(previous[index])!.Item;
                if (!ReferenceEquals(item, previous[index]))
                {
                    current ??= (object[])previous.Clone();
                    current[index] = item;
                }
            }
            return current ?? previous;
        }
    }

    /// <summary>
    /// Every shown sibling group, in natural order, for a sort run off the UI thread.
    /// </summary>
    internal IReadOnlyList<IReadOnlyList<object>> Groups(Preparation prepared, out Work work)
    {
        List<IReadOnlyList<object>> groups = new();
        IdentityMap<int> parents = _identity.CreateMap<int>();
        Flatten(prepared, (parent, items) =>
        {
            if (parent is not null)
            {
                parents.TryAdd(parent, groups.Count);
            }
            groups.Add(items);
            return items;
        });
        work = new(parents);
        return groups;
    }

    /// <summary>
    /// The prepared hierarchy in view order, each sibling group as sorted off the UI thread.
    /// </summary>
    internal IReadOnlyList<object> Project(Preparation prepared, Work hierarchy, SortedOrder.Work sorting, bool refresh) =>
        Flatten(prepared, (parent, items) =>
        {
            object[] result = sorting.Project(hierarchy.GroupFor(parent), items, refresh).ToArray();
            Keep(parent, result);
            return result;
        });

    private void Keep(object? parent, object[] order)
    {
        if (parent is null)
        {
            _candidateKeptRoots = order;
        }
        else
        {
            _candidateKept.Set(parent, order);
        }
    }

    private List<object> Flatten(Preparation prepared,
        Func<object?, IReadOnlyList<object>, IReadOnlyList<object>> order)
    {
        _candidateKept.Clear();
        _candidateKeptRoots = null;
        List<object> visible = new(_projectedRows);
        Stack<(IReadOnlyList<object> Items, int Next)> levels = new();
        levels.Push((order(null, prepared.Roots), 0));
        while (levels.TryPop(out var level))
        {
            if (level.Next >= level.Items.Count)
            {
                continue;
            }
            object item = level.Items[level.Next];
            levels.Push((level.Items, level.Next + 1));
            visible.Add(item);
            IReadOnlyList<object> items = prepared.ItemsOf(prepared.Find(item)!);
            if (items.Count > 0 && isExpanded(item))
            {
                levels.Push((order(item, items), 0));
            }
        }
        _projectedRows = visible.Count;
        return visible;
    }
}
