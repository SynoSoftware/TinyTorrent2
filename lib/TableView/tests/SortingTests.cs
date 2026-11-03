using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Storage.Streams;
using Windows.Foundation.Collections;

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
    public Task WithheldArrivalsKeepCurrentPinsAndRetargetWhenThePointerMoves() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 }, configure: table =>
            {
                table.Sort = new(table.Columns[0]);
                table.SortInterval = TimeSpan.Zero;
            });
            Proof.Call(h.Table, "PointAt", h.Rows[1]);
            h.Rows[1].Rank = 4;
            h.Table.RefreshView();
            CollectionAssert.AreEqual(new[] { "k1", "k2", "k0" }, h.ViewKeys());
            Proof.Call(h.Table, "PointAt", h.Rows[0]);
            h.Rows.Insert(0, new("waiting-a", 9));
            CollectionAssert.AreEqual(new[] { "k2", "k1", "k0" }, h.ViewKeys());
            h.Rows.Insert(0, new("waiting-b", 9));
            CollectionAssert.AreEqual(new[] { "k2", "k1", "k0" }, h.ViewKeys());
            h.Rows[3] = new("k1", 2);
            CollectionAssert.AreEqual(new[] { "k1", "k2", "k0" }, h.ViewKeys());
            Assert.AreSame(h.Rows[3], h.Surface().Items[0]);
            Proof.Call(h.Table, "StopPointing");
            CollectionAssert.AreEqual(new[] { "k1", "k2", "k0", "waiting-b", "waiting-a" }, h.ViewKeys());
        });

    /// <summary>
    /// Section 5.3's owner ruling: a different list starts at the top. A sort that moves every row
    /// keeps the offset, although its difference names every row as leaving and arriving, and a
    /// host's <c>ScrollTo</c> after the assignment wins, also when a hold shows the list later and
    /// the list changes meanwhile, but not for a later change that replaces every row. A list
    /// emptied and filled again before a frame is compared with the rows shown before it was
    /// emptied, whatever row comes back first, so its own rows keep the offset and other rows do
    /// not.
    /// </summary>
    [TestMethod]
    public Task ADifferentListStartsAtTheTopUnlessTheHostScrolls() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(Enumerable.Range(0, 200).ToArray(), configure: table =>
            {
                table.Sort = new(table.Columns[0]);
                table.SortInterval = TimeSpan.Zero;
            });
            Table table = h.Table;
            ScrollViewer viewer = SelectionHarness.Descendant<ScrollViewer>(h.Surface())!;

            await ScrolledAsync();
            foreach (SortRow row in h.Rows)
            {
                row.Rank = -row.Rank;
            }
            table.RefreshView();
            Assert.AreEqual(1000, await OffsetAsync(), 1, "a sort that moves every row keeps the offset");

            table.ItemsSource = List("a");
            Assert.AreEqual(0, await OffsetAsync(), 1, "a different list starts at the top");

            await ScrolledAsync();
            SortRow[] shown = List("b");
            table.ItemsSource = shown;
            table.ScrollTo(600);
            Assert.AreEqual(600, await OffsetAsync(), 1, "the host's ScrollTo after the assignment wins");

            await ScrolledAsync();
            Proof.Call(table, "PointAt", shown[25]);
            shown = List("c");
            table.ItemsSource = shown;
            table.ScrollTo(600);
            Proof.Call(table, "StopPointing");
            Assert.AreEqual(600, await OffsetAsync(), 1, "the host's ScrollTo wins when the hold ends");

            await ScrolledAsync();
            Proof.Call(table, "PointAt", shown[25]);
            ObservableCollection<SortRow> live = new(List("d"));
            table.ItemsSource = live;
            table.ScrollTo(600);
            live.Add(new SortRow("d-late", 200));
            Proof.Call(table, "StopPointing");
            Assert.AreEqual(600, await OffsetAsync(), 1,
                "the host's ScrollTo wins when the held list changed before the hold ended");

            await ScrolledAsync();
            SortRow[] own = live.ToArray();
            live.Clear();
            foreach (SortRow row in own)
            {
                live.Add(row);
            }
            Assert.AreEqual(1000, await OffsetAsync(), 1, "a list emptied and refilled with its own rows keeps the offset");

            live.Clear();
            live.Add(new SortRow("d-new", -1));
            foreach (SortRow row in own)
            {
                live.Add(row);
            }
            Assert.AreEqual(1000, await OffsetAsync(), 1,
                "a list refilled with a new row first and then its own rows keeps the offset");

            live.Clear();
            foreach (SortRow row in List("e"))
            {
                live.Add(row);
            }
            Assert.AreEqual(0, await OffsetAsync(), 1, "a list emptied and refilled with other rows starts at the top");

            QuietList<SortRow> quiet = new(List("f"));
            table.ItemsSource = quiet;
            table.ScrollTo(600);
            Assert.AreEqual(600, await OffsetAsync(), 1, "the host's ScrollTo after the assignment wins");
            for (int index = 0; index < 200; index++)
            {
                quiet.RemoveQuietly(0);
            }
            foreach (SortRow row in List("g"))
            {
                quiet.InsertQuietly(quiet.Count, row);
            }
            quiet.RaiseReset();
            Assert.AreEqual(0, await OffsetAsync(), 1,
                "a different list in the list the host scrolled starts at the top");

            static SortRow[] List(string prefix) =>
                Enumerable.Range(0, 200).Select(i => new SortRow(prefix + i, i)).ToArray();

            // As a person scrolls: not through ScrollTo, which names the source it follows.
            async Task ScrolledAsync()
            {
                viewer.ChangeView(null, 1000, null, disableAnimation: true);
                await TableHarness.WaitUntilAsync(() => Math.Abs(viewer.VerticalOffset - 1000) < 1, "the scroll");
            }

            async Task<double> OffsetAsync()
            {
                table.UpdateLayout();
                await TableHarness.WaitUntilAsync(() => !Proof.Field<bool>(table, "_layoutPending"), "the publish's frame");
                return viewer.VerticalOffset;
            }
        });

    /// <summary>
    /// Contract 5.3: a switch to a different list swaps the rows at once, and other changes in the
    /// same layout episode share that. A reorder of the new list in the same turn must not slide
    /// its rows from places the person never saw them in.
    /// </summary>
    [TestMethod]
    public Task AReorderAfterASwitchInOneTurnSlidesNothing() =>
        TestHost.RunAsync(async () =>
        {
            if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                Assert.Inconclusive("Windows animations are off, so no reorder slides here.");
            }
            SortHarness h = await SortHarness.LoadAsync(Enumerable.Range(0, 200).ToArray(), configure: table =>
            {
                table.Sort = new(table.Columns[0]);
                table.SortInterval = TimeSpan.Zero;
            });
            Table table = h.Table;
            await FrameAsync();

            ReverseRanks(h.Rows);
            table.RefreshView();
            Assert.IsNotNull(Proof.Field<object?>(table, "_slideFrom"), "a reorder on its own slides");
            await FrameAsync();

            SortRow[] other = Enumerable.Range(0, 200).Select(i => new SortRow("x" + i, i)).ToArray();
            table.ItemsSource = other;
            ReverseRanks(other);
            table.RefreshView();
            Assert.IsNull(Proof.Field<object?>(table, "_slideFrom"), "no row of the new list slides");

            static void ReverseRanks(IEnumerable<SortRow> rows)
            {
                foreach (SortRow row in rows)
                {
                    row.Rank = -row.Rank;
                }
            }

            // The layout episode ends in the frame, before the layout note clears.
            async Task FrameAsync()
            {
                table.UpdateLayout();
                await TableHarness.WaitUntilAsync(() => !Proof.Field<bool>(table, "_layoutPending"), "the frame");
            }
        });

    /// <summary>
    /// Requirement 3: a row that stays on screen is never deleted and added again. One Replace
    /// that gives two sorted rows each other's place is a move of both, so they slide.
    /// </summary>
    [TestMethod]
    public Task ASortedReplaceThatCrossesTwoRowsSlidesThem() =>
        TestHost.RunAsync(async () =>
        {
            if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                Assert.Inconclusive("Windows animations are off, so no reorder slides here.");
            }
            SortHarness h = await SortHarness.LoadAsync(Enumerable.Range(0, 200).ToArray(), configure: table =>
            {
                table.Sort = new(table.Columns[0]);
                table.SortInterval = TimeSpan.Zero;
            });
            Table table = h.Table;
            NullSenderRows rows = new();
            rows.AddRange(h.Rows);
            table.ItemsSource = rows;
            table.UpdateLayout();
            await TableHarness.WaitUntilAsync(() => !Proof.Field<bool>(table, "_layoutPending"), "the frame");

            rows.ReplaceNotified(0, 2, new SortRow("k1", 0), new SortRow("k0", 1));
            CollectionAssert.AreEqual(new[] { "k1", "k0", "k2" }, h.ViewKeys()[..3]);
            Assert.IsNotNull(Proof.Field<object?>(table, "_slideFrom"), "the two rows slide");
        });

    [TestMethod]
    [DataRow(SortDirection.Ascending)]
    [DataRow(SortDirection.Descending)]
    public Task CollectionTiesKeepSourceOrderAcrossDeferredOrdinalChanges(SortDirection direction) =>
        TestHost.RunAsync(async () =>
        {
            bool reject = false;
            SortHarness h = await SortHarness.LoadAsync(Enumerable.Repeat(1, 32).ToArray(), configure: table =>
            {
                table.Schema<SortRow>().SortKey(table.Columns[0], row =>
                {
                    if (reject && row.Key == "rejected")
                    {
                        throw new InvalidOperationException("Rejected entrant");
                    }
                    return row.Rank;
                });
                table.Sort = new(table.Columns[0], direction);
                table.SortInterval = TimeSpan.Zero;
            });
            for (int turn = 0; turn < 64; turn++)
            {
                h.Rows.Insert(turn * 17 % (h.Rows.Count + 1), new($"arrival{turn}", 1));
                if (turn % 3 == 0)
                {
                    h.Rows.RemoveAt(turn * 7 % h.Rows.Count);
                }
                Check(h.Rows);
            }
            h.Rows[5].Rank = 0;
            h.Table.RefreshView(new object[] { h.Rows[5] });
            Check(h.Rows);
            h.Table.RefreshView();
            h.Rows.Insert(0, new("after-refresh", 1));
            Check(h.Rows);

            string[] accepted = h.ViewKeys();
            reject = true;
            Expect.Throws<InvalidOperationException>(() => h.Rows.Insert(0, new("rejected", 1)));
            CollectionAssert.AreEqual(accepted, h.ViewKeys());
            reject = false;
            h.Rows.RemoveAt(0);
            Check(h.Rows);

            NullSenderRows range = new();
            range.AddRange(h.Rows);
            h.Table.ItemsSource = range;
            range.ReplaceNotified(3, 7, new SortRow("range-a", 0), new SortRow("range-b", 1), new SortRow("range-c", 2));
            Check(range);
            range.AddNotified(new("last", 1));
            Check(range);

            NullSenderRows indexed = new();
            indexed.AddRange(h.Rows.Take(4));
            h.Table.ItemsSource = indexed;
            indexed.AddNotified(new("before-index-a", 1));
            indexed.AddNotified(new("before-index-b", 1));
            accepted = h.ViewKeys();
            indexed.RefusedIndex = 0;
            Expect.Throws<InvalidOperationException>(() => indexed.AddNotified(new("index-failure", 1)));
            CollectionAssert.AreEqual(accepted, h.ViewKeys());
            indexed.RefusedIndex = null;
            indexed.RemoveAt(indexed.Count - 1);
            h.Table.RefreshView();
            indexed.AddNotified(new("after-index-failure", 1));
            Check(indexed);

            void Check(IEnumerable<SortRow> source)
            {
                IEnumerable<SortRow> sorted = direction == SortDirection.Ascending
                    ? source.OrderBy(row => row.Rank) : source.OrderByDescending(row => row.Rank);
                CollectionAssert.AreEqual(sorted.Select(row => row.Key).ToArray(), h.ViewKeys());
            }
        });

    [TestMethod]
    public Task NotificationsCaptureOnlyEntrantsAndRecoverAfterARejectedDelta() =>
        TestHost.RunAsync(async () =>
        {
            int keys = 0;
            SortHarness h = await SortHarness.LoadAsync(new[] { 1, 3, 5 }, configure: table =>
            {
                table.Schema<SortRow>().Key(row => row.Key).SortKey(table.Columns[0], row =>
                {
                    keys++;
                    return row.Rank;
                });
                table.Sort = new(table.Columns[0]);
            });
            keys = 0;
            h.Rows.Add(new("arrival", 2));
            Assert.AreEqual(1, keys);
            CollectionAssert.AreEqual(new[] { "k0", "arrival", "k1", "k2" }, h.ViewKeys());
            h.Rows[1] = new("k1", 4);
            Assert.AreEqual(2, keys);
            Assert.AreSame(h.Rows[1], h.Surface().Items[2]);
            h.Rows.RemoveAt(0);
            Assert.AreEqual(2, keys, "a removal retains every survivor's sort key");
            Expect.Throws<InvalidOperationException>(() => h.Rows.Add(new("arrival", 9)));
            CollectionAssert.AreEqual(new[] { "arrival", "k1", "k2" }, h.ViewKeys());
            h.Rows.RemoveAt(h.Rows.Count - 1);
            CollectionAssert.AreEqual(new[] { "arrival", "k1", "k2" }, h.ViewKeys());
            h.Rows.Move(2, 0);
            CollectionAssert.AreEqual(new[] { "arrival", "k1", "k2" }, h.ViewKeys());
        });

    [TestMethod]
    public Task NamedValuesCaptureOnlyCurrentNamedKeysAndKeepCountedPositionsCorrect() =>
        TestHost.RunAsync(async () =>
        {
            int keys = 0;
            SortHarness h = await SortHarness.LoadAsync(Enumerable.Range(0, 48).ToArray(), configure: table =>
            {
                table.Schema<SortRow>().SortKey(table.Columns[0], row =>
                {
                    keys++;
                    return row.Rank;
                });
                table.SortInterval = TimeSpan.Zero;
                table.Sort = new(table.Columns[0]);
            });
            for (int index = 0; index < 20; index++)
            {
                SortRow row = h.Rows[(index * 17) % h.Rows.Count];
                row.Rank = -index - 1;
                keys = 0;
                int enumerated = 0;
                IEnumerable<object> Named()
                {
                    enumerated++;
                    yield return new SortRow(row.Key, 999);
                    yield return row;
                    yield return new SortRow("unavailable", 0);
                }
                h.Table.RefreshView(Named());
                Assert.AreEqual(1, enumerated);
                Assert.AreEqual(1, keys);
                Assert.AreSame(row, h.Surface().Items[0]);
                foreach (SortRow current in h.Rows)
                {
                    Assert.AreSame(current, Proof.Call(h.Table, "ResolveItem", current));
                }
            }
            h.Table.SortInterval = TimeSpan.FromHours(1);
            Proof.SetField(h.Table, "_orderSettledAt", DateTimeOffset.UtcNow);
            h.Rows[0].Rank = -100;
            keys = 0;
            h.Table.RefreshView(new object[] { h.Rows[0] });
            Assert.AreEqual(1, keys, "an eligible named update retains its key bound inside the interval");
            Assert.AreNotSame(h.Rows[0], h.Surface().Items[0], "the interval keeps the previous place");
            Proof.SetField(h.Table, "_orderSettledAt", DateTimeOffset.UtcNow.AddHours(-2));
            keys = 0;
            h.Table.RefreshView(new object[] { h.Rows[1] });
            Assert.AreEqual(h.Rows.Count, keys, "due global settling uses whole preparation");
            Assert.AreSame(h.Rows[0], h.Surface().Items[0]);
            Expect.Throws<ArgumentNullException>(() => h.Table.RefreshView(null!));
        });

    [TestMethod]
    public Task AnOldSubscriptionCannotApplyItsIndicesToANewSource() =>
        TestHost.RunAsync(async () =>
        {
            SortRow replacement = new("replacement", 1);
            SortHarness h = await SortHarness.LoadAsync(new[] { 1, 2 }, configure: table =>
            {
                var rows = (ObservableCollection<SortRow>)table.ItemsSource!;
                rows.CollectionChanged += (_, _) => table.ItemsSource = new[] { replacement };
            });
            h.Rows.Add(new("old-arrival", 3));
            CollectionAssert.AreEqual(new[] { "replacement" }, h.ViewKeys());
            NullSenderRows current = new() { replacement };
            h.Table.ItemsSource = current;
            current.AddNotified(new("current-arrival", 2));
            CollectionAssert.AreEqual(new[] { "replacement", "current-arrival" }, h.ViewKeys());
        });

    private sealed class NullSenderRows : List<SortRow>, INotifyCollectionChanged, IReadOnlyList<SortRow>
    {
        public event NotifyCollectionChangedEventHandler? CollectionChanged;
        internal int? RefusedIndex { get; set; }

        SortRow IReadOnlyList<SortRow>.this[int index]
        {
            get
            {
                if (RefusedIndex == index)
                {
                    throw new InvalidOperationException("Rejected source read");
                }
                return this[index];
            }
        }

        internal void AddNotified(SortRow item)
        {
            Add(item);
            CollectionChanged?.Invoke(null, new(NotifyCollectionChangedAction.Add, item, Count - 1));
        }

        internal void ReplaceNotified(int index, int count, params SortRow[] items)
        {
            SortRow[] removed = GetRange(index, count).ToArray();
            RemoveRange(index, count);
            InsertRange(index, items);
            CollectionChanged?.Invoke(null, new(NotifyCollectionChangedAction.Replace, items, removed, index));
        }
    }

    [TestMethod]
    public Task AWorkerRebasesCurrentInstancesKeysMembershipAndBaseTiesOnUI() =>
        TestHost.RunAsync(async () =>
        {
            int ui = Environment.CurrentManagedThreadId;
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 }, configure: table =>
                table.Schema<SortRow>().Key(row =>
                {
                    Assert.AreEqual(ui, Environment.CurrentManagedThreadId);
                    return row.Key;
                }).SortKey(table.Columns[0], row =>
                {
                    Assert.AreEqual(ui, Environment.CurrentManagedThreadId);
                    return row.Rank;
                }));
            h.Activate(0);
            object queued = Proof.Field<object>(h.Table, "_queuedSort");
            Assert.IsTrue((bool)Proof.Call(h.Table, "StartWork", queued)!);
            h.Rows[0].Rank = 0;
            SortRow arrival = new("arrival", 2);
            SortRow replacement = new("k1", 2);
            h.Table.ItemsSource = new[] { arrival, replacement, h.Rows[0] };
            await h.WaitForSortAsync();
            CollectionAssert.AreEqual(new[] { "k0", "arrival", "k1" }, h.ViewKeys());
            Assert.AreSame(replacement, h.Surface().Items[2]);
        });

    [TestMethod]
    [DataRow("newer")]
    [DataRow("assignment")]
    [DataRow("hidden")]
    [DataRow("reload")]
    public Task ACompletedSupersededWorkerCannotCommitItsCriterion(string change) =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 }, sortableColumns: 2);
            h.Table.Sort = new(h.Table.Columns[1], SortDirection.Descending);
            h.Activate(0);
            object queued = Proof.Field<object>(h.Table, "_queuedSort");
            Assert.IsTrue((bool)Proof.Call(h.Table, "StartWork", queued)!);
            switch (change)
            {
                case "newer":
                    h.Activate(1);
                    break;
                case "assignment":
                    h.Table.Sort = null;
                    break;
                case "hidden":
                    Proof.Call(h.Table, "SetColumnVisibility", TableHarness.EffectiveColumn(h.Table, "a"), false);
                    break;
                case "reload":
                    TestHost.RootPanel.Children.Remove(h.Table);
                    await TableHarness.WaitUntilAsync(() => Proof.Field<bool>(h.Table, "_detached"), "worker detachment");
                    await TableHarness.LoadAsync(h.Table);
                    break;
            }
            await h.WaitForSortAsync();
            Proof.Call(h.Table, "ResumeSort", queued);
            Assert.AreEqual(change == "assignment" ? null : "b", h.Table.Sort?.Column.Id);
            CollectionAssert.AreEqual(change == "assignment" ? new[] { "k0", "k1", "k2" }
                : change == "newer" ? new[] { "k1", "k2", "k0" } : new[] { "k0", "k2", "k1" }, h.ViewKeys());
        });

    [TestMethod]
    public Task AWorkerRebasesSiblingGroupsThroughTheirCapturedParentIdentities() =>
        TestHost.RunAsync(async () =>
        {
            SortRow first = new("child-a", 2);
            SortRow second = new("child-b", 1);
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1 }, configure: table =>
                table.Schema<SortRow>().Hierarchy(table.Columns[0],
                    row => row.Key == "k0" ? new[] { first, second } : Array.Empty<SortRow>(),
                    _ => true, (_, _) => { }));
            h.Activate(0);
            object queued = Proof.Field<object>(h.Table, "_queuedSort");
            Assert.IsTrue((bool)Proof.Call(h.Table, "StartWork", queued)!);
            first.Rank = 4;
            SortRow root = new("k0", 0);
            h.Table.ItemsSource = new[] { h.Rows[1], root, new SortRow("new-root", 2) };
            await h.WaitForSortAsync();
            CollectionAssert.AreEqual(new[] { "k0", "child-b", "child-a", "k1", "new-root" }, h.ViewKeys());
            Assert.AreSame(root, h.Surface().Items[0]);
        });

    [TestMethod]
    public Task SparseEditsExposeCurrentRowsAndSelectedRangesDuringEveryVectorCallback() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(Enumerable.Range(0, 60).ToArray(), configure: table =>
            {
                table.SortInterval = TimeSpan.Zero;
                table.Sort = new(table.Columns[0]);
            });
            SortRow[] selected = new[] { h.Rows[0], h.Rows[17], h.Rows[49] };
            h.Table.Selection = new(selected);
            object view = h.Surface().ItemsSource;
            IObservableVector<object> vector = (IObservableVector<object>)view;
            int callbacks = 0;
            void Changed(IObservableVector<object> sender, IVectorChangedEventArgs args)
            {
                callbacks++;
                SortRow[] current = ((IEnumerable)sender).Cast<SortRow>().ToArray();
                Assert.AreEqual(current.Length, current.Select(row => row.Key).Distinct().Count());
                foreach (SortRow row in current)
                {
                    Assert.AreSame(row, Proof.Call(h.Table, "ResolveItem", row));
                }
                List<int> indices = new();
                foreach (ItemIndexRange range in ((ISelectionInfo)view).GetSelectedRanges())
                {
                    for (int offset = 0; offset < range.Length; offset++)
                    {
                        indices.Add(range.FirstIndex + offset);
                    }
                }
                int[] expected = current.Select((row, index) => (row, index))
                    .Where(entry => selected.Any(row => row.Key == entry.row.Key)).Select(entry => entry.index).ToArray();
                CollectionAssert.AreEqual(expected, indices);
            }
            vector.VectorChanged += Changed;
            try
            {
                h.Rows[49].Rank = -1;
                h.Table.RefreshView(new object[] { h.Rows[49] });
                h.Rows[17] = new("k17", 17);
                h.Rows.RemoveAt(0);
            }
            finally
            {
                vector.VectorChanged -= Changed;
            }
            Assert.IsTrue(callbacks > 0);
        });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task MutableRowKeysUseTheirComparerBeforeSortingOrArrivalPlacement(bool arrival) =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 1, 2, 3 }, configure: table =>
            {
                table.Schema<SortRow>().SortKey(table.Columns[0], row => row,
                    Comparer<SortRow>.Create((left, right) => left.Rank.CompareTo(right.Rank)));
                table.Sort = new(table.Columns[0]);
                table.SortInterval = arrival ? TimeSpan.FromHours(1) : TimeSpan.Zero;
            });
            if (arrival)
            {
                Proof.SetField(h.Table, "_orderSettledAt", DateTimeOffset.UtcNow.AddMinutes(-30));
            }
            DateTimeOffset published = DateTimeOffset.UtcNow;
            h.Rows[0].Rank = 4;
            if (arrival)
            {
                h.Rows.Add(new("arrival", 0));
            }
            else
            {
                h.Table.RefreshView();
            }
            CollectionAssert.AreEqual(arrival ? new[] { "arrival", "k1", "k2", "k0" }
                : new[] { "k1", "k2", "k0" }, h.ViewKeys());
            if (arrival)
            {
                Assert.IsTrue(Proof.Field<DateTimeOffset>(h.Table, "_orderSettledAt") >= published,
                    "the fallback full sort starts a fresh settle interval");
                h.Rows.Add(new("second", 5));
                Assert.IsFalse(Proof.Field<bool>(h.Table, "_settlePending"),
                    "a successful current-key order proof and placement consumes the work");
            }
        });

    [TestMethod]
    public Task AnArrivalUsesKeptKeysAndChangedBaseTiesStillTakeTheFullSort() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 1, 2, 3 }, configure: table =>
            {
                table.Sort = new(table.Columns[0]);
                table.SortInterval = TimeSpan.FromHours(1);
            });
            h.Rows[0].Rank = 100;
            h.Rows[1].Rank = -1;
            h.Rows[2].Rank = -1;
            h.Rows.Add(new("arrival", 0));
            CollectionAssert.AreEqual(new[] { "arrival", "k0", "k1", "k2" }, h.ViewKeys());
            h.Table.SortInterval = TimeSpan.Zero;
            CollectionAssert.AreEqual(new[] { "k1", "k2", "arrival", "k0" }, h.ViewKeys());

            h.Table.ItemsSource = new[] { h.Rows[2], h.Rows[1], new SortRow("next", -1), h.Rows[0] };
            CollectionAssert.AreEqual(new[] { "k2", "k1", "next", "k0" }, h.ViewKeys());
        });

    [TestMethod]
    public Task DefaultStringOrderingRechecksTheCurrentCulture() =>
        TestHost.RunAsync(async () =>
        {
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new("en-US");
                SortHarness h = await SortHarness.LoadAsync(new[] { 0, 1 }, configure: table =>
                {
                    table.Schema<SortRow>().SortKey(table.Columns[0], row => row.Rank == 0 ? "ä" : "z");
                    table.SortInterval = TimeSpan.Zero;
                    table.Sort = new(table.Columns[0]);
                });
                CollectionAssert.AreEqual(new[] { "k0", "k1" }, h.ViewKeys());
                System.Globalization.CultureInfo.CurrentCulture = new("sv-SE");
                h.Table.RefreshView();
                CollectionAssert.AreEqual(new[] { "k1", "k0" }, h.ViewKeys());
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }
        });

    [TestMethod]
    [DataRow("assignment")]
    [DataRow("return")]
    [DataRow("hidden")]
    [DataRow("reload")]
    [DataRow("noop")]
    public Task QueuedHeaderSortChecksTheCommittedAndDetachEpisodes(string change) =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 }, sortableColumns: 2);
            h.Table.Sort = new(h.Table.Columns[1]);
            h.Activate(0);
            Assert.AreSame(h.Table.Columns[1], h.Table.Sort!.Value.Column);
            Assert.AreEqual(Visibility.Visible, h.Glyph(0).Visibility);
            switch (change)
            {
                case "assignment":
                    h.Table.Sort = null;
                    break;
                case "return":
                    h.Table.Sort = null;
                    h.Table.Sort = new(h.Table.Columns[1]);
                    break;
                case "hidden":
                    Proof.Call(h.Table, "SetColumnVisibility", TableHarness.EffectiveColumn(h.Table, "a"), false);
                    break;
                case "reload":
                    long lifetime = Proof.Field<long>(h.Table, "_lifetimeRevision");
                    TestHost.RootPanel.Children.Remove(h.Table);
                    Proof.Note($"queued reload after removal: IsLoaded={h.Table.IsLoaded}, detached={Proof.Field<bool>(h.Table, "_detached")}, lifetime={Proof.Field<long>(h.Table, "_lifetimeRevision")}, committed={h.Table.Sort?.Column.Id}");
                    Assert.IsFalse(h.Table.IsLoaded);
                    Proof.Call(h.Table, "RequestFrame");
                    await TableHarness.WaitUntilAsync(() => Proof.Field<bool>(h.Table, "_detached"), "actual detachment");
                    Proof.Note($"queued reload after Unloaded: lifetime={Proof.Field<long>(h.Table, "_lifetimeRevision")}, committed={h.Table.Sort?.Column.Id}");
                    Assert.IsTrue(Proof.Field<long>(h.Table, "_lifetimeRevision") > lifetime);
                    Assert.AreEqual("b", h.Table.Sort?.Column.Id, "removal refuses the queued commit before reloading");
                    await TableHarness.LoadAsync(h.Table);
                    break;
                case "noop":
                    h.Table.Sort = h.Table.Sort;
                    break;
            }
            await h.WaitForSortAsync();
            Assert.AreEqual(change == "noop" ? "a" : change == "assignment" ? null : "b",
                h.Table.Sort?.Column.Id);
        });

    [TestMethod]
    public Task AFailedQueuedComparisonRestoresTheCommittedGlyphAndWorkspace() =>
        TestHost.RunAsync(async () =>
        {
            InvalidOperationException failure = new("Unavailable key");
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 }, configure: table =>
            {
                table.Schema<SortRow>().SortKey<int>(table.Columns[1], _ =>
                    throw failure);
                table.Sort = new(table.Columns[0]);
            });
            Task<Exception> failed = TestHost.TrapAsync();
            h.Activate(1);
            Assert.AreSame(failure, await failed.WaitAsync(TimeSpan.FromSeconds(5)),
                "the host receives the original queued failure");
            await h.WaitForSortAsync();
            Assert.AreEqual("a", h.Table.Sort?.Column.Id);
            Assert.AreEqual(Visibility.Visible, h.Glyph(0).Visibility);
            Assert.AreEqual(Visibility.Collapsed, h.Glyph(1).Visibility);
            h.Table.SortInterval = TimeSpan.Zero;
            h.Rows[0].Rank = 0;
            h.Table.RefreshView();
            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2" }, h.ViewKeys());
        });

    [TestMethod]
    public Task AHeaderChoiceAfterRenderingWaitsForItsOwnFrame()
        => TestHost.RunAsync(async () =>
        {
            int frames = 0;
            int comparedAt = -1;
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 }, configure: table =>
                table.Schema<SortRow>().SortKey(table.Columns[1], row =>
                {
                    comparedAt = frames;
                    return row.Rank;
                }));
            void OnFrame(object? sender, object args)
            {
                frames++;
                if (frames == 1)
                {
                    h.Activate(1);
                }
            }
            h.Activate(0);
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
            try
            {
                await TableHarness.WaitUntilAsync(() => h.Table.Sort?.Column.Id == "b", "the second header choice");
                Assert.IsTrue(comparedAt >= 2, "the first callback cannot consume a choice whose glyph has not had a frame");
            }
            finally
            {
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
            }
        });

    [TestMethod]
    [DataRow("refresh")]
    [DataRow("sort")]
    [DataRow("layout")]
    public Task ARejectedNotifyingPublishCanRecoverThroughRefreshOrExplicitSort(string recovery) =>
        TestHost.RunAsync(async () =>
        {
            bool fails = false;
            SortHarness h = await SortHarness.LoadAsync(new[] { 1, 2 }, configure: table =>
            {
                table.Schema<SortRow>().SortKey(table.Columns[0], row =>
                    fails ? throw new InvalidOperationException("Unavailable sort key") : row.Rank);
                table.SortInterval = TimeSpan.Zero;
                table.Sort = new(table.Columns[0]);
            });
            fails = true;
            Expect.Throws<InvalidOperationException>(() => h.Rows.Add(new("arrival", 3)));
            CollectionAssert.AreEqual(new[] { "k0", "k1" }, h.ViewKeys());

            fails = false;
            if (recovery == "sort")
            {
                h.Table.Sort = new(h.Table.Columns[0], SortDirection.Descending);
            }
            else if (recovery == "layout")
            {
                h.Table.Layout = TestData.Layout(sortColumnId: "a", direction: SortDirection.Descending);
            }
            else
            {
                h.Table.RefreshView();
            }

            CollectionAssert.AreEqual(recovery == "refresh" ? new[] { "k0", "k1", "arrival" }
                : new[] { "arrival", "k1", "k0" }, h.ViewKeys());
        });

    [TestMethod]
    public Task AFrameCompletionCannotClearOrRearmAnExpiredLifetime() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 }, sortableColumns: 2);
            h.Table.Sort = new(h.Table.Columns[1]);
            h.Activate(0);
            object queued = Proof.Field<object>(h.Table, "_queuedSort");
            long lifetime = Proof.Field<long>(h.Table, "_lifetimeRevision");
            Proof.Call(h.Table, "OnLaidOut", null, new object());
            TestHost.RootPanel.Children.Remove(h.Table);
            Proof.Note($"frame after removal: IsLoaded={h.Table.IsLoaded}, detached={Proof.Field<bool>(h.Table, "_detached")}, lifetime={Proof.Field<long>(h.Table, "_lifetimeRevision")}");
            Assert.IsFalse(h.Table.IsLoaded);
            Proof.Call(h.Table, "RequestFrame");
            Assert.IsFalse(Proof.Field<bool>(h.Table, "_frameRequested"),
                "removal refuses a static frame subscription before deferred Unloaded");
            Proof.Call(h.Table, "ConsumeSort", queued);
            Assert.AreEqual("b", h.Table.Sort?.Column.Id);
            Proof.Call(h.Table, "OnUnloaded", h.Table, new RoutedEventArgs());
            Assert.IsTrue(Proof.Field<bool>(h.Table, "_detached"));
            Assert.IsTrue(Proof.Field<long>(h.Table, "_lifetimeRevision") > lifetime);
            Proof.SetField(h.Table, "_layoutPending", true);

            TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.IsTrue(TestHost.Dispatcher.TryEnqueue(() => drained.TrySetResult()));
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(Proof.Field<bool>(h.Table, "_layoutPending"),
                "an old frame cannot clear another lifetime's pending work");
            Assert.IsFalse(Proof.Field<bool>(h.Table, "_frameRequested"),
                "an old frame cannot restore the detached static subscription");

            await TableHarness.LoadAsync(h.Table);
            Assert.IsFalse(Proof.Field<bool>(h.Table, "_layoutPending"));
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
            fails = true;

            Expect.Throws<InvalidOperationException>(() =>
                h.Table.Release(new object[] { removed })
            );

            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2" }, h.ViewKeys());
            Assert.IsTrue((bool)Proof.Call(h.Table, "IsHeld", removed)!);
            if (restore)
                h.Table.Layout = TestData.Layout(sortColumnId: "b");
            else
                h.Table.Sort = new(h.Table.Columns[1]);
            CollectionAssert.AreEqual(new[] { "k1", "k2" }, h.ViewKeys());
            Assert.IsFalse((bool)Proof.Call(h.Table, "IsHeld", removed)!);

            h.Rows.Add(new SortRow("arrival", 4));
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

            await h.ActivateAsync(0);
            CollectionAssert.AreEqual(new[] { "k1", "k3", "k2", "k5", "k0", "k4" }, h.ViewKeys());

            await h.ActivateAsync(0);
            CollectionAssert.AreEqual(new[] { "k0", "k4", "k2", "k5", "k1", "k3" }, h.ViewKeys());

            await h.ActivateAsync(0);
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

            await h.ActivateAsync(0);
            await h.ActivateAsync(0);
            Assert.AreEqual(SortDirection.Descending, h.Table.Layout.SortDirection);

            await h.ActivateAsync(1);

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
            await h.ActivateAsync(1);

            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2" }, h.ViewKeys());
            Assert.IsNull(h.Table.Layout.SortColumnId);
            Assert.AreEqual(0, events);
        });

    [TestMethod]
    public Task Section9_SortingNeverReordersItemsSource() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });

            await h.ActivateAsync(0);

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

            await h.ActivateAsync(0);

            Assert.AreEqual(1, raised.Count);
            Assert.AreEqual(LayoutChange.Sort, raised[0]);
            Assert.AreEqual("a", h.Table.Layout.SortColumnId);
            Assert.AreEqual(SortDirection.Ascending, h.Table.Layout.SortDirection);

            await h.ActivateAsync(0);
            await h.ActivateAsync(0);

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

            CollectionAssert.AreEqual(new[] { "k0", "k2", "k1" }, h.ViewKeys());
            Assert.AreEqual(0, events, "restoration is silent");
            Assert.AreEqual(SortDirection.Descending, h.Table.Layout.SortDirection);
        });

    [TestMethod]
    public Task Section18_AnUnknownSortColumnFallsBackToNaturalOrder() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });
            await h.ActivateAsync(0);

            h.Table.Layout = TestData.Layout(order: new[] { "a" }, sortColumnId: "gone");

            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2" }, h.ViewKeys());
            Assert.IsNull(h.Table.Layout.SortColumnId);
        });

    [TestMethod]
    public Task Section18_ASavedSortOnANonSortableColumnFallsBackToNaturalOrder() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });
            await h.ActivateAsync(0);

            // "b" declares no sort, so the saved sort cannot be honoured.
            h.Table.Layout = TestData.Layout(order: new[] { "a" }, sortColumnId: "b");

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
            await h.ActivateAsync(0);
            CollectionAssert.AreEqual(new[] { "k1", "k2", "k0" }, h.ViewKeys());

            // A batch changes the sorted value without any collection notification.
            h.Rows[0].Rank = 0;
            CollectionAssert.AreEqual(
                new[] { "k1", "k2", "k0" },
                h.ViewKeys(),
                "a property change alone does not resort"
            );

            h.Table.RefreshView();

            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2" }, h.ViewKeys());
        });

    /// <summary>
    /// Section 9's owner ruling. When the rows take their sorted order, the row under the pointer
    /// keeps its place and the others sort around it. Pointing at another row moves nothing; the
    /// next refresh freezes that row instead.
    /// </summary>
    [TestMethod]
    public Task Section9_ALiveSortLeavesTheRowUnderThePointerWhereItIs() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(
                new[] { 1, 2, 3, 4, 5 },
                configure: table => table.SortInterval = TimeSpan.Zero
            );
            await h.ActivateAsync(0);
            Proof.Call(h.Table, "PointAt", h.Rows[1]);

            h.Rows[1].Rank = 10;
            h.Rows[3].Rank = 0;
            h.Table.RefreshView();

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

            CollectionAssert.AreEqual(
                new[] { "k3", "k0", "k4", "k2", "k1" },
                h.ViewKeys(),
                "the next refresh freezes the newly pointed row instead"
            );
        });

    [TestMethod]
    public Task Section9_AnAcceptedSourceUpdateReAppliesTheActiveSort() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });
            await h.ActivateAsync(0);

            h.Rows.Add(new SortRow("k3", 0));

            CollectionAssert.AreEqual(new[] { "k3", "k1", "k2", "k0" }, h.ViewKeys());
        });

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

            await h.ActivateAsync(0);

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

            await h.ActivateAsync(0);
            h.Table.UpdateLayout();

            Assert.AreEqual(Visibility.Visible, h.Glyph(0).Visibility);
            string ascendingGlyph = h.Glyph(0).Glyph;
            string ascendingStatus = AutomationProperties.GetItemStatus(h.Cell(0));
            Assert.AreNotEqual(string.Empty, ascendingStatus, "the direction is announced");
            Assert.AreEqual(Visibility.Collapsed, h.Glyph(1).Visibility, "only one column sorts");
            Assert.AreEqual(string.Empty, AutomationProperties.GetItemStatus(h.Cell(1)));

            await h.ActivateAsync(0);
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

            await h.ActivateAsync(0);
            h.Table.UpdateLayout();

            Assert.AreEqual(Visibility.Collapsed, h.Glyph(0).Visibility);
            Assert.AreEqual(string.Empty, AutomationProperties.GetItemStatus(h.Cell(0)));
        });

    [TestMethod]
    public Task Section9_TheGlyphFollowsTheColumnWhenItMoves() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 3, 1, 2 });
            await h.ActivateAsync(0);

            h.Table.Layout = TestData.Layout(order: new[] { "b", "c", "a" }, sortColumnId: "a");
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

            await h.ActivateAsync(0);
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
    internal async Task ActivateAsync(int visibleIndex)
    {
        Activate(visibleIndex);
        await WaitForSortAsync();
    }

    internal async Task WaitForSortAsync()
    {
        await TableHarness.WaitUntilAsync(() => typeof(Table)
            .GetField("_queuedSort", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Table) is null,
            "the queued header sort to finish");
        Table.UpdateLayout();
    }

    internal void Activate(int visibleIndex)
    {
        MethodInfo method = typeof(Header.Strip).GetMethod(
            "ActivateSortFrom",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        method.Invoke(Strip, new object?[] { Cell(visibleIndex) });
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
