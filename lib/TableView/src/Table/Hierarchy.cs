using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Syno.TableView;

public sealed partial class Table
{
    private Hierarchy? _hierarchy;
    private Hierarchy.Preparation? _preparedHierarchy;

    internal Hierarchy? Hierarchy => _hierarchy;

    internal void SetHierarchy(Hierarchy hierarchy)
    {
        if (_hierarchy is not null)
            throw ConfigurationError("A table has one hierarchy.");
        _hierarchy = hierarchy;
        hierarchy.Changed += (_, _) =>
        {
            RowVisualsChanged?.Invoke(this, EventArgs.Empty);
            _surface?.RefreshHierarchy();
        };
        hierarchy.CollectionChanged += (_, change) =>
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                throw ConfigurationError("Hierarchy updates must use the table's UI thread.");
            }
            hierarchy.Invalidate(change.Source);
            Body.Source.Capture capture = _source.Current;
            bool recapture = !capture.Validated || hierarchy.Captured?.AssignmentVersion != capture.AssignmentVersion;
            _preparedHierarchy = recapture
                ? hierarchy.Capture(capture.Rows, _identity, ValidateRows, capture.AssignmentVersion)
                : hierarchy.Prepare(change, _identity, ValidateRows, _view.Positions);
            if (recapture || _preparedHierarchy.RequiresProjection)
            {
                RebuildView(capture, change: new Change.Collection(capture, change.Notification, change.HasPredecessor));
                return;
            }
            bool changed;
            try
            {
                _preparedHierarchy.Accept();
                changed = hierarchy.UpdateChanged(_preparedHierarchy, _view.Positions);
            }
            finally
            {
                _preparedHierarchy.Complete();
                _preparedHierarchy = null;
            }
            if (changed)
            {
                hierarchy.PublishChanged();
            }
        };
    }

    private Hierarchy.Projection PrepareHierarchy(Body.Source.Capture capture)
    {
        var hierarchy = _hierarchy!;
        _preparedHierarchy ??=
            !capture.Validated || hierarchy.NeedsCapture(capture.AssignmentVersion)
                ? hierarchy.Capture(capture.Rows, _identity, ValidateRows, capture.AssignmentVersion)
                : hierarchy.Reuse();
        if (_preparedHierarchy.StructureChanged)
        {
            _orderSettledAt = DateTimeOffset.MinValue;
        }
        return hierarchy.Project(_preparedHierarchy.Snapshot, _identity);
    }

    private void AcceptHierarchy(IReadOnlyList<object> order, bool moved)
    {
        if (_hierarchy is null)
            return;
        _preparedHierarchy?.Accept();
        _hierarchy.UpdatePresentation(order, moved);
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

    private object? CollapsedAncestor(object? item, PositionIndex? positions = null)
    {
        if (item is null || _hierarchy is null)
            return null;
        positions ??= _view.Positions;
        if (positions.IndexOf(item) >= 0 || _hierarchy.Find(item) is not { } node)
            return null;
        while (node.Parent is { } parent && _hierarchy.Find(parent) is { } ancestor)
        {
            if (positions.IndexOf(parent) >= 0 && IsInteractive(parent))
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
                int index = IndexInView(child);
                if (index < 0)
                    continue;
                if (!IsInteractive(child) || index >= firstIndex)
                    continue;
                target = child;
                firstIndex = index;
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

    internal int HierarchyPosition(object item) => _hierarchy?.Find(item)?.Position ?? 0;

    internal int HierarchyCount(object item)
    {
        if (_hierarchy?.Find(item) is not { } node)
            return 0;
        if (node.Parent is { } parent)
            return _hierarchy.Find(parent)!.Children.Count;
        return _hierarchy.Captured!.Roots.Count;
    }

    private (object? Item, FocusState State) CollapsingFocus(PositionIndex positions)
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
                return CollapsedAncestor(item, positions) is { } ancestor ? (ancestor, state) : default;
        }
        return default;
    }
}
