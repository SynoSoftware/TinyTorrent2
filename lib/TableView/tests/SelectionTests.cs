using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Syno.TableView.Tests;

/// <summary>
/// Sections 5, 5.3 and 13: the table-owned selection set, current item, anchor, the four mode
/// limits, an idempotent Selection request, and keyed reconciliation across source snapshots.
/// </summary>
[TestClass]
public class SelectionTests
{
    [TestMethod]
    public Task PlainClickReplacesAndSetsTheAnchor() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6);

        h.Click(h[1]);
        CollectionAssert.AreEqual(new[] { "k1" }, h.SelectedKeys());
        Assert.AreEqual("k1", h.CurrentKey());

        h.Click(h[3]);
        CollectionAssert.AreEqual(new[] { "k3" }, h.SelectedKeys());

        // The anchor moved to k3 with that plain click, so the range runs back from it.
        h.Click(h[1], shift: true);
        CollectionAssert.AreEqual(new[] { "k1", "k2", "k3" }, h.SelectedKeys());
        Assert.AreEqual(3, h.Events);
    });

    [TestMethod]
    public Task CtrlClickTogglesOneRow() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6);

        h.Click(h[1]);
        h.Click(h[4], ctrl: true);
        CollectionAssert.AreEqual(new[] { "k1", "k4" }, h.SelectedKeys());

        h.Click(h[1], ctrl: true);
        CollectionAssert.AreEqual(new[] { "k4" }, h.SelectedKeys());
        Assert.AreEqual("k1", h.CurrentKey(), "A Ctrl-click makes its row current whether or not it stays selected.");
    });

    [TestMethod]
    public Task ShiftClickTakesTheInclusiveRangeFromTheAnchorAndTheAnchorDoesNotMove() =>
        TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6);

        h.Click(h[2]);
        h.Click(h[4], shift: true);
        CollectionAssert.AreEqual(new[] { "k2", "k3", "k4" }, h.SelectedKeys());

        // Still anchored at k2, so a second Shift click re-projects instead of growing.
        h.Click(h[0], shift: true);
        CollectionAssert.AreEqual(new[] { "k0", "k1", "k2" }, h.SelectedKeys());
    });

    [TestMethod]
    public Task CtrlShiftClickAddsTheRange() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6);

        h.Click(h[0]);
        h.Click(h[3], ctrl: true);
        h.Click(h[5], ctrl: true, shift: true);
        CollectionAssert.AreEqual(new[] { "k0", "k3", "k4", "k5" }, h.SelectedKeys());
    });

    // ------------------------------------------------------------------ the four mode limits

    [TestMethod]
    public Task NoneAllowsNoSelectedItemsButStillMakesARowCurrent() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(
            6, t => t.SelectionMode = ListViewSelectionMode.None);

        h.Click(h[2]);
        Assert.AreEqual(0, h.SelectedKeys().Length);
        Assert.AreEqual("k2", h.CurrentKey());

        h.Table.Selection = new(new object[] { h[0], h[1] });
        Assert.AreEqual(0, h.SelectedKeys().Length);
    });

    [TestMethod]
    public Task SingleAllowsAtMostOne() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(
            6, t => t.SelectionMode = ListViewSelectionMode.Single);

        h.Click(h[1]);
        h.Click(h[4], shift: true);
        CollectionAssert.AreEqual(new[] { "k4" }, h.SelectedKeys());

        h.Table.Selection = new(new object[] { h[0], h[2], h[5] });
        CollectionAssert.AreEqual(
            new[] { "k0" }, h.SelectedKeys(), "Single retains the first resolved item in visual order.");
    });

    [TestMethod]
    public Task SingleCtrlSelectionReplacesAnotherRowAndDeselectsTheSameRow() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(
            6, t => t.SelectionMode = ListViewSelectionMode.Single);
        h.Click(h[1]);
        h.Click(h[4], ctrl: true);
        CollectionAssert.AreEqual(new[] { "k4" }, h.SelectedKeys());

        h.Click(h[4], ctrl: true);
        Assert.AreEqual(0, h.SelectedKeys().Length);
        Assert.AreEqual("k4", h.CurrentKey());
    });

    [TestMethod]
    public Task MultipleTogglesOnAPlainClickAndKeepsMany() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(
            6, t => t.SelectionMode = ListViewSelectionMode.Multiple);

        h.Click(h[1]);
        h.Click(h[3]);
        CollectionAssert.AreEqual(new[] { "k1", "k3" }, h.SelectedKeys());

        h.Click(h[1]);
        CollectionAssert.AreEqual(new[] { "k3" }, h.SelectedKeys());
    });

    [TestMethod]
    public Task ExtendedKeepsMany() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6);

        h.Table.Selection = new(new object[] { h[0], h[2], h[5] });
        CollectionAssert.AreEqual(new[] { "k0", "k2", "k5" }, h.SelectedKeys());
    });

    // ------------------------------------------------------------------ Selection

    [TestMethod]
    public Task AssigningTheSameSelectionRaisesNothing() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6);

        h.Table.Selection = new(new object[] { h[1], h[2] }, h[2]);
        Assert.AreEqual(1, h.Events);

        // Same identities, different request order, and an equal current: no second event.
        h.Table.Selection = new(new object[] { h[2], h[1] }, h[2]);
        Assert.AreEqual(1, h.Events);

        h.Table.Selection = new(new object[] { h[2], h[1] }, h[1]);
        Assert.AreEqual(2, h.Events, "A current-item change alone is still a change.");
    });

    [TestMethod]
    public Task SelectionResolvesByKeyAndDropsWhatCannotBeSelected() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(
            6, t => t.Schema<Row>().CanInteract(row => row.Interactive));

        h[4].Interactive = false;

        // A detached instance with a known key, a duplicate, an unknown key, and a passive row.
        h.Table.Selection = new(new object[]
        {
            new Row("k1"), h[1], new Row("nope"), h[4], h[3],
        });

        CollectionAssert.AreEqual(new[] { "k1", "k3" }, h.SelectedKeys());
        Assert.AreEqual("k1", h.CurrentKey(), "An omitted current uses the first selected item in visual order.");
        Assert.IsTrue(
            ReferenceEquals(h[1], h.Table.Selection.Items[0]),
            "A resolved identity must expose the current view instance, not the supplied one.");
    });

    [TestMethod]
    public Task SelectionAcceptsAnUnselectedCurrentAndFallsBackWhenItIsGone() =>
        TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6);

        h.Table.Selection = new(new object[] { h[1], h[2] }, h[5]);
        CollectionAssert.AreEqual(new[] { "k1", "k2" }, h.SelectedKeys());
        Assert.AreEqual("k5", h.CurrentKey());

        h.Table.Selection = new(new object[] { h[1], h[2] }, new Row("gone"));
        Assert.AreEqual("k1", h.CurrentKey());
    });

    // ------------------------------------------------------------------ reconciliation

    [TestMethod]
    public Task SameKeyRehydrationNotifiesWithReplacementRows() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6);

        h.Table.Selection = new(new object[] { h[1], h[3] }, h[3]);
        h.Events = 0;

        ObservableCollection<Row> replacement = new(h.Rows.Select(r => new Row(r.Key)));
        h.Table.ItemsSource = replacement;

        Assert.AreEqual(1, h.Events, "Observers must receive replacement instances.");
        Assert.AreSame(replacement[3], h.LastCurrent);
        CollectionAssert.AreEqual(new[] { "k1", "k3" }, h.SelectedKeys());
        Assert.AreEqual("k3", h.CurrentKey());
        Assert.IsTrue(
            ReferenceEquals(replacement[1], h.Table.Selection.Items[0]),
            "SelectedItems must expose the new instances.");
    });

    [TestMethod]
    public Task ReorderingAloneRaisesNoEvent() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6);

        h.Table.Selection = new(new object[] { h[1], h[3] }, h[3]);
        h.Events = 0;

        h.Rows.Move(0, 5);

        Assert.AreEqual(0, h.Events);
        CollectionAssert.AreEqual(new[] { "k1", "k3" }, h.SelectedKeys());
    });

    [TestMethod]
    public Task PositionsChangeButThePacketFollowsVisualOrder() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6);

        h.Table.Selection = new(new object[] { h[1], h[3] });
        h.Events = 0;
        h.Rows.Move(3, 0);
        Assert.AreEqual(1, h.Events, "The exposed packet changed order.");

        CollectionAssert.AreEqual(
            new[] { "k3", "k1" }, h.SelectedKeys(), "The packet is in current visual row order.");
    });

    [TestMethod]
    public Task RemovingTheCurrentItemPromotesTheFirstRetainedSelectedItemInOneEvent() =>
        TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6);

        h.Table.Selection = new(new object[] { h[1], h[2], h[3] }, h[2]);
        h.Events = 0;

        h.Rows.Remove(h[2]);

        Assert.AreEqual(1, h.Events);
        CollectionAssert.AreEqual(new[] { "k1", "k3" }, h.SelectedKeys());
        Assert.AreEqual("k1", h.CurrentKey());
        CollectionAssert.AreEqual(new[] { "k1", "k3" }, h.LastSelected!.Cast<Row>().Select(r => r.Key).ToArray());
    });

    [TestMethod]
    public Task RemovingEverySelectedItemClearsCurrent() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(4);

        h.Table.Selection = new(new object[] { h[1] });
        h.Events = 0;

        h.Rows.Remove(h[1]);

        Assert.AreEqual(1, h.Events);
        Assert.AreEqual(0, h.SelectedKeys().Length);
        Assert.IsNull(h.CurrentKey());
    });

    [TestMethod]
    public Task ABecameNonInteractiveItemIsPrunedByRefreshView() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(
            6, t => t.Schema<Row>().CanInteract(row => row.Interactive));

        h.Table.Selection = new(new object[] { h[1], h[2] }, h[1]);
        h.Events = 0;

        h[1].Interactive = false;
        h.Table.RefreshView();

        Assert.AreEqual(1, h.Events);
        CollectionAssert.AreEqual(new[] { "k2" }, h.SelectedKeys());
        Assert.AreEqual("k2", h.CurrentKey());
    });

    [TestMethod]
    public Task ANonInteractiveItemCannotBeClicked() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(
            6, t => t.Schema<Row>().CanInteract(row => row.Interactive));

        h[3].Interactive = false;
        h.Click(h[1]);
        h.Click(h[3]);

        CollectionAssert.AreEqual(new[] { "k1" }, h.SelectedKeys());
        Assert.AreEqual("k1", h.CurrentKey());
    });

    [TestMethod]
    public Task DuplicateItemKeysFailFast() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(3);

        h.Table.Selection = new(new object[] { h[1] });
        Expect.Throws<InvalidOperationException>(
            () => h.Table.ItemsSource = new ObservableCollection<Row>
            {
                new("dup"), new("dup"),
            });

        Assert.AreSame(h.Rows, h.Table.ItemsSource);
        Assert.AreSame(h[1], h.Table.Selection.Current);
    });

    [TestMethod]
    public Task AcceptedNestedSourceUpdateSurvivesHandlerFailure() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(3);
        h.Table.Selection = new(new object[] { h[1] });
        ObservableCollection<Row> replacement = new(h.Rows.Select(row => new Row(row.Key)));
        bool entered = false;
        void OnSelection(object? sender, Selection selection)
        {
            if (entered) return;
            entered = true;
            replacement.Add(new Row("k3"));
            throw new InvalidOperationException("Host callback failed after publishing.");
        }
        h.Table.SelectionChanged += OnSelection;
        Expect.Throws<InvalidOperationException>(() => h.Table.ItemsSource = replacement);
        h.Table.SelectionChanged -= OnSelection;

        Assert.AreSame(replacement, h.Table.ItemsSource);
        Assert.AreSame(replacement[1], h.Table.Selection.Current);
        replacement.RemoveAt(1);
        Assert.AreEqual(0, h.Table.Selection.Items.Count);
        Assert.IsNull(h.Table.Selection.Current);
    });

    // ------------------------------------------------------------------ the hosted list

    [TestMethod]
    public Task TheHostedListIsGivenTheTableSelection() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6);

        h.Table.Selection = new(new object[] { h[1], h[3] });
        CollectionAssert.AreEqual(new[] { "k1", "k3" }, h.ContainerSelectedKeys());

        h.Click(h[5]);
        CollectionAssert.AreEqual(new[] { "k5" }, h.ContainerSelectedKeys());
    });

    [TestMethod]
    public Task TheListsOwnSelectionChangeIsNotTruth() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6);

        h.Table.Selection = new(new object[] { h[1] });
        h.Events = 0;

        // Exactly what the container does to itself on a plain press in Multiple mode.
        ListView list = h.HostedList();
        list.SelectedItems.Add(h[4]);

        Assert.AreEqual(0, h.Events, "SelectionChanged from the hosted list must not reach the host.");
        CollectionAssert.AreEqual(new[] { "k1" }, h.SelectedKeys());
    });
}
