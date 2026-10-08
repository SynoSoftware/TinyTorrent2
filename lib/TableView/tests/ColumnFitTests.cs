using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Syno.TableView.Tests;

/// <summary>A row whose cell declares its own width, so a fit has a known number to find.</summary>
public sealed class FitRow
{
    public double CellWidth { get; set; }
}

/// <summary>
/// Section 10's fit commands, the first fill, and the geometry the resize separator grabs. The
/// point of the section is what a fit may not do: it measures the header and the cells the current visual layout has
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

    /// <summary>
    /// The control's own cell inset, which every cell and header cell now carries. A fit measures
    /// the cell as it is drawn, so the fitted width is the content plus this: a fit that left it
    /// out would produce a column that clips its own content by exactly this much.
    /// </summary>
    private static double Inset(Table table) => table.CellPadding.Left + table.CellPadding.Right;

    // ------------------------------------------------------------------ what a fit measures

    [TestMethod]
    public Task AFitTakesTheWidthOfTheRealizedCells() =>
        TestHost.RunAsync(async () =>
        {
            Column a = Column("a", 400);
            Table table = await LoadAsync(Rows(20, 100), a);

            table.Fit(a);

            Assert.AreEqual(100 + Inset(table), TableHarness.EffectiveWidth(table, "a"), 0d);
        });

    [TestMethod]
    public Task AFitTakesTheHeaderWhenTheHeaderIsTheWidest() =>
        TestHost.RunAsync(async () =>
        {
            Column column = Column("a", 400);
            column.Header = new Border { Width = 140, Height = 20 };
            Table table = await LoadAsync(Rows(20, 40), column);

            table.Fit(column);

            Assert.AreEqual(
                140 + Inset(table),
                TableHarness.EffectiveWidth(table, "a"),
                0d,
                "the separator line is drawn over the inset and adds no width"
            );
        });

    [TestMethod]
    public Task AFitStopsAtMinWidth() =>
        TestHost.RunAsync(async () =>
        {
            Column floor = Column("floor", 400);
            floor.MinWidth = 200;
            Table table = await LoadAsync(Rows(20, 20), floor);

            table.FitColumns();

            Assert.AreEqual(200d, TableHarness.EffectiveWidth(table, "floor"), 0d, "MinWidth wins");
        });

    /// <summary>
    /// The restriction section 10 exists for: a fit must not enumerate the source or instantiate
    /// off-screen templates, so a value far down the list is invisible to it until it scrolls in.
    /// </summary>
    [TestMethod]
    public Task AFitNeverSeesAnUnrealizedRow() =>
        TestHost.RunAsync(async () =>
        {
            List<FitRow> rows = Rows(500, 60);
            rows[400].CellWidth = 900;
            Column a = Column("a", 400);
            Table table = await LoadAsync(rows, a);

            table.Fit(a);

            Assert.AreEqual(
                60 + Inset(table),
                TableHarness.EffectiveWidth(table, "a"),
                0d,
                "row 400 is not realized, so its 900 DIP cell is not part of the fit"
            );

            ListView list = Surface(table);
            list.ScrollIntoView(rows[400]);
            list.ScrollIntoView(rows[400]);
            table.UpdateLayout();
            await Task.Delay(250);
            table.UpdateLayout();

            table.Fit(a);

            Assert.AreEqual(
                900 + Inset(table),
                TableHarness.EffectiveWidth(table, "a"),
                0d,
                "after scrolling, the same command finds it"
            );
        });

    // ------------------------------------------------------------------ the first fill

    /// <summary>
    /// The table fills its width from the declared widths before any row arrives, and rows that
    /// arrive afterwards resize nothing: a fit to them would move every column in front of the
    /// person as the data came in.
    /// </summary>
    [TestMethod]
    public Task ATableFillsItsWidthBeforeItsRowsAndKeepsItWhenTheyArrive() =>
        TestHost.RunAsync(async () =>
        {
            Table table = Build(Column("a", 200), Column("b", 150));
            Func<int> events = LayoutChanges(table);
            await TableHarness.LoadAsync(table);
            await SettleAsync(table);

            Assert.AreEqual(
                400d,
                TableHarness.EffectiveWidth(table, "a"),
                0d,
                "200:150 scaled to 700"
            );
            Assert.AreEqual(300d, TableHarness.EffectiveWidth(table, "b"), 0d);

            table.ItemsSource = Rows(20, 100);
            await SettleAsync(table);

            Assert.AreEqual(
                400d,
                TableHarness.EffectiveWidth(table, "a"),
                0d,
                "the rows resized nothing"
            );
            Assert.AreEqual(300d, TableHarness.EffectiveWidth(table, "b"), 0d);
            Assert.AreEqual(
                0,
                events(),
                "the fill is part of the initial layout, which reports nothing"
            );
        });

    /// <summary>The fill happens once: a table that later changes width keeps its columns.</summary>
    [TestMethod]
    public Task ALaterWidthIsNotFilledAgain() =>
        TestHost.RunAsync(async () =>
        {
            Table table = Build(Column("a", 200), Column("b", 150));
            await TableHarness.LoadAsync(table);
            await SettleAsync(table);

            table.Width = 500;
            await SettleAsync(table);

            Assert.AreEqual(400d, TableHarness.EffectiveWidth(table, "a"), 0d);
            Assert.AreEqual(300d, TableHarness.EffectiveWidth(table, "b"), 0d);
        });

    [TestMethod]
    public Task ASavedWidthKeepsTheTableFromFillingItsWidth() =>
        TestHost.RunAsync(async () =>
        {
            Table table = Build(Column("a", 400), Column("b", 200));
            table.Layout = TestData.Layout(widths: new Dictionary<string, double> { ["a"] = 250 });
            await TableHarness.LoadAsync(table);
            await SettleAsync(table);

            Assert.AreEqual(250d, TableHarness.EffectiveWidth(table, "a"), 0d, "the saved width");
            Assert.AreEqual(
                200d,
                TableHarness.EffectiveWidth(table, "b"),
                0d,
                "one chosen width means the person has sized the columns"
            );
        });

    // ------------------------------------------------------------------ what a fit refuses

    [TestMethod]
    public Task AFitOfAHiddenColumnDoesNothing() =>
        TestHost.RunAsync(async () =>
        {
            Column b = Column("b", 300);
            Table table = await LoadAsync(Rows(20, 100), Column("a", 400), b);
            table.Layout = TestData.Layout(
                visibility: new Dictionary<string, bool> { ["b"] = false }
            );
            Func<int> events = LayoutChanges(table);

            table.Fit(b);

            Assert.AreEqual(300d, TableHarness.EffectiveWidth(table, "b"), 0d);
            Assert.AreEqual(0, events(), "a no-op fit reports nothing");
        });

    [TestMethod]
    public Task AFitOfANonResizableColumnDoesNothing() =>
        TestHost.RunAsync(async () =>
        {
            Column fixedWidth = Column("a", 400);
            fixedWidth.CanResize = false;
            Table table = await LoadAsync(Rows(20, 100), fixedWidth);
            Func<int> events = LayoutChanges(table);

            table.Fit(fixedWidth);

            Assert.AreEqual(400d, TableHarness.EffectiveWidth(table, "a"), 0d);
            Assert.AreEqual(0, events());
        });

    [TestMethod]
    public Task AColumnTheTableDoesNotHoldIsAnArgumentError() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync(Rows(4, 100), Column("a", 400));

            ArgumentException error = Expect.Throws<ArgumentException>(() =>
                table.Fit(Column("a", 400))
            );

            Assert.AreEqual("column", error.ParamName);
        });

    // ------------------------------------------------------------------ one notification

    [TestMethod]
    public Task FittingEveryVisibleColumnReportsOnce() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync(
                Rows(20, 100),
                Column("a", 400),
                Column("b", 300),
                Column("c", 250)
            );
            Func<int> events = LayoutChanges(table);

            table.FitColumns();

            Assert.AreEqual(1, events(), "three fitted columns, one LayoutChanged");
            Assert.AreEqual(100 + Inset(table), TableHarness.EffectiveWidth(table, "a"), 0d);
            Assert.AreEqual(100 + Inset(table), TableHarness.EffectiveWidth(table, "b"), 0d);
            Assert.AreEqual(100 + Inset(table), TableHarness.EffectiveWidth(table, "c"), 0d);

            table.FitColumns();

            Assert.AreEqual(1, events(), "a second fit changes nothing, so it reports nothing");
        });

    /// <summary>
    /// The fill's one rule: the columns end where the header buttons begin and the space is shared
    /// in the fitted proportions. Filling to the strip's own edge hid the Fill button under the
    /// pointer that had just clicked it.
    /// </summary>
    [TestMethod]
    public Task FillingSharesTheSpareWidthAndEndsAtTheHeaderButtons() =>
        TestHost.RunAsync(async () =>
        {
            Column a = Column("a", 400);
            a.Header = new Border { Width = 200, Height = 20 };
            Table table = await LoadAsync(Rows(20, 100), a, Column("b", 300));
            table.ShowsHeaderButtons = true;
            table.FitColumns();
            Func<int> events = LayoutChanges(table);

            table.FillWidth();
            table.UpdateLayout();

            Header.Strip strip = Descendants<Header.Strip>(table).First();
            Button fit = Descendants<Button>(strip)
                .Single(button => button.Name == "PART_FitButton");
            Button fill = Descendants<Button>(strip)
                .Single(button => button.Name == "PART_FillButton");
            double buttons = fit.TransformToVisual(strip).TransformPoint(default).X;
            Assert.AreEqual(
                buttons,
                TableHarness.TotalWidth(table),
                0.5,
                "the columns end at the buttons"
            );
            Assert.AreEqual(Visibility.Visible, fit.Visibility, "the buttons keep their place");
            Assert.AreEqual(
                Visibility.Visible,
                fill.Visibility,
                "the button just used stays under the pointer"
            );

            // Fitted, a is its 200 header and b its 100 cell, each plus the inset; the one factor then
            // spreads those over the room the buttons leave.
            double fitted = 300 + 2 * Inset(table);
            Assert.AreEqual(
                (200 + Inset(table)) * buttons / fitted,
                TableHarness.EffectiveWidth(table, "a"),
                0.5
            );
            Assert.AreEqual(
                (100 + Inset(table)) * buttons / fitted,
                TableHarness.EffectiveWidth(table, "b"),
                0.5
            );
            Assert.AreEqual(1, events());

            table.FillWidth();

            Assert.AreEqual(
                1,
                events(),
                "a second fill ends where the first did, so it reports nothing"
            );
        });

    /// <summary>
    /// Columns wider than the table shrink by the same rule, and a column the shrink would take
    /// below its MinWidth stays there while the others share what is left.
    /// </summary>
    [TestMethod]
    public Task FillingShrinksInProportionAndStopsAtMinWidth() =>
        TestHost.RunAsync(async () =>
        {
            Column a = Column("a", 400);
            a.Header = new Border { Width = 1000, Height = 20 };
            Column c = Column("c", 300);
            c.MinWidth = 300;
            Table table = await LoadAsync(Rows(20, 600), a, Column("b", 300), c);
            table.FitColumns();

            table.FillWidth();

            // Fitted, a is its 1000 header, b and c their 600 cells, each plus the inset. Shrinking c in
            // proportion would take it below 300, so it stays there and a and b share what is left.
            double edge = Descendants<Header.Strip>(table).First().ActualWidth;
            double shared = (edge - 300) / (1000 + 600 + 2 * Inset(table));
            Assert.AreEqual(
                edge,
                TableHarness.TotalWidth(table),
                0.001,
                "the columns end at the edge"
            );
            Assert.AreEqual(
                300d,
                TableHarness.EffectiveWidth(table, "c"),
                0d,
                "MinWidth stops the shrink"
            );
            Assert.AreEqual(
                (1000 + Inset(table)) * shared,
                TableHarness.EffectiveWidth(table, "a"),
                0.001
            );
            Assert.AreEqual(
                (600 + Inset(table)) * shared,
                TableHarness.EffectiveWidth(table, "b"),
                0.001
            );
        });

    [TestMethod]
    public Task AFitThatChangesAWidthReportsAResizedLayout() =>
        TestHost.RunAsync(async () =>
        {
            Column a = Column("a", 400);
            Table table = await LoadAsync(Rows(20, 100), a);
            LayoutChange? reported = null;
            table.LayoutChanged += (_, change) => reported = change;

            table.Fit(a);

            Assert.AreEqual(LayoutChange.Fit, reported);
            Assert.AreEqual(
                100 + Inset(table),
                table.Layout.WidthOverrides["a"],
                0d,
                "the snapshot read after the report carries the new width override"
            );
        });

    // ------------------------------------------------------------------ separator geometry

    /// <summary>
    /// The separator maths is only right if a header-strip x is the same x the effective
    /// layout arranges in. This measures the rendered header against it.
    /// </summary>
    [TestMethod]
    public Task ARenderedHeaderCellEndsWhereTheLayoutSaysItDoes() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await LoadAsync(Rows(4, 100), Column("a", 200), Column("b", 150));

            Header.Strip strip = Descendants<Header.Strip>(table).First();
            Panel panel = (Panel)
                strip
                    .GetType()
                    .GetProperty("Panel", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(strip)!;

            for (int i = 0; i < 2; i++)
            {
                FrameworkElement cell = (FrameworkElement)panel.Children[i];
                double renderedEdge = cell.TransformToVisual(strip)
                    .TransformPoint(new Windows.Foundation.Point(cell.ActualWidth, 0))
                    .X;

                Assert.AreEqual(
                    i == 0 ? 200d : 350d,
                    renderedEdge,
                    0.5,
                    "the rendered trailing edge is the one TrailingEdgeNear reports"
                );
            }
        });

    // ------------------------------------------------------------------ helpers

    private static readonly DataTemplate CellTemplate = (DataTemplate)XamlReader.Load(CellXaml);

    private static Column Column(string id, double defaultWidth)
    {
        Column column = TestData.Column(id, defaultWidth);
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

    /// <summary>A table restored with every declared width, so only a fit command fits it.</summary>
    private static async Task<Table> LoadAsync(List<FitRow> rows, params Column[] columns)
    {
        Table table = Build(columns);
        table.Layout = TestData.DeclaredWidths(table);
        table.ItemsSource = rows;

        await TableHarness.LoadAsync(table);
        await SettleAsync(table);
        return table;
    }

    private static Table Build(params Column[] columns)
    {
        Table table = TestData.Table(columns);
        table.Width = 700;
        table.Height = 220;
        return table;
    }

    private static async Task SettleAsync(Table table)
    {
        table.UpdateLayout();
        await Task.Delay(250);
        table.UpdateLayout();
    }

    /// <summary>Starts counting <c>LayoutChanged</c> now; call the result to read the count.</summary>
    private static Func<int> LayoutChanges(Table table)
    {
        int count = 0;
        table.LayoutChanged += (_, _) => count++;
        return () => count;
    }

    private static ListView Surface(Table table) => Descendants<ListView>(table).First();

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
