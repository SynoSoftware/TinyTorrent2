using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Syno.TableView.Body;

internal sealed partial class HierarchyCell : Grid
{
    private readonly Table _table;
    private readonly ContentPresenter _content;
    private SubtleButton? _expander;
    private FontIcon? _glyph;
    private readonly RectangleGeometry _clip = new();

    internal HierarchyCell(Table table, ContentPresenter content)
    {
        _table = table;
        _content = content;
        Clip = _clip;
        Padding = content.Padding;
        content.Padding = default;
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        SetColumn(content, 1);
        Children.Add(content);
        // Unloaded is ignored while the cell is still in the tree, so Loaded
        // can repeat; see Table's OnUnloaded.
        Loaded += (_, _) =>
        {
            _table.RowVisualsChanged -= OnRowsChanged;
            _table.RowVisualsChanged += OnRowsChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (!IsLoaded)
            {
                _table.RowVisualsChanged -= OnRowsChanged;
            }
        };
        RegisterPropertyChangedCallback(FlowDirectionProperty, (_, _) => Refresh());
        Refresh();
    }

    internal object? Item
    {
        set
        {
            _content.Content = value;
            Refresh();
        }
    }

    private void OnRowsChanged(object? sender, EventArgs args) => Refresh();

    protected override Size ArrangeOverride(Size finalSize)
    {
        _clip.Rect = new(0, 0, finalSize.Width, finalSize.Height);
        return base.ArrangeOverride(finalSize);
    }

    private void Refresh()
    {
        if (
            _table.Hierarchy is not { } hierarchy
            || _content.Content is not { } item
            || hierarchy.Find(item) is not { } node
        )
            return;
        // WinUI TreeView uses a 16-pixel depth step and a 12-pixel glyph between 14-pixel margins.
        double indent = node.Depth * 16;
        bool expandable = hierarchy.IsExpandable(node);
        ColumnDefinitions[0].Width = new(indent + (hierarchy.HasChildren ? 40 : 0));
        if (expandable && _expander is null)
        {
            _glyph = new() { FontFamily = Segoe.Font, FontSize = 12 };
            _expander = new()
            {
                Content = _glyph,
                Width = 40,
                IsTabStop = false,
                AllowFocusOnInteraction = false,
            };
            AutomationProperties.SetAccessibilityView(_expander,
                Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Table.SetIsRowGestureEnabled(_expander, false);
            _expander.Click += (_, _) =>
            {
                if (_content.Content is { } current && _table.Hierarchy is { } currentHierarchy)
                {
                    _table.Expand(current, !currentHierarchy.IsExpanded(current));
                }
            };
            Children.Insert(0, _expander);
        }
        if (_expander is null)
        {
            return;
        }
        _expander.Margin =
            FlowDirection == FlowDirection.LeftToRight
                ? new(indent, 0, 0, 0)
                : new(0, 0, indent, 0);
        _expander.Visibility = hierarchy.HasChildren ? Visibility.Visible : Visibility.Collapsed;
        _expander.Opacity = expandable ? 1 : 0;
        _expander.IsHitTestVisible = expandable;
        _expander.IsEnabled = _table.IsInteractive(item);
        _glyph!.Glyph =
            hierarchy.IsExpanded(item) ? Segoe.ChevronDown
            : FlowDirection == FlowDirection.LeftToRight ? Segoe.ChevronRight
            : Segoe.ChevronLeft;
    }
}
