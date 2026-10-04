using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Synapse;

namespace Synapse_Tests;

/// <summary>
/// Section 10's direct resize. The strip's gesture is a three-step state machine in header-strip
/// coordinates — begin on a separator, track, complete or cancel — and the pointer overrides only
/// translate events into it. These tests drive that state machine on a live loaded control;
/// <see cref="RealColumnResizeTests"/> drives the same three steps with injected mouse messages.
/// </summary>
[TestClass]
public class ColumnResizeTests
{
    /// <summary>Three 200 DIP columns, so the separators sit at x = 200 and x = 400.</summary>
    private const double FirstSeparator = 200;

    // ------------------------------------------------------------------ where a resize can start

    [TestMethod]
    public Task AResizeStartsOnlyOnASeparator() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync();
        TableHeaderStrip strip = Strip(table);

        Assert.IsFalse(BeginResizeAt(strip, 100), "the middle of a header is not a separator");
        Assert.IsTrue(BeginResizeAt(strip, FirstSeparator), "its trailing edge is");

        CancelGesture(strip);

        Assert.IsTrue(BeginResizeAt(strip, FirstSeparator + 3), "and so is 3 DIP either side of it");
        CancelGesture(strip);
        Assert.IsFalse(BeginResizeAt(strip, FirstSeparator + 6), "6 DIP away is not");
    });

    [TestMethod]
    public Task AColumnTheHostFixedHasNoSeparator() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync(column => column.CanResize = false);

        Assert.IsFalse(BeginResizeAt(Strip(table), FirstSeparator));
    });

    // ------------------------------------------------------------------ the live gesture

    [TestMethod]
    public Task TrackingMovesTheGuideAndNoWidthAtAll() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync();
        TableHeaderStrip strip = Strip(table);
        Func<int> events = LayoutChanges(table);

        BeginResizeAt(strip, FirstSeparator);
        TrackResize(strip, FirstSeparator + 40);

        Assert.AreEqual(240d, Preview(strip), "the guide follows the pointer");
        Assert.AreEqual(200d, TableHarness.ResolvedWidth(table, "a"), "and no width has moved");
        Assert.AreEqual(200d, TableHarness.ResolvedWidth(table, "b"), "the next column is untouched");

        TrackResize(strip, FirstSeparator + 90);
        TrackResize(strip, FirstSeparator + 60);

        Assert.AreEqual(260d, Preview(strip), "and follows it back");
        Assert.AreEqual(200d, TableHarness.ResolvedWidth(table, "a"), "still without moving one");
        Assert.AreEqual(0, events(), "and no pointer movement is a persistence event");

        CompleteResize(strip);

        Assert.AreEqual(260d, TableHarness.ResolvedWidth(table, "a"), "the release applies it");
        Assert.AreEqual(1, events(), "one gesture, one notification");
    });

    /// <summary>Section 10: a resize is clamped to the column's own limits and to nothing else.</summary>
    [TestMethod]
    public Task ADragIsClampedToTheColumnsOwnLimits() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync(column =>
        {
            column.MinWidth = 120;
            column.MaxWidth = 300;
        });
        TableHeaderStrip strip = Strip(table);

        BeginResizeAt(strip, FirstSeparator);

        TrackResize(strip, FirstSeparator - 400);
        Assert.AreEqual(120d, Preview(strip), "it stops at MinWidth");

        TrackResize(strip, FirstSeparator + 400);
        Assert.AreEqual(300d, Preview(strip), "and at MaxWidth");

        TrackResize(strip, FirstSeparator + 50);
        Assert.AreEqual(250d, Preview(strip),
            "and comes back off the limit from the position it was captured at, not from the limit");

        CompleteResize(strip);

        Assert.AreEqual(250d, TableHarness.ResolvedWidth(table, "a"), "and the release applies that");
    });

    /// <summary>
    /// Section 10: the table surface is not a constraint. A column may be widened past the
    /// viewport, and narrowed below its content, because the cell template owns its overflow.
    /// </summary>
    [TestMethod]
    public Task AResizeIsNotLimitedByTheViewportOrByTheContent() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync(column => column.MinWidth = 10);
        TableHeaderStrip strip = Strip(table);

        BeginResizeAt(strip, FirstSeparator);
        TrackResize(strip, FirstSeparator + 900);
        CompleteResize(strip);

        Assert.AreEqual(1100d, TableHarness.ResolvedWidth(table, "a"));
        Assert.AreEqual(1500d, TableHarness.TotalWidth(table), "the table scrolls to reach it");

        BeginResizeAt(strip, 1100);
        TrackResize(strip, 20);
        CompleteResize(strip);

        Assert.AreEqual(20d, TableHarness.ResolvedWidth(table, "a"),
            "observed content never becomes a new hard minimum");
    });

    // ------------------------------------------------------------------ ending the gesture

    [TestMethod]
    public Task EscapeLeavesTheWidthTheGestureStartedFrom() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync();
        TableHeaderStrip strip = Strip(table);
        Func<int> events = LayoutChanges(table);

        BeginResizeAt(strip, FirstSeparator);
        TrackResize(strip, FirstSeparator + 60);
        Assert.AreEqual(260d, Preview(strip), "the guide had moved");

        CancelGesture(strip);

        Assert.AreEqual(200d, TableHarness.ResolvedWidth(table, "a"), "and no width ever did");
        Assert.AreEqual(0, events(), "a cancelled gesture changed no layout");
    });

    /// <summary>The width captured at the press is the one in force then, not the baseline.</summary>
    [TestMethod]
    public Task EscapeRestoresAPreviousOverrideRatherThanTheBaseline() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync();
        TableHeaderStrip strip = Strip(table);

        BeginResizeAt(strip, FirstSeparator);
        TrackResize(strip, FirstSeparator + 100);
        CompleteResize(strip);

        BeginResizeAt(strip, 300);
        TrackResize(strip, 500);
        CancelGesture(strip);

        Assert.AreEqual(300d, TableHarness.ResolvedWidth(table, "a"));
    });

    [TestMethod]
    public Task AGestureThatChangedNoWidthReportsNothing() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync();
        TableHeaderStrip strip = Strip(table);
        Func<int> events = LayoutChanges(table);

        BeginResizeAt(strip, FirstSeparator);
        TrackResize(strip, FirstSeparator + 40);
        TrackResize(strip, FirstSeparator);
        CompleteResize(strip);

        Assert.AreEqual(200d, TableHarness.ResolvedWidth(table, "a"));
        Assert.AreEqual(0, events());
    });

    /// <summary>A completed resize is a width override, and section 18 persists it.</summary>
    [TestMethod]
    public Task ACompletedResizeBecomesAPersistedOverride() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync();
        TableHeaderStrip strip = Strip(table);

        BeginResizeAt(strip, FirstSeparator);
        TrackResize(strip, FirstSeparator + 75);
        CompleteResize(strip);

        Assert.AreEqual(275d, table.Layout.Widths["a"]);
    });

    // ------------------------------------------------------------------ helpers

    private static async Task<TableView> LoadAsync(Action<TableColumn>? configureFirst = null)
    {
        TableView table = TestData.Table(
            TestData.Column("a", 200), TestData.Column("b", 200), TestData.Column("c", 200));
        configureFirst?.Invoke(table.Columns[0]);

        table.Width = 700;
        table.Height = 200;

        await TableHarness.LoadAsync(table);
        table.UpdateLayout();
        return table;
    }

    private static Func<int> LayoutChanges(TableView table)
    {
        int count = 0;
        table.LayoutChanged += (_, kind) =>
        {
            Assert.AreEqual(TableLayoutChangeKind.ColumnResize, kind);
            count++;
        };
        return () => count;
    }

    private static TableHeaderStrip Strip(TableView table) =>
        SelectionHarness.Descendant<TableHeaderStrip>(table)
        ?? throw new AssertFailedException("The table realized no header strip.");

    // The gesture steps are private to the strip and Synapse grants no InternalsVisibleTo.
    // Reflection runs them without widening the control's surface for a test.

    private static bool BeginResizeAt(TableHeaderStrip strip, double x) =>
        (bool)Invoke(strip, "BeginResizeAt", x, 1u)!;

    private static void TrackResize(TableHeaderStrip strip, double x) =>
        Invoke(strip, "TrackResize", x);

    private static void CompleteResize(TableHeaderStrip strip) => Invoke(strip, "CompleteResize");

    /// <summary>The width the release would apply. Nothing else in the gesture changes a width.</summary>
    private static double Preview(TableHeaderStrip strip) =>
        (double)strip.GetType()
            .GetField("_previewWidth", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(strip)!;

    /// <summary>What Escape and a lost pointer capture both run.</summary>
    private static void CancelGesture(TableHeaderStrip strip) => Invoke(strip, "CancelGesture");

    private static object? Invoke(object target, string method, params object[] arguments) =>
        target.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, arguments);
}
