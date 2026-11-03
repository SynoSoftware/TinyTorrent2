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
    /// <summary>An interactive cell descendant, a suppressed subtree, or outside the rows.</summary>
    Suppressed,

    Row,

    /// <summary>Row-surface space below the last row.</summary>
    EmptySurface,
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

internal enum PageStage
{
    Revealing,
    Scrolling,
    ReadyToScroll,
    Ready,
}

/// <summary>How the rows an order shares with the view change their places.</summary>
internal enum RowMoves : byte
{
    /// <summary>Every shared row keeps its place relative to the others.</summary>
    None,

    /// <summary>
    /// Each shared row that moves is named: it leaves its view index and arrives at its order
    /// index.
    /// </summary>
    Named,

    /// <summary>Shared rows change their relative order, and the difference names no row.</summary>
    Unnamed,
}

/// <summary>What a sorted publish does to one kept row.</summary>
internal enum SortFate : byte
{
    Stays,

    /// <summary>It stays, with a new entry or instance.</summary>
    Changes,

    /// <summary>It stays only if its new key keeps it in order with its neighbours.</summary>
    Rekeyed,

    Leaves,

    /// <summary>It leaves its place and is placed again.</summary>
    Moves,
}

/// <summary>
/// Which sibling groups a hierarchy publish sorts again. Every other group keeps the order it
/// was last shown in.
/// </summary>
internal enum SiblingSort : byte
{
    /// <summary>The groups whose list changed which rows it holds, or their order.</summary>
    Changed,

    /// <summary>
    /// Those, and the groups that hold a row the change named or replaced, because rows may move.
    /// </summary>
    Named,

    /// <summary>Every group.</summary>
    All,
}
