using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Syno.TableView;

public sealed partial class Table
{
    private Hierarchy? _hierarchy;
    private Hierarchy.Snapshot? _preparedHierarchy;
    private Dictionary<object, (int Index, int Sibling)>? _positions;

    internal Hierarchy? Hierarchy => _hierarchy;

    internal void SetHierarchy(Hierarchy hierarchy)
    {
        if (_hierarchy is not null)
            throw ConfigurationError("A table has one hierarchy.");
        _hierarchy = hierarchy;
        _source.Hierarchy = hierarchy;
        hierarchy.Changed += (_, _) =>
        {
            if (!DispatcherQueue.HasThreadAccess)
                throw ConfigurationError("Hierarchy updates must use the table's UI thread.");
            RebuildView(_source.Snapshot);
        };
    }

    private IReadOnlyList<object> PrepareHierarchy(IReadOnlyList<object> roots, bool capture)
    {
        var hierarchy = _hierarchy!;
        _preparedHierarchy =
            capture || hierarchy.Captured is null
                ? hierarchy.Capture(roots, _identity, _rowType)
                : hierarchy.Captured;
        if (!ReferenceEquals(_preparedHierarchy, hierarchy.Captured))
        {
            if (
                hierarchy.Captured is null
                || !_preparedHierarchy.MatchesStructure(hierarchy.Captured, _identity)
            )
                _orderSettledAt = DateTimeOffset.MinValue;
        }
        return hierarchy.Project(_preparedHierarchy, null, default);
    }

    private void AcceptHierarchy(IReadOnlyList<object> order)
    {
        if (_hierarchy is null)
            return;
        var snapshot = _preparedHierarchy ?? _hierarchy.Captured;
        if (snapshot is null)
            return;
        if (!ReferenceEquals(_hierarchy.Captured, snapshot))
            _hierarchy.Accept(snapshot);
        _positions = new(_identity);
        Dictionary<object, int> counts = new(_identity);
        int roots = 0;
        for (int i = 0; i < order.Count; i++)
        {
            object item = order[i];
            object? parent = snapshot.Nodes[item].Parent;
            int position = parent is null ? ++roots : counts.GetValueOrDefault(parent) + 1;
            if (parent is not null)
                counts[parent] = position;
            _positions.Add(item, (i, position));
        }
        _preparedHierarchy = null;
    }

    internal bool IsHierarchyColumn(Column column) => ReferenceEquals(_hierarchy?.Column, column);

    internal void Expand(object item, bool expanded)
    {
        if (
            _hierarchy is null
            || ResolveItem(item) is not { } current
            || _hierarchy.Find(current) is not { Children.Count: > 0 }
            || !IsInteractive(current)
            || _hierarchy.IsExpanded(current) == expanded
        )
            return;
        _hierarchy.SetExpanded(current, expanded);
        RefreshView();
    }

    private object? CollapsedAncestor(object? item)
    {
        if (item is null || _hierarchy is null || _positions is null)
            return null;
        if (_positions.ContainsKey(item) || _hierarchy.Find(item) is not { } node)
            return null;
        while (node.Parent is { } parent && _hierarchy.Find(parent) is { } ancestor)
        {
            if (_positions.ContainsKey(parent) && IsInteractive(parent))
                return ancestor.Item;
            node = ancestor;
        }
        return null;
    }

    private bool NavigateHierarchy(VirtualKey key, bool extend, bool ctrl)
    {
        if (
            _hierarchy is null
            || _selection.Current is not { } item
            || _hierarchy.Find(item) is not { } node
        )
            return false;
        bool open =
            key
            == (FlowDirection == FlowDirection.LeftToRight ? VirtualKey.Right : VirtualKey.Left);
        if (node.Children.Count > 0 && _hierarchy.IsExpanded(item) != open)
        {
            Expand(item, open);
            return true;
        }
        object? target = null;
        if (open)
        {
            int firstIndex = int.MaxValue;
            foreach (object child in node.Children)
            {
                if (_positions is null || !_positions.TryGetValue(child, out var position))
                    continue;
                if (!IsInteractive(child) || position.Index >= firstIndex)
                    continue;
                target = child;
                firstIndex = position.Index;
            }
        }
        else
        {
            while (node.Parent is { } parent && _hierarchy.Find(parent) is { } ancestor)
            {
                if (IsInteractive(parent))
                {
                    target = ancestor.Item;
                    break;
                }
                node = ancestor;
            }
        }
        if (target is not null)
            return MoveCurrentTo(target, extend, ctrl);
        return true;
    }

    internal int HierarchyPosition(object item) => _positions?.GetValueOrDefault(item).Sibling ?? 0;

    internal int HierarchyCount(object item)
    {
        if (_hierarchy?.Find(item) is not { } node)
            return 0;
        if (node.Parent is { } parent)
            return _hierarchy.Find(parent)!.Children.Count;
        return _hierarchy.Captured!.Roots.Count;
    }

    private (object? Item, FocusState State) CollapsingFocus()
    {
        if (_hierarchy is null || XamlRoot is null)
            return default;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        FocusState state = focused is Control control ? control.FocusState : FocusState.Unfocused;
        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, this))
                break;
            if (
                node is ListViewItem row
                && IsInsideRows(row)
                && _surface?.ItemFromContainer(row) is { } item
            )
                return CollapsedAncestor(item) is { } ancestor ? (ancestor, state) : default;
        }
        return default;
    }
}
