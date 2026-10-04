using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.System;
using Synapse;

namespace Synapse_Tests;

/// <summary>
/// Section 9 through real input: mouse messages through the window and real key messages to a
/// focused header. <c>TappedRoutedEventArgs</c> and <c>KeyRoutedEventArgs</c> cannot be
/// constructed, so this is the only way to run the activation paths themselves.
/// </summary>
[TestClass]
// Interactive: injects real mouse and key messages and takes the foreground window. Excluded
// from the default run by .runsettings; ask for it deliberately with --filter TestCategory=Interactive.
[TestCategory("Interactive")]
public class RealSortTests
{
    [TestMethod]
    public Task ARealHeaderClickRunsTheWholeSortCycle() => TestHost.RunAsync(async () =>
    {
        ObservableCollection<SortRow> rows = Rows(3, 1, 2, 1);
        DragHarness h = await DragHarness.LoadAsync(table => Configure(table, rows));
        List<TableLayoutChangeKind> kinds = new();
        h.Table.LayoutChanged += (_, kind) => kinds.Add(kind);

        await h.MoveAsync(100);
        await ClickAsync(h, 100);

        CollectionAssert.AreEqual(new[] { "k1", "k3", "k2", "k0" }, ViewKeys(h.Table));

        await ClickAsync(h, 100);

        CollectionAssert.AreEqual(new[] { "k0", "k2", "k1", "k3" }, ViewKeys(h.Table));

        await ClickAsync(h, 100);

        CollectionAssert.AreEqual(new[] { "k0", "k1", "k2", "k3" }, ViewKeys(h.Table));
        CollectionAssert.AreEqual(
            new[] { TableLayoutChangeKind.Sort, TableLayoutChangeKind.Sort, TableLayoutChangeKind.Sort },
            kinds);
    });

    [TestMethod]
    public Task ARealClickOnANonSortableHeaderChangesNothing() => TestHost.RunAsync(async () =>
    {
        ObservableCollection<SortRow> rows = Rows(3, 1, 2);
        DragHarness h = await DragHarness.LoadAsync(table => Configure(table, rows));
        int events = 0;
        h.Table.LayoutChanged += (_, _) => events++;

        // Column "b" spans 200 to 400 and declares no sort.
        await h.MoveAsync(300);
        await ClickAsync(h, 300);

        CollectionAssert.AreEqual(new[] { "k0", "k1", "k2" }, ViewKeys(h.Table));
        Assert.AreEqual(0, events);
    });

    [TestMethod]
    public Task ARealDragDoesNotAlsoSortTheHeaderItStartedOn() => TestHost.RunAsync(async () =>
    {
        ObservableCollection<SortRow> rows = Rows(3, 1, 2);
        DragHarness h = await DragHarness.LoadAsync(table => Configure(table, rows));

        await h.MoveAsync(100);
        await h.PressAsync(100);
        await h.MoveAsync(160);
        await h.MoveAsync(520);
        await h.ReleaseAsync(520);

        CollectionAssert.AreEqual(new[] { "b", "c", "a" }, TableHarness.Order(h.Table));
        CollectionAssert.AreEqual(
            new[] { "k0", "k1", "k2" }, ViewKeys(h.Table), "the drop moved the column, it did not sort");
    });

    [TestMethod]
    public Task RealEnterAndSpaceOnAFocusedHeaderRunTheSameCycle() => TestHost.RunAsync(async () =>
    {
        ObservableCollection<SortRow> rows = Rows(3, 1, 2, 1);
        DragHarness h = await DragHarness.LoadAsync(table => Configure(table, rows));

        Control cell = HeaderCell(h.Strip, 0);
        Assert.IsTrue(cell.Focus(FocusState.Keyboard), "the header took keyboard focus");

        await h.KeyAsync(VirtualKey.Enter);
        h.Table.UpdateLayout();

        CollectionAssert.AreEqual(new[] { "k1", "k3", "k2", "k0" }, ViewKeys(h.Table), "Enter sorted");

        await h.KeyAsync(VirtualKey.Space);
        h.Table.UpdateLayout();

        CollectionAssert.AreEqual(
            new[] { "k0", "k2", "k1", "k3" }, ViewKeys(h.Table), "Space continued the cycle");

        await h.KeyAsync(VirtualKey.Space);
        h.Table.UpdateLayout();

        CollectionAssert.AreEqual(new[] { "k0", "k1", "k2", "k3" }, ViewKeys(h.Table));
    });

    [TestMethod]
    public Task ARealHeaderClickKeepsTheSelectedRowsAndTheCurrentRow() => TestHost.RunAsync(async () =>
    {
        ObservableCollection<SortRow> rows = Rows(3, 1, 2, 1, 3, 2);
        DragHarness h = await DragHarness.LoadAsync(table => Configure(table, rows));

        h.Table.Selection = new(new object[] { rows[0], rows[3] }, rows[3]);
        int selectionEvents = 0;
        h.Table.SelectionStateChanged += (_, _) => selectionEvents++;

        await h.MoveAsync(100);
        await ClickAsync(h, 100);

        CollectionAssert.AreEqual(new[] { "k1", "k3", "k2", "k5", "k0", "k4" }, ViewKeys(h.Table));
        CollectionAssert.AreEqual(
            new[] { "k3", "k0" },
            h.Table.Selection.Items.Cast<SortRow>().Select(r => r.Key).ToArray(),
            "the same rows, now in the sorted visual order");
        Assert.AreEqual("k3", ((SortRow)h.Table.Selection.Current!).Key);
        Assert.AreEqual(0, selectionEvents, "nothing logical changed");

        ListView list = SelectionHarness.Descendant<ListView>(h.Table)!;
        CollectionAssert.AreEquivalent(
            new[] { "k0", "k3" },
            list.SelectedItems.Cast<SortRow>().Select(r => r.Key).ToArray(),
            "the reset wiped the hosted list's selection and the table put it back");
    });

    // ------------------------------------------------------------------ helpers

    private static ObservableCollection<SortRow> Rows(params int[] ranks)
    {
        ObservableCollection<SortRow> rows = new();
        for (int i = 0; i < ranks.Length; i++)
        {
            rows.Add(new SortRow("k" + i, ranks[i]));
        }

        return rows;
    }

    /// <summary>Column "a" sorts on the rank; "b" and "c" do not sort at all.</summary>
    private static void Configure(TableView table, ObservableCollection<SortRow> rows)
    {
        table.Schema<SortRow>()
            .Key(row => row.Key)
            .Sort(table.Columns[0], row => row.Rank);
        table.Height = 300;
        table.ItemsSource = rows;
    }

    private static async Task ClickAsync(DragHarness h, double x)
    {
        await h.PressAsync(x);
        await h.ReleaseAsync(x);
        h.Table.UpdateLayout();
    }

    private static string[] ViewKeys(TableView table) =>
        ((IEnumerable)SelectionHarness.Descendant<ListView>(table)!.ItemsSource)
        .Cast<SortRow>()
        .Select(r => r.Key)
        .ToArray();

    private static Control HeaderCell(TableHeaderStrip strip, int visibleIndex)
    {
        Panel panel = (Panel)typeof(TableHeaderStrip)
            .GetField("_panel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(strip)!;
        return (Control)panel.Children[visibleIndex];
    }
}
