using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.System;

namespace Syno.TableView.Tests;

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
    public Task ARealHeaderClickRunsTheWholeSortCycle() =>
        TestHost.RunAsync(async () =>
        {
            ObservableCollection<SortRow> rows = Rows(3, 1, 2, 1);
            DragHarness h = await DragHarness.LoadAsync(table => Configure(table, rows));
            List<LayoutChange> kinds = new();
            h.Table.LayoutChanged += (_, kind) => kinds.Add(kind);

            await h.MoveAsync(100);
            await ClickAsync(h, 100);

            CollectionAssert.AreEqual(new[] { "k1", "k3", "k2", "k0" }, ViewKeys(h.Table));

            await ClickAsync(h, 100);

            CollectionAssert.AreEqual(new[] { "k0", "k2", "k1", "k3" }, ViewKeys(h.Table));

            await ClickAsync(h, 100);

            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2", "k3" }, ViewKeys(h.Table));
            CollectionAssert.AreEqual(
                new[] { LayoutChange.Sort, LayoutChange.Sort, LayoutChange.Sort },
                kinds
            );
        });

    [TestMethod]
    public Task ARealDragDoesNotAlsoSortTheHeaderItStartedOn() =>
        TestHost.RunAsync(async () =>
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
                new[] { "k0", "k1", "k2" },
                ViewKeys(h.Table),
                "the drop moved the column, it did not sort"
            );
        });

    [TestMethod]
    public Task RealEnterAndSpaceOnAFocusedHeaderRunTheSameCycle() =>
        TestHost.RunAsync(async () =>
        {
            ObservableCollection<SortRow> rows = Rows(3, 1, 2, 1);
            DragHarness h = await DragHarness.LoadAsync(table => Configure(table, rows));

            Control cell = HeaderCell(h.Strip, 0);
            Assert.IsTrue(cell.Focus(FocusState.Keyboard), "the header took keyboard focus");

            await h.KeyAsync(VirtualKey.Enter);
            h.Table.UpdateLayout();

            CollectionAssert.AreEqual(
                new[] { "k1", "k3", "k2", "k0" },
                ViewKeys(h.Table),
                "Enter sorted"
            );

            await h.KeyAsync(VirtualKey.Space);
            h.Table.UpdateLayout();

            CollectionAssert.AreEqual(
                new[] { "k0", "k2", "k1", "k3" },
                ViewKeys(h.Table),
                "Space continued the cycle"
            );

            await h.KeyAsync(VirtualKey.Space);
            h.Table.UpdateLayout();

            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2", "k3" }, ViewKeys(h.Table));
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
    private static void Configure(Table table, ObservableCollection<SortRow> rows)
    {
        table.Schema<SortRow>().Key(row => row.Key).SortKey(table.Columns[0], row => row.Rank);
        table.Height = 300;
        table.ItemsSource = rows;
    }

    private static async Task ClickAsync(DragHarness h, double x)
    {
        await h.PressAsync(x);
        await h.ReleaseAsync(x);
        h.Table.UpdateLayout();
    }

    private static string[] ViewKeys(Table table) =>
        ((IEnumerable)SelectionHarness.Descendant<ListView>(table)!.ItemsSource)
            .Cast<SortRow>()
            .Select(r => r.Key)
            .ToArray();

    private static Control HeaderCell(Header.Strip strip, int visibleIndex)
    {
        Panel panel = (Panel)
            typeof(Header.Strip)
                .GetField("_panel", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(strip)!;
        return (Control)panel.Children[visibleIndex];
    }
}
