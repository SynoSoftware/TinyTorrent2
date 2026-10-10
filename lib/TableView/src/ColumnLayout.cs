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
/// <paramref name="VisibilityOverrides"/> and <paramref name="WidthOverrides"/> are sparse override maps: a missing
/// column ID means that column uses its baseline.
/// </summary>
public sealed record ColumnLayout(
    IReadOnlyList<string> Order,
    IReadOnlyDictionary<string, bool> VisibilityOverrides,
    IReadOnlyDictionary<string, double> WidthOverrides,
    string? SortColumnId,
    SortDirection SortDirection
);
