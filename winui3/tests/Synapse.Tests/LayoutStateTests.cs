using Microsoft.VisualStudio.TestTools.UnitTesting;
using Synapse;

namespace Synapse_Tests;

/// <summary>
/// Section 18, layout persistence. Every test names the rule it holds the control to.
/// </summary>
[TestClass]
public class LayoutStateTests
{
    // ------------------------------------------------------------ round trip

    [TestMethod]
    public Task Section18_RoundTripLeavesTheLayoutUnchanged() => TestHost.RunAsync(async () =>
    {
        TableView table = TestData.Table(
            TestData.Column("a", 100), TestData.Column("b", 200), TestData.Column("c", 150));
        await TableHarness.LoadAsync(table);

        table.Layout = TestData.Layout(
            order: new[] { "c", "a", "b" },
            visibility: new Dictionary<string, bool> { ["b"] = false },
            widths: new Dictionary<string, double> { ["a"] = 220 });

        TableLayout first = table.Layout;
        table.Layout = first;
        TableLayout second = table.Layout;

        CollectionAssert.AreEqual(first.Order.ToArray(), second.Order.ToArray());
        CollectionAssert.AreEquivalent(
            first.Visibility.ToArray(), second.Visibility.ToArray());
        CollectionAssert.AreEquivalent(first.Widths.ToArray(), second.Widths.ToArray());
        Assert.AreEqual(first.SortColumnId, second.SortColumnId);
        Assert.AreEqual(first.SortDirection, second.SortDirection);
    });

    // --------------------------------------------------------- sparse output

    [TestMethod]
    public Task Section18_ReadingLayoutEmitsSparseMaps() => TestHost.RunAsync(async () =>
    {
        // "Visibility and Widths are intentionally sparse: they contain only values
        // that override declared visibility and width baselines."
        TableView table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
        await TableHarness.LoadAsync(table);

        TableLayout state = table.Layout;

        CollectionAssert.AreEqual(new[] { "a", "b" }, state.Order.ToArray());
        Assert.AreEqual(0, state.Visibility.Count, "a column at its baseline must not appear");
        Assert.AreEqual(0, state.Widths.Count, "a column at its baseline must not appear");
    });

    [TestMethod]
    public Task Section18_ReadingLayoutEmitsOnlyTheOverriddenColumns() => TestHost.RunAsync(async () =>
    {
        TableView table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
        await TableHarness.LoadAsync(table);

        table.Layout = TestData.Layout(
            widths: new Dictionary<string, double> { ["a"] = 200 },
            visibility: new Dictionary<string, bool> { ["b"] = false });

        TableLayout state = table.Layout;

        CollectionAssert.AreEqual(new[] { "a" }, state.Widths.Keys.ToArray());
        Assert.AreEqual(200d, state.Widths["a"], 0d);
        CollectionAssert.AreEqual(new[] { "b" }, state.Visibility.Keys.ToArray());
        Assert.IsFalse(state.Visibility["b"]);
    });

    // ----------------------------------------------------- defensive restore

    [TestMethod]
    public Task Section18_UnknownColumnIdsAreIgnored() => TestHost.RunAsync(async () =>
    {
        TableView table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
        await TableHarness.LoadAsync(table);

        table.Layout = TestData.Layout(
            order: new[] { "ghost", "b", "a" },
            visibility: new Dictionary<string, bool> { ["ghost"] = false },
            widths: new Dictionary<string, double> { ["ghost"] = 400 });

        CollectionAssert.AreEqual(new[] { "b", "a" }, TableHarness.Order(table));
    });

    [TestMethod]
    public Task Section18_DuplicateIdsAreIgnoredAfterTheFirstOccurrence() => TestHost.RunAsync(async () =>
    {
        TableView table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
        await TableHarness.LoadAsync(table);

        table.Layout = TestData.Layout(order: new[] { "b", "b", "a" });

        CollectionAssert.AreEqual(new[] { "b", "a" }, TableHarness.Order(table));
    });

    [TestMethod]
    public Task Section18_NewColumnsAreAppendedInDefinitionOrder() => TestHost.RunAsync(async () =>
    {
        TableView table = TestData.Table(
            TestData.Column("a"), TestData.Column("b"), TestData.Column("c"));
        await TableHarness.LoadAsync(table);

        // The saved state predates columns a and b.
        table.Layout = TestData.Layout(order: new[] { "c" });

        CollectionAssert.AreEqual(new[] { "c", "a", "b" }, TableHarness.Order(table));
    });

    [TestMethod]
    public Task Section18_AnOmittedWidthClearsAnEarlierOverride() => TestHost.RunAsync(async () =>
    {
        // "treat Visibility and Widths as complete override maps: omitted values use
        // their column baseline and clear any earlier override".
        TableView table = TestData.Table(TestData.Column("a", 150));
        await TableHarness.LoadAsync(table);

        table.Layout = TestData.Layout(widths: new Dictionary<string, double> { ["a"] = 300 });
        Assert.AreEqual(300d, TableHarness.ResolvedWidth(table, "a"), 0d);

        table.Layout = TestData.Layout();

        Assert.AreEqual(150d, TableHarness.ResolvedWidth(table, "a"), 0d);
    });

    [TestMethod]
    public Task Section18_AnOmittedVisibilityClearsAnEarlierOverride() => TestHost.RunAsync(async () =>
    {
        TableView table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
        await TableHarness.LoadAsync(table);

        table.Layout = TestData.Layout(
            visibility: new Dictionary<string, bool> { ["b"] = false });
        Assert.IsFalse(TableHarness.IsVisible(table, "b"));

        table.Layout = TestData.Layout();

        Assert.IsTrue(TableHarness.IsVisible(table, "b"));
    });

