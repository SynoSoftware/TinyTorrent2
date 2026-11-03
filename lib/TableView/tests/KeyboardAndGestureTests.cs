using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Syno.TableView.Tests;

/// <summary>
/// Section 13's keyboard set and section 7's input rules: which surface owns a key or a pointer
/// gesture, and which one keeps its own.
/// </summary>
[TestClass]
public class KeyboardAndGestureTests
{
    private const string CellXaml = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      xmlns:s="using:Syno.TableView">
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="passive" />
                <Button Content="go" />
                <TextBox Width="40" />
                <Border s:Table.IsRowGestureEnabled="False" Width="20" Height="10">
                    <Rectangle Width="10" Height="10" />
                </Border>
            </StackPanel>
        </DataTemplate>
        """;

    // ------------------------------------------------------------------ navigation

    [TestMethod]
    public Task ArrowKeysMoveTheCurrentRowAndReplaceTheSelection() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(8);

            Assert.IsTrue(h.MoveBy(1, extend: false));
            Assert.AreEqual(
                "k0",
                h.CurrentKey(),
                "The first Down from nothing lands on the first row."
            );

            h.MoveBy(1, extend: false);
            h.MoveBy(1, extend: false);
            Assert.AreEqual("k2", h.CurrentKey());
            CollectionAssert.AreEqual(new[] { "k2" }, h.SelectedKeys());

            h.MoveBy(-1, extend: false);
            CollectionAssert.AreEqual(new[] { "k1" }, h.SelectedKeys());
        });

    [TestMethod]
    public Task ShiftArrowExtendsFromTheAnchor() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(8);

            h.Click(h[2]);
            h.MoveBy(1, extend: true);
            h.MoveBy(1, extend: true);
            CollectionAssert.AreEqual(new[] { "k2", "k3", "k4" }, h.SelectedKeys());

            // Back across the anchor: the range re-projects, it does not keep the old side.
            h.MoveBy(-1, extend: true);
            h.MoveBy(-1, extend: true);
            h.MoveBy(-1, extend: true);
            CollectionAssert.AreEqual(new[] { "k1", "k2" }, h.SelectedKeys());
        });

    [TestMethod]
    public Task CtrlArrowKeepsThePacketAndRangeAnchor() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(8);
            h.Click(h[1]);
            h.Click(h[3], ctrl: true);

            h.MoveBy(1, extend: false, ctrl: true);
            h.MoveBy(1, extend: false, ctrl: true);
            CollectionAssert.AreEqual(new[] { "k1", "k3" }, h.SelectedKeys());
            Assert.AreEqual("k5", h.CurrentKey());

            h.MoveBy(-1, extend: true);
            CollectionAssert.AreEqual(new[] { "k3", "k4" }, h.SelectedKeys());
        });

    [TestMethod]
    public Task MultipleNavigationKeepsThePacketAndSpaceTogglesTheFocusedRow() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(
                8,
                t => t.SelectionMode = ListViewSelectionMode.Multiple
            );
            h.Click(h[1]);
            h.Click(h[3]);

            h.MoveBy(1, extend: false);
            CollectionAssert.AreEqual(new[] { "k1", "k3" }, h.SelectedKeys());
            Assert.AreEqual("k4", h.CurrentKey());
            Assert.IsTrue(h.Space());
            CollectionAssert.AreEqual(new[] { "k1", "k3", "k4" }, h.SelectedKeys());
            Assert.IsTrue(h.Space());
            CollectionAssert.AreEqual(new[] { "k1", "k3" }, h.SelectedKeys());
        });

    [TestMethod]
    public Task SpaceSelectsPhysicalFocusRatherThanTheLogicalCurrent() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(6);
            h.Click(h[1]);
            ListViewItem row = (ListViewItem)h.Surface().ContainerFromItem(h[3]);
            Assert.IsTrue(row.Focus(FocusState.Keyboard));

            Assert.IsTrue(h.Space());
            CollectionAssert.AreEqual(new[] { "k3" }, h.SelectedKeys());
            Assert.AreEqual("k3", h.CurrentKey());
            Assert.IsTrue(h.Space(ctrl: true));
            Assert.AreEqual(0, h.SelectedKeys().Length);
            Assert.AreEqual("k3", h.CurrentKey());
        });

    [TestMethod]
    public Task TouchTapSelectsTheRowAndEmptySurfaceClearsIt() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(6);
            ListViewItem row = (ListViewItem)h.Surface().ContainerFromItem(h[2]);
            Assert.IsTrue(h.Tap(row));
            CollectionAssert.AreEqual(new[] { "k2" }, h.SelectedKeys());
            CollectionAssert.AreEqual(new[] { "k2" }, h.ContainerSelectedKeys());
            Assert.AreEqual("k2", h.CurrentKey());
            Assert.AreEqual(1, h.Events);

            Assert.IsTrue(h.Tap(h.Surface()));
            Assert.AreEqual(0, h.SelectedKeys().Length);
            Assert.IsNull(h.CurrentKey());
        });

    [TestMethod]
    public Task HomeAndEndMoveToTheFirstAndLastRow() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(8);

            h.MoveToEdge(first: false, extend: false);
            CollectionAssert.AreEqual(new[] { "k7" }, h.SelectedKeys());

            h.MoveToEdge(first: true, extend: false);
            CollectionAssert.AreEqual(new[] { "k0" }, h.SelectedKeys());
        });

    [TestMethod]
    public Task ShiftHomeAndShiftEndExtendToTheEdges() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(8);

            h.Click(h[3]);
            h.MoveToEdge(first: true, extend: true);
            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2", "k3" }, h.SelectedKeys());

            h.MoveToEdge(first: false, extend: true);
            CollectionAssert.AreEqual(
                new[] { "k3", "k4", "k5", "k6", "k7" },
                h.SelectedKeys(),
                "Shift+End still extends from the k3 anchor."
            );
        });

    [TestMethod]
    public Task PageDownMovesByAViewportOfRows() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(60);

            // The PageDown arm of the key handler is MoveCurrentBy(RowsPerPage()), the same step as here.
            h.MoveBy(1, extend: false);
            Assert.IsTrue(h.MoveBy(h.RowsPerPage(), extend: false));
            h.Table.UpdateLayout();

            int current = h.Rows.IndexOf(
                h.Table.Selection.Current as Row
                    ?? throw new AssertFailedException("Nothing is current.")
            );
            ItemsStackPanel rows = (ItemsStackPanel)h.Surface().ItemsPanelRoot;

            Assert.IsTrue(current > 1, $"One page step left the current row at k{current}.");
            Assert.IsTrue(
                current >= rows.FirstVisibleIndex && current <= rows.LastVisibleIndex,
                $"The current row k{current} is outside the visible rows "
                    + $"{rows.FirstVisibleIndex}..{rows.LastVisibleIndex}."
            );
        });

    [TestMethod]
    public Task NavigationSkipsNonInteractiveRows() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(
                6,
                t => t.Schema<Row>().CanInteract(row => row.Interactive)
            );

            h[1].Interactive = false;
            h[2].Interactive = false;
            h.Table.RefreshView();

            h.MoveBy(1, extend: false);
            Assert.AreEqual("k0", h.CurrentKey());

            h.MoveBy(1, extend: false);
            Assert.AreEqual("k3", h.CurrentKey(), "k1 and k2 are display-only.");
        });

    [TestMethod]
    public Task TheCurrentRowIsScrolledIntoView() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(200);

            ScrollViewer viewer = SelectionHarness.Descendant<ScrollViewer>(h.Surface())!;
            Assert.AreEqual(0d, viewer.VerticalOffset, 0.5);

            h.MoveToEdge(first: false, extend: false);
            h.Table.UpdateLayout();

            Assert.IsTrue(
                viewer.VerticalOffset > 0,
                $"End did not scroll: vertical offset {viewer.VerticalOffset}."
            );
        });

    // ------------------------------------------------------------------ Ctrl+A and Enter

    [TestMethod]
    public Task CtrlAselectsEveryInteractiveRowWhenMultipleSelectionIsEnabled() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(
                6,
                t => t.Schema<Row>().CanInteract(row => row.Interactive)
            );

            h[4].Interactive = false;
            h.Table.RefreshView();

            Assert.IsTrue(h.SelectAll());
            CollectionAssert.AreEqual(new[] { "k0", "k1", "k2", "k3", "k5" }, h.SelectedKeys());
        });

    [TestMethod]
    public Task CtrlAdoesNothingInSingleAndNone() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness single = await SelectionHarness.LoadAsync(
                4,
                t => t.SelectionMode = ListViewSelectionMode.Single
            );
            Assert.IsFalse(single.SelectAll());
            Assert.AreEqual(0, single.SelectedKeys().Length);

            SelectionHarness none = await SelectionHarness.LoadAsync(
                4,
                t => t.SelectionMode = ListViewSelectionMode.None
            );
            Assert.IsFalse(none.SelectAll());
        });

    // ------------------------------------------------------------------ who owns the input

    [TestMethod]
    public Task TheTableIgnoresItsKeysWhileACellEditorHasFocus() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await LoadWithRichCellsAsync();

            ListViewItem container = RealizedContainer(h);
            Assert.IsTrue(container.Focus(FocusState.Programmatic));
            Assert.IsTrue(
                h.RowSurfaceHasFocus(),
                "A focused row container is the passive row surface."
            );

            TextBox editor = SelectionHarness.Descendant<TextBox>(container)!;
            Assert.IsTrue(editor.Focus(FocusState.Programmatic));
            Assert.IsFalse(
                h.RowSurfaceHasFocus(),
                "A single-line TextBox leaves the arrow keys unhandled; the table must not act on them."
            );
            Assert.IsFalse(h.Space(), "Space belongs to the cell editor.");
        });

    [TestMethod]
    public Task PassiveCellContentFollowsNormalRowSelectionAndInteractiveContentDoesNot() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await LoadWithRichCellsAsync();
            ListViewItem container = RealizedContainer(h);

            TextBlock passive = SelectionHarness.Descendant<TextBlock>(container)!;
            Assert.AreEqual("Row", h.HitTest(passive));

            Button button = SelectionHarness.Descendant<Button>(container)!;
            Assert.AreEqual("Suppressed", h.HitTest(button));

            TextBox editor = SelectionHarness.Descendant<TextBox>(container)!;
            Assert.AreEqual("Suppressed", h.HitTest(editor));
            Assert.IsFalse(h.Tap(editor), "A touch tap belongs to the cell editor.");
            Assert.IsFalse(h.Tap(button), "A touch tap belongs to the cell button.");
            Assert.AreEqual(0, h.SelectedKeys().Length);
        });

    [TestMethod]
    public Task DisabledRowGestureStopsAGestureFromItsSubtree() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await LoadWithRichCellsAsync();
            ListViewItem container = RealizedContainer(h);

            Microsoft.UI.Xaml.Shapes.Rectangle inside =
                SelectionHarness.Descendant<Microsoft.UI.Xaml.Shapes.Rectangle>(
                    SelectionHarness.Descendant<Border>(container)!
                )!;

            Assert.AreEqual("Suppressed", h.HitTest(inside));
        });

    // ------------------------------------------------------------------ helpers

    private static async Task<SelectionHarness> LoadWithRichCellsAsync()
    {
        DataTemplate cell = (DataTemplate)XamlReader.Load(CellXaml);
        SelectionHarness h = await SelectionHarness.LoadAsync(
            6,
            t => t.Columns[0].CellTemplate = cell
        );
        await WaitForCellControlAsync<TextBox>(h, row: 0);
        return h;
    }

    /// <summary>
    /// Container content is realized in phases, so the cell controls do not exist on the frame the
    /// container does. Waits, with a bound, until the row's cell holds a control of type T.
    /// </summary>
    internal static Task WaitForCellControlAsync<T>(SelectionHarness h, int row)
        where T : DependencyObject =>
        TableHarness.WaitUntilAsync(
            () =>
            {
                h.Table.UpdateLayout();
                return h.Surface().ContainerFromItem(h[row]) is DependencyObject container
                    && SelectionHarness.Descendant<T>(container) is not null;
            },
            $"row {row} to realize a {typeof(T).Name} cell control"
        );

    private static ListViewItem RealizedContainer(SelectionHarness h)
    {
        h.Table.UpdateLayout();
        return h.Surface().ContainerFromItem(h[0]) as ListViewItem
            ?? throw new AssertFailedException("Row 0 was not realized.");
    }
}
