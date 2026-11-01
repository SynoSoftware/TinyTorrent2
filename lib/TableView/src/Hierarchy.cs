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
        IReadOnlyList<object> Children
    )
    {
        internal int Position { get; set; }
        internal bool Expandable { get; set; }
        internal bool Expanded { get; set; }
    }

    internal sealed record Snapshot(
        IReadOnlyList<object> Roots,
        IdentityMap<Node> Nodes,
        Dictionary<IEnumerable, IReadOnlyList<object>> Sources,
        long AssignmentVersion
    )
    {
        internal bool MatchesStructure(Snapshot other)
        {
            if (Nodes.Count != other.Nodes.Count || !SameSequence(Roots, other.Roots))
                return false;

            foreach (var (item, node) in Nodes)
            {
                if (!other.Nodes.TryGetValue(item, Nodes, out var previous))
                    return false;
                if ((node.Parent is null) != (previous.Parent is null))
                    return false;
                if (previous.Parent is not null
                    && (!Nodes.TryGetValue(previous.Parent, other.Nodes, out Node parent)
                        || !ReferenceEquals(parent.Item, node.Parent)))
                    return false;
                if (!SameSequence(node.Children, previous.Children))
                    return false;
            }
            return true;

            bool SameSequence(IReadOnlyList<object> next, IReadOnlyList<object> before)
            {
                if (next.Count != before.Count)
                    return false;
                for (int index = 0; index < next.Count; index++)
                {
                    if (!Nodes.TryGetValue(before[index], other.Nodes, out Node current)
                        || !ReferenceEquals(current.Item, next[index]))
                        return false;
                }
                return true;
            }
        }
    }

    private readonly HashSet<IEnumerable> _invalid = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<INotifyCollectionChanged> _subscriptions = new(
        ReferenceEqualityComparer.Instance
    );
    private bool _suspended;
    private IdentityMap<int>? _counts;
    private IdentityMap<object>? _projected;
    internal long Version { get; private set; }

    internal Column Column => column;
    internal Snapshot? Captured { get; private set; }
    internal event EventHandler? Changed;

    internal bool IsExpanded(object item) => isExpanded(item);

    internal void SetExpanded(object item, bool value) => setExpanded(item, value);

    internal Node? Find(object item) => Captured?.Nodes.GetValueOrDefault(item);

    /// <summary>Whether any shown row is expandable, so the expander slot is reserved.</summary>
    internal bool HasChildren { get; set; }

    /// <summary>
    /// Whether the person can expand the row: it has children, or the host has some it has
    /// not supplied yet. Supplied children take precedence over the host's flag.
    /// </summary>
    internal bool IsExpandable(Node node) =>
        node.Children.Count > 0 || hasUnrealizedChildren?.Invoke(node.Item) == true;

    internal bool NeedsCapture(long assignmentVersion) =>
        Captured is null || Captured.AssignmentVersion != assignmentVersion || _invalid.Count > 0;

    internal Snapshot Capture(IReadOnlyList<object> roots, IIdentity identity, Action<object> validate, long assignmentVersion)
    {
        IdentityMap<Node> nodes = identity.CreateMap<Node>(keepsKeys: true);
        _counts ??= identity.CreateMap<int>();
        _projected ??= identity.CreateMap<object>(keepsKeys: true);
        Dictionary<IEnumerable, IReadOnlyList<object>> sources = new(
            ReferenceEqualityComparer.Instance
        );
        Stack<(object Item, object? Parent, int Depth)> pending = new();
        for (int i = roots.Count - 1; i >= 0; i--)
            pending.Push((roots[i], null, 0));
        while (pending.TryPop(out var next))
        {
            validate(next.Item);
            if (!nodes.TryAdd(next.Item, new(next.Item, next.Parent, next.Depth, Array.Empty<object>())))
                throw new InvalidOperationException(
                    "The hierarchy contains duplicate row identities or a cycle."
                );
            IEnumerable source =
                children(next.Item)
                ?? throw new InvalidOperationException("The children selector returned null.");
            if (!sources.TryGetValue(source, out var items))
            {
                if (
                    Captured?.AssignmentVersion == assignmentVersion
                    && !_invalid.Contains(source)
                    && Captured is not null
                    && Captured.Sources.TryGetValue(source, out var cached)
                )
                {
                    items = cached;
                }
                else
                {
                    List<object> captured = new();
                    foreach (object? item in source)
                    {
                        captured.Add(item!);
                    }
                    items = captured;
                }
                sources.Add(source, items);
            }
            nodes.Set(next.Item, new(next.Item, next.Parent, next.Depth, items));
            for (int i = items.Count - 1; i >= 0; i--)
                pending.Push((items[i], next.Item, next.Depth + 1));
        }
        // A copy, because the next capture compares its roots with these, and the source list
        // they come from is read in place.
        return new(roots.ToArray(), nodes, sources, assignmentVersion);
    }

    internal void Accept(Snapshot snapshot)
    {
        if (Captured is { } previous)
        {
            foreach (var entry in snapshot.Nodes)
            {
                if (previous.Nodes.TryGetValue(entry.Key, snapshot.Nodes, out Node before))
                    entry.Value.Position = before.Position;
            }
        }
        Captured = snapshot;
        Version++;
        _invalid.Clear();
        Subscribe();
    }

    internal void UpdatePresentation(IReadOnlyList<object> order, bool moved)
    {
        if (Captured is not { } snapshot)
            return;
        if (moved)
            _counts!.Clear();
        int roots = 0;
        bool expandable = false;
        foreach (object item in order)
        {
            Node node = snapshot.Nodes[item];
            bool hasChildren = IsExpandable(node);
            bool expanded = IsExpanded(item);
            if (node.Expandable != hasChildren || node.Expanded != expanded)
                Version++;
            node.Expandable = hasChildren;
            node.Expanded = expanded;
            expandable |= hasChildren;
            if (!moved)
                continue;
            if (node.Parent is not { } parent)
                node.Position = ++roots;
            else
            {
                _counts!.TryGetValue(parent, snapshot.Nodes, out int count);
                node.Position = count + 1;
                _counts.Set(parent, node.Position, snapshot.Nodes);
            }
        }
        HasChildren = expandable;
    }

    internal void Suspend()
    {
        _suspended = true;
        foreach (var source in _subscriptions)
            source.CollectionChanged -= OnChanged;
        _subscriptions.Clear();
    }

    internal void Resume()
    {
        _suspended = false;
        if (Captured is not null)
            foreach (var source in Captured.Sources.Keys.OfType<INotifyCollectionChanged>())
                _invalid.Add((IEnumerable)source);
    }

    private void Subscribe()
    {
        if (_suspended || Captured is null)
            return;
        var current = Captured
            .Sources.Keys.OfType<INotifyCollectionChanged>()
            .ToHashSet<INotifyCollectionChanged>(ReferenceEqualityComparer.Instance);
        foreach (var source in _subscriptions.Where(source => !current.Contains(source)).ToArray())
        {
            source.CollectionChanged -= OnChanged;
            _subscriptions.Remove(source);
        }
        foreach (var source in current)
            if (_subscriptions.Add(source))
                source.CollectionChanged += OnChanged;
    }

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (sender is IEnumerable source)
            _invalid.Add(source);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal IReadOnlyList<object> Project(
        Snapshot snapshot,
        SortedOrder? sort,
        SortDirection direction
    )
    {
        _projected!.Clear();
        List<object> visible = new();
        Stack<IEnumerator<object>> levels = new();
        levels.Push(Order(snapshot.Roots).GetEnumerator());
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
                _projected.TryAdd(item, item, snapshot.Nodes);
                var node = snapshot.Nodes[item];
                if (node.Children.Count > 0 && isExpanded(item))
                    levels.Push(Order(node.Children).GetEnumerator());
            }
        }
        finally
        {
            foreach (var level in levels)
                level.Dispose();
        }
        return visible;

        IEnumerable<object> Order(IReadOnlyList<object> siblings)
        {
            if (sort is null)
                return siblings;
            return sort.SortSiblings(siblings, direction);
        }
    }

    internal IdentityMap<object> ProjectedMembership => _projected!;
}
