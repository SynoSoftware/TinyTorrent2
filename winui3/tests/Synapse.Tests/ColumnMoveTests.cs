using System.Numerics;
using System.Reflection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Synapse;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Synapse_Tests;

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
    public Task ADropOnAnotherBoundaryReordersTheVisibleColumns() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync("a", "b", "c");
        Func<int> events = LayoutChanges(table);

        Assert.IsTrue(MoveColumnTo(table, "a", 2), "a goes after c");

        CollectionAssert.AreEqual(new[] { "b", "c", "a" }, TableHarness.Order(table));
        Assert.AreEqual(1, events(), "one completed move, one notification");
    });

    [TestMethod]
    public Task EveryLegalBoundaryIsReachable() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync("a", "b", "c");

        MoveColumnTo(table, "c", 0);
        CollectionAssert.AreEqual(new[] { "c", "a", "b" }, TableHarness.Order(table), "before the first");

        MoveColumnTo(table, "c", 1);
        CollectionAssert.AreEqual(new[] { "a", "c", "b" }, TableHarness.Order(table), "between two");

        MoveColumnTo(table, "c", 2);
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, TableHarness.Order(table), "after the last");
    });

    /// <summary>
    /// The rule that makes hidden columns survive a reorder: the boundary is chosen among visible
    /// columns, the reinsertion happens in the full order, and nothing else moves relative to
    /// anything else.
    /// </summary>
    [TestMethod]
    public Task AHiddenColumnKeepsItsPlaceAmongItsNeighbours() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync("a", "b", "hidden", "c");
        table.Layout = TestData.Layout(
            visibility: new Dictionary<string, bool> { ["hidden"] = false });

        // Visually a, b, c. The drop is between b and c.
        Assert.IsTrue(MoveColumnTo(table, "a", 1));

        CollectionAssert.AreEqual(new[] { "b", "hidden", "a", "c" }, TableHarness.Order(table),
            "hidden stays between b and c, and a lands on the visible boundary it was dropped on");

        table.Layout = TestData.Layout(
            order: TableHarness.Order(table),
            visibility: new Dictionary<string, bool>());

        CollectionAssert.AreEqual(new[] { "b", "hidden", "a", "c" }, TableHarness.Order(table),
            "showing it again puts it back where it was");
    });

    [TestMethod]
    public Task AMoveToTheEndDoesNotStepOverATrailingHiddenColumn() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync("a", "b", "hidden");
        table.Layout = TestData.Layout(
            visibility: new Dictionary<string, bool> { ["hidden"] = false });

        Assert.IsTrue(MoveColumnTo(table, "a", 1), "a goes after the last visible column");

        CollectionAssert.AreEqual(new[] { "b", "a", "hidden" }, TableHarness.Order(table));
    });

    // ------------------------------------------------------------------ what changes nothing

    [TestMethod]
    public Task ANoOpPlacementChangesNothingAndReportsNothing() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync("a", "b", "c");
        Func<int> events = LayoutChanges(table);

        Assert.IsFalse(MoveColumnTo(table, "b", 1), "b is already at boundary 1");

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, TableHarness.Order(table));
        Assert.AreEqual(0, events());
    });

    [TestMethod]
    public Task AHiddenColumnCannotBeMoved() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync("a", "b", "hidden");
        table.Layout = TestData.Layout(
            visibility: new Dictionary<string, bool> { ["hidden"] = false });
        Func<int> events = LayoutChanges(table);

        Assert.IsFalse(MoveColumnTo(table, "hidden", 0));
        Assert.IsFalse(MoveColumnBy(table, "hidden", -1));

        CollectionAssert.AreEqual(new[] { "a", "b", "hidden" }, TableHarness.Order(table));
        Assert.AreEqual(0, events());
    });

    // ------------------------------------------------------------------ move left and move right

    [TestMethod]
    public Task MoveLeftAndMoveRightStepOneVisiblePosition() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync("a", "b", "c");

        Assert.IsTrue(MoveColumnBy(table, "c", -1));
        CollectionAssert.AreEqual(new[] { "a", "c", "b" }, TableHarness.Order(table));

        Assert.IsTrue(MoveColumnBy(table, "c", 1));
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, TableHarness.Order(table));
    });

    [TestMethod]
    public Task MoveLeftAndMoveRightUseTheSameNeighbourAsADrag() => TestHost.RunAsync(async () =>
    {
        TableView dragged = await LoadAsync("a", "b", "hidden", "c");
        TableView commanded = await LoadAsync("a", "b", "hidden", "c");
        foreach (TableView table in new[] { dragged, commanded })
        {
            table.Layout = TestData.Layout(
                visibility: new Dictionary<string, bool> { ["hidden"] = false });
        }

        // "c" is the third visible column; a drag onto the boundary left of "b" is boundary 1.
        MoveColumnTo(dragged, "c", 1);
        MoveColumnBy(commanded, "c", -1);

        CollectionAssert.AreEqual(TableHarness.Order(dragged), TableHarness.Order(commanded));
        CollectionAssert.AreEqual(new[] { "a", "c", "b", "hidden" }, TableHarness.Order(commanded));
    });

    [TestMethod]
    public Task MovingPastEitherEndIsANoOp() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync("a", "b", "c");
        Func<int> events = LayoutChanges(table);

        Assert.IsFalse(MoveColumnBy(table, "a", -1), "a is already first");
        Assert.IsFalse(MoveColumnBy(table, "c", 1), "c is already last");

        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, TableHarness.Order(table));
        Assert.AreEqual(0, events());
    });

    // ------------------------------------------------------------------ what a drop lands on

    /// <summary>
    /// The pointer takes the nearer edge of the column it is over, so every boundary is reachable
    /// including before the first column and after the last.
    /// </summary>
    [TestMethod]
    public Task ADropTakesTheNearerEdgeOfTheColumnUnderThePointer() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync(("a", 200), ("b", 100), ("c", 100));
        TableHeaderStrip strip = Strip(table);

        Assert.AreEqual(0, BoundaryAt(strip, 0), "the far left is before the first column");
        Assert.AreEqual(0, BoundaryAt(strip, 99), "the left half of a");
        Assert.AreEqual(1, BoundaryAt(strip, 101), "the right half of a");
        Assert.AreEqual(1, BoundaryAt(strip, 240), "the left half of b");
        Assert.AreEqual(2, BoundaryAt(strip, 260), "the right half of b");
        Assert.AreEqual(3, BoundaryAt(strip, 5000), "past the last column is after the last");
    });

    [TestMethod]
    public Task ADropFollowsTheScrolledHeader() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync(("a", 200), ("b", 100), ("c", 100));
        TableHeaderStrip strip = Strip(table);

        TableHarness.SetHorizontalOffset(table, 150);

        Assert.AreEqual(1, BoundaryAt(strip, 0), "x 0 is now 150 into a, its right half");
        Assert.AreEqual(2, BoundaryAt(strip, 110), "and 260 is the right half of b");
    });

    /// <summary>
    /// A drop is counted among the visible columns with the dragged one taken out, which is what
    /// makes both boundaries either side of the dragged column a no-op.
    /// </summary>
    [TestMethod]
    public Task BothBoundariesOfTheDraggedColumnAreItsOwnPlace() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync(("a", 100), ("b", 100), ("c", 100));
        TableHeaderStrip strip = Strip(table);
        object b = TableHarness.ResolvedColumn(table, "b");

        Assert.AreEqual(1, DropBoundary(strip, 140, b), "the left half of b: the boundary before it");
        Assert.AreEqual(1, DropBoundary(strip, 160, b), "the right half of b: the boundary after it");
        Assert.AreEqual(0, DropBoundary(strip, 40, b), "the left half of a");
        Assert.AreEqual(2, DropBoundary(strip, 260, b), "the right half of c");

        Func<int> events = LayoutChanges(table);
        Assert.IsFalse(MoveColumnTo(table, "b", DropBoundary(strip, 160, b)));
        Assert.AreEqual(0, events(), "a drop back on its own place raises nothing");
    });

    // ------------------------------------------------------------------ the insertion marker

    [TestMethod]
    public Task TheMarkerSitsOnTheBoundaryTheDropWouldUse() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync(("a", 200), ("b", 100), ("c", 100));
        TableHeaderStrip strip = Strip(table);
        FrameworkElement marker = Marker(strip);

        Assert.AreEqual(Visibility.Collapsed, marker.Visibility, "nothing shows until a drag");

        MoveInsertionMarker(strip, 240);

        Assert.AreEqual(Visibility.Visible, marker.Visibility);
        Assert.AreEqual(200 - (marker.Width / 2), MarkerX(strip), 0.01,
            "centred on the boundary between a and b");

        MoveInsertionMarker(strip, 5000);

        Assert.AreEqual(400 - (marker.Width / 2), MarkerX(strip), 0.01, "after the last column");

        MoveInsertionMarker(strip, 0);

        Assert.AreEqual(0, MarkerX(strip), 0.01,
            "the first boundary is held inside the strip rather than half clipped");
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

    private static Task MeasureMarkerAsync(ElementTheme theme) => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync(("a", 200), ("b", 200));
        table.RequestedTheme = theme;

        // The header's own brush is translucent, so it only composites to the colour a user sees
        // when there is an opaque surface behind it.
        table.Background = new SolidColorBrush(theme == ElementTheme.Light
            ? Color.FromArgb(255, 243, 243, 243)
            : Color.FromArgb(255, 32, 32, 32));
        table.UpdateLayout();

        TableHeaderStrip strip = Strip(table);

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

        Assert.IsTrue(
            shot.Contrast(background, origin.X, origin.X + 9, middle) >= 3.0,
            $"{theme}: the marker measured "
            + $"{shot.Contrast(background, origin.X, origin.X + 9, middle):0.00}:1 against the "
            + $"header background #{background:X6}; section 19 requires 3:1.");

        Assert.IsTrue(
            shot.DrawnWidth(background, origin.X, origin.X + 12, cap)
                > shot.DrawnWidth(background, origin.X, origin.X + 12, middle),
            $"{theme}: the cap at the leading end is the positional cue, so it must be wider than "
            + "the line below it.");
    });

    // ------------------------------------------------------------------ reposition continuity

    /// <summary>
    /// Section 19: a move must read as movement. <see cref="TableCellsPanel"/> arranges its cells
    /// at absolute positions and a custom panel's arrange carries no transition, so the control
    /// animates the cells whose column moved — and only those.
    /// </summary>
    [TestMethod]
    public Task AColumnMoveSlidesTheCellsWhosePositionChanged() => TestHost.RunAsync(async () =>
    {
        if (!new UISettings().AnimationsEnabled)
        {
            Assert.Inconclusive(
                "The system has UI animations turned off, and section 19 requires no substitute "
                + "motion in that case.");
        }

        TableView table = await LoadAsync(("a", 200), ("b", 200), ("c", 200), ("d", 200));
        TableHeaderStrip strip = Strip(table);

        // a goes one place right: a and b swap, c and d stay where they are.
        Assert.IsTrue(MoveColumnTo(table, "a", 1));
        CollectionAssert.AreEqual(new[] { "b", "a", "c", "d" }, TableHarness.Order(table));

        Assert.AreEqual(200f, TranslationX(strip, 0), 1f,
            "the cell now showing b starts back where b was and slides to the place a left");
        Assert.AreEqual(-200f, TranslationX(strip, 1), 1f,
            "and the cell now showing a starts back where a was");
        Assert.AreEqual(0f, TranslationX(strip, 2), 0.01f, "c did not move, so nothing displaces it");
        Assert.AreEqual(0f, TranslationX(strip, 3), 0.01f);

        await Task.Delay(600);

        for (int i = 0; i < 4; i++)
        {
            Assert.AreEqual(0f, TranslationX(strip, i), 0.01f,
                $"cell {i}: the slide finished where the layout arranged it");
        }
    });

    /// <summary>
    /// The order and visibility rules above are read from the resolved layout. This is the same
    /// question asked of what the control actually realized: the header cells a user reads, and
    /// the cells of every realized row.
    /// </summary>
    [TestMethod]
    public Task TheRealizedHeaderAndRowsFollowTheNewOrder() => TestHost.RunAsync(async () =>
    {
        SortHarness h = await SortHarness.LoadAsync(new[] { 1, 2, 3 });
        await Task.Delay(250);
        h.Table.UpdateLayout();

        TableHeaderStrip strip = Strip(h.Table);
        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, HeaderLabels(strip));

        MoveColumnTo(h.Table, "a", 2);
        h.Table.UpdateLayout();

        CollectionAssert.AreEqual(new[] { "B", "C", "A" }, HeaderLabels(strip),
            "the header a user reads is in the new order");
        Assert.AreEqual(3, RowCellCounts(h.Table).Distinct().Single(),
            "and every realized row still has one cell per visible column");
        Assert.AreEqual(-400f, TranslationX(RowPanel(h.Table), 2), 1f,
            "the row cells slide with their column, not the header alone");

        SetColumnVisibility(h.Table, "c", false);
        h.Table.UpdateLayout();

        CollectionAssert.AreEqual(new[] { "B", "A" }, HeaderLabels(strip), "a hidden column is gone");
        Assert.AreEqual(2, RowCellCounts(h.Table).Distinct().Single());

        SetColumnVisibility(h.Table, "c", true);
        h.Table.UpdateLayout();

        CollectionAssert.AreEqual(new[] { "B", "C", "A" }, HeaderLabels(strip),
            "and comes back in the place it kept while it was hidden");
    });

    // ------------------------------------------------------------------ helpers

    /// <summary>What each realized header cell reads, in visible order.</summary>
    private static string[] HeaderLabels(TableHeaderStrip strip) =>
        ((Panel)Field(strip, "_panel")!).Children
        .Cast<DependencyObject>()
        .Select(cell => SelectionHarness.Descendant<TextBlock>(cell)?.Text ?? "(none)")
        .ToArray();

    /// <summary>How many cells each realized row panel holds.</summary>
    private static int[] RowCellCounts(TableView table)
    {
        object header = Field(Strip(table), "_panel")!;
        int[] counts = Descendants<TableCellsPanel>(table)
            .Where(panel => !ReferenceEquals(panel, header))
            .Select(panel => panel.Children.Count)
            .ToArray();

        Assert.AreNotEqual(0, counts.Length, "no row panel was realized");
        return counts;
    }

    private static float TranslationX(TableHeaderStrip strip, int visibleIndex) =>
        TranslationX((Panel)Field(strip, "_panel")!, visibleIndex);

    /// <summary>
    /// How far one cell is displaced from the place its panel arranged it. Zero is both
    /// "never animated" and "the slide has finished".
    /// </summary>
    private static float TranslationX(Panel panel, int visibleIndex)
    {
        ElementCompositionPreview
            .GetElementVisual(panel.Children[visibleIndex])
            .Properties
            .TryGetVector3("Translation", out Vector3 translation);
        return translation.X;
    }

    /// <summary>The first realized row's cells panel.</summary>
    private static TableCellsPanel RowPanel(TableView table)
    {
        object header = Field(Strip(table), "_panel")!;
        return Descendants<TableCellsPanel>(table)
            .First(panel => !ReferenceEquals(panel, header) && panel.Children.Count > 0);
    }

    private static async Task<TableView> LoadAsync(params string[] ids) =>
        await LoadAsync(ids.Select(id => (id, 150d)).ToArray());

    private static async Task<TableView> LoadAsync(params (string Id, double Width)[] columns)
    {
        TableView table = TestData.Table(
            columns.Select(c => TestData.Column(c.Id, c.Width)).ToArray());
        table.Width = 700;
        table.Height = 200;

        await TableHarness.LoadAsync(table);
        table.UpdateLayout();
        return table;
    }

    /// <summary>Starts counting <c>LayoutChanged</c> now; call the result to read the count.</summary>
    private static Func<int> LayoutChanges(TableView table)
    {
        int count = 0;
        table.LayoutChanged += (_, kind) =>
        {
            Assert.AreEqual(TableLayoutChangeKind.ColumnMove, kind);
            count++;
        };
        return () => count;
    }

    private static TableHeaderStrip Strip(TableView table) =>
        Descendants<TableHeaderStrip>(table).First();

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    // The move operations, the resolved columns they act on, and the strip's drop arithmetic are
    // all internal to Synapse, which grants no InternalsVisibleTo. Reflection is the only way to
    // reach them without widening the control's public surface for a test.

    private static bool MoveColumnTo(TableView table, string id, int boundary) =>
        (bool)Invoke(table, "MoveColumnTo", TableHarness.ResolvedColumn(table, id), boundary, null!)!;

    private static void SetColumnVisibility(TableView table, string id, bool visible) =>
        Invoke(table, "SetColumnVisibility", TableHarness.ResolvedColumn(table, id), visible);

    private static bool MoveColumnBy(TableView table, string id, int step) =>
        (bool)Invoke(table, "MoveColumnBy", TableHarness.ResolvedColumn(table, id), step)!;

    private static int BoundaryAt(TableHeaderStrip strip, double x) =>
        (int)Invoke(strip, "BoundaryAt", x)!;

    private static int DropBoundary(TableHeaderStrip strip, double x, object dragged) =>
        (int)Invoke(strip, "DropBoundary", x, dragged)!;

    private static void MoveInsertionMarker(TableHeaderStrip strip, double x) =>
        Invoke(strip, "MoveInsertionMarker", x);

    private static FrameworkElement Marker(TableHeaderStrip strip) =>
        (FrameworkElement)Field(strip, "_marker")!;

    private static double MarkerX(TableHeaderStrip strip) =>
        ((TranslateTransform)Field(strip, "_markerOffset")!).X;

    private static object? Invoke(object target, string method, params object[] arguments) =>
        target.GetType()
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, arguments);

    private static object? Field(object target, string name) =>
        target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target);

    /// <summary>One rendered frame of the header, addressed in device-independent pixels.</summary>
    private sealed class Shot
    {
        private byte[] _pixels = Array.Empty<byte>();

        private int Width { get; set; }

        private int Height { get; set; }

        private double Scale { get; set; } = 1;

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

            Assert.IsTrue(shot.Width > 0 && shot.Height > 0, "The header rendered an empty bitmap.");
            return shot;
        }

        internal int X(double dips) => Math.Clamp((int)Math.Round(dips * Scale), 0, Width - 1);

        internal int Y(double dips) => Math.Clamp((int)Math.Round(dips * Scale), 0, Height - 1);

        internal uint At(int x, int y)
        {
            int i = ((y * Width) + x) * 4;
            return (uint)(_pixels[i] | (_pixels[i + 1] << 8) | (_pixels[i + 2] << 16));
        }

        /// <summary>The strongest contrast against <paramref name="reference"/> across one scan.</summary>
        internal double Contrast(uint reference, double fromDips, double toDips, double atDips)
        {
            double best = 0;
            int y = Y(atDips);
            for (int x = X(fromDips); x <= X(toDips); x++)
            {
                best = Math.Max(best, Ratio(At(x, y), reference));
            }

            return best;
        }

        /// <summary>How many pixels of one scan are painted with something other than the background.</summary>
        internal int DrawnWidth(uint background, double fromDips, double toDips, double atDips)
        {
            int drawn = 0;
            int y = Y(atDips);
            for (int x = X(fromDips); x <= X(toDips); x++)
            {
                if (Ratio(At(x, y), background) > 1.1)
                {
                    drawn++;
                }
            }

            return drawn;
        }

        private static double Ratio(uint a, uint b)
        {
            double first = Luminance(a);
            double second = Luminance(b);
            return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
        }

        private static double Luminance(uint rgb) =>
            (0.2126 * Linear((rgb >> 16) & 0xFF))
            + (0.7152 * Linear((rgb >> 8) & 0xFF))
            + (0.0722 * Linear(rgb & 0xFF));

        private static double Linear(uint channel)
        {
            double value = channel / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
    }
}
