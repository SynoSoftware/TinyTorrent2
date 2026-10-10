using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Syno.TableView;

/// <summary>
/// The column baseline, the cell padding setting, and every operation on the
/// effective column layout: section 10's widths and fits, section 11's move, and the visibility
/// change section 12's menu makes. Each operation has a matching predicate, so a menu item is
/// enabled by the same rule that decides whether the operation changes anything.
/// </summary>
public sealed partial class Table
{
    private static readonly Size Unbounded = new(double.PositiveInfinity, double.PositiveInfinity);

    private bool _filledOnce;

    private static readonly Thickness DefaultCellPadding = new(12, 6, 12, 6);

    public static readonly DependencyProperty CellPaddingProperty = DependencyProperty.Register(
        nameof(CellPadding),
        typeof(Thickness),
        typeof(Table),
        new PropertyMetadata(DefaultCellPadding)
    );

    /// <summary>
    /// The immutable column baseline. Setup-only: the table captures it at its first
    /// <c>Loaded</c> and a structural change afterwards is a configuration error.
    /// </summary>
    public ObservableCollection<Column> Columns { get; } = new();

    /// <summary>
    /// The inset inside every column, applied to the header cell and the row cell alike so that
    /// the two cannot drift apart. Read as each cell is realized, so a host that wants its own
    /// sets it in XAML or in an implicit <c>Style</c>, the way any control default is overridden.
    /// </summary>
    /// <remarks>
    /// The horizontal 12 is the platform's: it is what the resource tree publishes for an item in a
    /// list, corroborated three ways — <c>ListBoxItemPadding</c> 12,9,12,12,
    /// <c>SelectorBarItemPadding</c> 12,10,12,7, and <c>PivotItemMargin</c> 12,0,12,0 — and it is
    /// also what both hosts here had arrived at independently. The vertical 6 has no platform
    /// source: enumerating all 7,484 <c>Thickness</c> resources found no cell padding at all, and
    /// what settles it is that the two hosts had independently written 6 as well.
    /// </remarks>
    public Thickness CellPadding
    {
        get => (Thickness)GetValue(CellPaddingProperty);
        set => SetValue(CellPaddingProperty, value);
    }

    /// <summary>
    /// Fit one column to the header and cells the current visual layout has already realized.
    /// A hidden or non-resizable column is a no-op; a column this table does not hold is an
    /// argument error.
    /// </summary>
    public void Fit(Column column) => Fit(RequireColumn(column, nameof(column)));

    /// <summary>The same fit for a column the header's separator and menu have resolved.</summary>
    internal void Fit(EffectiveColumn column)
    {
        if (FitColumn(column))
        {
            RaiseLayoutChanged(LayoutChange.Fit);
        }
    }

    /// <summary>
    /// Fit each visible resizable column independently and report the whole operation once.
    /// </summary>
    public void FitColumns()
    {
        if (FitVisible())
        {
            RaiseLayoutChanged(LayoutChange.Fit);
        }
    }

    /// <summary>
    /// Fit each visible resizable column, then scale those columns so they end at the table's
    /// right edge, and report the whole operation once.
    /// </summary>
    public void FillWidth()
    {
        // The fit and the scale each move widths, and a second fill moves them away and back, so
        // only the widths the command ends with say whether it changed anything.
        double[] before = EffectiveLayout.VisibleColumns.Select(visible => visible.Width).ToArray();

        FitVisible();
        ScaleToWidth();

        if (!EffectiveLayout.VisibleColumns.Select(visible => visible.Width).SequenceEqual(before))
        {
            RaiseLayoutChanged(LayoutChange.Fit);
        }
    }

    /// <summary>
    /// Scale the visible resizable columns by one factor so the visible columns end at the
    /// header strip's right edge: wider when there is space left, narrower when they run past it.
    /// </summary>
    /// <remarks>
    /// One factor keeps the fitted proportions, so the columns a fit found widest, which are the
    /// most likely to hold longer values in rows not yet shown, keep the most room. A column the
    /// factor would take below its <see cref="Column.MinWidth"/> stays there and the others share
    /// what is left; when even the minimums do not fit, the columns run past the edge as section 8
    /// allows.
    /// </remarks>
    private void ScaleToWidth()
    {
        double width = _headerStrip?.ActualWidth ?? 0;
        if (width <= 0)
        {
            return;
        }

        List<EffectiveColumn> scaled = EffectiveLayout
            .VisibleColumns.Select(visible => visible.Column)
            .Where(CanFitColumn)
            .ToList();

        // What the scaled columns share: the room less the visible columns that keep theirs.
        double room = width - EffectiveLayout.TotalWidth + scaled.Sum(column => column.Width);

        while (scaled.Count > 0)
        {
            double factor = Math.Max(room, 0) / scaled.Sum(column => column.Width);
            EffectiveColumn[] floored = scaled
                .Where(column => column.Width * factor < column.Column.MinWidth)
                .ToArray();

            if (floored.Length == 0)
            {
                foreach (EffectiveColumn column in scaled)
                {
                    SetColumnWidth(column, column.Width * factor);
                }

                break;
            }

            foreach (EffectiveColumn column in floored)
            {
                SetColumnWidth(column, column.Column.MinWidth);
                room -= column.Column.MinWidth;
                scaled.Remove(column);
            }
        }
    }

