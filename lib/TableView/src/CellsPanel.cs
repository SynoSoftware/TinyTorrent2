using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Syno.TableView;

/// <summary>
/// The one panel type used by both the header strip and the row template. It reads the effective
/// layout, realizes one child per visible column, and arranges each child at its cumulative x.
/// </summary>
public sealed partial class CellsPanel : Panel
{
    // Children's cells in their order, each with the column it shows. SyncChildren is the only
    // writer of Children and keeps this list equal to it, so layout and cell fill read their
    // cells here instead of fetching each one back from WinUI and looking up its wrapper.
    private readonly List<(EffectiveColumn Column, UIElement Cell)> _cells = [];

    // Row cells whose template is not built yet, with their columns; see WatchCuts.
    private readonly Dictionary<ContentPresenter, Column> _unwatched = [];

    private Table? _owner;
    private EffectiveLayout? _layout;
    private bool _isHeader;

    public CellsPanel()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Claimed by <see cref="Header.Strip"/> before the panel is ever loaded.</summary>
    internal void AttachAsHeader(Table owner)
    {
        _isHeader = true;
        Attach(owner);
    }

    /// <summary>
    /// Read the owner's layout and realize the cells. <see cref="Body.Row"/> calls this from
    /// its template pass, before this panel's first measure, so a new row is laid out once, with
    /// its cells, rather than once empty and again after <c>Loaded</c>.
    /// </summary>
    internal void Attach(Table owner)
    {
        if (Claim(owner))
        {
            InvalidateMeasure();
        }
    }

    /// <summary>Take the owner's layout and realize the cells, asking for no measure.</summary>
    /// <returns>True when this call is the one that took the layout.</returns>
    private bool Claim(Table owner)
    {
        _owner = owner;
        if (_layout is not null)
        {
            return false;
        }

        _layout = owner.EffectiveLayout;
        _layout.Invalidated += OnLayoutInvalidated;
        SyncChildren();
        return true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_owner is not null)
        {
            Attach(_owner);
            return;
        }

        Table? owner = Body.Row.FindOwner(this);
        if (owner is not null)
        {
            Attach(owner);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // A late Unloaded for a panel still in the tree; see Table's OnUnloaded.
        if (IsLoaded)
        {
            return;
        }

        if (_layout is null)
        {
            return;
        }

        _layout.Invalidated -= OnLayoutInvalidated;
        _layout = null;
    }

    private void OnLayoutInvalidated(object? sender, LayoutInvalidationReason reason)
    {
        // New widths across the same columns leave every cell where it belongs, so a resize takes
        // the measure without the reconcile.
        if (reason == LayoutInvalidationReason.Columns)
        {
            SyncChildren();
        }

        InvalidateMeasure();
    }

    /// <summary>
    /// Show the row's new item in every cell. <see cref="Body.Row"/> passes on its own data context
    /// change, so a rebind raises one managed event for the row and its cells, not one each.
    /// </summary>
    internal void ShowItem(object? item)
    {
        if (_layout is null)
        {
            return;
        }

        foreach (var (_, cell) in _cells)
        {
            ShowItem(cell, item);
        }
    }

    private static void ShowItem(UIElement cell, object? item)
    {
        if (cell is ContentPresenter presenter)
        {
            presenter.Content = item;
        }
        else if (cell is Body.HierarchyCell hierarchyCell)
        {
            hierarchyCell.Item = item;
        }
    }

    /// <summary>
    /// One cell per visible column, in visible order. A cell stays with its column: it is created
    /// only for a column that has none, removed only when its column hides, and moved when its
    /// column moves. Nothing here re-inflates a template, so a column move or a hide costs each
    /// realized row a reorder of its existing elements and one measure.
    /// </summary>
    private void SyncChildren()
    {
        if (_layout is null)
        {
            return;
        }

        IReadOnlyList<VisibleColumn> visible = _layout.VisibleColumns;

        for (int i = _cells.Count - 1; i >= 0; i--)
        {
            if (_layout.IndexOfVisible(_cells[i].Column) < 0)
            {
                if (_cells[i].Cell is ContentPresenter removed)
                {
                    _unwatched.Remove(removed);
                }

                _cells.RemoveAt(i);
                Children.RemoveAt(i);
            }
        }

        for (int i = 0; i < visible.Count; i++)
        {
            EffectiveColumn column = visible[i].Column;
            int at = CellIndexOf(column, i);

            if (at < 0)
            {
                UIElement cell = CreateChild(column.Column);
                _cells.Insert(i, (column, cell));
                Children.Insert(i, cell);
            }
            else if (at != i)
            {
                // A reorder of visual children, not of a displayed collection: the rule against
                // Move is about an ObservableCollection bound to a ListView, and nothing here is
                // bound. Removing and re-adding the element would unload and reload it instead.
                var moved = _cells[at];
                _cells.RemoveAt(at);
                _cells.Insert(i, moved);
                Children.Move((uint)at, (uint)i);
            }

            RefreshChild(_cells[i].Cell);
        }
    }

    /// <summary>
    /// Re-apply each header cell's sort indicator. Sorting changes no geometry, so nothing else
    /// republishes the header.
    /// </summary>
    internal void RefreshHeader()
    {
        if (_isHeader)
        {
            SyncChildren();
        }
    }

