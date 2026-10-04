using Microsoft.VisualStudio.TestTools.UnitTesting;
using Synapse;

namespace Synapse_Tests;

/// <summary>
/// Section 6.1. "The table validates every column definition when it captures the schema at
/// Loaded. Missing or duplicate values are configuration errors rather than an unusable header
/// later."
/// </summary>
[TestClass]
public class SchemaValidationTests
{
    // ------------------------------------------------------------------ accepted

    [TestMethod]
    public Task Section6_1_AValidSchemaIsAccepted() => TestHost.RunAsync(async () =>
    {
        TableView table = TestData.Table(TestData.Column("a"), TestData.Column("b"));

        await TableHarness.LoadAsync(table);

        CollectionAssert.AreEqual(new[] { "a", "b" }, TableHarness.Order(table));
    });

    [TestMethod]
    public Task Section6_1_MaxWidthPositiveInfinityIsAccepted() => TestHost.RunAsync(async () =>
    {
        TableColumn column = TestData.Column("a");
        column.MaxWidth = double.PositiveInfinity;
        TableView table = TestData.Table(column);

        await TableHarness.LoadAsync(table);

        Assert.AreEqual(150d, TableHarness.ResolvedWidth(table, "a"), 0d);
    });

    // ------------------------------------------------------------------ rejected

    [TestMethod]
    public Task Section6_1_DuplicateIdIsRejected() =>
        RejectedAsync(() => new[] { TestData.Column("a"), TestData.Column("a") });

    [TestMethod]
    public Task Section6_1_EmptyIdIsRejected() =>
        RejectedAsync(() => new[] { new TableColumn { Id = string.Empty, DisplayName = "A" } });

    [TestMethod]
    public Task Section6_1_EmptyDisplayNameIsRejected() =>
        RejectedAsync(() => new[] { new TableColumn { Id = "a", DisplayName = string.Empty } });

    [TestMethod]
    public Task Section6_1_ZeroDefaultWidthIsRejected() =>
        RejectedAsync(() => new[] { TestData.Column("a", 0) });

    [TestMethod]
    public Task Section6_1_NegativeDefaultWidthIsRejected() =>
        RejectedAsync(() => new[] { TestData.Column("a", -10) });

    [TestMethod]
    public Task Section6_1_NaNDefaultWidthIsRejected() =>
        RejectedAsync(() => new[] { TestData.Column("a", double.NaN) });

    [TestMethod]
    public Task Section6_1_InfiniteDefaultWidthIsRejected() =>
        RejectedAsync(() => new[] { TestData.Column("a", double.PositiveInfinity) });

    [TestMethod]
    public Task Section6_1_NegativeMinWidthIsRejected() =>
        RejectedAsync(() => new[] { Column(c => c.MinWidth = -1) });

    [TestMethod]
    public Task Section6_1_NaNMaxWidthIsRejected() =>
        RejectedAsync(() => new[] { Column(c => c.MaxWidth = double.NaN) });

    [TestMethod]
    public Task Section6_1_MinWidthAboveMaxWidthIsRejected() => RejectedAsync(() => new[]
    {
        Column(c =>
        {
            c.MinWidth = 300;
            c.MaxWidth = 200;
            c.Width = 250;
        }),
    });

    /// <summary>
    /// Section 6.1: a column with no Id is legal, because a table whose layout is never saved has
    /// no persistence keys to invent. There used to be a rejection here for a column that claimed
    /// to sort without a comparer; it cannot be written any more, which is the point of the change.
    /// </summary>
    [TestMethod]
    public Task Section6_1_AColumnWithNoIdIsAccepted() => TestHost.RunAsync(async () =>
    {
        TableView table = TestData.Table(
            new TableColumn { DisplayName = "A" }, new TableColumn { DisplayName = "B" });

        await TableHarness.LoadAsync(table);

        Assert.AreEqual(2, TableHarness.VisibleColumns(table).Length);
        Assert.AreEqual(0, table.Layout.Order.Count, "an unnamed column is not persisted");
    });

    [TestMethod]
    public Task Section6_1_ASecondRowOrderColumnIsRejected() => RejectedAsync(() => new[]
    {
        // "at most one column defines the row order".
        Column(c => c.DefinesRowOrder = true),
        Column(c => c.DefinesRowOrder = true, id: "b"),
    });

    [TestMethod]
    public Task Section6_1_ZeroVisibleColumnsIsRejected() => RejectedAsync(() => new[]
    {
        Column(c => c.IsVisible = false, "a"),
        Column(c => c.IsVisible = false, "b"),
    });

    [TestMethod]
    public Task Section6_1_ATableDeclaringNoColumnsHasNoVisibleColumn() => TestHost.RunAsync(async () =>
    {
        // "at least one column remains visible" — a table declaring no columns has none.
        TableView table = TestData.Table();

        Exception error = await TableHarness.LoadExpectingFailureAsync(table);

        Assert.IsInstanceOfType<InvalidOperationException>(error, error.ToString());
    });

    // ------------------------------------------------------- setup-only schema

    [TestMethod]
    public Task Section6_1_AddingAColumnAfterTheFirstLoadedIsAConfigurationError() =>
        TestHost.RunAsync(async () =>
        {
            TableView table = TestData.Table(TestData.Column("a"));
            await TableHarness.LoadAsync(table);

            // "Changing a captured column definition ... after that point is unsupported and is a
            // configuration error."
            Expect.Throws<InvalidOperationException>(() => table.Columns.Add(TestData.Column("b")));
        });

    [TestMethod]
    public Task Section6_1_RemovingAColumnAfterTheFirstLoadedIsAConfigurationError() =>
        TestHost.RunAsync(async () =>
        {
            TableView table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
            await TableHarness.LoadAsync(table);

            Expect.Throws<InvalidOperationException>(() => table.Columns.RemoveAt(1));
        });

    // ------------------------------------------------------------------ helpers

    private static TableColumn Column(Action<TableColumn> configure, string id = "a")
    {
        TableColumn column = TestData.Column(id);
        configure(column);
        return column;
    }

    /// <summary>
    /// Columns are built inside the callback: <see cref="TableColumn"/> is a
    /// <c>DependencyObject</c> and can only be constructed on the UI thread.
    /// </summary>
    private static Task RejectedAsync(Func<TableColumn[]> build) => TestHost.RunAsync(async () =>
    {
        TableView table = TestData.Table(build());

        Exception error = await TableHarness.LoadExpectingFailureAsync(table);

        Assert.IsInstanceOfType<InvalidOperationException>(error, error.ToString());
    });
}