    /// <returns>False when no width changed.</returns>
    private bool FitVisible()
    {
        bool changed = false;

        // Each fit republishes the layout, so the visible set is taken before the first one.
        foreach (VisibleColumn visible in EffectiveLayout.VisibleColumns.ToArray())
        {
            changed |= FitColumn(visible.Column);
        }

        return changed;
    }

    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e) => FillOnce();

    /// <summary>
    /// Section 10's first fill: the first time the table has a width, scale the declared widths to
    /// it, unless a column already has a width the person chose. That width came from a saved
    /// layout, a resize or a fit, and the person's choice wins.
    /// </summary>
    /// <remarks>
    /// The scale alone, without <see cref="FillWidth"/>'s fit: a fit would measure whatever rows
    /// happen to be realized at that moment, and the declared widths are the host's proportions.
    /// Silent, as the rest of the initial layout is.
    /// </remarks>
    private void FillOnce()
    {
        if (_filledOnce || !_schemaCaptured || _headerStrip is not { ActualWidth: > 0 } strip)
        {
            return;
        }

        _filledOnce = true;
        strip.SizeChanged -= OnHeaderSizeChanged;

        if (EffectiveLayout.Order.All(column => column.WidthOverride is null))
        {
            ScaleToWidth();
        }
    }

    /// <summary>
    /// Sections 5 and 10: return to the captured baseline. Every width and visibility override is
    /// discarded, the effective order returns to the
    /// declared one, and because that baseline carries no sort criterion the local sort goes with
    /// it. It fits nothing to the current data.
    /// </summary>
    public void ResetLayout()
    {
        bool changed = !EffectiveLayout.Order.SequenceEqual(_baselineOrder);

        foreach (EffectiveColumn column in _baselineOrder)
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

        // SetOrder republishes the layout, which re-realizes each header cell and so clears the
        // sort glyph of the column that had one.
        EffectiveLayout.SetOrder(_baselineOrder);

        if (sorted)
        {
            RebuildView();
        }

        RaiseLayoutChanged(LayoutChange.Reset);
    }

    /// <summary>Section 12: a fit is offered for a visible column the host allows to be resized.</summary>
    internal bool CanFitColumn(EffectiveColumn column) =>
        column.IsVisible && column.Column.CanResize;

    internal bool CanFitColumns =>
        EffectiveLayout.VisibleColumns.Any(visible => CanFitColumn(visible.Column));

    /// <summary>Give this column a width override and republish the layout.</summary>
    /// <returns>False when the clamped width is already the effective width.</returns>
    internal bool SetColumnWidth(EffectiveColumn column, double width)
    {
        double clamped = column.Clamp(width);
        if (clamped == column.Width)
        {
            return false;
        }

        column.WidthOverride = clamped;
        EffectiveLayout.Rebuild();
        return true;
    }

    /// <summary>
    /// Section 12: a column can be hidden while the host allows it and another visible column would
    /// remain. It is also what disables the last visible column's toggle, so the menu cannot reach
    /// a state with no visible column.
    /// </summary>
    internal bool CanHideColumn(EffectiveColumn column) =>
        column.IsVisible && column.CanHide && EffectiveLayout.VisibleColumns.Count > 1;

    internal void SetColumnVisibility(EffectiveColumn column, bool visible)
    {
        if (visible == column.IsVisible || (!visible && !CanHideColumn(column)))
        {
            return;
        }

        // Section 10: a column keeps its effective width across a hide and a show.
        column.VisibilityOverride = visible;

        // Section 9: the header is the only place the sort shows and the only place it is
        // changed, so hiding the sorted column takes the sort with it. Left in force, the view
        // would stay ordered by a column no longer on screen, with no chevron to say so and, under
        // section 16, no row drag either, and nothing on screen to explain why.
        bool sortCleared = !visible && ReferenceEquals(_sortColumn, column) && ClearSort();

        EffectiveLayout.Rebuild();
        if (sortCleared)
        {
            RebuildView();
        }

        RaiseLayoutChanged(LayoutChange.Visibility);
    }

    /// <summary>
    /// Section 11's one placement rule, shared by the header drag and the menu's move commands.
    /// <paramref name="boundary"/> is counted among the visible columns with this one taken out:
    /// 0 is before the first, the count is after the last, and the column's own visible index puts
    /// it back where it was. Every other column, hidden ones included, keeps its relative order.
    /// </summary>
    /// <returns>False when the placement leaves the order as it is.</returns>
    internal bool MoveColumnTo(EffectiveColumn column, int boundary, FocusState? focus)
    {
        int index = EffectiveLayout.IndexOfVisible(column);
        if (index < 0 || column.IsHierarchy)
        {
            return false;
        }

        boundary = Math.Clamp(
            boundary,
            _hierarchy is null ? 0 : 1,
            EffectiveLayout.VisibleColumns.Count - 1
        );
        if (boundary == index)
        {
            return false;
        }

        List<EffectiveColumn> order = new(EffectiveLayout.Order);
        order.Remove(column);
        order.Insert(InsertionPoint(order, boundary), column);

        // Section 19: the move has to read as movement. Where every cell is rendered now is the
        // only thing the animation needs; everything after this is an ordinary layout change.
        Dictionary<Column, double> before = Motion.CaptureOffsets(EffectiveLayout);

        EffectiveLayout.SetOrder(order);
        Motion.SlideFrom(this, before);
        FocusHeaderOf(column, focus);
        RaiseLayoutChanged(LayoutChange.Move);
        return true;
    }

    /// <summary>
    /// Section 12's Move left and Move right, by the same rule as a drag. They move no focus: the
    /// menu they are invoked from stays open across the move and holds focus while it is open, and
    /// it returns focus to the header it was opened on when it closes, with the state the request
    /// arrived in. Asking for focus here put a focus visual on a header behind an open menu, and
    /// put it there for a mouse click, which draws a focus visual nowhere else in the table.
    /// </summary>
    internal bool MoveColumnBy(EffectiveColumn column, int step) =>
        MoveColumnTo(column, EffectiveLayout.IndexOfVisible(column) + step, focus: null);

    /// <summary>A move is offered while there is a neighbouring visible place to move into.</summary>
    internal bool CanMoveColumnBy(EffectiveColumn column, int step)
    {
        int index = EffectiveLayout.IndexOfVisible(column);
        return !column.IsHierarchy
            && index >= 0
            && Math.Clamp(
                index + step,
                _hierarchy is null ? 0 : 1,
                EffectiveLayout.VisibleColumns.Count - 1
            ) != index;
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
    private void FocusHeaderOf(EffectiveColumn column, FocusState? focus)
    {
        if (focus is null)
        {
            return;
        }

        int index = EffectiveLayout.IndexOfVisible(column);
        if (
            _headerStrip?.Panel is Panel header
            && index < header.Children.Count
            && header.Children[index] is Control cell
        )
        {
            cell.Focus(focus.Value);
        }
    }

    /// <summary>
    /// Where a visible boundary sits in the full order. It is anchored to the neighbouring visible
    /// column, so a hidden column beside the boundary is not stepped over and a drop on the moving
    /// column's own boundary stays a no-op.
    /// </summary>
    private static int InsertionPoint(List<EffectiveColumn> order, int boundary)
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

    internal void RaiseLayoutChanged(LayoutChange kind) => LayoutChanged?.Invoke(this, kind);

    private EffectiveColumn RequireColumn(Column column, string parameter)
    {
        ArgumentNullException.ThrowIfNull(column, parameter);

        if (EffectiveLayout.Find(column) is EffectiveColumn resolved)
        {
            return resolved;
        }

        throw new ArgumentException(
            _schemaCaptured
                ? "That column is not one of this table's columns."
                : "The column schema is captured at the first Loaded, so no column resolves yet.",
            parameter
        );
    }

    /// <summary>
    /// Section 10's bounded fit: the widest realized header cell or row cell for this column,
    /// raised to the column's minimum. Nothing realized means nothing to fit.
    /// </summary>
    private bool FitColumn(EffectiveColumn column)
    {
        if (!CanFitColumn(column))
        {
            return false;
        }

        int index = EffectiveLayout.IndexOfVisible(column);
        double widest = 0;
        bool measured = false;

        foreach (CellsPanel panel in RealizedPanels())
        {
            // A realized panel is already synced to the current visible columns: a column-set
            // change reconciles every attached panel's cells synchronously, and CellsPanel's
            // measure and arrange throw if the counts ever disagree. This guard is defensive, not
            // load-bearing — index is already within panel.Children.Count here.
            if (index >= panel.Children.Count)
            {
                continue;
            }

            widest = Math.Max(widest, UnboundedWidth(panel.Children[index]));
            measured = true;

            // An unbounded desired size is not the one the panel arranges with, and the panel is
            // the only thing that measures a cell at its effective width.
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
        if (cell is Header.Cell header)
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
    internal IEnumerable<CellsPanel> RealizedPanels()
    {
        if (_headerStrip?.Panel is CellsPanel header)
        {
            yield return header;
        }

        if (_surface?.ItemsPanelRoot is not ItemsStackPanel rows)
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
            if (
                _surface.ContainerFromIndex(index) is DependencyObject container
                && FindDescendant<CellsPanel>(container) is CellsPanel panel
            )
            {
                yield return panel;
            }
        }
    }
}
