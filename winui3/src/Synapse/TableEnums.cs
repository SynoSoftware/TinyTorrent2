namespace Synapse;

/// <summary>
/// What the table shows in place of rows while the view has none. Only the host can tell these
/// apart, so it says which one applies.
/// </summary>
public enum TablePlaceholder
{
    /// <summary>The host's wider source has no items.</summary>
    Empty,

    /// <summary>The host is fetching. Existing rows stay visible until they are replaced.</summary>
    Loading,

    /// <summary>An external filter excluded every item.</summary>
    NoResults,
}

/// <summary>Direction of an active header sort.</summary>
public enum TableSortDirection
{
    Ascending,
    Descending,
}

/// <summary>What completed layout operation produced a <see cref="TableView.LayoutChanged"/> event.</summary>
public enum TableLayoutChangeKind
{
    Sort,
    ColumnMove,
    ColumnResize,
    AutoFit,
    Visibility,
    Reset,
}
