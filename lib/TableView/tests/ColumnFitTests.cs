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

    private const string EmptyXaml = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <Border Height="24" />
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
            column.ShortName = "Q";
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
    public Task AShortNameDoesNotLetTheFullHeaderWidenAFit() =>
        TestHost.RunAsync(async () =>
        {
            Column column = Column("a", 400);
            column.DisplayName = new string('W', 40);
            column.ShortName = "Q";
            column.MinWidth = 100;
            Table table = await LoadAsync(Rows(20, 40), column);

            table.Fit(column);

            Assert.AreEqual(100d, TableHarness.EffectiveWidth(table, "a"), 0d);
        });

    /// <summary>
    /// The sorted column always shows its glyph, because it is the only sign that a click sorted
    /// when every value in the column is equal. With automatic fill off the label gives way, and a
    /// fit makes room. An unsorted header reserves nothing.
    /// </summary>
    [TestMethod]
    public Task TheSortedColumnAlwaysShowsItsGlyph() =>
        TestHost.RunAsync(async () =>
        {
            Column tight = Column("tight", 400);
            tight.MinWidth = 124;
            Column other = Column("other", 400);
            foreach (var column in new[] { tight, other })
                column.Header = new Border { Width = 100, Height = 20 };
            Table table = Build(tight, other);
            table.Schema<FitRow>()
                .SortKey(tight, row => row.CellWidth)
                .SortKey(other, row => row.CellWidth);
            table.ItemsSource = Rows(20, 40);
            await TableHarness.LoadAsync(table);
            await SettleAsync(table);

            // A sort fills the width while automatic fill is on, and this is about the glyph.
            table.Layout = table.Layout with { IsAutomaticFillEnabled = false };
            table.FitColumns();
            Assert.AreEqual(124d, TableHarness.EffectiveWidth(table, "tight"), 0d);

            table.Sort = new Sort(tight);
            await SettleAsync(table);
            var tightGlyph = Descendants<FontIcon>(Descendants<Header.Cell>(table).First()).Single();
            Assert.AreEqual(124d, TableHarness.EffectiveWidth(table, "tight"), 0d, "no width changed");
            Assert.AreEqual(
                tightGlyph.DesiredSize.Width,
                GlyphSpace(tightGlyph),
                0.001,
                "the label gave way to the glyph"
            );

            table.Fit(tight);
            await SettleAsync(table);
            Assert.AreEqual(
                100 + tightGlyph.DesiredSize.Width + Inset(table),
                TableHarness.EffectiveWidth(table, "tight"),
                0.001,
                "a fit makes room for the glyph beside the whole label"
            );

            table.Sort = null;
            await SettleAsync(table);
            Assert.AreEqual(Visibility.Collapsed, tightGlyph.Visibility);
            Assert.AreEqual(0d, GlyphSpace(tightGlyph), 0d, "an unsorted header reserves nothing");
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
    /// A table shown before its rows fills from its headers, and its first rows fill again. Rows
    /// that join the rows on screen resize nothing: a fit to them would move every column in
    /// front of the person as the data came in. A different list fills after the frame that shows
    /// it, because a host switches lists at every selection click and a fill in that frame slowed
    /// each click.
    /// </summary>
    [TestMethod]
    public Task ListsThatReplaceTheScreenFillAndJoiningRowsDoNot() =>
        TestHost.RunAsync(async () =>
        {
            // a cannot grow past its fit, so its width is the widest cell or header the last fill
            // measured.
            Column a = Column("a", 200);
            a.CanGrow = false;
            Column b = Column("b", 150);
            b.CellTemplate = EmptyTemplate;
            foreach (Column column in new[] { a, b })
                column.Header = new Border { Width = 100, Height = 20 };
            Table table = Build(a, b);
            Func<int> events = LayoutChanges(table);
            await TableHarness.LoadAsync(table);
            await SettleAsync(table);

            Assert.AreEqual(100 + Inset(table), TableHarness.EffectiveWidth(table, "a"), 0.001);

            List<FitRow> first = Rows(20, 300);
            table.ItemsSource = first;
            await SettleAsync(table);

            Assert.AreEqual(
                300 + Inset(table),
                TableHarness.EffectiveWidth(table, "a"),
                0.001,
                "the first rows fill"
            );

            List<FitRow> joined = Rows(1, 500);
            joined.AddRange(first);
            table.ItemsSource = joined;
            await SettleAsync(table);

            Assert.AreEqual(
                300 + Inset(table),
                TableHarness.EffectiveWidth(table, "a"),
                0.001,
                "a row joining them resizes nothing"
            );

            table.ItemsSource = Rows(20, 100);
            await table.ViewReady;
            table.UpdateLayout();

            Assert.AreEqual(
                300 + Inset(table),
                TableHarness.EffectiveWidth(table, "a"),
                0.001,
                "the layout that shows a different list does not fill"
            );

            await SettleAsync(table);

            Assert.AreEqual(
                100 + Inset(table),
                TableHarness.EffectiveWidth(table, "a"),
                0.001,
                "a different list fills after its frame"
            );
            Assert.AreEqual(0, events(), "automatic fills report nothing");
        });

    /// <summary>
    /// A sort command fills for the order it shows. A re-sort of live values under the same sort
    /// does not: a fill at every update would move the columns every few seconds.
    /// </summary>
    [TestMethod]
    public Task ASortFillsAndALiveReSortDoesNot() =>
        TestHost.RunAsync(async () =>
        {
            Column a = Column("a", 200);
            a.CanGrow = false;
            Column b = Column("b", 150);
            b.CellTemplate = EmptyTemplate;
            Table table = Build(a, b);
            table.Schema<FitRow>().SortKey(a, row => row.CellWidth);
            table.SortInterval = TimeSpan.Zero;
            List<FitRow> rows = Rows(20, 100);
            rows[19].CellWidth = 400;
            table.ItemsSource = rows;
            await TableHarness.LoadAsync(table);
            await SettleAsync(table);
            Assert.AreEqual(100 + Inset(table), TableHarness.EffectiveWidth(table, "a"), 0.001);

            table.Sort = new Sort(a, SortDirection.Descending);
            await SettleAsync(table);

            Assert.AreEqual(
                400 + Inset(table),
                TableHarness.EffectiveWidth(table, "a"),
                0.001,
                "the sort brought the wide row on screen and filled"
            );

            rows[19].CellWidth = 50;
            table.RefreshView(new object[] { rows[19] });
            await SettleAsync(table);

            Assert.AreEqual(
                400 + Inset(table),
                TableHarness.EffectiveWidth(table, "a"),
                0.001,
                "the re-sort took the wide row away and resized nothing"
            );
        });

    [TestMethod]
    public Task ResizingFillsTheNewWidthWithoutReportingALayoutChange() =>
        TestHost.RunAsync(async () =>
        {
            Table table = Build(Column("a", 200), Column("b", 150));
            foreach (var column in table.Columns)
                column.Header = new Border { Width = 100, Height = 20 };
            await TableHarness.LoadAsync(table);
            await SettleAsync(table);
            Func<int> events = LayoutChanges(table);

            table.Width = 500;
            await SettleAsync(table);

            Assert.AreEqual(250d, TableHarness.EffectiveWidth(table, "a"), 0d);
            Assert.AreEqual(250d, TableHarness.EffectiveWidth(table, "b"), 0d);
            Assert.AreEqual(0, events());
        });

    [TestMethod]
    public Task AResizeBurstReusesItsFirstFitAndMeasuresAgainWhenItSettles() =>
        TestHost.RunAsync(async () =>
        {
            Column a = Column("a", 200);
            Border header = new() { Width = 176, Height = 20 };
            a.Header = header;
            a.MinWidth = 80;
            Column b = Column("b", 200);
            b.Header = new Border { Width = 276, Height = 20 };
            Table table = Build(a, b);
            await TableHarness.LoadAsync(table);
            await SettleAsync(table);

            header.Width = 76;
            table.Width = 200;
            table.UpdateLayout();
            Assert.AreEqual(80d, TableHarness.EffectiveWidth(table, "a"), 0.01);
            Assert.AreEqual(120d, TableHarness.EffectiveWidth(table, "b"), 0.01);

            header.Width = 276;
            table.Width = 600;
            table.UpdateLayout();
            Assert.AreEqual(150d, TableHarness.EffectiveWidth(table, "a"), 0.01);
            Assert.AreEqual(450d, TableHarness.EffectiveWidth(table, "b"), 0.01);

            var timer = Proof.Field<Microsoft.UI.Dispatching.DispatcherQueueTimer>(table, "_resizeDue");
            await TableHarness.WaitUntilAsync(() => !timer.IsRunning, "the resize burst to settle");
            Assert.AreEqual(300d, TableHarness.EffectiveWidth(table, "a"), 0.01);
            Assert.AreEqual(300d, TableHarness.EffectiveWidth(table, "b"), 0.01);

            table.Width = 500;
            table.UpdateLayout();
            Assert.AreEqual(250d, TableHarness.EffectiveWidth(table, "a"), 0.01);
            Assert.AreEqual(250d, TableHarness.EffectiveWidth(table, "b"), 0.01);
        });

    [TestMethod]
    public Task NewRowsAreMeasuredOnTheNextResizeWithoutMovingColumnsOnArrival() =>
        TestHost.RunAsync(async () =>
        {
            Column a = Column("a", 200);
            a.Header = new Border { Width = 400, Height = 20 };
            Table table = Build(a, Column("b", 200));
            List<FitRow> shown = Rows(20, 100);
            table.ItemsSource = shown;
            await TableHarness.LoadAsync(table);
            await SettleAsync(table);

            table.Width = 600;
            table.UpdateLayout();
            var timer = Proof.Field<Microsoft.UI.Dispatching.DispatcherQueueTimer>(table, "_resizeDue");
            await TableHarness.WaitUntilAsync(() => !timer.IsRunning, "the first resize to settle");
            double before = TableHarness.EffectiveWidth(table, "a");

            // They join the rows on screen; a different list would fill after its frame.
            List<FitRow> arrived = Rows(20, 600);
            arrived.AddRange(shown);
            table.ItemsSource = arrived;
            await table.ViewReady;
            table.UpdateLayout();
            Assert.AreEqual(before, TableHarness.EffectiveWidth(table, "a"), 0d);

            table.Width = 500;
            table.UpdateLayout();
            Assert.AreEqual(250d, TableHarness.EffectiveWidth(table, "a"), 0.01);
            Assert.AreEqual(250d, TableHarness.EffectiveWidth(table, "b"), 0.01);
        });

    [TestMethod]
    public Task RestoringLayoutDoesNotMoveLiveWidths() =>
        TestHost.RunAsync(async () =>
        {
            Table table = Build(Column("a", 400), Column("b", 200));
            await TableHarness.LoadAsync(table);
            await SettleAsync(table);
            TableHarness.SetWidth(table, "a", 250);
            table.Layout = TestData.Layout();
            await SettleAsync(table);
            Assert.AreEqual(250d, TableHarness.EffectiveWidth(table, "a"), 0d);
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
    /// The fill's one rule: the columns end at the table's edge and the space is shared in the
    /// fitted proportions.
    /// </summary>
    [TestMethod]
    public Task FillingSharesTheSpareWidthAndEndsAtTheEdge() =>
        TestHost.RunAsync(async () =>
        {
            Column a = Column("a", 400);
            a.Header = new Border { Width = 200, Height = 20 };
            Table table = await LoadAsync(Rows(20, 100), a, Column("b", 300));
            table.FitColumns();
            Func<int> events = LayoutChanges(table);

            table.FitWidth();

            double edge = Descendants<Header.Strip>(table).First().ActualWidth;
            Assert.AreEqual(
                edge,
                TableHarness.TotalWidth(table),
                0.5,
                "the columns end at the edge"
            );

            // Fitted, a is its 200 header and b its 100 cell, each plus the inset; the one factor then
            // spreads those over the whole width.
            double fitted = 300 + 2 * Inset(table);
            Assert.AreEqual(
                (200 + Inset(table)) * edge / fitted,
                TableHarness.EffectiveWidth(table, "a"),
                0.5
            );
            Assert.AreEqual(
                (100 + Inset(table)) * edge / fitted,
                TableHarness.EffectiveWidth(table, "b"),
                0.5
            );
            Assert.AreEqual(1, events());

            table.FitWidth();

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

            table.FitWidth();

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
    public Task FillingZeroWidthColumnsSharesTheRoomWithoutNaN() =>
        TestHost.RunAsync(async () =>
        {
            Column a = Column("a", 1);
            Column b = Column("b", 1);
            foreach (Column column in new[] { a, b })
            {
                column.MinWidth = 0;
                column.Header = new Border();
                column.CellTemplate = EmptyTemplate;
            }
            Table table = Build(a, b);
            table.CellPadding = new(0);
            table.ItemsSource = Array.Empty<FitRow>();
            await TableHarness.LoadAsync(table);

            table.FitWidth();

            double width = Descendants<Header.Strip>(table).First().ActualWidth / 2;
            Assert.IsTrue(width > 0);
            Assert.AreEqual(width, TableHarness.EffectiveWidth(table, "a"), .001);
            Assert.AreEqual(width, TableHarness.EffectiveWidth(table, "b"), .001);
        });

    [TestMethod]
    public Task FillingPreservesProtectedContentAndHeaders() =>
        TestHost.RunAsync(async () =>
        {
            Column name = Column("name", 300);
            name.Header = new Border { Width = 1000, Height = 20 };
            Column value = Column("value", 200);
            value.CanShrink = false;
            Column empty = Column("empty", 200);
            empty.Header = new Border { Width = 180, Height = 20 };
            empty.CellTemplate = EmptyTemplate;
            empty.CanShrink = false;
            Table table = await LoadAsync(Rows(20, 100), name, value, empty);

            table.FitWidth();

            double edge = Descendants<Header.Strip>(table).First().ActualWidth;
            Assert.AreEqual(100 + Inset(table), TableHarness.EffectiveWidth(table, "value"), 0.001);
            Assert.AreEqual(180 + Inset(table), TableHarness.EffectiveWidth(table, "empty"), 0.001);
            Assert.AreEqual(
                edge - 280 - 2 * Inset(table),
                TableHarness.EffectiveWidth(table, "name"),
                0.001
            );

            table.Width = 200;
            await SettleAsync(table);

            Assert.AreEqual(100 + Inset(table), TableHarness.EffectiveWidth(table, "value"), 0.001);
            Assert.AreEqual(180 + Inset(table), TableHarness.EffectiveWidth(table, "empty"), 0.001);
            Assert.AreEqual(10d, TableHarness.EffectiveWidth(table, "name"), 0.001);
        });

    [TestMethod]
    public Task FillingGivesSpareWidthOnlyToColumnsWithContent() =>
        TestHost.RunAsync(async () =>
        {
            Column empty = Column("empty", 300);
            empty.Header = new Border { Width = 100, Height = 20 };
            empty.CellTemplate = EmptyTemplate;
            Table table = await LoadAsync(Rows(20, 100), Column("a", 300), empty);

            table.FitWidth();

            double edge = Descendants<Header.Strip>(table).First().ActualWidth;
            Assert.AreEqual(
                100 + Inset(table),
                TableHarness.EffectiveWidth(table, "empty"),
                0.001,
                "the empty column keeps its header width"
            );
            Assert.AreEqual(
                edge - 100 - Inset(table),
                TableHarness.EffectiveWidth(table, "a"),
                0.001
            );
        });

    [TestMethod]
    public Task FillingKeepsAColumnThatCannotGrowAtItsFit() =>
        TestHost.RunAsync(async () =>
        {
            Column value = Column("value", 300);
            value.CanGrow = false;
            Table table = await LoadAsync(Rows(20, 100), Column("a", 300), value);

            table.FitWidth();

            double edge = Descendants<Header.Strip>(table).First().ActualWidth;
            Assert.AreEqual(
                100 + Inset(table),
                TableHarness.EffectiveWidth(table, "value"),
                0.001,
                "the value keeps its fitted width"
            );
            Assert.AreEqual(
                edge - 100 - Inset(table),
                TableHarness.EffectiveWidth(table, "a"),
                0.001
            );
        });

    /// <summary>
    /// A shortfall the empty columns can absorb leaves the columns with content at their fit, and
    /// the empty columns give up only the missing width rather than collapsing to MinWidth.
    /// </summary>
    [TestMethod]
    public Task FillingTakesAShortfallFromEmptyColumnsFirst() =>
        TestHost.RunAsync(async () =>
        {
            Column empty = Column("empty", 300);
            empty.Header = new Border { Width = 400, Height = 20 };
            empty.CellTemplate = EmptyTemplate;
            Table table = await LoadAsync(Rows(20, 400), Column("a", 300), empty);

            table.FitWidth();

            double edge = Descendants<Header.Strip>(table).First().ActualWidth;
            Assert.AreEqual(400 + Inset(table), TableHarness.EffectiveWidth(table, "a"), 0.001);
            Assert.AreEqual(
                edge - 400 - Inset(table),
                TableHarness.EffectiveWidth(table, "empty"),
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
                TableHarness.EffectiveWidth(table, "a"),
                0d,
                "the live column carries the fitted width"
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
    private static readonly DataTemplate EmptyTemplate = (DataTemplate)XamlReader.Load(EmptyXaml);

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

    /// <summary>Start fit tests at declared widths after initialization has completed.</summary>
    private static async Task<Table> LoadAsync(List<FitRow> rows, params Column[] columns)
    {
        Table table = Build(columns);
        table.ItemsSource = rows;

        await TableHarness.LoadAsync(table);
        TestData.ApplyDeclaredWidths(table);
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
        await table.ViewReady;
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

    private static double GlyphSpace(FontIcon glyph)
    {
        var panel = (Panel)Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(glyph);
        var label = (FrameworkElement)panel.Children[0];
        return panel.ActualWidth - Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(label).Width;
    }

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
