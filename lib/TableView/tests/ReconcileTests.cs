using System.Collections;
using System.Reflection;
using Microsoft.UI.Xaml.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Foundation.Collections;

namespace Syno.TableView.Tests;

/// <summary>
/// The private view's reconcile, on its own and without a list.
/// </summary>
/// <remarks>
/// The claim under test is the one the whole design rests on: a reorder is announced only at the
/// positions the list is holding a container for, and every other position is changed without a
/// notification because the list keeps nothing there but a count. What that means for correctness
/// is checkable here, away from any panel. A shadow list is kept beside the view and is fed nothing
/// but the notifications the view raised, which is exactly what a list receives. Where the list
/// holds a container, the shadow must agree with the view; everywhere else it is allowed to be
/// stale, and being allowed to be stale is the saving.
/// </remarks>
[TestClass]
public class ReconcileTests
{
    private static readonly Assembly Control = typeof(Table).Assembly;

    // ------------------------------------------------------------------ a reorder

    [TestMethod]
    public void AReorderIsAnnouncedOnlyWhereTheListHoldsAContainer()
    {
        object[] rows = Rows(60);
        object view = NewView(rows);

        // One run of realized positions, and one pinned far from it: the shape a focused row makes
        // once it has been scrolled away from.
        int[] realized = Enumerable.Range(10, 16).Append(50).ToArray();

        Watcher watch = new(view);
        object[] snapshot = rows.Reverse().ToArray();
        Reconcile(view, snapshot, realized);

        CollectionAssert.AreEqual(snapshot, Rows(view), "the view is the snapshot");
        watch.AssertListAgreesAt(realized, view);
        Assert.IsTrue(
            watch.Notifications < 60,
            $"a reversal of 60 rows raised {watch.Notifications} notifications for 17 containers"
        );
    }

    [TestMethod]
    public void AReorderLeavesNoRowInTheViewTwice() =>
        AssertNoDuplicatesThroughout(
            Rows(40),
            s => s.Reverse().ToArray(),
            new[] { 5, 6, 7, 8, 30 }
        );

    /// <summary>
    /// The case that makes two runs certain, and the one a single run-at-a-time script cannot do
    /// without holding a duplicate: the row that belongs under the pinned container is currently
    /// inside the visible run, and the row under the visible run belongs where the pinned one is.
    /// </summary>
    [TestMethod]
    public void ARowMovingBetweenTwoRunsIsNeverInTheViewTwice()
    {
        object[] rows = Rows(30);
        object[] snapshot = (object[])rows.Clone();
        (snapshot[4], snapshot[25]) = (snapshot[25], snapshot[4]);

        AssertNoDuplicatesThroughout(rows, _ => snapshot, new[] { 3, 4, 5, 25 });
    }

    // ------------------------------------------------------------------ membership

    [TestMethod]
    public void RowsJoiningAndLeavingAreAnnouncedWithinTheCache()
    {
        object[] rows = Rows(50);
        object view = NewView(rows);

        List<object> snapshot = rows.Where((_, i) => i % 5 != 0).ToList();
        object[] arrivals = Rows(3);
        snapshot.InsertRange(5, arrivals);

        int[] realized = Enumerable.Range(2, 12).ToArray();
        Watcher watch = new(view);
        Reconcile(view, snapshot, realized);

        CollectionAssert.IsSubsetOf(
            arrivals,
            watch.Inserted,
            "rows joining inside the cache are announced"
        );
        CollectionAssert.IsSubsetOf(
            new[] { rows[0], rows[5], rows[10] },
            watch.Removed,
            "rows leaving inside the cache are announced"
        );
        CollectionAssert.AreEqual(snapshot.ToArray(), Rows(view), "the view is the snapshot");
        Assert.AreEqual(
            snapshot.Count,
            watch.Count,
            "the count the notifications describe is the real count"
        );
    }

    /// <summary>
    /// Past the list's cache a change in membership changes only the count, which the table tells
    /// the list in slices after the update. A notice per row in the update held the UI thread for
    /// every row of a long list, and the list wrapped every row it was told of for COM, on screen
    /// or not, so an arriving row is announced without its row.
    /// </summary>
    [TestMethod]
    public void RowsPastTheCacheChangeOnlyTheCountAndArriveWithoutTheirRow()
    {
        object[] rows = Rows(10);
        object view = NewView(rows);
        object[] longer = rows.Concat(Rows(1000)).ToArray();
        int[] realized = Enumerable.Range(0, 10).ToArray();

        Watcher watch = new(view);
        Apply(view, longer, realized);
        Assert.AreEqual(0, watch.Notifications, "the update raised nothing past the cache");
        Assert.AreEqual(10, ((IList)view).Count, "the list has not been told of the arrivals");

        CatchUp(view);
        Assert.AreEqual(longer.Length, watch.Count, "the catch-up told the list the count");
        Assert.IsTrue(
            watch.Inserted.All(row => row is null),
            "an arrival was announced with its row"
        );
        CollectionAssert.AreEqual(longer, Rows(view), "the list reads each row where it stands");

        watch = new(view);
        Apply(view, rows, realized);
        Assert.AreEqual(0, watch.Notifications, "the update raised nothing past the cache");
        Assert.IsNull(((IList)view)[500], "a position past the last row is vacant");

        CatchUp(view);
        CollectionAssert.AreEqual(rows, Rows(view), "the vacant positions are gone");
    }

