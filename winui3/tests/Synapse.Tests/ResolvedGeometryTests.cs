using Microsoft.VisualStudio.TestTools.UnitTesting;
using Synapse;

namespace Synapse_Tests;

/// <summary>
/// The single resolved geometry the header strip and every realized row read.
/// Section 6.1: "the resolved width is clamped to [MinWidth, MaxWidth]" and "hidden columns retain
/// their resolved position and most recent width".
/// </summary>
[TestClass]
public class ResolvedGeometryTests
{
    private static async Task<TableView> ThreeColumnTableAsync()
    {
        TableView table = TestData.Table(
            TestData.Column("a", 100), TestData.Column("b", 200), TestData.Column("c", 150));
        await TableHarness.LoadAsync(table);
        return table;
    }

    [TestMethod]
    public Task CumulativeOffsetsFollowTheVisibleWidths() => TestHost.RunAsync(async () =>
    {
        TableView table = await ThreeColumnTableAsync();

        (string Id, double Offset, double Width)[] visible = TableHarness.VisibleColumns(table);

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, visible.Select(v => v.Id).ToArray());
        Assert.AreEqual(0d, visible[0].Offset, 0d);
        Assert.AreEqual(100d, visible[1].Offset, 0d);
        Assert.AreEqual(300d, visible[2].Offset, 0d);
    });

    [TestMethod]
    public Task TotalWidthIsTheSumOfTheVisibleWidths() => TestHost.RunAsync(async () =>
    {
        TableView table = await ThreeColumnTableAsync();

        Assert.AreEqual(450d, TableHarness.TotalWidth(table), 0d);
    });

    [TestMethod]
    public Task HidingAMiddleColumnShiftsTheColumnsAfterIt() => TestHost.RunAsync(async () =>
    {
        TableView table = await ThreeColumnTableAsync();

        table.Layout = TestData.Layout(
            visibility: new Dictionary<string, bool> { ["b"] = false });

        (string Id, double Offset, double Width)[] visible = TableHarness.VisibleColumns(table);

        CollectionAssert.AreEqual(new[] { "a", "c" }, visible.Select(v => v.Id).ToArray());
        Assert.AreEqual(0d, visible[0].Offset, 0d);
        Assert.AreEqual(100d, visible[1].Offset, 0d, "c moves into b's place");
        Assert.AreEqual(250d, TableHarness.TotalWidth(table), 0d);
    });

    [TestMethod]
    public Task AHiddenColumnKeepsItsPositionAndItsStoredWidth() => TestHost.RunAsync(async () =>
    {
        TableView table = await ThreeColumnTableAsync();

        table.Layout = TestData.Layout(
            visibility: new Dictionary<string, bool> { ["b"] = false },
            widths: new Dictionary<string, double> { ["b"] = 260 });

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, TableHarness.Order(table),
            "a hidden column keeps its resolved position");
        Assert.AreEqual(260d, TableHarness.ResolvedWidth(table, "b"), 0d,
            "a hidden column keeps its most recent width");
        Assert.AreEqual(250d, TableHarness.TotalWidth(table), 0d,
            "a hidden column contributes no width");
    });

    [TestMethod]
    public Task AWidthOverrideWinsOverTheBaselineAndAResetRestoresIt() => TestHost.RunAsync(async () =>
    {
        TableView table = await ThreeColumnTableAsync();

        table.Layout = TestData.Layout(widths: new Dictionary<string, double> { ["b"] = 340 });

        Assert.AreEqual(340d, TableHarness.ResolvedWidth(table, "b"), 0d);
        Assert.AreEqual(590d, TableHarness.TotalWidth(table), 0d);
        Assert.AreEqual(440d, TableHarness.VisibleColumns(table)[2].Offset, 0d);

        table.Layout = TestData.Layout();

        Assert.AreEqual(200d, TableHarness.ResolvedWidth(table, "b"), 0d, "the baseline returns");
        Assert.AreEqual(450d, TableHarness.TotalWidth(table), 0d);
    });

    [TestMethod]
    public Task ReorderingChangesTheOffsetsButNotTheWidths() => TestHost.RunAsync(async () =>
    {
        TableView table = await ThreeColumnTableAsync();

        table.Layout = TestData.Layout(order: new[] { "c", "b", "a" });

        (string Id, double Offset, double Width)[] visible = TableHarness.VisibleColumns(table);

        CollectionAssert.AreEqual(new[] { "c", "b", "a" }, visible.Select(v => v.Id).ToArray());
        Assert.AreEqual(0d, visible[0].Offset, 0d);
        Assert.AreEqual(150d, visible[1].Offset, 0d);
        Assert.AreEqual(350d, visible[2].Offset, 0d);
        Assert.AreEqual(450d, TableHarness.TotalWidth(table), 0d);
    });

    [TestMethod]
    public Task TheBaselineWidthIsTheDeclaredDefaultClampedToTheColumnBounds() =>
        TestHost.RunAsync(async () =>
        {
            TableColumn narrow = TestData.Column("a", 20);
            narrow.MinWidth = 60;
            TableColumn wide = TestData.Column("b", 900);
            wide.MaxWidth = 400;
            TableView table = TestData.Table(narrow, wide);

            await TableHarness.LoadAsync(table);

            Assert.AreEqual(60d, TableHarness.ResolvedWidth(table, "a"), 0d);
            Assert.AreEqual(400d, TableHarness.ResolvedWidth(table, "b"), 0d);
        });
}
