using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Storage.Streams;

namespace Syno.TableView.Tests;

/// <summary>One row with a stable key and a sort rank, so equal values can be told apart.</summary>
internal sealed class SortRow
{
    internal SortRow(string key, int rank)
    {
        Key = key;
        Rank = rank;
    }

    internal string Key { get; }

    internal int Rank { get; set; }

    public override string ToString() => Key;
}

/// <summary>
/// Section 9 and section 18's sort clauses. The two real input paths both call the strip's own
/// activation, so these tests drive that one entry point; <see cref="RealSortTests"/> drives it
/// with injected mouse and key messages.
/// </summary>
[TestClass]
public class SortingTests
{
    [TestMethod]
    public Task ABlockedProjectionLeavesSelectionResponsiveAndCannotPublishOverANewerSource() =>
        TestHost.RunAsync(async () =>
        {
            var ui = Environment.CurrentManagedThreadId;
            var comparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var comparer = Comparer<int>.Create((left, right) =>
            {
                if (Environment.CurrentManagedThreadId == ui)
                    throw new AssertFailedException("Sorting ran on the UI thread.");
                comparing.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The test did not release the comparison.");
                return left.CompareTo(right);
            });
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 }, configure: table =>
                table.Schema<SortRow>().SortKey(table.Columns[0], row =>
                {
                    // The request's own projection uses the keys read here. The test
                    // clears the sort before a later projection could read them on the worker.
                    Assert.AreEqual(ui, Environment.CurrentManagedThreadId, "A sort request reads its keys on the UI thread.");
                    return row.Rank;
                }, comparer));
            try
            {
                h.Table.Sort = new(h.Table.Columns[0]);
                await comparing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                h.Table.Selection = new(new object[] { h.Rows[1] }, h.Rows[1]);
                Proof.Call(h.Table, "SelectItem", h.Rows[2], false, false);
                Assert.AreSame(h.Rows[2], h.Table.Selection.Current, "A blocked projection must not hold selection input.");
                h.Table.ItemsSource = new[] { new SortRow("latest", 7) };
                h.Table.Sort = null;
            }
            finally
            {
                release.Set();
            }
            await h.Table.ViewReady.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { "latest" }, h.ViewKeys());
            Assert.AreEqual(0, h.Table.Selection.Items.Count);
        });

    [TestMethod]
    public Task FailedLayoutComparisonPreservesAcceptedState() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(
                new[] { 3, 1, 2 },
                configure: table =>
                {
                    table
                        .Schema<SortRow>()
                        .SortKey<int>(
                            table.Columns[1],
                            _ => throw new InvalidOperationException("Unavailable sort key")
                        );
                    table.Layout = TestData.Layout(
                        visibility: new Dictionary<string, bool> { ["b"] = false },
                        sortColumnId: "a"
                    );
                }
            );
            h.Table.Selection = new(new object[] { h.Rows[0] });
            TableHarness.SetWidth(h.Table, "a", 230);
            int layouts = 0;
            int selections = 0;
            h.Table.LayoutChanged += (_, _) => layouts++;
            h.Table.SelectionChanged += (_, _) => selections++;

            Expect.Throws<InvalidOperationException>(() =>
                h.Table.Layout = TestData.Layout(order: new[] { "b", "c", "a" }, sortColumnId: "b")
            );

            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, TableHarness.Order(h.Table));
            CollectionAssert.AreEqual(new[] { "k1", "k2", "k0" }, h.ViewKeys());
            Assert.IsFalse(TableHarness.IsVisible(h.Table, "b"));
            Assert.AreEqual(230d, TableHarness.EffectiveWidth(h.Table, "a"));
            Assert.AreEqual("a", h.Table.Layout.SortColumnId);
            Assert.AreSame(h.Rows[0], h.Table.Selection.Current);
            Assert.AreEqual(0, layouts);
            Assert.AreEqual(0, selections);
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task FailedReleaseCanRecoverThroughSortWithoutReleasingLaterArrivals(bool restore) =>
        TestHost.RunAsync(async () =>
        {
            bool fails = false;
            SortHarness h = await SortHarness.LoadAsync(
                new[] { 1, 2, 3 },
                sortableColumns: 2,
                configure: table =>
                {
                    table
                        .Schema<SortRow>()
                        .SortKey(
                            table.Columns[0],
                            row =>
                                fails
                                    ? throw new InvalidOperationException("Unavailable sort key")
                                    : row.Rank
                        );
                    table.SortInterval = TimeSpan.Zero;
                    table.Sort = new(table.Columns[0]);
                }
            );
            SortRow removed = h.Rows[0];
            Proof.Call(h.Table, "PointAt", h.Rows[1]);
            h.Rows.Remove(removed);
            await h.Table.ViewReady;
            fails = true;

            // The release re-projects the accepted sort, so its keys are read on the worker:
            // the failure faults ViewReady and reaches the UI exception boundary.
            var failure = TestHost.TrapAsync();
            h.Table.Release(new object[] { removed });
            Assert.IsInstanceOfType<InvalidOperationException>(await failure.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(h.Table.ViewReady.IsFaulted);

            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2" }, h.ViewKeys());
            Assert.IsTrue((bool)Proof.Call(h.Table, "IsHeld", removed)!);
            if (restore)
                h.Table.Layout = TestData.Layout(sortColumnId: "b");
            else
                h.Table.Sort = new(h.Table.Columns[1]);
            await h.Table.ViewReady;
            CollectionAssert.AreEqual(new[] { "k1", "k2" }, h.ViewKeys());
            Assert.IsFalse((bool)Proof.Call(h.Table, "IsHeld", removed)!);

            h.Rows.Add(new SortRow("arrival", 4));
            await h.Table.ViewReady;
            CollectionAssert.AreEqual(new[] { "k1", "k2" }, h.ViewKeys());
        });

    [TestMethod]
    public Task InitialStateResolvesTogetherWithTheLatestSort() =>
        TestHost.RunAsync(async () =>
        {
            Table table = TestData.Table(TestData.Column("a", 160), TestData.Column("b", 160));
            Row[] rows = { new("k1"), new("k2"), new("k3") };
            table.ItemsSource = rows;
            table.Selection = new(new object[] { new Row("k2") });
            table.Layout = new ColumnLayout(
                new[] { "a", "b" },
                new Dictionary<string, bool>(),
                "b",
                SortDirection.Ascending
            );
            table.Sort = new Sort(table.Columns[0], SortDirection.Descending);
            table
                .Schema<Row>()
                .Key(row => row.Rank)
                .SortKey(table.Columns[0], row => (int?)row.Rank)
                .SortKey(table.Columns[1], row => (object)row);
            int selections = 0;
            int layouts = 0;
            table.SelectionChanged += (_, _) => selections++;
            table.LayoutChanged += (_, _) => layouts++;
            Assert.AreEqual("a", table.Layout.SortColumnId);

            await TableHarness.LoadAsync(table);

            ListView list = SelectionHarness.Descendant<ListView>(table)!;
            CollectionAssert.AreEqual(
                new[] { "k3", "k2", "k1" },
                ((IEnumerable)list.ItemsSource).Cast<Row>().Select(row => row.Key).ToArray()
            );
            Assert.AreSame(rows[1], table.Selection.Current);
            Assert.AreEqual(1, selections);
            Assert.AreEqual(0, layouts);
        });

    [TestMethod]
    public Task Section9_TheCycleIsAscendingThenDescendingThenNaturalOrder() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2, 1, 3, 2 });

            await h.Activate(0);
            CollectionAssert.AreEqual(new[] { "k1", "k3", "k2", "k5", "k0", "k4" }, h.ViewKeys());

            await h.Activate(0);
            CollectionAssert.AreEqual(new[] { "k0", "k4", "k2", "k5", "k1", "k3" }, h.ViewKeys());

            await h.Activate(0);
            CollectionAssert.AreEqual(
                new[] { "k0", "k1", "k2", "k3", "k4", "k5" },
                h.ViewKeys(),
                "natural order returns"
            );
        });

    [TestMethod]
    public Task Section9_ASecondColumnStartsItsOwnCycleAtAscending() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 }, sortableColumns: 2);

            await h.Activate(0);
            await h.Activate(0);
            Assert.AreEqual(SortDirection.Descending, h.Table.Layout.SortDirection);

            await h.Activate(1);

            ColumnLayout state = h.Table.Layout;
            Assert.AreEqual("b", state.SortColumnId);
            Assert.AreEqual(SortDirection.Ascending, state.SortDirection);
        });

    [TestMethod]
    public Task Section9_ANonSortableHeaderHasNoSortAction() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });
            int events = 0;
            h.Table.LayoutChanged += (_, _) => events++;

            // Column "b" declares no sort.
            await h.Activate(1);

            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2" }, h.ViewKeys());
            Assert.IsNull(h.Table.Layout.SortColumnId);
            Assert.AreEqual(0, events);
        });

    [TestMethod]
    public Task Section9_SortingNeverReordersItemsSource() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });

            await h.Activate(0);

            CollectionAssert.AreEqual(new[] { "k1", "k2", "k0" }, h.ViewKeys());
            CollectionAssert.AreEqual(
                new[] { "k0", "k1", "k2" },
                h.Rows.Select(r => r.Key).ToArray(),
                "the source keeps its own order"
            );
        });

    [TestMethod]
    public Task Section18_OneLayoutChangedOfKindSortPerCompletedSort() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });
            List<LayoutChange> raised = new();
            h.Table.LayoutChanged += (_, kind) => raised.Add(kind);

            await h.Activate(0);

            Assert.AreEqual(1, raised.Count);
            Assert.AreEqual(LayoutChange.Sort, raised[0]);
            Assert.AreEqual("a", h.Table.Layout.SortColumnId);
            Assert.AreEqual(SortDirection.Ascending, h.Table.Layout.SortDirection);

            await h.Activate(0);
            await h.Activate(0);

            Assert.AreEqual(3, raised.Count);
            Assert.IsNull(h.Table.Layout.SortColumnId, "the cycle ended in natural order");
        });

    // ------------------------------------------------------------------ persistence

    [TestMethod]
    public Task Section18_AssigningLayoutRestoresASavedSortSilently() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });
            int events = 0;
            h.Table.LayoutChanged += (_, _) => events++;

            h.Table.Layout = TestData.Layout(
                order: new[] { "a", "b", "c" },
                sortColumnId: "a",
                direction: SortDirection.Descending
            );
            await h.Table.ViewReady;

            CollectionAssert.AreEqual(new[] { "k0", "k2", "k1" }, h.ViewKeys());
            Assert.AreEqual(0, events, "restoration is silent");
            Assert.AreEqual(SortDirection.Descending, h.Table.Layout.SortDirection);
        });

    [TestMethod]
    public Task Section18_AnUnknownSortColumnFallsBackToNaturalOrder() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });
            await h.Activate(0);

            h.Table.Layout = TestData.Layout(order: new[] { "a" }, sortColumnId: "gone");
            await h.Table.ViewReady;

            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2" }, h.ViewKeys());
            Assert.IsNull(h.Table.Layout.SortColumnId);
        });

    [TestMethod]
    public Task Section18_ASavedSortOnANonSortableColumnFallsBackToNaturalOrder() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });
            await h.Activate(0);

            // "b" declares no sort, so the saved sort cannot be honoured.
            h.Table.Layout = TestData.Layout(order: new[] { "a" }, sortColumnId: "b");
            await h.Table.ViewReady;

            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2" }, h.ViewKeys());
            Assert.IsNull(h.Table.Layout.SortColumnId);
        });

    [TestMethod]
    public Task Section18_AnInvalidSavedDirectionFallsBackToNaturalOrder() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });

            h.Table.Layout = TestData.Layout(
                order: new[] { "a" },
                sortColumnId: "a",
                direction: (SortDirection)7
            );
            await h.Table.ViewReady;

            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2" }, h.ViewKeys());
            Assert.IsNull(h.Table.Layout.SortColumnId);
        });

    // ------------------------------------------------------------------ source updates

    /// <summary>
    /// Settling is off here, and that is the point rather than a convenience. What
    /// <see cref="Table.RefreshView"/> promises is that it re-reads the snapshot and applies
    /// the sort without re-enumerating the source. At the default
    /// <see cref="Table.SortInterval"/> of three seconds this refresh lands inside the window
    /// that has just been taken by the header activation above, so the view would hold its order
    /// and prove nothing either way.
    /// </summary>
    [TestMethod]
    public Task Section9_RefreshViewReSortsWithoutReEnumeratingTheSource() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(
                new[] { 3, 1, 2 },
                configure: table => table.SortInterval = TimeSpan.Zero
            );
            await h.Activate(0);
            CollectionAssert.AreEqual(new[] { "k1", "k2", "k0" }, h.ViewKeys());

            // A batch changes the sorted value without any collection notification.
            h.Rows[0].Rank = 0;
            CollectionAssert.AreEqual(
                new[] { "k1", "k2", "k0" },
                h.ViewKeys(),
                "a property change alone does not resort"
            );

            h.Table.RefreshView();
            await h.Table.ViewReady;

            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2" }, h.ViewKeys());
        });

    /// <summary>
    /// Section 9's owner ruling. When the rows take their sorted order, the row under the pointer
    /// keeps its place and the others sort around it. Pointing at another row moves nothing; the
    /// next refresh freezes that row instead, and leaving the rows sorts it.
    /// </summary>
    [TestMethod]
    public Task Section9_ALiveSortLeavesTheRowUnderThePointerWhereItIs() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(
                new[] { 1, 2, 3, 4, 5 },
                configure: table => table.SortInterval = TimeSpan.Zero
            );
            await h.Activate(0);
            Proof.Call(h.Table, "PointAt", h.Rows[1]);

            h.Rows[1].Rank = 10;
            h.Rows[3].Rank = 0;
            h.Table.RefreshView();
            await h.Table.ViewReady;

            CollectionAssert.AreEqual(
                new[] { "k3", "k1", "k0", "k2", "k4" },
                h.ViewKeys(),
                "k1 keeps its place and the others take their sorted order around it"
            );

            Proof.Call(h.Table, "PointAt", h.Rows[2]);

            CollectionAssert.AreEqual(
                new[] { "k3", "k1", "k0", "k2", "k4" },
                h.ViewKeys(),
                "pointing at another row re-sorts nothing"
            );

            h.Table.RefreshView();
            await h.Table.ViewReady;

            CollectionAssert.AreEqual(
                new[] { "k3", "k0", "k4", "k2", "k1" },
                h.ViewKeys(),
                "the next refresh freezes the newly pointed row instead"
            );

            Proof.Call(h.Table, "StopPointing");
            await h.Table.ViewReady;

            CollectionAssert.AreEqual(
                new[] { "k3", "k0", "k2", "k4", "k1" },
                h.ViewKeys(),
                "leaving the rows puts the frozen row in its sorted place"
            );
        });

    [TestMethod]
    public Task Section9_AnAcceptedSourceUpdateReAppliesTheActiveSort() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });
            await h.Activate(0);

            h.Rows.Add(new SortRow("k3", 0));
            await h.Table.ViewReady;

            CollectionAssert.AreEqual(new[] { "k3", "k1", "k2", "k0" }, h.ViewKeys());
        });

    /// <summary>
    /// Section 9: inside the interval arrivals and leavers wait, and the settle applies them with
    /// the moves in one update. A waiting leaver refuses actions at once, and looks as any row
    /// until the pointer holds the rows.
    /// </summary>
    [TestMethod]
    public Task ChangesInsideTheIntervalWaitForTheSettle() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 1, 2, 3, 4 }, configure: Hourly);
            Table table = h.Table;
            await ShowSettleAsync(table);

            SortRow leaver = h.Rows[1];
            h.Rows.Remove(leaver);
            await h.Table.ViewReady;
            h.Rows.Add(new("arrival", 0));
            await h.Table.ViewReady;
            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2", "k3" }, h.ViewKeys(), "the change waits");
            Assert.IsFalse((bool)Proof.Call(table, "ShowsHeld", leaver)!, "the waiting leaver keeps its look");
            Assert.IsFalse((bool)Proof.Call(table, "IsInteractive", leaver)!, "the source no longer has the waiting leaver");
            Proof.Call(table, "PointAt", h.Rows[0]);
            Assert.IsTrue((bool)Proof.Call(table, "ShowsHeld", leaver)!, "the pointer gives it the held look");
            Assert.IsFalse((bool)Proof.Call(table, "IsInteractive", leaver)!, "it still refuses actions");
            Proof.Call(table, "StopPointing");
            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2", "k3" }, h.ViewKeys(), "it still waits for the settle");

            Proof.SetField(table, "_orderSettledAt", null);
            table.RefreshView();
            h.Rows.Add(new("late", 5));
            await h.Table.ViewReady;
            CollectionAssert.AreEqual(new[] { "arrival", "k0", "k2", "k3", "late" }, h.ViewKeys(),
                "the settle applies the change, and a change in its turn lands with it");
            await ShowSettleAsync(table);

            table.ItemsSource = new[] { new SortRow("other", 1) };
            await table.ViewReady;
            CollectionAssert.AreEqual(new[] { "other" }, h.ViewKeys(), "a different list applies at once");
        });

    /// <summary>
    /// Section 5.3: a change the person named applies inside the interval, and a name whose change
    /// never came ends after its lifetime, so it lets no later background change through.
    /// </summary>
    [TestMethod]
    public Task AReleasedChangeAppliesAtOnceUntilItsNameEnds() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 1, 2, 3 }, configure: Hourly);
            Table table = h.Table;
            await ShowSettleAsync(table);

            SortRow removed = h.Rows[0];
            table.Release(new[] { removed });
            await table.ViewReady;
            h.Rows.Remove(removed);
            await h.Table.ViewReady;
            CollectionAssert.AreEqual(new[] { "k1", "k2" }, h.ViewKeys(), "the person's change applies at once");
            await ShowSettleAsync(table);

            SortRow kept = h.Rows[0];
            table.Release(new[] { kept });
            await table.ViewReady;
            var released = (System.Collections.IDictionary)Proof.Field<object>(table, "_released");
            released[kept] = (false, System.Diagnostics.Stopwatch.GetTimestamp()
                - 2 * 3600 * System.Diagnostics.Stopwatch.Frequency);
            h.Rows.Remove(kept);
            await h.Table.ViewReady;
            CollectionAssert.AreEqual(new[] { "k1", "k2" }, h.ViewKeys(), "the ended name lets nothing through");
        });

    /// <summary>
    /// Section 5.3: a new instance of a row that stays keeps the row's container, so the row does
    /// not fade in again as if it had arrived.
    /// </summary>
    [TestMethod]
    public Task AReplacedRowKeepsItsContainer() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 1, 2, 3 });
            ListView surface = h.Surface();
            DependencyObject before = surface.ContainerFromIndex(1);
            Assert.IsNotNull(before, "the fixture needs the row realized");

            SortRow replacement = new("k1", 2);
            h.Rows[1] = replacement;
            await h.Table.ViewReady;

            Assert.AreSame(before, surface.ContainerFromIndex(1), "the row keeps its container");
            Assert.AreSame(replacement, ((ContentControl)before).Content, "the container shows the new instance");
        });

    private static void Hourly(Table table)
    {
        table.Sort = new(table.Columns[0]);
        table.SortInterval = TimeSpan.FromHours(1);
    }

    /// <summary>Wait for the frame that shows the last settle, which starts the interval.</summary>
    private static async Task ShowSettleAsync(Table table)
    {
        await table.ViewReady;
        await TableHarness.WaitUntilAsync(() => !Proof.Field<bool>(table, "_settleShowing"), "the frame");
    }

    // ------------------------------------------------------------------ selection

    [TestMethod]
    public Task Section9_SortingKeepsTheSelectionAndTheCurrentRow() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2, 1, 3, 2 });
            int selectionEvents = 0;
            h.Table.SelectionChanged += (_, _) => selectionEvents++;

            h.Table.Selection = new(new object[] { h.Rows[0], h.Rows[4] }, h.Rows[4]);
            selectionEvents = 0;

            await h.Activate(0);

            CollectionAssert.AreEqual(
                new[] { "k0", "k4" },
                h.Table.Selection.Items.Cast<SortRow>().Select(r => r.Key).ToArray(),
                "the packet survived, in the new visual order"
            );
            Assert.AreEqual("k4", ((SortRow)h.Table.Selection.Current!).Key);
            Assert.AreEqual(0, selectionEvents, "only positions changed");

            // The containers are what the person sees, so this is the real check.
            CollectionAssert.AreEquivalent(
                new[] { "k0", "k4" },
                TableHarness
                    .SelectedContainers(h.Surface())
                    .Cast<SortRow>()
                    .Select(r => r.Key)
                    .ToArray(),
                "the row surface shows the table's selection"
            );
        });

    // ------------------------------------------------------------------ header state

    [TestMethod]
    public Task Section9_TheActiveHeaderShowsItsDirectionAsAGlyphAndAsItemStatus() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });

            Assert.AreEqual(Visibility.Collapsed, h.Glyph(0).Visibility, "unsorted shows nothing");
            Assert.AreEqual(string.Empty, AutomationProperties.GetItemStatus(h.Cell(0)));

            await h.Activate(0);
            h.Table.UpdateLayout();

            Assert.AreEqual(Visibility.Visible, h.Glyph(0).Visibility);
            string ascendingGlyph = h.Glyph(0).Glyph;
            string ascendingStatus = AutomationProperties.GetItemStatus(h.Cell(0));
            Assert.AreNotEqual(string.Empty, ascendingStatus, "the direction is announced");
            Assert.AreEqual(Visibility.Collapsed, h.Glyph(1).Visibility, "only one column sorts");
            Assert.AreEqual(string.Empty, AutomationProperties.GetItemStatus(h.Cell(1)));

            await h.Activate(0);
            h.Table.UpdateLayout();

            Assert.AreEqual(Visibility.Visible, h.Glyph(0).Visibility);
            Assert.AreNotEqual(
                ascendingGlyph,
                h.Glyph(0).Glyph,
                "the glyph tells the directions apart"
            );
            string descendingStatus = AutomationProperties.GetItemStatus(h.Cell(0));
            Assert.AreNotEqual(string.Empty, descendingStatus);
            Assert.AreNotEqual(
                ascendingStatus,
                descendingStatus,
                "the status tells the directions apart"
            );

            await h.Activate(0);
            h.Table.UpdateLayout();

            Assert.AreEqual(Visibility.Collapsed, h.Glyph(0).Visibility);
            Assert.AreEqual(string.Empty, AutomationProperties.GetItemStatus(h.Cell(0)));
        });

    [TestMethod]
    public Task Section9_TheGlyphFollowsTheColumnWhenItMoves() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });
            await h.Activate(0);

            h.Table.Layout = TestData.Layout(order: new[] { "b", "c", "a" }, sortColumnId: "a");
            await h.Table.ViewReady;
            h.Table.UpdateLayout();

            Assert.AreEqual(
                Visibility.Collapsed,
                h.Glyph(0).Visibility,
                "b is not the sorted column"
            );
            Assert.AreEqual(
                Visibility.Visible,
                h.Glyph(2).Visibility,
                "a moved to the end with its glyph"
            );
        });

    [TestMethod]
    public Task Section19_TheSortGlyphMeetsThreeToOneInLight() =>
        GlyphContrastAsync(ElementTheme.Light);

    [TestMethod]
    public Task Section19_TheSortGlyphMeetsThreeToOneInDark() =>
        GlyphContrastAsync(ElementTheme.Dark);

    /// <summary>
    /// Section 19: the direction glyph is table-owned non-text information, so it must reach 3:1.
    /// A resource name is not proof, so this renders the header and measures the drawn pixels
    /// inside the glyph's own bounds against the header background beside it.
    /// </summary>
    private static Task GlyphContrastAsync(ElementTheme theme) =>
        TestHost.RunAsync(async () =>
        {
            Windows.UI.Color backdrop =
                theme == ElementTheme.Light
                    ? Windows.UI.Color.FromArgb(255, 243, 243, 243)
                    : Windows.UI.Color.FromArgb(255, 32, 32, 32);

            SortHarness h = await SortHarness.LoadAsync(
                new[] { 3, 1, 2 },
                configure: table =>
                {
                    table.RequestedTheme = theme;
                    table.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(backdrop);
                }
            );

            await h.Activate(0);
            h.Table.UpdateLayout();

            FontIcon glyph = h.Glyph(0);
            await TableHarness.WaitUntilAsync(
                () => glyph.ActualWidth > 0 && glyph.ActualHeight > 0,
                "the sort glyph to render"
            );

            Windows.Foundation.Point origin = glyph
                .TransformToVisual(h.Table)
                .TransformPoint(new Windows.Foundation.Point(0, 0));

            Shot shot = await Shot.TakeAsync(h.Table);
            double scale = h.Table.XamlRoot.RasterizationScale;
            int PixelX(double dips) => Math.Clamp((int)Math.Round(dips * scale), 0, shot.Width - 1);

            int top = shot.Y(origin.Y);
            int bottom = shot.Y(origin.Y + glyph.ActualHeight);
            int left = PixelX(origin.X);
            int right = PixelX(origin.X + glyph.ActualWidth);

            // The header background immediately left of the glyph, on the glyph's own centre line.
            uint background = shot.At(PixelX(origin.X - 4), (top + bottom) / 2);
            double best = shot.MaxContrast(background, left, right, top, bottom);

            Assert.IsTrue(
                best >= 3.0,
                $"{theme} sort glyph: measured {best:0.00}:1 against the header background "
                    + $"#{background:X6}; section 19 requires 3.0:1."
            );
        });

    // ------------------------------------------------------------------ schema

    /// <summary>
    /// Section 9: a column that was never given a sort key does not sort, and asking the table to
    /// sort by one is the host asking for something impossible. The old pair — a CanSort flag
    /// beside a comparer, which had to agree — is gone, so a column that claims to sort without
    /// one can no longer be written.
    /// </summary>
    [TestMethod]
    public Task Section9_SortingByAColumnWithNoSortKeyIsRefused() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });

            Expect.Throws<ArgumentException>(() => h.Table.Sort = new(h.Table.Columns[1]));
            Assert.IsNull(h.Table.Sort, "the refused request left the order alone");
        });

    [TestMethod]
    public Task Section9_HiddenSortIsRefusedWithoutChangingTheView() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 }, sortableColumns: 2);
            h.Table.Layout = TestData.Layout(
                visibility: new Dictionary<string, bool> { ["b"] = false },
                sortColumnId: "a"
            );
            await h.Table.ViewReady;
            h.Table.Selection = new(new object[] { h.Rows[1] });
            Sort? before = h.Table.Sort;
            int events = 0;
            h.Table.LayoutChanged += (_, _) => events++;

            Expect.Throws<ArgumentException>(() =>
                h.Table.Sort = new(h.Table.Columns[1], SortDirection.Descending)
            );

            Assert.AreEqual(before, h.Table.Sort);
            CollectionAssert.AreEqual(new[] { "k1", "k2", "k0" }, h.ViewKeys());
            Assert.AreSame(h.Rows[1], h.Table.Selection.Current);
            Assert.AreEqual(0, events);

            h.Table.Layout = h.Table.Layout;
            await h.Table.ViewReady;

            Assert.AreEqual(before, h.Table.Sort);
            CollectionAssert.AreEqual(new[] { "k1", "k2", "k0" }, h.ViewKeys());
            Assert.IsFalse(TableHarness.IsVisible(h.Table, "b"));
            Assert.AreEqual(0, events);
        });

    [TestMethod]
    public Task Section9_InitialSortRejectsAColumnHiddenByPendingLayout() =>
        TestHost.RunAsync(async () =>
        {
            Table table = TestData.Table(TestData.Column("a"), TestData.Column("b"));
            table.Schema<SortRow>().SortKey(table.Columns[1], row => row.Rank);
            table.Layout = TestData.Layout(
                visibility: new Dictionary<string, bool> { ["b"] = false }
            );
            table.Sort = new(table.Columns[1]);

            Exception error = await TableHarness.LoadExpectingFailureAsync(table);

            Assert.IsInstanceOfType<ArgumentException>(error, error.ToString());
            Assert.IsNull(table.Sort);
        });

    /// <summary>One rendered frame of the control, addressed in pixels.</summary>
    private sealed class Shot
    {
        private byte[] _pixels = Array.Empty<byte>();

        internal int Width { get; private set; }

        internal int Height { get; private set; }

        internal double Scale { get; private set; } = 1;

        internal static async Task<Shot> TakeAsync(FrameworkElement element)
        {
            RenderTargetBitmap bitmap = new();
            await bitmap.RenderAsync(element);
            IBuffer buffer = await bitmap.GetPixelsAsync();
            byte[] pixels = new byte[buffer.Length];
            DataReader.FromBuffer(buffer).ReadBytes(pixels);

            Shot shot = new()
            {
                _pixels = pixels,
                Width = bitmap.PixelWidth,
                Height = bitmap.PixelHeight,
                Scale = element.XamlRoot?.RasterizationScale ?? 1,
            };

            Assert.IsTrue(
                shot.Width > 0 && shot.Height > 0,
                "The control rendered an empty bitmap."
            );
            return shot;
        }

        internal int Y(double dips) => Math.Clamp((int)Math.Round(dips * Scale), 0, Height - 1);

        internal uint At(int x, int y)
        {
            int i = (((y * Width) + x) * 4);
            return (uint)(_pixels[i] | (_pixels[i + 1] << 8) | (_pixels[i + 2] << 16));
        }

        internal double MaxContrast(uint reference, int x0, int x1, int y0, int y1)
        {
            double best = 0;
            for (int y = Math.Max(0, y0); y <= Math.Min(Height - 1, y1); y++)
            {
                for (int x = Math.Max(0, x0); x <= Math.Min(Width - 1, x1); x++)
                {
                    best = Math.Max(best, Contrast(At(x, y), reference));
                }
            }

            return best;
        }

        private static double Contrast(uint a, uint b)
        {
            double first = Luminance(a);
            double second = Luminance(b);
            return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
        }

        private static double Luminance(uint bgr) =>
            (0.2126 * Linear((bgr >> 16) & 0xFF))
            + (0.7152 * Linear((bgr >> 8) & 0xFF))
            + (0.0722 * Linear(bgr & 0xFF));

        private static double Linear(uint channel)
        {
            double value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
    }
}

