using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Syno.TableView.Tests;

/// <summary>
/// The single effective layout the header strip and every realized row read.
/// Section 6.1: "the effective width is never below MinWidth" and "hidden columns retain
/// their effective position and most recent width".
/// </summary>
[TestClass]
public class EffectiveLayoutTests
{
    private static async Task<Table> ThreeColumnTableAsync()
    {
        Table table = TestData.Table(
            TestData.Column("a", 100),
            TestData.Column("b", 200),
            TestData.Column("c", 150)
        );
        await TableHarness.LoadAsync(table);
        return table;
    }

    [TestMethod]
    public Task HidingAMiddleColumnShiftsTheColumnsAfterIt() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await ThreeColumnTableAsync();

            table.Layout = TestData.Layout(
                visibility: new Dictionary<string, bool> { ["b"] = false }
            );

            (string Id, double Offset, double Width)[] visible = TableHarness.VisibleColumns(table);

            CollectionAssert.AreEqual(new[] { "a", "c" }, visible.Select(v => v.Id).ToArray());
            Assert.AreEqual(0d, visible[0].Offset, 0d);
            Assert.AreEqual(100d, visible[1].Offset, 0d, "c moves into b's place");
            Assert.AreEqual(250d, TableHarness.TotalWidth(table), 0d);
        });

    [TestMethod]
    public Task AHiddenColumnKeepsItsStoredWidthInTheSnapshot() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await ThreeColumnTableAsync();

            table.Layout = TestData.Layout(
                visibility: new Dictionary<string, bool> { ["b"] = false },
                widths: new Dictionary<string, double> { ["b"] = 260 }
            );

            Assert.AreEqual(
                260d,
                table.Layout.WidthOverrides["b"],
                0d,
                "a hidden column keeps its most recent width in the snapshot"
            );
            Assert.IsFalse(
                table.Layout.VisibilityOverrides["b"],
                "and is still recorded as hidden"
            );
        });

    [TestMethod]
    public Task ReorderingChangesTheOffsetsButNotTheWidths() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await ThreeColumnTableAsync();

            table.Layout = TestData.Layout(order: new[] { "c", "b", "a" });

            (string Id, double Offset, double Width)[] visible = TableHarness.VisibleColumns(table);

            CollectionAssert.AreEqual(new[] { "c", "b", "a" }, visible.Select(v => v.Id).ToArray());
            Assert.AreEqual(0d, visible[0].Offset, 0d);
            Assert.AreEqual(150d, visible[1].Offset, 0d);
            Assert.AreEqual(350d, visible[2].Offset, 0d);
            Assert.AreEqual(450d, TableHarness.TotalWidth(table), 0d);
        });

    [TestMethod]
    public Task TheBaselineWidthIsTheDeclaredDefaultRaisedToMinWidth() =>
        TestHost.RunAsync(async () =>
        {
            Column narrow = TestData.Column("a", 20);
            narrow.MinWidth = 60;
            Table table = TestData.Table(narrow);

            await TableHarness.LoadAsync(table);

            // A reset returns to the baseline, past the first fill.
            table.ResetLayout();

            Assert.AreEqual(60d, TableHarness.EffectiveWidth(table, "a"), 0d);
        });
}
