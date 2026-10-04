using Microsoft.UI.Xaml;

namespace Synapse;

/// <summary>
/// One column definition. Setup-only schema: <see cref="TableView"/> captures every value here
/// exactly once, at its first <c>Loaded</c>. Changing a value afterwards is unsupported.
/// </summary>
public sealed partial class TableColumn : DependencyObject
{
    /// <summary>
    /// This column's persistence key: stable, unique, and non-empty when it is supplied. Null is a
    /// column the host does not persist, which is the ordinary case for a table whose layout is
    /// never saved. A layout snapshot then carries nothing about it, and a restore leaves it in the
    /// declared order after the columns the snapshot did name.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>Non-empty localized plain-text name used by generated menus and UI Automation.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Header content. When null and <see cref="HeaderTemplate"/> is null the table
    /// generates a trimmed one-line label from <see cref="DisplayName"/>.</summary>
    public object? Header { get; set; }

    public DataTemplate? HeaderTemplate { get; set; }

    /// <summary>Cell content template. It receives the row item as its content.</summary>
    public DataTemplate? CellTemplate { get; set; }

    /// <summary>Baseline width in DIPs. Finite and greater than zero.</summary>
    public double Width { get; set; } = 150;

    /// <summary>Lower width bound in DIPs. Finite and non-negative.</summary>
    public double MinWidth { get; set; } = 48;

    /// <summary>Upper width bound in DIPs. A finite positive value or positive infinity.</summary>
    public double MaxWidth { get; set; } = double.PositiveInfinity;

    /// <summary>Whether the column is shown before the user or a restored layout says otherwise.</summary>
    public bool IsVisible { get; set; } = true;

    public bool CanHide { get; set; } = true;

    public bool CanResize { get; set; } = true;

    /// <summary>
    /// This column's ascending values are the host's row order: the order the unsorted view shows
    /// and the order a row drag changes. At most one column may say so. Sorted by any other column,
    /// a row's place on screen is a place in that sort and not in the row order, so the table
    /// withholds the drag; sorted by this column, up or down, it offers it, and reports the request
    /// in row order either way.
    /// </summary>
    public bool DefinesRowOrder { get; set; }

    // No per-column icon here on purpose. The one place it would be drawn is the generated menu's
    // column list, and that list is a set of on/off states where the checkmark is the glyph the eye
    // reads. A second glyph beside it competes with the check, and a host that gave icons to some
    // columns and not others would leave the rest of the list indented past a blank. A column's
    // identity already has a home: Header and HeaderTemplate take whatever the host wants to show.

    public HorizontalAlignment CellAlignment { get; set; } = HorizontalAlignment.Left;

    /// <summary>
    /// The order this column sorts by, supplied by <see cref="TableView.Schema{TRow}"/>. There is
    /// no separate switch: a column given a sort key is sortable and one that was not is not, so
    /// the two cannot disagree.
    /// </summary>
    internal IComparer<object>? Comparer { get; set; }

    internal bool CanSort => Comparer is not null;
}
