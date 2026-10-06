using System.Collections;
using System.Collections.Specialized;

namespace Syno.TableView;

internal sealed class Hierarchy(
    Column column,
    Func<object, IEnumerable> children,
    Func<object, bool> isExpanded,
    Action<object, bool> setExpanded)
{
    internal sealed record Node(object Item, object? Parent, int Depth, IReadOnlyList<object> Children);
    internal sealed record Snapshot(IReadOnlyList<object> Roots, Dictionary<object, Node> Nodes,
        Dictionary<IEnumerable, IReadOnlyList<object>> Sources, bool HasChildren)
    {
        internal bool MatchesStructure(Snapshot other, ItemIdentity identity)
        {
            if (Nodes.Count != other.Nodes.Count || !Roots.SequenceEqual(other.Roots, identity)) return false;

            foreach (var (item, node) in Nodes)
            {
                if (!other.Nodes.TryGetValue(item, out var previous)) return false;
                if (!identity.Equals(node.Parent, previous.Parent)) return false;
                if (!node.Children.SequenceEqual(previous.Children, identity)) return false;
            }
            return true;
        }
    }

    private readonly HashSet<IEnumerable> _invalid = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<INotifyCollectionChanged> _subscriptions = new(ReferenceEqualityComparer.Instance);
    private bool _recapture;
    private bool _suspended;

    internal Column Column => column;
    internal Snapshot? Captured { get; private set; }
    internal event EventHandler? Changed;

    internal bool IsExpanded(object item) => isExpanded(item);
    internal void SetExpanded(object item, bool value) => setExpanded(item, value);
    internal Node? Find(object item) => Captured?.Nodes.GetValueOrDefault(item);
    internal bool HasChildren => Captured?.HasChildren == true;

    internal void Invalidate() => _recapture = true;

    internal Snapshot Capture(IReadOnlyList<object> roots, ItemIdentity identity, Type? rowType)
    {
        Dictionary<object, Node> nodes = new(identity);
        Dictionary<IEnumerable, IReadOnlyList<object>> sources = new(ReferenceEqualityComparer.Instance);
        Stack<(object Item, object? Parent, int Depth)> pending = new();
        bool hasChildren = false;
        for (int i = roots.Count - 1; i >= 0; i--) pending.Push((roots[i], null, 0));
        while (pending.TryPop(out var next))
        {
            if (rowType is not null && !rowType.IsInstanceOfType(next.Item))
                throw new InvalidOperationException($"The source contains a row outside {rowType}.");
            if (identity.KeySelector is not null && identity.KeySelector(next.Item) is null)
                throw new InvalidOperationException("The schema key selector returned null.");
            if (nodes.ContainsKey(next.Item))
                throw new InvalidOperationException("The hierarchy contains duplicate row identities or a cycle.");
            IEnumerable source = children(next.Item)
                ?? throw new InvalidOperationException("The children selector returned null.");
            if (!sources.TryGetValue(source, out var items))
            {
                if (!_recapture && !_invalid.Contains(source) &&
                    Captured is not null && Captured.Sources.TryGetValue(source, out var cached))
                {
                    items = cached;
                }
                else
                {
                    List<object> captured = new();
                    foreach (object? item in source)
                    {
                        if (item is null) throw new InvalidOperationException("The hierarchy contains a null row.");
                        captured.Add(item);
                    }
                    items = captured;
                }
                sources.Add(source, items);
            }
            nodes.Add(next.Item, new(next.Item, next.Parent, next.Depth, items));
            hasChildren |= items.Count > 0;
            for (int i = items.Count - 1; i >= 0; i--)
                pending.Push((items[i], next.Item, next.Depth + 1));
        }
        return new(roots, nodes, sources, hasChildren);
    }

    internal void Accept(Snapshot snapshot)
    {
        Captured = snapshot;
        _invalid.Clear();
        _recapture = false;
        Subscribe();
    }

    internal void Suspend()
    {
        _suspended = true;
        foreach (var source in _subscriptions) source.CollectionChanged -= OnChanged;
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
        if (_suspended || Captured is null) return;
        var current = Captured.Sources.Keys.OfType<INotifyCollectionChanged>()
            .ToHashSet<INotifyCollectionChanged>(ReferenceEqualityComparer.Instance);
        foreach (var source in _subscriptions.Where(source => !current.Contains(source)).ToArray())
        {
            source.CollectionChanged -= OnChanged;
            _subscriptions.Remove(source);
        }
        foreach (var source in current)
            if (_subscriptions.Add(source)) source.CollectionChanged += OnChanged;
    }

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (sender is IEnumerable source) _invalid.Add(source);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal IReadOnlyList<object> Project(Snapshot snapshot,
        IComparer<object>? comparer, SortDirection direction)
    {
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
                var node = snapshot.Nodes[item];
                if (node.Children.Count > 0 && isExpanded(item))
                    levels.Push(Order(node.Children).GetEnumerator());
            }
        }
        finally
        {
            foreach (var level in levels) level.Dispose();
        }
        return visible;

        IEnumerable<object> Order(IReadOnlyList<object> siblings)
        {
            if (comparer is null) return siblings;
            if (direction == SortDirection.Ascending) return siblings.OrderBy(item => item, comparer);
            return siblings.OrderByDescending(item => item, comparer);
        }
    }
}
