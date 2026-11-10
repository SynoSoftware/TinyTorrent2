namespace Syno.TableView;

/// <summary>
/// What the table shows in place of rows while the view has none. Only the host can tell these
/// apart, so it says which one applies.
/// </summary>
public enum Placeholder
{
    /// <summary>The host's wider source has no items.</summary>
    Empty,

    /// <summary>The host is fetching. Existing rows stay visible until they are replaced.</summary>
    Loading,

    /// <summary>An external filter excluded every item.</summary>
    NoResults,
}

/// <summary>Direction of an active header sort.</summary>
public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>Who applies the active sort to the supplied rows.</summary>
public enum SortMode
{
    Local,
    External,
}

/// <summary>What completed layout operation produced a <see cref="Table.LayoutChanged"/> event.</summary>
public enum LayoutChange
{
    Sort,
    Move,
    Resize,
    Fit,
    Visibility,
    Reset,
}

/// <summary>
/// What changed about the effective layout, in increasing order of what a subscriber has to redo.
/// </summary>
internal enum LayoutInvalidationReason
{
    /// <summary>The same visible columns at new widths. Measure is stale; the cells are not.</summary>
    Widths,

    /// <summary>The visible set or its order changed. Children and measure are both stale.</summary>
    Columns,
}

/// <summary>The gesture on the row surface, from press to release.</summary>
internal enum RowGesture
{
    /// <summary>No button is down on the row surface.</summary>
    None,

    /// <summary>Pressed, and no gesture has begun. Release here is a click.</summary>
    Pressed,

    /// <summary>
    /// Section 14's rectangle, from a press on empty row surface or on a row the table would
    /// not drag.
    /// </summary>
    Marquee,

    /// <summary>Section 16's row drag, from a press on a row.</summary>
    RowDrag,
}

/// <summary>What a press on the row surface landed on.</summary>
internal enum HitTarget
{
    /// <summary>
    /// An interactive cell descendant, a suppressed subtree, outside the rows, or a row that is
    /// still invisible as it arrives.
    /// </summary>
    Suppressed,

    Row,

    /// <summary>Row-surface space below the last row.</summary>
    EmptySurface,
}

/// <summary>
/// Where a change the person caused stands after the host withdrew its source
/// (<see cref="Table.Withdraw"/>).
/// </summary>
internal enum PersonChange
{
    /// <summary>No change of the person's is on its way.</summary>
    None,

    /// <summary>The host withdrew its source, and the next source it sets is the person's change.</summary>
    Expected,

    /// <summary>The table holds the person's source and has not shown it yet; it applies whole.</summary>
    Pending,
}

/// <summary>What one of <see cref="Body.Changes"/>' edits does to the view.</summary>
internal enum EditKind
{
    /// <summary>Insert a row, with a notification, so the list animates it in.</summary>
    Insert,

    /// <summary>Remove a row, with a notification, so the list animates it out.</summary>
    Remove,

    /// <summary>Set a row without a notification; the list holds no container there or the table rebinds it.</summary>
    Replace,

    /// <summary>
    /// Make the view as many rows long as the edit's index, without a notification. The list
    /// learns the change in count at the end of the view, from <see cref="Body.View.CatchUp(long)"/>.
    /// </summary>
    Resize,
}

/// <summary>The gesture on the header, from press to release.</summary>
internal enum HeaderGesture
{
    /// <summary>No button is down on the header.</summary>
    None,

    /// <summary>A resize separator is captured and tracking the pointer.</summary>
    Resizing,

    /// <summary>A header is pressed, still inside the drag threshold. Release here is a click.</summary>
    Pressed,

    /// <summary>The threshold was crossed and the pressed header is being reordered.</summary>
    Dragging,
}
