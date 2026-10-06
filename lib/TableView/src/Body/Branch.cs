using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Syno.TableView.Body;

internal sealed partial class Branch : Grid
{
    private readonly Table _table;
    private readonly ContentPresenter _content;
    private readonly Button _expander;
    private readonly FontIcon _glyph;
    private readonly RectangleGeometry _clip = new();

    internal Branch(Table table, ContentPresenter content)
    {
        _table = table;
        _content = content;
        Clip = _clip;
        Padding = content.Padding;
        content.Padding = default;
        ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        _glyph = new() { FontFamily = Lucide.Font, FontSize = 12 };
        _expander = new()
        {
            Content = _glyph, Width = 40, MinWidth = 0, Padding = default,
            IsTabStop = false, AllowFocusOnInteraction = false,
            VerticalAlignment = VerticalAlignment.Stretch,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"]
        };
        AutomationProperties.SetAccessibilityView(_expander, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Table.SetIsRowGestureEnabled(_expander, false);
        _expander.Click += (_, _) =>
        {
            if (_content.Content is { } item && _table.Hierarchy is { } hierarchy)
                _table.Expand(item, !hierarchy.IsExpanded(item));
        };
        Children.Add(_expander);
        SetColumn(content, 1);
        Children.Add(content);
        Loaded += (_, _) => { _table.RowVisualsChanged += OnRowsChanged; Refresh(); };
        Unloaded += (_, _) => _table.RowVisualsChanged -= OnRowsChanged;
        RegisterPropertyChangedCallback(FlowDirectionProperty, (_, _) => Refresh());
        Refresh();
    }

    internal object? Item
    {
        set { _content.Content = value; Refresh(); }
    }

    private void OnRowsChanged(object? sender, EventArgs args) => Refresh();

    protected override Size ArrangeOverride(Size finalSize)
    {
        _clip.Rect = new(0, 0, finalSize.Width, finalSize.Height);
        return base.ArrangeOverride(finalSize);
    }

    private void Refresh()
    {
        if (_table.Hierarchy is not { } hierarchy || _content.Content is not { } item ||
            hierarchy.Find(item) is not { } node) return;
        // WinUI TreeView uses a 16-pixel depth step and a 12-pixel glyph between 14-pixel margins.
        double indent = node.Depth * 16;
        _expander.Margin = FlowDirection == FlowDirection.LeftToRight
            ? new(indent, 0, 0, 0) : new(0, 0, indent, 0);
        _expander.Visibility = hierarchy.HasChildren ? Visibility.Visible : Visibility.Collapsed;
        _expander.Opacity = node.Children.Count > 0 ? 1 : 0;
        _expander.IsHitTestVisible = node.Children.Count > 0;
        _expander.IsEnabled = _table.CanInteract?.Invoke(item) != false;
        _glyph.Glyph = hierarchy.IsExpanded(item) ? Lucide.ChevronDown :
            FlowDirection == FlowDirection.LeftToRight ? Lucide.ChevronRight : Lucide.ChevronLeft;
    }
}
