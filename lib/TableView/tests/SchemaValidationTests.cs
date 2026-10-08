using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Syno.TableView.Tests;

/// <summary>
/// Section 6.1. "The table validates every column definition when it captures the schema at
/// Loaded. Missing or duplicate values are configuration errors rather than an unusable header
/// later."
/// </summary>
[TestClass]
public class SchemaValidationTests
{
    // ------------------------------------------------------------------ rejected

    [TestMethod]
    public Task Section6_1_DuplicateIdIsRejected() =>
        RejectedAsync(() => new[] { TestData.Column("a"), TestData.Column("a") });

    [TestMethod]
    public Task Section6_1_EmptyIdIsRejected() =>
        RejectedAsync(() =>
            new[]
            {
                new Column { Id = string.Empty, DisplayName = "A" },
            }
        );

    [TestMethod]
    public Task Section6_1_EmptyDisplayNameIsRejected() =>
        RejectedAsync(() =>
            new[]
            {
                new Column { Id = "a", DisplayName = string.Empty },
            }
        );

    [TestMethod]
    public Task Section6_1_ZeroDefaultWidthIsRejected() =>
        RejectedAsync(() => new[] { TestData.Column("a", 0) });

    [TestMethod]
    public Task Section6_1_NaNDefaultWidthIsRejected() =>
        RejectedAsync(() => new[] { TestData.Column("a", double.NaN) });

    [TestMethod]
    public Task Section6_1_NegativeMinWidthIsRejected() =>
        RejectedAsync(() => new[] { Column(c => c.MinWidth = -1) });

    /// <summary>
    /// Section 6.1: a column with no Id is legal, because a table whose layout is never saved has
    /// no persistence keys to invent. There used to be a rejection here for a column that claimed
    /// to sort without a comparer; it cannot be written any more, which is the point of the change.
    /// </summary>
    [TestMethod]
    public Task Section6_1_AColumnWithNoIdIsAccepted() =>
        TestHost.RunAsync(async () =>
        {
            Table table = TestData.Table(
                new Column { DisplayName = "A" },
                new Column { DisplayName = "B" }
            );

            await TableHarness.LoadAsync(table);

            Assert.AreEqual(2, TableHarness.VisibleColumns(table).Length);
            Assert.AreEqual(0, table.Layout.Order.Count, "an unnamed column is not persisted");
        });

    [TestMethod]
    public Task Section6_1_ASecondRowOrderColumnIsRejected() =>
        RejectedAsync(() =>
            new[]
            {
                // "at most one column defines the row order".
                Column(c => c.DefinesRowOrder = true),
                Column(c => c.DefinesRowOrder = true, id: "b"),
            }
        );

    [TestMethod]
    public Task Section6_1_ZeroVisibleColumnsIsRejected() =>
        RejectedAsync(() =>
            new[] { Column(c => c.IsVisible = false, "a"), Column(c => c.IsVisible = false, "b") }
        );

    // ------------------------------------------------------- setup-only schema

    [TestMethod]
    public Task Section6_1_AddingAColumnAfterTheFirstLoadedIsAConfigurationError() =>
        TestHost.RunAsync(async () =>
        {
            Table table = TestData.Table(TestData.Column("a"));
            await TableHarness.LoadAsync(table);

            // "Changing a captured column definition ... after that point is unsupported and is a
            // configuration error."
            Expect.Throws<InvalidOperationException>(() => table.Columns.Add(TestData.Column("b")));
        });

    // ------------------------------------------------------------------ helpers

    private static Column Column(Action<Column> configure, string id = "a")
    {
        Column column = TestData.Column(id);
        configure(column);
        return column;
    }

    /// <summary>
    /// Columns are built inside the callback: <see cref="Column"/> is a
    /// <c>DependencyObject</c> and can only be constructed on the UI thread.
    /// </summary>
    private static Task RejectedAsync(Func<Column[]> build) =>
        TestHost.RunAsync(async () =>
        {
            Table table = TestData.Table(build());

            Exception error = await TableHarness.LoadExpectingFailureAsync(table);

            Assert.IsInstanceOfType<InvalidOperationException>(error, error.ToString());
        });
}