/// <summary>
/// A loaded table over sortable rows, plus the strip's own sort activation. Both real input paths
/// call <c>ActivateSortFrom</c>, so exercising it here runs the same code a click and a key run.
/// </summary>
internal sealed class SortHarness
{
    private SortHarness(Table table, ObservableCollection<SortRow> rows, Header.Strip strip)
    {
        Table = table;
        Rows = rows;
        Strip = strip;
    }

    internal Table Table { get; }

    internal ObservableCollection<SortRow> Rows { get; }

    internal Header.Strip Strip { get; }

    /// <summary>Ranks in source order become rows k0..kN, with column "a" sorting on the rank.</summary>
    internal static async Task<SortHarness> LoadAsync(
        int[] ranks,
        int sortableColumns = 1,
        Action<Table>? configure = null
    )
    {
        ObservableCollection<SortRow> rows = new();
        for (int i = 0; i < ranks.Length; i++)
        {
            rows.Add(new SortRow("k" + i, ranks[i]));
        }

        Table table = TestData.Table(
            TestData.Column("a", 200),
            TestData.Column("b", 200),
            TestData.Column("c", 200)
        );

        Schema<SortRow> schema = table.Schema<SortRow>().Key(row => row.Key);
        for (int i = 0; i < sortableColumns; i++)
        {
            schema.SortKey(table.Columns[i], row => row.Rank);
        }

        table.Width = 700;
        table.Height = 300;
        table.ItemsSource = rows;
        configure?.Invoke(table);

        await TableHarness.LoadAsync(table);
        table.UpdateLayout();

        return new SortHarness(table, rows, SelectionHarness.Descendant<Header.Strip>(table)!);
    }

    /// <summary>Run the header's sort cycle for the header at this visible index.</summary>
    internal async Task Activate(int visibleIndex)
    {
        MethodInfo method = typeof(Header.Strip).GetMethod(
            "ActivateSortFrom",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        method.Invoke(Strip, new object?[] { Cell(visibleIndex) });
        await Table.ViewReady;
        Table.UpdateLayout();
    }

    internal ListView Surface() =>
        SelectionHarness.Descendant<ListView>(Table)
        ?? throw new InvalidOperationException("No row surface.");

    /// <summary>The private view the row surface actually shows, in visual order.</summary>
    internal string[] ViewKeys() =>
        ((IEnumerable)Surface().ItemsSource).Cast<SortRow>().Select(r => r.Key).ToArray();

    internal Header.Cell Cell(int visibleIndex) => (Header.Cell)Panel().Children[visibleIndex];

    internal FontIcon Glyph(int visibleIndex) =>
        SelectionHarness.Descendant<FontIcon>(Cell(visibleIndex))
        ?? throw new InvalidOperationException("The header cell has no sort glyph.");

    private Panel Panel() =>
        (Panel)
            typeof(Header.Strip)
                .GetField("_panel", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Strip)!;
}
