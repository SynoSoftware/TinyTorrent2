using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Synapse;

/// <summary>
/// Every operation on the resolved column layout: section 10's widths and fits, section 11's move,
/// and the visibility change section 12's menu makes. Each one has a matching predicate, so a menu
/// item is enabled by the same rule that decides whether the operation changes anything.
/// </summary>
public sealed partial class TableView
{
    private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);

    /// <summary>
    /// Fit one column to the header and cells the current visual layout has already realized.
    /// A hidden or non-resizable column is a no-op; an ID no column declares is an argument error.
    /// </summary>
    public void AutoFitColumn(string columnId)
    {
        ArgumentException.ThrowIfNullOrEmpty(columnId);
        AutoFitColumn(RequireColumn(columnId));
    }

    /// <summary>
    /// The same fit for a column the table has already resolved, which is what the header's own
    /// separator and menu have in hand. They do not go through the ID: a column need not have one.
    /// </summary>
    internal void AutoFitColumn(ResolvedColumn column)
    {
        if (FitColumn(column))
        {
            RaiseLayoutChanged(TableLayoutChangeKind.AutoFit);
        }
    }

    /// <summary>
    /// Fit each visible resizable column independently and report the whole operation once.
    /// </summary>
    public void AutoFitVisibleColumns()
    {
        bool changed = false;

        // Each fit republishes the geometry, so the visible set is taken before the first one.
        foreach (VisibleColumn visible in Geometry.VisibleColumns.ToArray())
        {
            changed |= FitColumn(visible.Column);
        }

        if (changed)
        {
            RaiseLayoutChanged(TableLayoutChangeKind.AutoFit);
        }
    }

    /// <summary>
    /// Sections 5 and 10: return to the captured baseline. Every width and visibility override is
    /// discarded, the effective order returns to the declared one, and because that baseline
    /// carries no sort criterion the local sort goes with it. It fits nothing to the current data.
    /// </summary>
    public void ResetColumnLayout()
    {
        bool changed = !Geometry.Order.SequenceEqual(_resolved);

        foreach (ResolvedColumn column in _resolved)
        {
            changed |= column.WidthOverride is not null || column.VisibilityOverride is not null;
            column.WidthOverride = null;
            column.VisibilityOverride = null;
        }

        bool sorted = ClearSort();
        if (!changed && !sorted)
        {
            return;
        }

        // SetOrder republishes the geometry, which re-realizes each header cell and so clears the
        // sort glyph of the column that had one.
        Geometry.SetOrder(_resolved);
        UpdateHorizontalRange();

        if (sorted)
        {
            RebuildView();
        }

        RaiseLayoutChanged(TableLayoutChangeKind.Reset);
    }

    /// <summary>Section 12: a fit is offered for a visible column the host allows to be resized.</summary>
    internal bool CanFitColumn(ResolvedColumn column) => column.IsVisible && column.Column.CanResize;

    internal bool CanFitVisibleColumns =>
        Geometry.VisibleColumns.Any(visible => CanFitColumn(visible.Column));

    /// <summary>Give this column a width override and republish the geometry.</summary>
    /// <returns>False when the clamped width is the width already resolved.</returns>
    internal bool SetColumnWidth(ResolvedColumn column, double width)
    {
        double clamped = ResolvedColumn.Clamp(width, column.Column.MinWidth, column.Column.MaxWidth);
        if (clamped == column.Width)
        {
            return false;
        }

        column.WidthOverride = clamped;
        Geometry.Rebuild();
        return true;
    }

    /// <summary>
    /// Section 12: a column can be hidden while the host allows it and another visible column would
    /// remain. It is also what disables the last visible column's toggle, so the menu cannot reach
    /// a state with no visible column.
    /// </summary>
    internal bool CanHideColumn(ResolvedColumn column) =>
        column.IsVisible && column.Column.CanHide && Geometry.VisibleColumns.Count > 1;

    internal void SetColumnVisibility(ResolvedColumn column, bool visible)
    {
        if (visible == column.IsVisible || (!visible && !CanHideColumn(column)))
        {
            return;
        }

        // Section 10: a column keeps its resolved width across a hide and a show.
        column.VisibilityOverride = visible;

        // Section 9: the header is the only place the sort shows and the only place it is
        // changed, so hiding the sorted column takes the sort with it. Left in force, the view
        // would stay ordered by a column no longer on screen, with no chevron to say so and, under
        // section 16, no row drag either, and nothing on screen to explain why.
        bool sortCleared = !visible && ReferenceEquals(_sortColumn, column) && ClearSort();

        Geometry.Rebuild();
        if (sortCleared)
        {
            RebuildView();
        }

        RaiseLayoutChanged(TableLayoutChangeKind.Visibility);
    }

    /// <summary>
    /// Section 11's one placement rule, shared by the header drag and the menu's move commands.
    /// <paramref name="boundary"/> is counted among the visible columns with this one taken out:
    /// 0 is before the first, the count is after the last, and the column's own visible index puts
    /// it back where it was. Every other column, hidden ones included, keeps its relative order.
    /// </summary>
    /// <returns>False when the placement leaves the order as it is.</returns>
    internal bool MoveColumnTo(ResolvedColumn column, int boundary, FocusState? focus)
    {
        int index = Geometry.IndexOfVisible(column);
        if (index < 0)
        {
            return false;
        }

        boundary = Math.Clamp(boundary, 0, Geometry.VisibleColumns.Count - 1);
        if (boundary == index)
        {
            return false;
        }

        List<ResolvedColumn> order = new(Geometry.Order);
        order.Remove(column);
        order.Insert(InsertionPoint(order, boundary), column);

        // Section 19: the move has to read as movement. Where every cell is rendered now is the
        // only thing the animation needs; everything after this is an ordinary layout change.
        Dictionary<TableColumn, double> before = TableColumnMotion.CaptureOffsets(Geometry);

        Geometry.SetOrder(order);
        TableColumnMotion.SlideFrom(this, before);
        FocusHeaderOf(column, focus);
        RaiseLayoutChanged(TableLayoutChangeKind.ColumnMove);
        return true;
    }

    /// <summary>
    /// Section 12's Move left and Move right, by the same rule as a drag. They move no focus: the
    /// menu they are invoked from stays open across the move and holds focus while it is open, and
    /// it returns focus to the header it was opened on when it closes, with the state the request
    /// arrived in. Asking for focus here put a focus visual on a header behind an open menu, and
    /// put it there for a mouse click, which draws a focus visual nowhere else in the table.
    /// </summary>
    internal bool MoveColumnBy(ResolvedColumn column, int step) =>
        MoveColumnTo(column, Geometry.IndexOfVisible(column) + step, focus: null);

    /// <summary>A move is offered while there is a neighbouring visible place to move into.</summary>
    internal bool CanMoveColumnBy(ResolvedColumn column, int step)
    {
        int index = Geometry.IndexOfVisible(column);
        return index >= 0 && Math.Clamp(index + step, 0, Geometry.VisibleColumns.Count - 1) != index;
    }

    /// <summary>
    /// Section 11: the moved header keeps focus, with the state the move arrived in. Null is a move
    /// that leaves focus where it is, which is what a menu command wants: the menu holds focus for
    /// as long as it is open and restores it itself.
    /// </summary>
    /// <remarks>
    /// This asked for Programmatic on the belief that it draws no ring. It does: the platform draws
    /// its focus visual for Keyboard and for Programmatic, and suppresses it only for Pointer. So a
    /// header drag left a focus ring behind on the moved header.
    /// </remarks>
    private void FocusHeaderOf(ResolvedColumn column, FocusState? focus)
    {
        if (focus is null)
        {
            return;
        }

        int index = Geometry.IndexOfVisible(column);
        if (_headerStrip?.Panel is Panel header && index < header.Children.Count
            && header.Children[index] is Control cell)
        {
            cell.Focus(focus.Value);
        }
    }

    /// <summary>
    /// Where a visible boundary sits in the full order. It is anchored to the neighbouring visible
    /// column, so a hidden column beside the boundary is not stepped over and a drop on the moving
    /// column's own boundary stays a no-op.
    /// </summary>
    private static int InsertionPoint(List<ResolvedColumn> order, int boundary)
    {
        int visible = 0;
        int afterLastVisible = 0;

        for (int i = 0; i < order.Count; i++)
        {
            if (!order[i].IsVisible)
            {
                continue;
            }

            if (visible == boundary)
            {
                return i;
            }

            visible++;
            afterLastVisible = i + 1;
        }

        return afterLastVisible;
    }

    internal void RaiseLayoutChanged(TableLayoutChangeKind kind)
    {
        // Widths, visibility and order all move where the columns end, which is what decides
        // whether the strip has trailing space to offer its fit command in.
        _headerStrip?.UpdateFitAllVisibility();
        LayoutChanged?.Invoke(this, kind);
    }

    private ResolvedColumn RequireColumn(string columnId)
    {
        if (Geometry.Find(columnId) is ResolvedColumn column)
        {
            return column;
        }

        throw new ArgumentException(
            _schemaCaptured
                ? $"No column has the Id '{columnId}'."
                : "The column schema is captured at the first Loaded, so no column resolves yet.",
            nameof(columnId));
    }

    /// <summary>
    /// Section 10's bounded fit: the widest realized header cell or row cell for this column,
    /// clamped to the column's limits. Nothing realized means nothing to fit.
    /// </summary>
    private bool FitColumn(ResolvedColumn column)
    {
        if (!CanFitColumn(column))
        {
            return false;
        }

        int index = Geometry.IndexOfVisible(column);
        double widest = 0;
        bool measured = false;

        foreach (TableCellsPanel panel in RealizedPanels())
        {
            // A realized panel is already synced to the current visible columns: a column-set
            // change reconciles every attached panel's cells synchronously, and TableCellsPanel's
            // measure and arrange throw if the counts ever disagree. This guard is defensive, not
            // load-bearing — index is already within panel.Children.Count here.
            if (index >= panel.Children.Count)
            {
                continue;
            }

            widest = Math.Max(widest, UnboundedWidth(panel.Children[index]));
            measured = true;

            // An unbounded desired size is not the one the panel arranges with, and the panel is
            // the only thing that measures a cell at its resolved width.
            panel.InvalidateMeasure();
        }

        return measured && SetColumnWidth(column, widest);
    }

    /// <summary>
    /// Section 10: the fit includes the sort glyph, so a fitted column still shows its whole header
    /// once it becomes the sorted one. Only a header cell has a glyph to include.
    /// </summary>
    private static double UnboundedWidth(UIElement cell)
    {
        if (cell is TableHeaderCell header)
        {
            return header.MeasureWithSortGlyph(Unbounded).Width;
        }

        cell.Measure(Unbounded);
        return cell.DesiredSize.Width;
    }

    /// <summary>
    /// The panels a fit may measure: the header strip's, and one for each row container the list
    /// has already realized. Section 10 forbids realizing anything here to widen the search.
    /// </summary>
    /// <remarks>
    /// The panel's cache range, not its <c>Children</c>: a container left over from before a scroll
    /// stays in <c>Children</c>, still holding the content of the row it last showed. Measuring one
    /// would fit the column to a row the current visual layout does not contain.
    /// </remarks>
    internal IEnumerable<TableCellsPanel> RealizedPanels()
    {
        if (_headerStrip?.Panel is TableCellsPanel header)
        {
            yield return header;
        }

        if (_itemsView?.ItemsPanelRoot is not ItemsStackPanel rows)
        {
            yield break;
        }

        int first = rows.FirstCacheIndex;
        int last = rows.LastCacheIndex;
        if (first < 0 || last < first)
        {
            yield break;
        }

        for (int index = first; index <= last; index++)
        {
            if (_itemsView.ContainerFromIndex(index) is DependencyObject container
                && FindDescendant<TableCellsPanel>(container) is TableCellsPanel panel)
            {
                yield return panel;
            }
        }
    }
}
