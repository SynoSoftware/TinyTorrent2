using System.Collections;
using System.Reflection;
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
    public void RowsJoiningAndLeavingAreAnnouncedWhereverTheyAre()
    {
        object[] rows = Rows(50);
        object view = NewView(rows);

        List<object> snapshot = rows.Where((_, i) => i % 5 != 0).ToList();
        object[] arrivals = Rows(3);
        snapshot.InsertRange(20, arrivals);

        int[] realized = Enumerable.Range(2, 12).ToArray();
        Watcher watch = new(view);
        Reconcile(view, snapshot, realized);

        CollectionAssert.AreEqual(snapshot.ToArray(), Rows(view), "the view is the snapshot");
        Assert.AreEqual(
            snapshot.Count,
            watch.Count,
            "the count the notifications describe is the real count"
        );
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

        VectorChangedEventHandler<object> check = (_, _) =>
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

        ((IObservableVector<object>)view).VectorChanged += check;
        object[] snapshot = order(rows);
        Reconcile(view, snapshot, realized);
        ((IObservableVector<object>)view).VectorChanged -= check;

        CollectionAssert.AreEqual(snapshot, Rows(view), "the view is the snapshot");
    }

    private static object[] Rows(int count) =>
        Enumerable.Range(0, count).Select(_ => new object()).ToArray();

    private static object[] Rows(object view) => ((IList)view).Cast<object>().ToArray();

    private static object NewView(IEnumerable<object> rows)
    {
        Type identity = Control.GetType("Syno.TableView.ItemIdentity")!;
        Type view = Control.GetType("Syno.TableView.Body.View")!;

        object comparer = Activator.CreateInstance(
            identity,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null,
            null,
            null
        )!;

        object created = Activator.CreateInstance(
            view,
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { comparer },
            null
        )!;

        IList list = (IList)created;
        foreach (object row in rows)
        {
            list.Add(row);
        }

        return created;
    }

    private static void Reconcile(
        object view,
        IReadOnlyList<object> snapshot,
        IReadOnlyList<int> realized,
        IReadOnlySet<object>? pinned = null
    ) =>
        view.GetType()
            .GetMethod("Reconcile", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, new object[] { snapshot, realized, pinned ?? new HashSet<object>() });

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
            ((IObservableVector<object>)view).VectorChanged += Apply;
        }

        internal int Notifications { get; private set; }

        internal int Count => _shadow.Count;

        internal List<object> Removed { get; } = new();

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

        private void Apply(IObservableVector<object> sender, IVectorChangedEventArgs e)
        {
            Notifications++;
            int index = (int)e.Index;

            switch (e.CollectionChange)
            {
                case CollectionChange.ItemRemoved:
                    Removed.Add(_shadow[index]);
                    _shadow.RemoveAt(index);
                    break;
                case CollectionChange.ItemInserted:
                    _shadow.Insert(index, ((IList)sender)[index]!);
                    break;
                case CollectionChange.ItemChanged:
                    _shadow[index] = ((IList)sender)[index]!;
                    break;
                default:
                    Assert.Fail($"the view raised {e.CollectionChange}, which the list is never sent");
                    break;
            }
        }
    }
}
