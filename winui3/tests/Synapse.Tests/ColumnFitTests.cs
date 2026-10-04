using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Synapse;

namespace Synapse_Tests;

/// <summary>A row whose cell declares its own width, so a fit has a known number to find.</summary>
public sealed class FitRow
{
    public double CellWidth { get; set; }
}

/// <summary>
/// Section 10's fit commands and the geometry the resize separator grabs. The point of the section
/// is what a fit may not do: it measures the header and the cells the current visual layout has
/// already realized, and nothing else.
/// </summary>
[TestClass]
public class ColumnFitTests
{
    private const string CellXaml = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Border Width="{Binding CellWidth}" Height="24" />
        </DataTemplate>
        """;

    /// <summary>The one DIP a header cell adds for the visible separator line.</summary>
    private const double SeparatorLine = 1;

    /// <summary>
    /// The control's own cell inset, which every cell and header cell now carries. A fit measures
    /// the cell as it is drawn, so the fitted width is the content plus this: a fit that left it
    /// out would produce a column that clips its own content by exactly this much.
    /// </summary>
    private static double Inset(TableView table) => table.CellPadding.Left + table.CellPadding.Right;

    // ------------------------------------------------------------------ what a fit measures

    [TestMethod]
    public Task AFitTakesTheWidthOfTheRealizedCells() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync(Rows(20, 100), Column("a", 400));

        table.AutoFitColumn("a");

        Assert.AreEqual(100 + Inset(table), TableHarness.ResolvedWidth(table, "a"), 0d);
    });

    [TestMethod]
    public Task AFitTakesTheHeaderWhenTheHeaderIsTheWidest() => TestHost.RunAsync(async () =>
    {
        TableColumn column = Column("a", 400);
        column.Header = new Border { Width = 140, Height = 20 };
        TableView table = await LoadAsync(Rows(20, 40), column);

        table.AutoFitColumn("a");

        Assert.AreEqual(
            140 + Inset(table) + SeparatorLine, TableHarness.ResolvedWidth(table, "a"), 0d);
    });

    [TestMethod]
    public Task AFitIsClampedToTheColumnLimits() => TestHost.RunAsync(async () =>
    {
        TableColumn floor = Column("floor", 400);
        floor.MinWidth = 200;
        TableColumn ceiling = Column("ceiling", 100);
        ceiling.MaxWidth = 60;
        ceiling.Header = new Border { Width = 300, Height = 20 };
        TableView table = await LoadAsync(Rows(20, 20), floor, ceiling);

        table.AutoFitVisibleColumns();

        Assert.AreEqual(200d, TableHarness.ResolvedWidth(table, "floor"), 0d, "MinWidth wins");
        Assert.AreEqual(60d, TableHarness.ResolvedWidth(table, "ceiling"), 0d, "MaxWidth wins");
    });

    /// <summary>
    /// The restriction section 10 exists for: a fit must not enumerate the source or instantiate
    /// off-screen templates, so a value far down the list is invisible to it until it scrolls in.
    /// </summary>
    [TestMethod]
    public Task AFitNeverSeesAnUnrealizedRow() => TestHost.RunAsync(async () =>
    {
        List<FitRow> rows = Rows(500, 60);
        rows[400].CellWidth = 900;
        TableView table = await LoadAsync(rows, Column("a", 400));

        table.AutoFitColumn("a");

        Assert.AreEqual(60 + Inset(table), TableHarness.ResolvedWidth(table, "a"), 0d,
            "row 400 is not realized, so its 900 DIP cell is not part of the fit");

        ListView list = HostedList(table);
        list.ScrollIntoView(rows[400]);
        list.ScrollIntoView(rows[400]);
        table.UpdateLayout();
        await Task.Delay(250);
        table.UpdateLayout();

        table.AutoFitColumn("a");

        Assert.AreEqual(900 + Inset(table), TableHarness.ResolvedWidth(table, "a"), 0d,
            "after scrolling, the same command finds it");
    });

    // ------------------------------------------------------------------ what a fit refuses

    [TestMethod]
    public Task AFitOfAHiddenColumnDoesNothing() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync(Rows(20, 100), Column("a", 400), Column("b", 300));
        table.Layout = TestData.Layout(
            visibility: new Dictionary<string, bool> { ["b"] = false });
        Func<int> events = LayoutChanges(table);

        table.AutoFitColumn("b");

        Assert.AreEqual(300d, TableHarness.ResolvedWidth(table, "b"), 0d);
        Assert.AreEqual(0, events(), "a no-op fit reports nothing");
    });

    [TestMethod]
    public Task AFitOfANonResizableColumnDoesNothing() => TestHost.RunAsync(async () =>
    {
        TableColumn fixedWidth = Column("a", 400);
        fixedWidth.CanResize = false;
        TableView table = await LoadAsync(Rows(20, 100), fixedWidth);
        Func<int> events = LayoutChanges(table);

        table.AutoFitColumn("a");

        Assert.AreEqual(400d, TableHarness.ResolvedWidth(table, "a"), 0d);
        Assert.AreEqual(0, events());
    });

    [TestMethod]
    public Task AnUnknownColumnIdIsAnArgumentError() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync(Rows(4, 100), Column("a", 400));

        ArgumentException error = Expect.Throws<ArgumentException>(() => table.AutoFitColumn("nope"));

        Assert.AreEqual("columnId", error.ParamName);
    });

    // ------------------------------------------------------------------ one notification

    [TestMethod]
    public Task FittingEveryVisibleColumnReportsOnce() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync(
            Rows(20, 100), Column("a", 400), Column("b", 300), Column("c", 250));
        Func<int> events = LayoutChanges(table);

        table.AutoFitVisibleColumns();

        Assert.AreEqual(1, events(), "three fitted columns, one LayoutChanged");
        Assert.AreEqual(100 + Inset(table), TableHarness.ResolvedWidth(table, "a"), 0d);
        Assert.AreEqual(100 + Inset(table), TableHarness.ResolvedWidth(table, "b"), 0d);
        Assert.AreEqual(100 + Inset(table), TableHarness.ResolvedWidth(table, "c"), 0d);

        table.AutoFitVisibleColumns();

        Assert.AreEqual(1, events(), "a second fit changes nothing, so it reports nothing");
    });

    [TestMethod]
    public Task AFitThatChangesAWidthReportsAResizedLayout() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync(Rows(20, 100), Column("a", 400));
        TableLayoutChangeKind? reported = null;
        table.LayoutChanged += (_, kind) => reported = kind;

        table.AutoFitColumn("a");

        Assert.AreEqual(TableLayoutChangeKind.AutoFit, reported);
        Assert.AreEqual(100 + Inset(table), table.Layout.Widths["a"], 0d,
            "the snapshot read after the report carries the new width override");
    });

    // ------------------------------------------------------------------ separator geometry

    /// <summary>
    /// The separator the pointer grabs is centred on a column's trailing edge and moves with the
    /// table's horizontal offset. This is the arithmetic <c>TableHeaderStrip</c> presses on.
    /// </summary>
    [TestMethod]
    public Task TheSeparatorZoneIsCentredOnEachTrailingEdge() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync(Rows(4, 100), Column("a", 200), Column("b", 150));

        Assert.AreEqual(0, Near(table, 200), "a's edge");
        Assert.AreEqual(0, Near(table, 196), "the zone reaches into a");
        Assert.AreEqual(0, Near(table, 204), "and into b");
        Assert.AreEqual(-1, Near(table, 190), "away from any edge");
        Assert.AreEqual(1, Near(table, 350), "b's edge");

        TableHarness.SetHorizontalOffset(table, 60);

        Assert.AreEqual(0, Near(table, 140), "the zone follows the scrolled header");
        Assert.AreEqual(-1, Near(table, 200));
    });

    /// <summary>
    /// The separator maths above is only right if a header-strip x is the same x the resolved
    /// layout arranges in. This measures the rendered header against it.
    /// </summary>
    [TestMethod]
    public Task ARenderedHeaderCellEndsWhereTheLayoutSaysItDoes() => TestHost.RunAsync(async () =>
    {
        TableView table = await LoadAsync(Rows(4, 100), Column("a", 200), Column("b", 150));

        TableHeaderStrip strip = Descendants<TableHeaderStrip>(table).First();
        Panel panel = (Panel)strip.GetType()
            .GetProperty("Panel", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(strip)!;

        for (int i = 0; i < 2; i++)
        {
            FrameworkElement cell = (FrameworkElement)panel.Children[i];
            double renderedEdge = cell
                .TransformToVisual(strip)
                .TransformPoint(new Windows.Foundation.Point(cell.ActualWidth, 0))
                .X;

            Assert.AreEqual(i == 0 ? 200d : 350d, renderedEdge, 0.5,
                "the rendered trailing edge is the one TrailingEdgeNear reports");
        }
    });

    // ------------------------------------------------------------------ helpers

    private static readonly DataTemplate CellTemplate = (DataTemplate)XamlReader.Load(CellXaml);

    private static TableColumn Column(string id, double defaultWidth)
    {
        TableColumn column = TestData.Column(id, defaultWidth);
        column.MinWidth = 10;
        column.CellTemplate = CellTemplate;
        return column;
    }

    private static List<FitRow> Rows(int count, double cellWidth)
    {
        List<FitRow> rows = new();
        for (int i = 0; i < count; i++)
        {
            rows.Add(new FitRow { CellWidth = cellWidth });
        }

        return rows;
    }

    private static async Task<TableView> LoadAsync(List<FitRow> rows, params TableColumn[] columns)
    {
        TableView table = TestData.Table(columns);
        table.Width = 700;
        table.Height = 220;
        table.ItemsSource = rows;

        await TableHarness.LoadAsync(table);
        table.UpdateLayout();
        await Task.Delay(250);
        table.UpdateLayout();
        return table;
    }

    /// <summary>Starts counting <c>LayoutChanged</c> now; call the result to read the count.</summary>
    private static Func<int> LayoutChanges(TableView table)
    {
        int count = 0;
        table.LayoutChanged += (_, _) => count++;
        return () => count;
    }

    /// <summary>The separator index at this header-strip x, at the grab tolerance section 10 uses.</summary>
    private static int Near(TableView table, double x) =>
        TableHarness.TrailingEdgeNear(table, x, tolerance: 4);

    private static ListView HostedList(TableView table) =>
        Descendants<ListView>(table).First();

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
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
}
