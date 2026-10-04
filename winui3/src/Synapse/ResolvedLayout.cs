namespace Synapse;

/// <summary>
/// What changed about the resolved layout, in increasing order of what a subscriber has to redo.
/// </summary>
internal enum LayoutInvalidationReason
{
    /// <summary>Only <see cref="ResolvedLayout.HorizontalOffset"/> moved. Arrange is enough.</summary>
    Offset,

    /// <summary>The same visible columns at new widths. Measure is stale; the cells are not.</summary>
    Widths,

    /// <summary>The visible set or its order changed. Children and measure are both stale.</summary>
    Columns,
}

/// <summary>
/// One column's resolved state. Width and visibility are baseline plus an optional override so
/// that section 18's sparse override maps can be emitted without guessing.
/// </summary>
internal sealed class ResolvedColumn
{
    internal ResolvedColumn(TableColumn column)
    {
        Column = column;
        BaselineWidth = Clamp(column.Width, column.MinWidth, column.MaxWidth);
    }

    internal TableColumn Column { get; }

    /// <summary>The persistence key, or null for a column the host does not persist.</summary>
    internal string? Id => Column.Id;

    /// <summary>Declared default width after the column's own bounds are applied.</summary>
    internal double BaselineWidth { get; }

    internal bool BaselineVisibility => Column.IsVisible;

    internal double? WidthOverride { get; set; }

    internal bool? VisibilityOverride { get; set; }

    internal double Width => WidthOverride ?? BaselineWidth;

    internal bool IsVisible => VisibilityOverride ?? BaselineVisibility;

    internal static double Clamp(double value, double min, double max)
    {
        if (value < min)
        {
            value = min;
        }

        if (value > max)
        {
            value = max;
        }

        return value;
    }
}

/// <summary>A visible column with its derived geometry.</summary>
internal readonly struct VisibleColumn
{
    internal VisibleColumn(ResolvedColumn column, double offset)
    {
        Column = column;
        Offset = offset;
    }

    internal ResolvedColumn Column { get; }

    /// <summary>Cumulative x of this column's left edge, before the horizontal offset.</summary>
    internal double Offset { get; }

    internal double Width => Column.Width;
}

/// <summary>
/// The single geometry source. The header panel and every realized row panel read it and
/// nothing else computes column geometry.
/// </summary>
internal sealed class ResolvedLayout
{
    private readonly List<ResolvedColumn> _order = new();
    private readonly List<VisibleColumn> _visible = new();
    private double _totalWidth;
    private double _horizontalOffset;

    internal event EventHandler<LayoutInvalidationReason>? Invalidated;

    /// <summary>The complete ordered column list, including hidden columns.</summary>
    internal IReadOnlyList<ResolvedColumn> Order => _order;

    /// <summary>Derived visible geometry, in effective order.</summary>
    internal IReadOnlyList<VisibleColumn> VisibleColumns => _visible;

    /// <summary>Sum of visible resolved widths.</summary>
    internal double TotalWidth => _totalWidth;

    /// <summary>The table-owned horizontal offset. Panels subtract it at arrange time.</summary>
    internal double HorizontalOffset
    {
        get => _horizontalOffset;
        set
        {
            if (_horizontalOffset == value)
            {
                return;
            }

            _horizontalOffset = value;
            Invalidated?.Invoke(this, LayoutInvalidationReason.Offset);
        }
    }

    /// <summary>The resolved column with this ID, or null when no column declares it.</summary>
    internal ResolvedColumn? Find(string id)
    {
        foreach (ResolvedColumn column in _order)
        {
            if (string.Equals(column.Id, id, StringComparison.Ordinal))
            {
                return column;
            }
        }

        return null;
    }

    /// <summary>
    /// The resolved column for this definition, or null when the table did not declare it. By
    /// reference, so it answers for a column the host chose not to give a persistence key.
    /// </summary>
    internal ResolvedColumn? Find(TableColumn declared)
    {
        foreach (ResolvedColumn column in _order)
        {
            if (ReferenceEquals(column.Column, declared))
            {
                return column;
            }
        }

        return null;
    }

    /// <summary>This column's place among the visible ones, or -1 when it is hidden.</summary>
    internal int IndexOfVisible(ResolvedColumn column)
    {
        for (int i = 0; i < _visible.Count; i++)
        {
            if (ReferenceEquals(_visible[i].Column, column))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The visible column whose trailing edge lies within <paramref name="tolerance"/> of
    /// <paramref name="x"/>, or -1 when none does. <paramref name="x"/> is a header-strip
    /// coordinate, so the horizontal offset is subtracted here.
    /// </summary>
    internal int TrailingEdgeNear(double x, double tolerance)
    {
        for (int i = 0; i < _visible.Count; i++)
        {
            double edge = _visible[i].Offset + _visible[i].Width - _horizontalOffset;
            if (Math.Abs(x - edge) <= tolerance)
            {
                return i;
            }
        }

        return -1;
    }

    internal void SetOrder(IEnumerable<ResolvedColumn> columns)
    {
        _order.Clear();
        _order.AddRange(columns);

        // Always the structural reason, whatever the new order turns out to look like. This call is
        // also what re-applies each header cell's sort indicator, and both a reset and a restored
        // layout can change the sort while leaving the visible columns exactly as they were.
        Resolve(LayoutInvalidationReason.Columns);
    }

    /// <summary>Recompute derived visible geometry and announce what changed about it.</summary>
    internal void Rebuild() => Resolve(VisibleColumnsUnchanged()
        ? LayoutInvalidationReason.Widths
        : LayoutInvalidationReason.Columns);

    private void Resolve(LayoutInvalidationReason reason)
    {
        _visible.Clear();
        double x = 0;
        foreach (ResolvedColumn column in _order)
        {
            if (!column.IsVisible)
            {
                continue;
            }

            _visible.Add(new VisibleColumn(column, x));
            x += column.Width;
        }

        _totalWidth = x;
        Invalidated?.Invoke(this, reason);
    }

    /// <summary>
    /// Whether the columns about to be published are the ones already published, in the same order.
    /// </summary>
    /// <remarks>
    /// A resize moves widths inside a set that has not changed, and it does so on every pointer
    /// move of the drag. Told only that the geometry moved, every realized row panel reconciles its
    /// cells against the visible columns before measuring — a scan per child and a content write per
    /// cell, across every realized row — to arrive at the children it already had. Separating the
    /// two lets a resize ask for the measure it needs and nothing else.
    /// </remarks>
    private bool VisibleColumnsUnchanged()
    {
        int published = 0;

        foreach (ResolvedColumn column in _order)
        {
            if (!column.IsVisible)
            {
                continue;
            }

            if (published >= _visible.Count
                || !ReferenceEquals(_visible[published].Column, column))
            {
                return false;
            }

            published++;
        }

        return published == _visible.Count;
    }
}