    private int CellIndexOf(EffectiveColumn column, int start)
    {
        for (int i = start; i < _cells.Count; i++)
        {
            if (ReferenceEquals(_cells[i].Column, column))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The header cell and the row cell, built four lines apart and reading one inset, because a
    /// column's header and its cells must line up or the table looks broken. Supplied separately
    /// they drift: both hosts wrote the same two values by hand, in sixteen places, and deleting
    /// the control's own default once left it rendering misaligned until a host wrote a style.
    /// </summary>
    private UIElement CreateChild(Column column)
    {
        Thickness padding = _owner?.CellPadding ?? default;

        if (_isHeader)
        {
            Header.Cell cell = new()
            {
                Padding = padding,
                HorizontalContentAlignment =
                    column.CellAlignment == HorizontalAlignment.Stretch
                        ? HorizontalAlignment.Left
                        : column.CellAlignment,
            };
            cell.SetColumn(column);
            return cell;
        }

        ContentPresenter presenter = new()
        {
            ContentTemplate = column.CellTemplate,
            HorizontalContentAlignment = column.CellAlignment,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Padding = padding,
            Content = DataContext,
        };
        _unwatched[presenter] = column;
        return _owner?.IsHierarchyColumn(column) == true
            ? new Body.HierarchyCell(_owner, presenter)
            : presenter;
    }

    /// <summary>
    /// Section 10: a text block in a row cell that starts to cut its text off asks the table for a
    /// fill. A cell's template is built by the first measure that gives it a row, and it stays
    /// with the cell when the row changes, so each cell's text blocks are watched once.
    /// </summary>
    private void WatchCuts()
    {
        List<ContentPresenter>? built = null;
        foreach (var (presenter, column) in _unwatched)
        {
            if (VisualTreeHelper.GetChildrenCount(presenter) != 0)
            {
                (built ??= []).Add(presenter);
                WatchCuts(presenter, column);
            }
        }

        foreach (ContentPresenter presenter in built ?? [])
        {
            _unwatched.Remove(presenter);
        }
    }

    private void WatchCuts(DependencyObject node, Column column)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            if (child is TextBlock text)
            {
                text.IsTextTrimmedChanged += (_, _) =>
                {
                    if (text.IsTextTrimmed)
                    {
                        _owner?.FillAfterCut(column);
                    }
                };
            }

            WatchCuts(child, column);
        }
    }

    /// <summary>
    /// What can change about a cell that already exists: a header's sort glyph, and the row item of
    /// a cell that was created before its panel had one.
    /// </summary>
    private void RefreshChild(UIElement child)
    {
        if (child is Header.Cell cell)
        {
            if (cell.Column is Column column)
            {
                cell.RefreshText(_owner?.Strings ?? Strings.English);
                cell.SetSort(_owner?.SortDirectionOf(column));
            }

            return;
        }

        ShowItem(child, DataContext);
    }

    /// <summary>
    /// <see cref="SyncChildren"/> is the only thing that adds or removes a cell, and it leaves
    /// exactly one per visible column in visible order. Both layout passes read a child and its
    /// column by the same index, so a disagreement means that reconcile did not run for a column
    /// change that reached the layout.
    /// </summary>
    /// <remarks>
    /// Laying out only what the two have in common is what this replaces, and it would draw the
    /// first n declared columns under the first n visible columns' geometry: a table that renders
    /// plausibly, lines up with its header, and shows the wrong columns. Nothing constructs that
    /// state today; it stops here so that whatever introduces it says so at once.
    /// </remarks>
    private void RequireOneCellPerColumn(int visible)
    {
        if (_cells.Count != visible)
        {
            throw new InvalidOperationException(
                $"The panel holds {_cells.Count} cells against {visible} visible columns. "
                    + "SyncChildren owns that reconcile, so every column change must reach it."
            );
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // A panel is measured before it is loaded, and a recycled row is unloaded and measured
        // again before its Loaded runs. Unloading drops the layout, so without this the panel
        // measures zero height and the row draws nothing while the list still holds every item and
        // still shows a scroll thumb — a table that has gone blank with a full scroll bar beside
        // it. Measure is the first moment the layout is actually needed, so it is where a panel
        // that has lost it takes it back.
        // Claim, not Attach: this is inside the panel's own measure, and Attach ends by
        // invalidating it. Marking a panel dirty from within its own MeasureOverride makes the
        // layout manager run the whole override a second time in the same tick, and this branch is
        // the recycle path — so every row realized on every scroll was measured twice. Adding the
        // children here is not the problem; the pass measures them. Only the invalidation is.
        if (_layout is null && _owner is not null)
        {
            Claim(_owner);
        }

        if (_layout is null)
        {
            return new Size(0, 0);
        }

        IReadOnlyList<VisibleColumn> visible = _layout.VisibleColumns;
        RequireOneCellPerColumn(visible.Count);
        double height = 0;

        for (int i = 0; i < visible.Count; i++)
        {
            UIElement child = _cells[i].Cell;
            child.Measure(new Size(visible[i].Width, double.PositiveInfinity));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        if (_unwatched.Count != 0)
        {
            WatchCuts();
        }

        return new Size(_layout.TotalWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_layout is null)
        {
            return finalSize;
        }

        IReadOnlyList<VisibleColumn> visible = _layout.VisibleColumns;
        RequireOneCellPerColumn(visible.Count);

        for (int i = 0; i < visible.Count; i++)
        {
            _cells[i].Cell.Arrange(new Rect(visible[i].Offset, 0, visible[i].Width, finalSize.Height));
        }

        return finalSize;
    }
}
