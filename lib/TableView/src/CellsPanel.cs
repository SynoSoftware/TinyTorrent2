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
    /// <summary>The column a row cell shows. A header cell names its own.</summary>
    private static readonly DependencyProperty ColumnProperty = DependencyProperty.RegisterAttached(
        "Column",
        typeof(Column),
        typeof(CellsPanel),
        new PropertyMetadata(null)
    );

    private Table? _owner;
    private EffectiveLayout? _layout;
    private bool _isHeaderPanel;

    public CellsPanel()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>Claimed by <see cref="Header.Strip"/> before the panel is ever loaded.</summary>
    internal void AttachAsHeader(Table owner)
    {
        _isHeaderPanel = true;
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

        Table? owner = FindOwner();
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

    private Table? FindOwner()
    {
        DependencyObject? node = VisualTreeHelper.GetParent(this);
        while (node is not null)
        {
            if (node is Table table)
            {
                return table;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
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

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_isHeaderPanel || _layout is null)
        {
            return;
        }

        foreach (UIElement child in Children)
        {
            if (child is ContentPresenter presenter)
            {
                presenter.Content = args.NewValue;
            }
            else if (child is Body.HierarchyCell hierarchyCell)
                hierarchyCell.Item = args.NewValue;
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

        for (int i = Children.Count - 1; i >= 0; i--)
        {
            if (VisibleIndexOf(visible, ColumnOf(Children[i])) < 0)
            {
                Children.RemoveAt(i);
            }
        }

        for (int i = 0; i < visible.Count; i++)
        {
            Column column = visible[i].Column.Column;
            int at = ChildIndexOf(column, i);

            if (at < 0)
            {
                Children.Insert(i, CreateChild(column));
            }
            else if (at != i)
            {
                // A reorder of visual children, not of a displayed collection: the rule against
                // Move is about an ObservableCollection bound to a ListView, and nothing here is
                // bound. Removing and re-adding the element would unload and reload it instead.
                Children.Move((uint)at, (uint)i);
            }

            RefreshChild(Children[i]);
        }
    }

    /// <summary>
    /// Re-apply each header cell's sort indicator. Sorting changes no geometry, so nothing else
    /// republishes the header.
    /// </summary>
    internal void RefreshHeaderCells()
    {
        if (_isHeaderPanel)
        {
            SyncChildren();
        }
    }

    private static int VisibleIndexOf(IReadOnlyList<VisibleColumn> visible, Column? column)
    {
        for (int i = 0; i < visible.Count; i++)
        {
            if (ReferenceEquals(visible[i].Column.Column, column))
            {
                return i;
            }
        }

        return -1;
    }

    private int ChildIndexOf(Column column, int start)
    {
        for (int i = start; i < Children.Count; i++)
        {
            if (ReferenceEquals(ColumnOf(Children[i]), column))
            {
                return i;
            }
        }

        return -1;
    }

    private static Column? ColumnOf(UIElement child) =>
        child is Header.Cell cell ? cell.Column : child.GetValue(ColumnProperty) as Column;

    /// <summary>
    /// The header cell and the row cell, built four lines apart and reading one inset, because a
    /// column's header and its cells must line up or the table looks broken. Supplied separately
    /// they drift: both hosts wrote the same two values by hand, in sixteen places, and deleting
    /// the control's own default once left it rendering misaligned until a host wrote a style.
    /// </summary>
    private UIElement CreateChild(Column column)
    {
        Thickness padding = _owner?.CellPadding ?? default;

        if (_isHeaderPanel)
        {
            Header.Cell cell = new() { Padding = padding };
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
        UIElement child =
            _owner?.IsHierarchyColumn(column) == true
                ? new Body.HierarchyCell(_owner, presenter)
                : presenter;
        child.SetValue(ColumnProperty, column);
        return child;
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

        if (child is ContentPresenter presenter)
        {
            presenter.Content = DataContext;
        }
        else if (child is Body.HierarchyCell hierarchyCell)
            hierarchyCell.Item = DataContext;
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
        if (Children.Count != visible)
        {
            throw new InvalidOperationException(
                $"The panel holds {Children.Count} cells against {visible} visible columns. "
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
            UIElement child = Children[i];
            child.Measure(new Size(visible[i].Width, double.PositiveInfinity));
            height = Math.Max(height, child.DesiredSize.Height);
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
            Children[i].Arrange(new Rect(visible[i].Offset, 0, visible[i].Width, finalSize.Height));
        }

        return finalSize;
    }
}