    /// <summary>
    /// A different list keeps the list's containers, which the table rebinds, so the list is told
    /// only the change in count. A removal and an arrival per held position would make it build a
    /// new row for every row on screen, the work a click made during the switch waits for.
    /// </summary>
    [TestMethod]
    public void ADifferentListRaisesOnlyTheChangeInCount()
    {
        object view = NewView(Rows(60));
        object[] snapshot = Rows(40);

        Watcher watch = new(view);
        Reconcile(view, snapshot, Enumerable.Range(10, 16).ToArray());

        CollectionAssert.AreEqual(snapshot, Rows(view), "the view is the snapshot");
        Assert.AreEqual(snapshot.Length, watch.Count, "the count the notifications describe");
        Assert.AreEqual(20, watch.Notifications, "only the 20 rows the shorter list lacks");
    }

    // ------------------------------------------------------------------ a pinned row

    /// <summary>
    /// A pinned row keeps its container through a reorder around it. Unpinned, the longest run of
    /// rows already in order here is B then C, so P would be removed and inserted again at the
    /// same index: it would animate out and back in under the pointer that froze it.
    /// </summary>
    [TestMethod]
    public void APinnedRowIsNeverRemovedByAReorderAroundIt()
    {
        object[] rows = Rows(4);
        object pinned = rows[1];
        object view = NewView(rows);
        object[] snapshot = { rows[2], pinned, rows[3], rows[0] };

        Watcher watch = new(view);

        Reconcile(view, snapshot, new[] { 0, 1, 2, 3 }, new HashSet<object> { pinned });

        CollectionAssert.AreEqual(snapshot, Rows(view), "the view is the snapshot");
        CollectionAssert.DoesNotContain(watch.Removed, pinned, "the pinned row kept its container");
    }

    // ------------------------------------------------------------------ the unwindowed case

    /// <summary>
    /// Every position realized, which is every table small enough to fit on screen and so nearly
    /// every test in this project. The quiet pass is then empty and this is the algorithm that ran
    /// before the change: one removal and one insertion for each row that moved.
    /// </summary>
    [TestMethod]
    public void WithEveryPositionRealizedNothingIsPlacedQuietly()
    {
        object[] rows = Rows(12);
        object view = NewView(rows);

        Watcher watch = new(view);
        object[] snapshot = rows.Reverse().ToArray();
        Reconcile(view, snapshot, Enumerable.Range(0, 12).ToArray());

        CollectionAssert.AreEqual(snapshot, Rows(view));
        watch.AssertListAgreesAt(Enumerable.Range(0, 12).ToArray(), view);
    }

