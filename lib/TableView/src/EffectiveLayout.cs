namespace Syno.TableView;

/// <summary>
/// One column's effective state. Width and visibility are baseline plus an optional override so
/// that section 18's sparse override maps can be emitted without guessing.
/// </summary>
internal sealed class EffectiveColumn
{
    internal EffectiveColumn(Column column, bool hierarchy = false)
    {
        Column = column;
        IsHierarchy = hierarchy;
        BaselineWidth = Clamp(column.Width);
    }

    internal Column Column { get; }
    internal bool IsHierarchy { get; }
    internal bool CanHide => !IsHierarchy && Column.CanHide;

    /// <summary>The persistence key, or null for a column the host does not persist.</summary>
    internal string? Id => Column.Id;

    /// <summary>Declared default width, raised to the column's minimum.</summary>
    internal double BaselineWidth { get; }

    internal bool IsBaselineVisible => IsHierarchy || Column.IsVisible;

    internal double? WidthOverride { get; set; }

    internal bool? VisibilityOverride { get; set; }

    internal double Width => WidthOverride ?? BaselineWidth;

    internal bool IsVisible => IsHierarchy || (VisibilityOverride ?? IsBaselineVisible);

    /// <summary>This width raised to the column's minimum, the only bound a width has.</summary>
    internal double Clamp(double width) => Math.Max(width, Column.MinWidth);
}

/// <summary>A visible column with its derived geometry.</summary>
internal readonly struct VisibleColumn
{
    internal VisibleColumn(EffectiveColumn column, double offset)
    {
        Column = column;
        Offset = offset;
    }

    internal EffectiveColumn Column { get; }

    /// <summary>Cumulative x of this column's left edge.</summary>
    internal double Offset { get; }

    internal double Width => Column.Width;
}

/// <summary>
/// The single layout source. The header panel and every realized row panel read it and
/// nothing else computes column geometry.
/// </summary>
internal sealed class EffectiveLayout
{
    private readonly List<EffectiveColumn> _order = new();
    private readonly List<VisibleColumn> _visible = new();
    private double _totalWidth;

    internal event EventHandler<LayoutInvalidationReason>? Invalidated;

    /// <summary>The complete ordered column list, including hidden columns.</summary>
    internal IReadOnlyList<EffectiveColumn> Order => _order;

    /// <summary>Derived visible geometry, in effective order.</summary>
    internal IReadOnlyList<VisibleColumn> VisibleColumns => _visible;

    /// <summary>Sum of visible effective widths.</summary>
    internal double TotalWidth => _totalWidth;

    /// <summary>The effective column with this ID, or null when no column declares it.</summary>
    internal EffectiveColumn? Find(string id)
    {
        foreach (EffectiveColumn column in _order)
        {
            if (string.Equals(column.Id, id, StringComparison.Ordinal))
            {
                return column;
            }
        }

        return null;
    }

    /// <summary>
    /// The effective column for this definition, or null when the table did not declare it. By
    /// reference, so it answers for a column the host chose not to give a persistence key.
    /// </summary>
    internal EffectiveColumn? Find(Column declared)
    {
        foreach (EffectiveColumn column in _order)
        {
            if (ReferenceEquals(column.Column, declared))
            {
                return column;
            }
        }

        return null;
    }

    /// <summary>This column's place among the visible ones, or -1 when it is hidden.</summary>
    internal int IndexOfVisible(EffectiveColumn column)
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
    /// <paramref name="x"/>, or -1 when none does.
    /// </summary>
    internal int TrailingEdgeNear(double x, double tolerance)
    {
        for (int i = 0; i < _visible.Count; i++)
        {
            double edge = _visible[i].Offset + _visible[i].Width;
            if (Math.Abs(x - edge) <= tolerance)
            {
                return i;
            }
        }

        return -1;
    }

    internal void SetOrder(IEnumerable<EffectiveColumn> columns)
    {
        _order.Clear();
        _order.AddRange(columns);
        int index = _order.FindIndex(column => column.IsHierarchy);
        if (index > 0)
        {
            var first = _order[index];
            _order.RemoveAt(index);
            _order.Insert(0, first);
        }

        // Always the structural reason, whatever the new order turns out to look like. This call is
        // also what re-applies each header cell's sort indicator, and both a reset and a restored
        // layout can change the sort while leaving the visible columns exactly as they were.
        Publish(LayoutInvalidationReason.Columns);
    }

    /// <summary>Recompute derived visible geometry and announce what changed about it.</summary>
    internal void Rebuild() =>
        Publish(
            VisibleColumnsUnchanged()
                ? LayoutInvalidationReason.Widths
                : LayoutInvalidationReason.Columns
        );

    private void Publish(LayoutInvalidationReason reason)
    {
        _visible.Clear();
        double x = 0;
        foreach (EffectiveColumn column in _order)
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
    /// move of the drag. Told only that the layout changed, every realized row panel reconciles its
    /// cells against the visible columns before measuring — a scan per child and a content write per
    /// cell, across every realized row — to arrive at the children it already had. Separating the
    /// two lets a resize ask for the measure it needs and nothing else.
    /// </remarks>
    private bool VisibleColumnsUnchanged()
    {
        int published = 0;

        foreach (EffectiveColumn column in _order)
        {
            if (!column.IsVisible)
            {
                continue;
            }

            if (published >= _visible.Count || !ReferenceEquals(_visible[published].Column, column))
            {
                return false;
            }

            published++;
        }

        return published == _visible.Count;
    }
}
