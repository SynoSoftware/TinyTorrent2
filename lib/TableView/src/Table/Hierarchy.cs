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

    private Hierarchy.Snapshot CaptureHierarchy(IReadOnlyList<object> roots)
    {
        var hierarchy = _hierarchy!;
        _preparedHierarchy = hierarchy.NeedsCapture ? hierarchy.Capture(roots, _identity, _rowType) : hierarchy.Captured!;
        return _preparedHierarchy;
    }

    internal bool IsHierarchyColumn(Column column) => ReferenceEquals(_hierarchy?.Column, column);

    internal void Expand(object item, bool expanded)
    {
        if (
            _hierarchy is null
            || ResolveItem(item) is not { } current
            || _hierarchy.Find(current) is not { } node
            || !_hierarchy.IsExpandable(node)
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
        if (_positions.ContainsKey(KeyOf(item)) || _hierarchy.Find(item) is not { } node)
            return null;
        while (node.Parent is { } parent && _hierarchy.Find(parent) is { } ancestor)
        {
            if (_positions.ContainsKey(KeyOf(parent)) && IsInteractive(parent))
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
        if (_hierarchy.IsExpandable(node) && _hierarchy.IsExpanded(item) != open)
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
                if (_positions is null || !_positions.TryGetValue(KeyOf(child), out var position))
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

    internal int HierarchyPosition(object item) => _positions?.GetValueOrDefault(KeyOf(item)).Sibling ?? 0;

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
                && RowItem(row) is { } item
            )
                return CollapsedAncestor(item) is { } ancestor ? (ancestor, state) : default;
        }
        return default;
    }
}
