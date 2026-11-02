using System.Collections;
using System.Collections.Specialized;

namespace Syno.TableView;

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

    internal sealed record Snapshot(
        IReadOnlyList<object> Roots,
        IdentityMap<Node> Nodes,
        Dictionary<IEnumerable, ChildSource> Sources,
        long AssignmentVersion
    );

    internal sealed class ChildSource(IEnumerable source, IReadOnlyList<object> items)
    {
        internal IEnumerable Source { get; } = source;
        internal IReadOnlyList<object> Items { get; set; } = items;
        internal HashSet<Node> Parents { get; } = new(ReferenceEqualityComparer.Instance);
        internal NotifyCollectionChangedEventHandler? Handler { get; set; }
    }

    internal sealed record CollectionChange(ChildSource Source,
        NotifyCollectionChangedEventArgs Notification, bool HasPredecessor);

    // Notifying lists with a notification that no accepted capture has applied yet.
    private readonly HashSet<ChildSource> _stale = new();
    private bool _suspended;
    private int _expandableRows;
    private IdentityMap<int>? _counts;
    private IdentityMap<object>? _projected;
    internal long Version { get; private set; }

    internal Column Column => column;
    internal Snapshot? Captured { get; private set; }
    internal event EventHandler? Changed;
    internal event EventHandler<CollectionChange>? CollectionChanged;

    internal bool IsExpanded(object item) => isExpanded(item);

    internal void SetExpanded(object item, bool value) => setExpanded(item, value);

    internal Node? Find(object item) => Captured?.Nodes.GetValueOrDefault(item);

    /// <summary>Whether any shown row is expandable, so the expander slot is reserved.</summary>
    internal bool HasChildren => _expandableRows > 0;

    /// <summary>
    /// Whether the person can expand the row: it has children, or the host has some it has
    /// not supplied yet. Supplied children take precedence over the host's flag.
    /// </summary>
    internal bool IsExpandable(Node node) =>
        node.Children.Count > 0 || hasUnrealizedChildren?.Invoke(node.Item) == true;

    internal bool NeedsCapture(long assignmentVersion) =>
        Captured is not { } snapshot || snapshot.AssignmentVersion != assignmentVersion
            || snapshot.Sources.Values.Any(source => !Trusts(source, assignmentVersion));

    /// <summary>
    /// Whether a captured child list's items still match the host's list, so a
    /// capture may reuse them. A plain list raises no changes, so it matches only
    /// within the root assignment it was read in. A notifying list matches only
    /// while its handler has been attached since it was read and every
    /// notification since has been applied; an unloaded table observes no list.
    /// </summary>
    private bool Trusts(ChildSource source, long assignmentVersion) =>
        source.Source is INotifyCollectionChanged
            ? source.Handler is not null && !_stale.Contains(source)
            : Captured!.AssignmentVersion == assignmentVersion;

    internal Preparation Capture(IReadOnlyList<object> roots, IIdentity identity, Action<object> validate, long assignmentVersion)
    {
        IdentityMap<Node> nodes = identity.CreateMap<Node>(keepsKeys: true);
        _counts ??= identity.CreateMap<int>();
        _projected ??= identity.CreateMap<object>(keepsKeys: true);
        Dictionary<IEnumerable, ChildSource> sources = new(ReferenceEqualityComparer.Instance);
        bool changed = Captured is null || roots.Count != Captured.Roots.Count;
        List<(IReadOnlyList<object> Next, IReadOnlyList<object> Before)> comparisons = new();
        Stack<(object Item, object? Parent, int Depth)> pending = new();
        for (int i = roots.Count - 1; i >= 0; i--)
        {
            pending.Push((roots[i], null, 0));
        }
        while (pending.TryPop(out var next))
        {
            validate(next.Item);
            IEnumerable enumerable = children(next.Item)
                ?? throw new InvalidOperationException("The children selector returned null.");
            if (!sources.TryGetValue(enumerable, out ChildSource? source))
            {
                IReadOnlyList<object> items;
                if (Captured is { } before && before.Sources.TryGetValue(enumerable, out ChildSource? cached)
                    && Trusts(cached, assignmentVersion))
                {
                    items = cached.Items;
                }
                else
                {
                    if (Captured is { } previous && previous.Sources.TryGetValue(enumerable, out cached)
                        && enumerable is IList indexed && SameReferences(indexed, cached.Items))
                    {
                        items = cached.Items;
                    }
                    else
                    {
                        items = Read(enumerable);
                    }
                }
                source = new(enumerable, items);
                sources.Add(enumerable, source);
            }
            Node node = new(next.Item, next.Parent, next.Depth, source);
            if (!nodes.TryAdd(next.Item, node))
            {
                throw new InvalidOperationException(
                    "The hierarchy contains duplicate row identities or a cycle."
                );
            }
            source.Parents.Add(node);
            if (Captured is { } accepted && accepted.Nodes.TryGetValue(next.Item, nodes, out Node old))
            {
                node.Position = old.Position;
                node.Expandable = old.Expandable;
                node.Expanded = old.Expanded;
                if ((old.Parent is null) != (next.Parent is null)
                    || old.Parent is not null && !ReferenceEquals(nodes.Resolve(old.Parent, accepted.Nodes), next.Parent)
                )
                {
                    changed = true;
                }
                if (!ReferenceEquals(source.Items, old.Children))
                {
                    comparisons.Add((source.Items, old.Children));
                }
            }
            else
            {
                changed = true;
            }
            for (int i = source.Items.Count - 1; i >= 0; i--)
            {
                pending.Push((source.Items[i], next.Item, next.Depth + 1));
            }
        }
        if (Captured is { } previousSnapshot)
        {
            changed |= nodes.Count != previousSnapshot.Nodes.Count
                || !SameIdentities(roots, previousSnapshot.Roots, nodes, previousSnapshot.Nodes);
            foreach (var pair in comparisons)
            {
                changed |= !SameIdentities(pair.Next, pair.Before, nodes, previousSnapshot.Nodes);
            }
        }
        return new(this, new(roots.ToArray(), nodes, sources, assignmentVersion), changed);
    }

    private static bool SameReferences(IList next, IReadOnlyList<object> before)
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

    private static bool SameIdentities(IReadOnlyList<object> next, IReadOnlyList<object> before,
        IdentityMap<Node> nodes, IIdentityKeys keys)
    {
        if (next.Count != before.Count)
        {
            return false;
        }
        for (int index = 0; index < next.Count; index++)
        {
            if (!ReferenceEquals(nodes.Resolve(before[index], keys), next[index]))
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

    internal sealed class Preparation : IIdentityKeys
    {
        private readonly Hierarchy _owner;
        private readonly Snapshot? _previous;
        private readonly IdentityMap<Node>? _added;
        private readonly IdentityMap<Node>? _removed;
        private readonly HashSet<ChildSource> _touched = new();
        private readonly List<(ChildSource Source, Node Parent, bool Added)> _relations = new();
        private readonly List<ChildSource> _created = new();
        private (ChildSource Source, IReadOnlyList<object> Items)? _changed;
        private bool _accepted;
        internal Snapshot Snapshot { get; }
        internal IdentityMap<Node> Nodes => Snapshot.Nodes;
        internal bool StructureChanged { get; }
        internal bool RequiresProjection { get; set; }
        internal IReadOnlyList<Node> Parents { get; set; } = Array.Empty<Node>();

        internal Preparation(Hierarchy owner, Snapshot snapshot, bool changed)
        {
            _owner = owner;
            _previous = owner.Captured;
            Snapshot = snapshot;
            StructureChanged = changed;
        }

        private Preparation(Hierarchy owner, IIdentity identity)
            : this(owner, owner.Captured!, true)
        {
            _added = identity.CreateMap<Node>(keepsKeys: true);
            _removed = identity.CreateMap<Node>(keepsKeys: true);
        }

        public bool ContainsReference(object item) => _added?.ContainsReference(item) == true
            || _removed?.ContainsReference(item) == true || Nodes.ContainsReference(item)
            || _previous?.Nodes.ContainsReference(item) == true;

        public IIdentityKeys CapturedFor(object item) => _added?.ContainsReference(item) == true ? _added
            : _removed?.ContainsReference(item) == true ? _removed
            : Nodes.ContainsReference(item) ? Nodes : _previous!.Nodes;

        internal static Preparation Apply(Hierarchy owner, CollectionChange change,
            IIdentity identity, Action<object> validate, IIdentityKeys visible)
        {
            Preparation prepared = new(owner, identity);
            try
            {
                ChildSource source = change.Source;
                prepared.Parents = source.Parents.ToArray();
                prepared.RequiresProjection = prepared.Parents.Any(node => visible.ContainsReference(node.Item)
                    && owner.IsExpanded(node.Item));
                prepared._touched.Add(source);
                prepared._changed = (source, source.Items);
                IReadOnlyList<object> before = source.Items;
                IReadOnlyList<object> next = ApplyNotification(before, change.Notification);
                if (change.Notification.Action != NotifyCollectionChangedAction.Move)
                {
                    foreach (Node parent in prepared.Parents)
                    {
                        foreach (object item in change.Notification.OldItems ?? Array.Empty<object>())
                        {
                            prepared.Remove(item);
                        }
                    }
                }
                source.Items = next;
                if (change.Notification.Action != NotifyCollectionChangedAction.Move)
                {
                    foreach (Node parent in prepared.Parents)
                    {
                        foreach (object item in change.Notification.NewItems ?? Array.Empty<object>())
                        {
                            prepared.Add(item, parent.Item, parent.Depth + 1, validate);
                        }
                    }
                }
                return prepared;
            }
            catch
            {
                prepared.Complete();
                throw;
            }
        }

        private void Remove(object item)
        {
            Stack<Node> pending = new();
            pending.Push(Nodes[item]);
            while (pending.TryPop(out var node))
            {
                foreach (object child in node.Children)
                {
                    pending.Push(Nodes[child]);
                }
                _touched.Add(node.Source);
                node.Source.Parents.Remove(node);
                _relations.Add((node.Source, node, false));
                _removed!.TryAdd(node.Item, node, Nodes);
                Nodes.Remove(node.Item, _removed);
            }
        }

        private void Add(object item, object parent, int depth, Action<object> validate)
        {
            Stack<(object Item, object Parent, int Depth)> pending = new();
            pending.Push((item, parent, depth));
            while (pending.TryPop(out var next))
            {
                validate(next.Item);
                IEnumerable enumerable = _owner.Children(next.Item);
                if (!Snapshot.Sources.TryGetValue(enumerable, out ChildSource? source))
                {
                    source = new(enumerable, Read(enumerable));
                    Snapshot.Sources.Add(enumerable, source);
                    _created.Add(source);
                }
                Node node = new(next.Item, next.Parent, next.Depth, source);
                if (!Nodes.TryAdd(next.Item, node))
                {
                    throw new InvalidOperationException("The hierarchy contains duplicate row identities or a cycle.");
                }
                _added!.TryAdd(next.Item, node, Nodes);
                _touched.Add(source);
                source.Parents.Add(node);
                _relations.Add((source, node, true));
                for (int index = source.Items.Count - 1; index >= 0; index--)
                {
                    pending.Push((source.Items[index], next.Item, next.Depth + 1));
                }
            }
        }

        internal void Accept()
        {
            _accepted = true;
            if (_added is null && ReferenceEquals(Snapshot, _previous))
            {
                return;
            }
            if (_added is null)
            {
                if (_owner.Captured is { } old)
                {
                    foreach (ChildSource source in old.Sources.Values)
                    {
                        Detach(source);
                    }
                }
                _owner.Captured = Snapshot;
                _owner._stale.Clear();
                foreach (ChildSource source in Snapshot.Sources.Values)
                {
                    _owner.Attach(source);
                }
            }
            else
            {
                foreach (ChildSource source in _touched)
                {
                    if (source.Parents.Count == 0)
                    {
                        Detach(source);
                        Snapshot.Sources.Remove(source.Source);
                        _owner._stale.Remove(source);
                    }
                    else
                    {
                        _owner._stale.Remove(source);
                        _owner.Attach(source);
                    }
                }
            }
            _owner.Version++;
        }

        internal void Complete()
        {
            if (!_accepted && _added is not null)
            {
                foreach (var entry in _added)
                {
                    Nodes.Remove(entry.Key, _added);
                }
                foreach (var entry in _removed!)
                {
                    Nodes.TryAdd(entry.Key, entry.Value, _removed);
                }
                for (int index = _relations.Count - 1; index >= 0; index--)
                {
                    var relation = _relations[index];
                    if (relation.Added)
                    {
                        relation.Source.Parents.Remove(relation.Parent);
                    }
                    else
                    {
                        relation.Source.Parents.Add(relation.Parent);
                    }
                }
                if (_changed is { } before)
                {
                    before.Source.Items = before.Items;
                }
                foreach (ChildSource source in _created)
                {
                    Snapshot.Sources.Remove(source.Source);
                }
            }
            _added?.Clear();
            _removed?.Clear();
            _touched.Clear();
            _relations.Clear();
            _created.Clear();
            _changed = null;
        }
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

    internal Preparation Prepare(CollectionChange change, IIdentity identity, Action<object> validate,
        IIdentityKeys visible)
    {
        Invalidate(change.Source);
        if (!change.HasPredecessor || _stale.Count > 1
            || change.Notification.Action == NotifyCollectionChangedAction.Reset
            || change.Notification.OldItems is { Count: > 0 } && change.Notification.OldStartingIndex < 0
            || change.Notification.NewItems is { Count: > 0 } && change.Notification.NewStartingIndex < 0)
        {
            Node[] parents = _stale.SelectMany(source => source.Parents)
                .Distinct<Node>(ReferenceEqualityComparer.Instance).ToArray();
            Preparation prepared = Capture(Captured!.Roots, identity, validate, Captured.AssignmentVersion);
            prepared.Parents = parents;
            prepared.RequiresProjection = parents.Any(node => visible.ContainsReference(node.Item)
                && IsExpanded(node.Item));
            return prepared;
        }
        return Preparation.Apply(this, change, identity, validate, visible);
    }

    internal Preparation Reuse() => new(this, Captured!, false);

    internal void Invalidate(ChildSource source) => _stale.Add(source);

    internal bool UpdateChanged(Preparation prepared, IIdentityKeys visible)
    {
        bool changed = false;
        foreach (Node old in prepared.Parents)
        {
            if (!visible.ContainsReference(old.Item) || Captured!.Nodes.Resolve(old.Item, prepared) is not { } item)
            {
                continue;
            }
            Node node = Captured.Nodes[item];
            bool expandable = IsExpandable(node);
            bool expanded = IsExpanded(item);
            if (node.Expandable != expandable || node.Expanded != expanded)
            {
                _expandableRows += (expandable ? 1 : 0) - (node.Expandable ? 1 : 0);
                node.Expandable = expandable;
                node.Expanded = expanded;
                changed = true;
            }
        }
        if (changed)
        {
            Version++;
        }
        return changed;
    }

    internal void PublishChanged() => Changed?.Invoke(this, EventArgs.Empty);

    internal void UpdatePresentation(IReadOnlyList<object> order, bool moved)
    {
        if (Captured is not { } snapshot)
        {
            return;
        }
        if (moved)
        {
            _counts!.Clear();
        }
        int roots = 0;
        _expandableRows = 0;
        foreach (object item in order)
        {
            Node node = snapshot.Nodes[item];
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
                _counts!.TryGetValue(parent, snapshot.Nodes, out int count);
                node.Position = count + 1;
                _counts.Set(parent, node.Position, snapshot.Nodes);
            }
        }
    }

    private IEnumerable Children(object item) => children(item)
        ?? throw new InvalidOperationException("The children selector returned null.");

    internal void Suspend()
    {
        _suspended = true;
        if (Captured is { } snapshot)
        {
            foreach (ChildSource source in snapshot.Sources.Values)
            {
                Detach(source);
            }
        }
    }

    internal void Resume()
    {
        _suspended = false;
    }

    private static void Detach(ChildSource source)
    {
        NotifyCollectionChangedEventHandler? handler = source.Handler;
        source.Handler = null;
        if (handler is not null && source.Source is INotifyCollectionChanged notifier)
        {
            notifier.CollectionChanged -= handler;
        }
    }

    private void Attach(ChildSource source)
    {
        if (_suspended || source.Handler is not null || source.Source is not INotifyCollectionChanged notifier)
        {
            return;
        }
        NotifyCollectionChangedEventHandler? handler = null;
        handler = (_, args) =>
        {
            if (!ReferenceEquals(source.Handler, handler))
            {
                return;
            }
            CollectionChanged?.Invoke(this, new(source, args, !_stale.Contains(source)));
        };
        source.Handler = handler;
        notifier.CollectionChanged += handler;
    }

    internal sealed class Work(IdentityMap<int> parents)
    {
        private readonly IdentityMap<int> _parents = parents;
        internal int GroupFor(object? parent, IIdentityKeys keys) => parent is null ? 0
            : _parents.TryGetValue(parent, keys, out int group) ? group : -1;
    }

    internal sealed class Projection(Snapshot snapshot, IdentityMap<int> parents,
        IReadOnlyList<(object? Parent, IReadOnlyList<object> Rows)> groups,
        IdentityMap<object> membership)
    {
        internal IdentityMap<object> Membership => membership;

        internal IReadOnlyList<object> Order(SortedOrder? sort, SortDirection direction) =>
            Flatten((_, rows) => sort is null ? rows : sort.SortSiblings(rows, direction));

        internal IReadOnlyList<object> Order(Work hierarchy, SortedOrder.Work sorting, bool refresh) =>
            Flatten((parent, rows) => sorting.Project(hierarchy.GroupFor(parent, snapshot.Nodes),
                rows, snapshot.Nodes, refresh));

        internal SortedOrder.Work CaptureWork(SortedOrder sort, SortDirection direction, out Work hierarchy)
        {
            hierarchy = new(parents);
            return sort.CaptureWork(groups.Select(group => group.Rows).ToArray(), snapshot.Nodes, direction);
        }

        private IReadOnlyList<object> Flatten(Func<object?, IReadOnlyList<object>, IReadOnlyList<object>> order)
        {
            IReadOnlyList<object>[] ordered = new IReadOnlyList<object>[groups.Count];
            for (int index = 0; index < groups.Count; index++)
            {
                ordered[index] = order(groups[index].Parent, groups[index].Rows);
            }
            List<object> visible = new(membership.Count);
            Stack<IEnumerator<object>> levels = new();
            levels.Push(ordered[0].GetEnumerator());
            try
            {
                while (levels.TryPeek(out var level))
                {
                    if (!level.MoveNext())
                    {
                        levels.Pop().Dispose();
                        continue;
                    }
                    object item = level.Current;
                    visible.Add(item);
                    if (parents.TryGetValue(item, snapshot.Nodes, out int group))
                    {
                        levels.Push(ordered[group].GetEnumerator());
                    }
                }
            }
            finally
            {
                foreach (var level in levels)
                {
                    level.Dispose();
                }
            }
            return visible;
        }
    }

    internal Projection Project(Snapshot snapshot, IIdentity identity)
    {
        _projected!.Clear();
        IdentityMap<int> parents = identity.CreateMap<int>(keepsKeys: true);
        List<(object? Parent, IReadOnlyList<object> Rows)> groups = new() { (null, snapshot.Roots) };
        for (int group = 0; group < groups.Count; group++)
        {
            foreach (object item in groups[group].Rows)
            {
                _projected.TryAdd(item, item, snapshot.Nodes);
                Node node = snapshot.Nodes[item];
                if (node.Children.Count > 0 && isExpanded(item))
                {
                    parents.TryAdd(item, groups.Count, snapshot.Nodes);
                    groups.Add((item, node.Children));
                }
            }
        }
        return new(snapshot, parents, groups, _projected);
    }
}