    [TestMethod]
    public void AnUnchangedSnapshotRaisesNothing()
    {
        object[] rows = Rows(20);
        object view = NewView(rows);

        Watcher watch = new(view);
        Reconcile(view, rows, new[] { 0, 1, 2 });

        Assert.AreEqual(0, watch.Notifications);
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertNoDuplicatesThroughout(
        object[] rows,
        Func<object[], object[]> order,
        int[] realized
    )
    {
        object view = NewView(rows);
        IList live = (IList)view;

        BindableVectorChangedEventHandler check = (_, _) =>
        {
            HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
            foreach (object row in live)
            {
                Assert.IsTrue(
                    seen.Add(row),
                    "a row was in the view twice while the list was watching"
                );
            }
        };

        ((IBindableObservableVector)view).VectorChanged += check;
        object[] snapshot = order(rows);
        Reconcile(view, snapshot, realized);
        ((IBindableObservableVector)view).VectorChanged -= check;

        CollectionAssert.AreEqual(snapshot, Rows(view), "the view is the snapshot");
    }

    private static object[] Rows(int count) =>
        Enumerable.Range(0, count).Select(_ => new object()).ToArray();

    private static object[] Rows(object view) => ((IList)view).Cast<object>().ToArray();

    private static object NewView(IEnumerable<object> rows)
    {
        Type view = Control.GetType("Syno.TableView.Body.View")!;

        // No list and no table here, so no row is ever selected.
        object created = Activator.CreateInstance(
            view,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new object[]
            {
                new Func<object, bool>(_ => false),
                new Func<IEnumerable<int>>(() => []),
            },
            null
        )!;

        IList list = (IList)created;
        foreach (object row in rows)
        {
            list.Add(row);
        }

        return created;
    }

    /// <summary>Apply the update, then tell the list the change in count, as the table does.</summary>
    private static void Reconcile(
        object view,
        IReadOnlyList<object> snapshot,
        IReadOnlyList<int> realized,
        IReadOnlySet<object>? pinned = null
    )
    {
        Apply(view, snapshot, realized, pinned);
        CatchUp(view);
    }

    /// <summary>
    /// The update alone. The list's cache reaches as far as the last realized position, which is
    /// where it reaches unless a container is pinned far from the rest.
    /// </summary>
    private static void Apply(
        object view,
        IReadOnlyList<object> snapshot,
        IReadOnlyList<int> realized,
        IReadOnlySet<object>? pinned = null
    )
    {
        var rows = Rows(view);
        Type changesType = Control.GetType("Syno.TableView.Body.Changes")!;
        object screen = Activator.CreateInstance(
            changesType.GetNestedType("Screen", BindingFlags.NonPublic)!,
            realized,
            realized.Count == 0 ? 0 : realized.Max() + 1
        )!;

        // Each row's slot, as the table numbers rows: one small number per row in either list.
        Dictionary<object, int> slots = new(ReferenceEqualityComparer.Instance);
        foreach (object row in rows.Concat(snapshot))
        {
            slots.TryAdd(row, slots.Count);
        }

        object[] bySlot = slots.OrderBy(entry => entry.Value).Select(entry => entry.Key).ToArray();
        int[] order = snapshot.Select(row => slots[row]).ToArray();
        int[] targets = Enumerable.Repeat(-1, slots.Count).ToArray();
        for (int i = 0; i < order.Length; i++)
        {
            targets[order[i]] = i;
        }

        object[] arguments =
        {
            rows,
            new ArraySegment<int>(rows.Select(row => slots[row]).ToArray()),
            new ArraySegment<int>(order),
            targets,
            new Func<int, object>(slot => bySlot[slot]),
            screen,
            (pinned ?? new HashSet<object>()).Select(row => slots[row]).ToHashSet(),
        };
        var changes = Task.Run(() =>
        {
            var prepared = Activator.CreateInstance(changesType, nonPublic: true)!;
            changesType.GetMethod("Prepare", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(prepared, arguments);
            return prepared;
        }).GetAwaiter().GetResult();
        view.GetType().GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, new[] { changes });
    }

    /// <summary>Tell the list the whole change in count the view owes it.</summary>
    private static void CatchUp(object view) =>
        view.GetType().GetMethod("CatchUp", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, new object[] { long.MaxValue });

    /// <summary>
    /// A list's-eye view: it is told nothing except the notifications, and it applies them the way
    /// a list applies them. Where it disagrees with the view afterwards, a list would be showing a
    /// stale row.
    /// </summary>
    private sealed class Watcher
    {
        private readonly List<object> _shadow;

        internal Watcher(object view)
        {
            _shadow = ((IList)view).Cast<object>().ToList();
            ((IBindableObservableVector)view).VectorChanged += Apply;
        }

        internal int Notifications { get; private set; }

        internal int Count => _shadow.Count;

        internal List<object> Removed { get; } = new();

        /// <summary>What the list read at each inserted position during the notice.</summary>
        internal List<object?> Inserted { get; } = new();

        internal void AssertListAgreesAt(IReadOnlyList<int> realized, object view)
        {
            object[] rows = ((IList)view).Cast<object>().ToArray();
            foreach (int index in realized)
            {
                Assert.AreSame(
                    rows[index],
                    _shadow[index],
                    $"the list holds a container at {index} and was not told what stands there"
                );
            }
        }

        private void Apply(IBindableObservableVector sender, object args)
        {
            var e = (IVectorChangedEventArgs)args;
            Notifications++;
            int index = (int)e.Index;

            switch (e.CollectionChange)
            {
                case CollectionChange.ItemRemoved:
                    Removed.Add(_shadow[index]);
                    _shadow.RemoveAt(index);
                    break;
                case CollectionChange.ItemInserted:
                    object? read = ((IList)sender)[index];
                    Inserted.Add(read);
                    _shadow.Insert(index, read!);
                    break;
                case CollectionChange.ItemChanged:
                    _shadow[index] = ((IList)sender)[index]!;
                    break;
                default:
                    Assert.Fail(
                        $"the view raised {e.CollectionChange}, which the list is never sent"
                    );
                    break;
            }
        }
    }
}
