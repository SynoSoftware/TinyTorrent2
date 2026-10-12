using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Syno.TableView.Tests;

/// <summary>
/// Section 18, layout persistence. Every test names the rule it holds the control to.
/// </summary>
[TestClass]
public class LayoutStateTests
{
    // --------------------------------------------------------- sparse output

    [TestMethod]
    public Task Section18_ReadingLayoutEmitsOnlyTheOverriddenColumns() =>
        TestHost.RunAsync(async () =>
        {
            Table table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
            await TableHarness.LoadAsync(table);

            table.Layout = TestData.Layout(
                visibility: new Dictionary<string, bool> { ["b"] = false }
            );

            ColumnLayout state = table.Layout;

            CollectionAssert.AreEqual(new[] { "b" }, state.VisibilityOverrides.Keys.ToArray());
            Assert.IsFalse(state.VisibilityOverrides["b"]);
        });

    // ----------------------------------------------------- defensive restore

    [TestMethod]
    public Task Section18_UnknownColumnIdsAreIgnored() =>
        TestHost.RunAsync(async () =>
        {
            Table table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
            await TableHarness.LoadAsync(table);

            table.Layout = TestData.Layout(
                order: new[] { "ghost", "b", "a" },
                visibility: new Dictionary<string, bool> { ["ghost"] = false }
            );

            CollectionAssert.AreEqual(new[] { "b", "a" }, TableHarness.Order(table));
        });

    [TestMethod]
    public Task Section18_DuplicateIdsAreIgnoredAfterTheFirstOccurrence() =>
        TestHost.RunAsync(async () =>
        {
            Table table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
            await TableHarness.LoadAsync(table);

            table.Layout = TestData.Layout(order: new[] { "b", "b", "a" });

            CollectionAssert.AreEqual(new[] { "b", "a" }, TableHarness.Order(table));
        });

    [TestMethod]
    public Task Section18_NewColumnsAreAppendedInDefinitionOrder() =>
        TestHost.RunAsync(async () =>
        {
            Table table = TestData.Table(
                TestData.Column("a"),
                TestData.Column("b"),
                TestData.Column("c")
            );
            await TableHarness.LoadAsync(table);

            // The saved state predates columns a and b.
            table.Layout = TestData.Layout(order: new[] { "c" });

            CollectionAssert.AreEqual(new[] { "c", "a", "b" }, TableHarness.Order(table));
        });

    [TestMethod]
    public Task Section18_AnOmittedVisibilityClearsAnEarlierOverride() =>
        TestHost.RunAsync(async () =>
        {
            Table table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
            await TableHarness.LoadAsync(table);

            table.Layout = TestData.Layout(
                visibility: new Dictionary<string, bool> { ["b"] = false }
            );
            Assert.IsFalse(TableHarness.IsVisible(table, "b"));

            table.Layout = TestData.Layout();

            Assert.IsTrue(TableHarness.IsVisible(table, "b"));
        });

    [TestMethod]
    public Task Section18_ARequiredColumnSavedAsHiddenIsRestored() =>
        TestHost.RunAsync(async () =>
        {
            Column required = TestData.Column("a");
            required.CanHide = false;
            Table table = TestData.Table(required, TestData.Column("b"));
            await TableHarness.LoadAsync(table);

            table.Layout = TestData.Layout(
                visibility: new Dictionary<string, bool> { ["a"] = false }
            );

            Assert.IsTrue(TableHarness.IsVisible(table, "a"), "CanHide == false prevents hiding");
        });

    [TestMethod]
    public Task Section18_AtLeastOneColumnStaysVisible() =>
        TestHost.RunAsync(async () =>
        {
            Table table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
            await TableHarness.LoadAsync(table);

            table.Layout = TestData.Layout(
                visibility: new Dictionary<string, bool> { ["a"] = false, ["b"] = false }
            );

            Assert.AreEqual(
                1,
                TableHarness.VisibleColumns(table).Length,
                "guarantee at least one visible column"
            );
        });

    // ------------------------------------------------------------ event rule

    [TestMethod]
    public Task Section18_AssigningLayoutRaisesNoLayoutChanged() =>
        TestHost.RunAsync(async () =>
        {
            // "It is not raised by initial setup or by assigning Layout; this prevents
            // restore-and-persist loops."
            Table table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
            int raised = 0;
            table.LayoutChanged += (_, _) => raised++;

            await TableHarness.LoadAsync(table);
            table.Layout = TestData.Layout(
                order: new[] { "b", "a" },
                visibility: new Dictionary<string, bool> { ["b"] = false }
            );

            Assert.AreEqual(0, raised);
        });

    // ----------------------------------------------------- restore before load

    [TestMethod]
    public Task Section18_StateAppliedBeforeTheFirstLoadedIsHeldThenResolved() =>
        TestHost.RunAsync(async () =>
        {
            Table table = TestData.Table(
                TestData.Column("a", 150),
                TestData.Column("b", 150),
                TestData.Column("c", 150)
            );

            table.Layout = TestData.Layout(order: new[] { "c", "b", "a" });

            await TableHarness.LoadAsync(table);

            CollectionAssert.AreEqual(new[] { "c", "b", "a" }, TableHarness.Order(table));
        });
}
