namespace Syno.TableView;

/// <summary>
/// The active sort: which column, and which way. One value, so no state exists where the column
/// and the direction disagree, and <c>null</c> is natural order.
/// </summary>
public readonly record struct Sort(
    Column Column,
    SortDirection Direction = SortDirection.Ascending
);

/// <summary>
/// Data-only layout snapshot. The table produces and validates it; the host stores it.
/// <paramref name="VisibilityOverrides"/> is a sparse override map: a missing column ID uses its baseline.
/// Widths belong to the live table and are not part of its saved layout.
/// </summary>
public sealed record ColumnLayout(
    IReadOnlyList<string> Order,
    IReadOnlyDictionary<string, bool> VisibilityOverrides,
    string? SortColumnId,
    SortDirection SortDirection
);