    [TestMethod]
    public Task Section18_NonFiniteWidthsAreIgnored() => TestHost.RunAsync(async () =>
    {
        TableView table = TestData.Table(TestData.Column("a", 150), TestData.Column("b", 150));
        await TableHarness.LoadAsync(table);

        table.Layout = TestData.Layout(widths: new Dictionary<string, double>
        {
            ["a"] = double.NaN,
            ["b"] = double.PositiveInfinity,
        });

        Assert.AreEqual(150d, TableHarness.ResolvedWidth(table, "a"), 0d);
        Assert.AreEqual(150d, TableHarness.ResolvedWidth(table, "b"), 0d);
    });

    [TestMethod]
    public Task Section18_NonPositiveWidthsAreIgnored() => TestHost.RunAsync(async () =>
    {
        TableView table = TestData.Table(TestData.Column("a", 150), TestData.Column("b", 150));
        await TableHarness.LoadAsync(table);

        table.Layout = TestData.Layout(widths: new Dictionary<string, double>
        {
            ["a"] = 0,
            ["b"] = -40,
        });

        Assert.AreEqual(150d, TableHarness.ResolvedWidth(table, "a"), 0d);
        Assert.AreEqual(150d, TableHarness.ResolvedWidth(table, "b"), 0d);
    });

    [TestMethod]
    public Task Section18_AWidthForANonResizableColumnIsIgnored() => TestHost.RunAsync(async () =>
    {
        TableColumn fixedWidth = TestData.Column("a", 150);
        fixedWidth.CanResize = false;
        TableView table = TestData.Table(fixedWidth);
        await TableHarness.LoadAsync(table);

        table.Layout = TestData.Layout(widths: new Dictionary<string, double> { ["a"] = 300 });

        Assert.AreEqual(150d, TableHarness.ResolvedWidth(table, "a"), 0d);
    });

    [TestMethod]
    public Task Section18_ValidWidthsAreClampedToTheColumnBounds() => TestHost.RunAsync(async () =>
    {
        TableColumn bounded = TestData.Column("a", 150);
        bounded.MinWidth = 100;
        bounded.MaxWidth = 200;
        TableView table = TestData.Table(bounded);
        await TableHarness.LoadAsync(table);

        table.Layout = TestData.Layout(widths: new Dictionary<string, double> { ["a"] = 500 });
        Assert.AreEqual(200d, TableHarness.ResolvedWidth(table, "a"), 0d, "clamped to MaxWidth");

        table.Layout = TestData.Layout(widths: new Dictionary<string, double> { ["a"] = 10 });
        Assert.AreEqual(100d, TableHarness.ResolvedWidth(table, "a"), 0d, "clamped to MinWidth");
    });

    [TestMethod]
    public Task Section18_ARequiredColumnSavedAsHiddenIsRestored() => TestHost.RunAsync(async () =>
    {
        TableColumn required = TestData.Column("a");
        required.CanHide = false;
        TableView table = TestData.Table(required, TestData.Column("b"));
        await TableHarness.LoadAsync(table);

        table.Layout = TestData.Layout(
            visibility: new Dictionary<string, bool> { ["a"] = false });

        Assert.IsTrue(TableHarness.IsVisible(table, "a"), "CanHide == false prevents hiding");
    });

    [TestMethod]
    public Task Section18_AtLeastOneColumnStaysVisible() => TestHost.RunAsync(async () =>
    {
        TableView table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
        await TableHarness.LoadAsync(table);

        table.Layout = TestData.Layout(visibility: new Dictionary<string, bool>
        {
            ["a"] = false,
            ["b"] = false,
        });

        Assert.AreEqual(1, TableHarness.VisibleColumns(table).Length,
            "guarantee at least one visible column");
    });

    // ------------------------------------------------------------ event rule

    [TestMethod]
    public Task Section18_AssigningLayoutRaisesNoLayoutChanged() => TestHost.RunAsync(async () =>
    {
        // "It is not raised by initial setup or by assigning Layout; this prevents
        // restore-and-persist loops."
        TableView table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
        int raised = 0;
        table.LayoutChanged += (_, _) => raised++;

        await TableHarness.LoadAsync(table);
        table.Layout = TestData.Layout(
            order: new[] { "b", "a" },
            widths: new Dictionary<string, double> { ["a"] = 200 },
            visibility: new Dictionary<string, bool> { ["b"] = false });

        Assert.AreEqual(0, raised);
    });

    // ----------------------------------------------------- restore before load

    [TestMethod]
    public Task Section18_StateAppliedBeforeTheFirstLoadedIsHeldThenResolved() =>
        TestHost.RunAsync(async () =>
        {
            TableView table = TestData.Table(
                TestData.Column("a", 150), TestData.Column("b", 150), TestData.Column("c", 150));

            table.Layout = TestData.Layout(
                order: new[] { "c", "b", "a" },
                widths: new Dictionary<string, double> { ["b"] = 260 });

            await TableHarness.LoadAsync(table);

            CollectionAssert.AreEqual(new[] { "c", "b", "a" }, TableHarness.Order(table));
            Assert.AreEqual(260d, TableHarness.ResolvedWidth(table, "b"), 0d);
        });

    [TestMethod]
    public Task Section18_AssigningLayoutRejectsNull() => TestHost.RunAsync(async () =>
    {
        TableView table = TestData.Table(TestData.Column("a"));
        await TableHarness.LoadAsync(table);

        Expect.Throws<ArgumentNullException>(() => table.Layout = null!);
    });
}
