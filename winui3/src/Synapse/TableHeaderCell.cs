using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Synapse;

/// <summary>
/// One header cell. It shows the column's own header content when the host supplied any, and a
/// trimmed one-line label from <see cref="TableColumn.DisplayName"/> when it did not.
/// </summary>
public sealed partial class TableHeaderCell : Control
{
    private const string ContentPartName = "PART_Header";
    private const string SortGlyphPartName = "PART_SortGlyph";

    /// <summary>
    /// The sort indicators, from the library's own icon set rather than the platform's. They sit
    /// two inches from the menu these headers open, so drawing them from a second family was the
    /// most visible place the control disagreed with itself about what an icon looks like.
    /// </summary>
    private const string AscendingGlyph = Lucide.ChevronUp;
    private const string DescendingGlyph = Lucide.ChevronDown;

    private ContentPresenter? _presenter;
    private FontIcon? _sortGlyph;
    private TableSortDirection? _sort;

    public TableHeaderCell() => DefaultStyleKey = typeof(TableHeaderCell);

    internal TableColumn? Column { get; private set; }

    /// <summary>
    /// Section 11's "the dragged header remains identifiable". The state carries the platform's own
    /// drag opacity, so no colour and no new resource is involved, and it applies without a
    /// transition because section 19 requires drag feedback to track the input.
    /// </summary>
    internal void SetDragging(bool dragging) =>
        VisualStateManager.GoToState(this, dragging ? "Dragging" : "NotDragging", useTransitions: false);

    /// <summary>
    /// Section 10's fit includes the sort glyph. A column that can sort shows no glyph while
    /// another column is the sorted one, so the measure reveals it and puts it back.
    /// </summary>
    internal Size MeasureWithSortGlyph(Size available)
    {
        FontIcon? revealed = Column?.CanSort == true && _sortGlyph?.Visibility == Visibility.Collapsed
            ? _sortGlyph
            : null;

        if (revealed is not null)
        {
            revealed.Visibility = Visibility.Visible;
        }

        Measure(available);
        Size desired = DesiredSize;

        if (revealed is not null)
        {
            revealed.Visibility = Visibility.Collapsed;
        }

        return desired;
    }

    internal void SetColumn(TableColumn column)
    {
        if (ReferenceEquals(Column, column))
        {
            return;
        }

        Column = column;
        AutomationProperties.SetName(this, column.DisplayName);
        ApplyContent();
    }

    /// <summary>
    /// Section 9: the active header shows its direction with a native theme-aware glyph and states
    /// it through UI Automation. Null is "this column is not the sorted one".
    /// </summary>
    internal void SetSort(TableSortDirection? direction)
    {
        _sort = direction;
        ApplySort();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _presenter = GetTemplateChild(ContentPartName) as ContentPresenter;
        _sortGlyph = GetTemplateChild(SortGlyphPartName) as FontIcon;
        ApplyContent();
        ApplySort();
    }

    private void ApplySort()
    {
        AutomationProperties.SetItemStatus(this, _sort switch
        {
            TableSortDirection.Ascending => TableResources.SortedAscending,
            TableSortDirection.Descending => TableResources.SortedDescending,
            _ => string.Empty,
        });

        if (_sortGlyph is null)
        {
            return;
        }

        _sortGlyph.Glyph = _sort == TableSortDirection.Descending ? DescendingGlyph : AscendingGlyph;
        _sortGlyph.Visibility = _sort is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyContent()
    {
        if (_presenter is null || Column is null)
        {
            return;
        }

        if (Column.Header is not null || Column.HeaderTemplate is not null)
        {
            _presenter.Content = Column.Header;
            _presenter.ContentTemplate = Column.HeaderTemplate;
            return;
        }

        _presenter.ContentTemplate = null;
        _presenter.Content = CreateGeneratedLabel(Column.DisplayName);
    }

    /// <summary>
    /// The generated label keeps a stable one-line treatment, trims when necessary, and exposes
    /// the full DisplayName through its tooltip.
    /// </summary>
    private static TextBlock CreateGeneratedLabel(string displayName)
    {
        TextBlock label = new()
        {
            Text = displayName,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        ToolTipService.SetToolTip(label, displayName);
        return label;
    }
}
