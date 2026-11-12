using System.Numerics;
using System.Reflection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Syno.TableView.Tests;

/// <summary>
/// Section 11's reorder rule. The rule the section exists for is the last one: a move is chosen at
/// a visible boundary but applied to the full logical order, so a hidden column keeps its place
/// among its neighbours and comes back where the user would expect it.
/// </summary>
[TestClass]
public class ColumnMoveTests
{
    // ------------------------------------------------------------------ the placement rule

    [TestMethod]
    public Task ADropOnAnotherBoundaryReordersTheVisibleColumns() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync("a", "b", "c");
            Func<int> events = LayoutChanges(table);

            Assert.IsTrue(MoveColumnTo(table, "a", 2), "a goes after c");

            CollectionAssert.AreEqual(new[] { "b", "c", "a" }, TableHarness.Order(table));
            Assert.AreEqual(1, events(), "one completed move, one notification");
        });

    [TestMethod]
    public Task AColumnCanBeDroppedBeforeTheFirstColumn() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync("a", "b", "c");

            MoveColumnTo(table, "c", 0);

            CollectionAssert.AreEqual(new[] { "c", "a", "b" }, TableHarness.Order(table));
        });

    /// <summary>
    /// The rule that makes hidden columns survive a reorder: the boundary is chosen among visible
    /// columns, the reinsertion happens in the full order, and nothing else moves relative to
    /// anything else.
    /// </summary>
    [TestMethod]
    public Task AHiddenColumnKeepsItsPlaceAmongItsNeighbours() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync("a", "b", "hidden", "c");
            table.Layout = TestData.Layout(
                visibility: new Dictionary<string, bool> { ["hidden"] = false }
            );

            // Visually a, b, c. The drop is between b and c.
            Assert.IsTrue(MoveColumnTo(table, "a", 1));

            CollectionAssert.AreEqual(
                new[] { "b", "hidden", "a", "c" },
                TableHarness.Order(table),
                "hidden stays between b and c, and a lands on the visible boundary it was dropped on"
            );
        });

    [TestMethod]
    public Task AMoveToTheEndDoesNotStepOverATrailingHiddenColumn() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync("a", "b", "hidden");
            table.Layout = TestData.Layout(
                visibility: new Dictionary<string, bool> { ["hidden"] = false }
            );

            Assert.IsTrue(MoveColumnTo(table, "a", 1), "a goes after the last visible column");

            CollectionAssert.AreEqual(new[] { "b", "a", "hidden" }, TableHarness.Order(table));
        });

    // ------------------------------------------------------------------ what changes nothing

    [TestMethod]
    public Task ANoOpPlacementChangesNothingAndReportsNothing() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync("a", "b", "c");
            Func<int> events = LayoutChanges(table);

            Assert.IsFalse(MoveColumnTo(table, "b", 1), "b is already at boundary 1");

            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, TableHarness.Order(table));
            Assert.AreEqual(0, events());
        });

    // ------------------------------------------------------------------ move left and move right

    [TestMethod]
    public Task MoveLeftAndMoveRightStepOneVisiblePosition() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync("a", "b", "c");

            Assert.IsTrue(MoveColumnBy(table, "c", -1));
            CollectionAssert.AreEqual(new[] { "a", "c", "b" }, TableHarness.Order(table));

            Assert.IsTrue(MoveColumnBy(table, "c", 1));
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, TableHarness.Order(table));
        });

    // ------------------------------------------------------------------ what a drop lands on

    /// <summary>
    /// The pointer takes the nearer edge of the column it is over, and past the last column it
    /// takes the end.
    /// </summary>
    [TestMethod]
    public Task ADropTakesTheNearerEdgeOfTheColumnUnderThePointer() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync(("a", 200), ("b", 100), ("c", 100));
            Header.Strip strip = Strip(table);

            Assert.AreEqual(0, BoundaryAt(strip, 99), "the left half of a");
            Assert.AreEqual(1, BoundaryAt(strip, 101), "the right half of a");
            Assert.AreEqual(3, BoundaryAt(strip, 5000), "past the last column is after the last");
        });

    /// <summary>
    /// A drop is counted among the visible columns with the dragged one taken out, which is what
    /// makes both boundaries either side of the dragged column a no-op.
    /// </summary>
    [TestMethod]
    public Task BothBoundariesOfTheDraggedColumnAreItsOwnPlace() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync(("a", 100), ("b", 100), ("c", 100));
            Header.Strip strip = Strip(table);
            object b = TableHarness.EffectiveColumn(table, "b");

            Assert.AreEqual(
                1,
                DropBoundary(strip, 140, b),
                "the left half of b: the boundary before it"
            );
            Assert.AreEqual(
                1,
                DropBoundary(strip, 160, b),
                "the right half of b: the boundary after it"
            );
            Assert.AreEqual(2, DropBoundary(strip, 260, b), "the right half of c");
        });

    // ------------------------------------------------------------------ the insertion marker

    [TestMethod]
    public Task TheMarkerSitsOnTheBoundaryTheDropWouldUse() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync(("a", 200), ("b", 100), ("c", 100));
            Header.Strip strip = Strip(table);
            FrameworkElement marker = Marker(strip);

            Assert.AreEqual(Visibility.Collapsed, marker.Visibility, "nothing shows until a drag");

            MoveInsertionMarker(strip, 240);

            Assert.AreEqual(Visibility.Visible, marker.Visibility);
            Assert.AreEqual(
                200 - (marker.Width / 2),
                MarkerX(strip),
                0.01,
                "centred on the boundary between a and b"
            );

            MoveInsertionMarker(strip, 5000);

            Assert.AreEqual(
                400 - (marker.Width / 2),
                MarkerX(strip),
                0.01,
                "after the last column"
            );

            MoveInsertionMarker(strip, 0);

            Assert.AreEqual(
                0,
                MarkerX(strip),
                0.01,
                "the first boundary is held inside the strip rather than half clipped"
            );
        });

    /// <summary>
    /// Section 19: the drag destination is table-owned non-text information, so it must reach 3:1
    /// and must not depend on colour. A resource name is not proof of either, so this renders the
    /// header and measures the drawn pixels.
    /// </summary>
    [TestMethod]
    public Task TheMarkerIsVisibleAndShapedInLight() => MeasureMarkerAsync(ElementTheme.Light);

    [TestMethod]
    public Task TheMarkerIsVisibleAndShapedInDark() => MeasureMarkerAsync(ElementTheme.Dark);

    private static Task MeasureMarkerAsync(ElementTheme theme) =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync(("a", 200), ("b", 200));
            table.RequestedTheme = theme;

            // The header's own brush is translucent, so it only composites to the colour a user sees
            // when there is an opaque surface behind it.
            table.Background = new SolidColorBrush(
                theme == ElementTheme.Light
                    ? Color.FromArgb(255, 243, 243, 243)
                    : Color.FromArgb(255, 32, 32, 32)
            );
            table.UpdateLayout();

            Header.Strip strip = Strip(table);

            // The first boundary. Every other one sits under a header cell's own separator line, which
            // would be measured along with the marker.
            MoveInsertionMarker(strip, 0);
            table.UpdateLayout();
            await Task.Delay(100);

            // The whole control is rendered, because the header brush composites onto what is behind it.
            Shot shot = await Shot.TakeAsync(table);
            Point origin = strip.TransformToVisual(table).TransformPoint(new Point(0, 0));
            double middle = origin.Y + (strip.ActualHeight / 2);
            double cap = origin.Y + 2;
            uint background = shot.At(shot.X(origin.X + 40), shot.Y(middle));
            double contrast = shot.Contrast(background, new Rect(origin.X, middle, 9, 0));

            Assert.IsTrue(
                contrast >= 3.0,
                $"{theme}: the marker measured {contrast:0.00}:1 against the "
                    + $"header background #{background:X6}; section 19 requires 3:1."
            );

            Assert.IsTrue(
                shot.DrawnWidth(background, origin.X, origin.X + 12, cap)
                    > shot.DrawnWidth(background, origin.X, origin.X + 12, middle),
                $"{theme}: the cap at the leading end is the positional cue, so it must be wider than "
                    + "the line below it."
            );
        });

    // ------------------------------------------------------------------ reposition continuity

    /// <summary>
    /// Section 19: a move must read as movement. <see cref="CellsPanel"/> arranges its cells
    /// at absolute positions and a custom panel's arrange carries no transition, so the control
    /// animates the cells whose column moved — and only those.
    /// </summary>
    [TestMethod]
    public Task AColumnMoveSlidesTheCellsWhosePositionChanged() =>
        TestHost.RunAsync(async () =>
        {
            if (!new UISettings().AnimationsEnabled)
            {
                Assert.Inconclusive(
                    "The system has UI animations turned off, and section 19 requires no substitute "
                        + "motion in that case."
                );
            }

            Table table = await LoadAsync(("a", 200), ("b", 200), ("c", 200), ("d", 200));
            Header.Strip strip = Strip(table);

            // a goes one place right: a and b swap, c and d stay where they are.
            Assert.IsTrue(MoveColumnTo(table, "a", 1));
            CollectionAssert.AreEqual(new[] { "b", "a", "c", "d" }, TableHarness.Order(table));

            Assert.AreEqual(
                200f,
                TranslationX(strip, 0),
                1f,
                "the cell now showing b starts back where b was and slides to the place a left"
            );
            Assert.AreEqual(
                -200f,
                TranslationX(strip, 1),
                1f,
                "and the cell now showing a starts back where a was"
            );
            Assert.AreEqual(
                0f,
                TranslationX(strip, 2),
                0.01f,
                "c did not move, so nothing displaces it"
            );
            Assert.AreEqual(0f, TranslationX(strip, 3), 0.01f);

            await Task.Delay(600);

            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(
                    0f,
                    TranslationX(strip, i),
                    0.01f,
                    $"cell {i}: the slide finished where the layout arranged it"
                );
            }
        });

    /// <summary>
    /// The order and visibility rules above are read from the effective layout. This is the same
    /// question asked of what the control actually realized: the header cells a user reads, and
    /// the cells of every realized row.
    /// </summary>
    [TestMethod]
    public Task TheRealizedHeaderAndRowsFollowTheNewOrder() =>
        TestHost.RunAsync(async () =>
        {
            SortHarness h = await SortHarness.LoadAsync(new[] { 1, 2, 3 });
            TestData.ApplyDeclaredWidths(h.Table);
            await Task.Delay(250);
            h.Table.UpdateLayout();

            Header.Strip strip = Strip(h.Table);
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, HeaderLabels(strip));

            MoveColumnTo(h.Table, "a", 2);
            h.Table.UpdateLayout();

            CollectionAssert.AreEqual(
                new[] { "B", "C", "A" },
                HeaderLabels(strip),
                "the header a user reads is in the new order"
            );
            Assert.AreEqual(
                3,
                RowCellCounts(h.Table).Distinct().Single(),
                "and every realized row still has one cell per visible column"
            );
            Assert.AreEqual(
                -400f,
                TranslationX(RowPanel(h.Table), 2),
                1f,
                "the row cells slide with their column, not the header alone"
            );

            SetColumnVisibility(h.Table, "c", false);
            h.Table.UpdateLayout();

            CollectionAssert.AreEqual(
                new[] { "B", "A" },
                HeaderLabels(strip),
                "a hidden column is gone"
            );
            Assert.AreEqual(2, RowCellCounts(h.Table).Distinct().Single());

            SetColumnVisibility(h.Table, "c", true);
            h.Table.UpdateLayout();

            CollectionAssert.AreEqual(
                new[] { "B", "C", "A" },
                HeaderLabels(strip),
                "and comes back in the place it kept while it was hidden"
            );
        });

    // ------------------------------------------------------------------ helpers

    /// <summary>What each realized header cell reads, in visible order.</summary>
    private static string[] HeaderLabels(Header.Strip strip) =>
        ((Panel)Field(strip, "_panel")!)
            .Children.Cast<DependencyObject>()
            .Select(cell => Proof.Descendant<TextBlock>(cell)?.Text ?? "(none)")
            .ToArray();

    /// <summary>How many cells each realized row panel holds.</summary>
    private static int[] RowCellCounts(Table table)
    {
        object header = Field(Strip(table), "_panel")!;
        int[] counts = Proof.Descendants<CellsPanel>(table)
            .Where(panel => !ReferenceEquals(panel, header))
            .Select(panel => panel.Children.Count)
            .ToArray();

        Assert.AreNotEqual(0, counts.Length, "no row panel was realized");
        return counts;
    }

    private static float TranslationX(Header.Strip strip, int visibleIndex) =>
        TranslationX((Panel)Field(strip, "_panel")!, visibleIndex);

    /// <summary>
    /// How far one cell is displaced from the place its panel arranged it. Zero is both
    /// "never animated" and "the slide has finished".
    /// </summary>
    private static float TranslationX(Panel panel, int visibleIndex)
    {
        ElementCompositionPreview
            .GetElementVisual(panel.Children[visibleIndex])
            .Properties.TryGetVector3("Translation", out Vector3 translation);
        return translation.X;
    }

    /// <summary>The first realized row's cells panel.</summary>
    private static CellsPanel RowPanel(Table table)
    {
        object header = Field(Strip(table), "_panel")!;
        return Proof.Descendants<CellsPanel>(table)
            .First(panel => !ReferenceEquals(panel, header) && panel.Children.Count > 0);
    }

    private static async Task<Table> LoadAsync(params string[] ids) =>
        await LoadAsync(ids.Select(id => (id, 150d)).ToArray());

    /// <summary>A table that keeps its declared widths instead of filling its width.</summary>
    private static async Task<Table> LoadAsync(params (string Id, double Width)[] columns)
    {
        Table table = TestData.Table(columns.Select(c => TestData.Column(c.Id, c.Width)).ToArray());
        table.Width = 700;
        table.Height = 200;

        await TableHarness.LoadAsync(table);
        TestData.ApplyDeclaredWidths(table);
        table.UpdateLayout();
        return table;
    }

    /// <summary>Starts counting <c>LayoutChanged</c> now; call the result to read the count.</summary>
    private static Func<int> LayoutChanges(Table table)
    {
        int count = 0;
        table.LayoutChanged += (_, kind) =>
        {
            Assert.AreEqual(LayoutChange.Move, kind);
            count++;
        };
        return () => count;
    }

    private static Header.Strip Strip(Table table) => Proof.Descendant<Header.Strip>(table)!;

    // The move operations, the effective columns they act on, and the strip's drop arithmetic are
    // all internal to TableView, which grants no InternalsVisibleTo. Reflection is the only way to
    // reach them without widening the control's public surface for a test.

    private static bool MoveColumnTo(Table table, string id, int boundary) =>
        (bool)
            Invoke(
                table,
                "MoveColumnTo",
                TableHarness.EffectiveColumn(table, id),
                boundary,
                null!
            )!;

    private static void SetColumnVisibility(Table table, string id, bool visible) =>
        Invoke(table, "SetColumnVisibility", TableHarness.EffectiveColumn(table, id), visible);

    private static bool MoveColumnBy(Table table, string id, int step) =>
        (bool)Invoke(table, "MoveColumnBy", TableHarness.EffectiveColumn(table, id), step)!;

    private static int BoundaryAt(Header.Strip strip, double x) =>
        (int)Invoke(strip, "BoundaryAt", x)!;

    private static int DropBoundary(Header.Strip strip, double x, object dragged) =>
        (int)Invoke(strip, "DropBoundary", x, dragged)!;

    private static void MoveInsertionMarker(Header.Strip strip, double x) =>
        Invoke(strip, "MoveInsertionMarker", x);

    private static FrameworkElement Marker(Header.Strip strip) =>
        (FrameworkElement)Field(strip, "_marker")!;

    private static double MarkerX(Header.Strip strip) =>
        ((TranslateTransform)Field(strip, "_markerOffset")!).X;

    private static object? Invoke(object target, string method, params object[] arguments) =>
        target
            .GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, arguments);

    private static object? Field(object target, string name) =>
        target
            .GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target);
}
