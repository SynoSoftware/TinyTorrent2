using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Foundation;

namespace Syno.TableView.Tests;

/// <summary>
/// An independent pass over the whole feature list, taken without reference to any earlier claim.
/// Every test exercises the control on a live loaded visual tree and records what it measured.
/// </summary>
[TestClass]
public class FeatureProofTests
{
    private static readonly string LogPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "proof-run.txt");

    [ClassCleanup]
    public static void WriteLog() => Proof.Flush(LogPath);

    // ================================================================ F01 columns

    /// <summary>
    /// The eleven download columns at their declared widths, with the first-run visible set of
    /// seven.
    /// </summary>
    [TestMethod]
    public Task F01_ElevenColumnsResolveWithTheDeclaredWidthsAndSevenVisible() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await Downloads.LoadAsync();

            string[] order = TableHarness.Order(table);
            Proof.Note("F01 resolved order: " + string.Join(", ", order));
            Assert.AreEqual(11, order.Length, "the schema resolves eleven columns");
            CollectionAssert.AreEqual(Downloads.Ids, order);

            foreach ((string id, double width, double min) in Downloads.Declared)
            {
                Assert.AreEqual(width, TableHarness.ResolvedWidth(table, id), $"{id} width");
                Assert.AreEqual(
                    min,
                    table.Columns.Single(c => c.Id == id).MinWidth,
                    $"{id} minimum");
            }

            (string Id, double Offset, double Width)[] visible = TableHarness.VisibleColumns(table);
            Proof.Note("F01 visible: " + string.Join(
                ", ", visible.Select(v => $"{v.Id}@{v.Offset}w{v.Width}")));

            CollectionAssert.AreEqual(
                new[] { "name", "progress", "status", "queue", "speed", "peers", "size" },
                visible.Select(v => v.Id).ToArray(),
                "the first-run visible set is seven");

            CollectionAssert.AreEqual(
                new[] { 0d, 150, 370, 480, 560, 740, 828 },
                visible.Select(v => v.Offset).ToArray(),
                "and they are laid out contiguously from the declared widths");

            Proof.Note($"F01 total width = {TableHarness.TotalWidth(table)}");
            Assert.AreEqual(
                928d, TableHarness.TotalWidth(table), "the seven visible widths, and only those");
        });

    /// <summary>Every realized header cell carries the column's display name and resolved width.</summary>
    [TestMethod]
    public Task F01_TheHeaderRealizesOneCellPerVisibleColumn() => TestHost.RunAsync(async () =>
    {
        Table table = await Downloads.LoadAsync();
        List<Header.Cell> cells = Downloads.HeaderCells(table);

        string realized = string.Join(", ", cells.Select(c => $"{ColumnId(c)}:{c.ActualWidth}"));
        Proof.Note("F01 realized header cells: " + realized);

        Assert.AreEqual(7, cells.Count);
        CollectionAssert.AreEqual(
            new[] { "name", "progress", "status", "queue", "speed", "peers", "size" },
            cells.Select(c => ColumnId(c)).ToArray());
        CollectionAssert.AreEqual(
            new[] { 150d, 220, 110, 80, 180, 88, 100 },
            cells.Select(c => c.ActualWidth).ToArray());
    });

    // ================================================================ F02 sorting

    /// <summary>
    /// Section 9's cycle, entered where a header click enters it: the strip's own
    /// <c>ActivateSortFrom</c>, resolved from a real realized header cell.
    /// </summary>
    [TestMethod]
    public Task F02_HeaderActivationCyclesAscendingDescendingNatural() => TestHost.RunAsync(async () =>
    {
        Table table = await Downloads.LoadAsync();
        Header.Strip strip = Downloads.Strip(table);
        Header.Cell queue = Downloads.HeaderCells(table).Single(c => ColumnId(c) == "queue");

        List<LayoutChange> kinds = new();
        table.LayoutChanged += (_, kind) => kinds.Add(kind);

        Proof.Note("F02 natural: " + Downloads.ViewNames(table));
        CollectionAssert.AreEqual(Downloads.NaturalNames, Downloads.ViewNameArray(table));

        Assert.IsTrue(ActivateSort(strip, queue), "the header resolved a sortable column");
        Proof.Note("F02 ascending:  " + Downloads.ViewNames(table));
        CollectionAssert.AreEqual(Downloads.QueueAscending, Downloads.ViewNameArray(table));

        ActivateSort(strip, queue);
        Proof.Note("F02 descending: " + Downloads.ViewNames(table));
        CollectionAssert.AreEqual(
            Downloads.QueueAscending.Reverse().ToArray(), Downloads.ViewNameArray(table));

        ActivateSort(strip, queue);
        Proof.Note("F02 natural:    " + Downloads.ViewNames(table));
        CollectionAssert.AreEqual(Downloads.NaturalNames, Downloads.ViewNameArray(table));

        CollectionAssert.AreEqual(
            new[]
            {
                LayoutChange.Sort, LayoutChange.Sort, LayoutChange.Sort,
            },
            kinds,
            "one LayoutChanged per activation");
    });

    /// <summary>Only one column sorts at a time; a second column starts its own cycle ascending.</summary>
    [TestMethod]
    public Task F02_ASecondColumnTakesOverAtAscending() => TestHost.RunAsync(async () =>
    {
        Table table = await Downloads.LoadAsync();
        Header.Strip strip = Downloads.Strip(table);
        List<Header.Cell> cells = Downloads.HeaderCells(table);

        ActivateSort(strip, cells.Single(c => ColumnId(c) == "queue"));
        ActivateSort(strip, cells.Single(c => ColumnId(c) == "queue"));
        Assert.AreEqual("queue", table.Layout.SortColumnId);
        Assert.AreEqual(SortDirection.Descending, table.Layout.SortDirection);

        ActivateSort(strip, cells.Single(c => ColumnId(c) == "name"));
        ColumnLayout state = table.Layout;
        Proof.Note($"F02 after switching column: sort={state.SortColumnId} {state.SortDirection}");

        Assert.AreEqual("name", state.SortColumnId);
        Assert.AreEqual(SortDirection.Ascending, state.SortDirection);
        CollectionAssert.AreEqual(
            Downloads.NaturalNames.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            Downloads.ViewNameArray(table));
    });

    /// <summary>The sort indicator, read off the realized header rather than off the sort state.</summary>
    [TestMethod]
    public Task F02_TheActiveHeaderShowsTheDirectionGlyphAndAnnouncesIt() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await Downloads.LoadAsync();
            Header.Strip strip = Downloads.Strip(table);
            Header.Cell queue =
                Downloads.HeaderCells(table).Single(c => ColumnId(c) == "queue");
            Header.Cell name =
                Downloads.HeaderCells(table).Single(c => ColumnId(c) == "name");

            Assert.AreEqual(("", Visibility.Collapsed, string.Empty), Glyph(queue));

            ActivateSort(strip, queue);
            (string ascGlyph, Visibility ascVisible, string ascStatus) = Glyph(queue);
            Proof.Note($"F02 ascending glyph U+{(int)ascGlyph[0]:X4} {ascVisible} status='{ascStatus}'");
            Assert.AreEqual(Visibility.Visible, ascVisible);
            Assert.AreEqual(Lucide.ChevronUp, ascGlyph, "ChevronUp");
            Assert.AreEqual("Sorted ascending", ascStatus);
            Assert.AreEqual(Visibility.Collapsed, Glyph(name).Item2, "the sibling stays clear");

            ActivateSort(strip, queue);
            (string descGlyph, _, string descStatus) = Glyph(queue);
            Proof.Note($"F02 descending glyph U+{(int)descGlyph[0]:X4} status='{descStatus}'");
            Assert.AreEqual(Lucide.ChevronDown, descGlyph, "ChevronDown");
            Assert.AreEqual("Sorted descending", descStatus);

            ActivateSort(strip, queue);
            Assert.AreEqual(("", Visibility.Collapsed, string.Empty), Glyph(queue));
        });

    // ================================================================ F03 column reorder by drag

    /// <summary>
    /// The header drag: the boundary it resolves at a given x, the insertion indicator it moves to
    /// that boundary, and the order the drop produces.
    /// </summary>
    [TestMethod]
    public Task F03_AHeaderDragShowsAnInsertionIndicatorAndMovesTheColumn() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await Downloads.LoadAsync();
            Header.Strip strip = Downloads.Strip(table);
            FrameworkElement marker = Marker(strip);
            object name = Column(table, "name");

            Assert.AreEqual(Visibility.Collapsed, marker.Visibility, "idle: no indicator");

            // What OnPointerPressed does on a passive header, then what the threshold crossing does.
            Proof.Call(strip, "Begin", Proof.Parse(strip, "_gesture", "Pressed"), name, 1u, 60d);
            Proof.SetField(strip, "_gesture", Proof.Parse(strip, "_gesture", "Dragging"));

            // Drag right, over the middle of "status" (visible span 370..480).
            Proof.Call(strip, "MoveInsertionMarker", 460d);
            Proof.Note(
                $"F03 marker at x=460: visibility={marker.Visibility} " +
                $"translateX={Proof.TranslateX(marker)} width={marker.Width}");
            Assert.AreEqual(Visibility.Visible, marker.Visibility, "the indicator is drawn");
            Assert.AreEqual(480 - (marker.Width / 2), Proof.TranslateX(marker), 0.01,
                "centred on the boundary after 'status'");

            int boundary = (int)Proof.Call(strip, "DropBoundary", 460d, name)!;
            Proof.Note($"F03 drop boundary at x=460 = {boundary}");
            Assert.AreEqual(2, boundary);

            Assert.IsTrue(MoveColumn(table, name, boundary));
            Proof.Call(strip, "EndGesture");

            string[] after = TableHarness.VisibleColumns(table).Select(v => v.Id).ToArray();
            Proof.Note("F03 order after drop: " + string.Join(", ", after));
            CollectionAssert.AreEqual(
                new[] { "progress", "status", "name", "queue", "speed", "peers", "size" }, after);
            Assert.AreEqual(Visibility.Collapsed, marker.Visibility, "the indicator is taken down");
        });

    /// <summary>The realized cells follow the move: header labels and every realized row panel.</summary>
    [TestMethod]
    public Task F03_TheRealizedHeaderAndRowsFollowTheMove() => TestHost.RunAsync(async () =>
    {
        Table table = await Downloads.LoadAsync();

        Proof.Note("F03 header before: " + string.Join(
            ", ", Downloads.HeaderCells(table).Select(c => ColumnId(c))));

        Assert.IsTrue(MoveColumn(table, Column(table, "size"), 0));
        table.UpdateLayout();

        string[] header = Downloads.HeaderCells(table).Select(c => ColumnId(c)).ToArray();
        Proof.Note("F03 header after:  " + string.Join(", ", header));
        Assert.AreEqual("size", header[0]);

        List<CellsPanel> rows = Downloads.RowPanels(table);
        Proof.Note($"F03 realized row panels = {rows.Count}, " +
                   $"cells per row = {string.Join(",", rows.Select(r => r.Children.Count))}");
        Assert.IsTrue(rows.Count > 0, "rows realized");
        Assert.IsTrue(
            rows.All(r => r.Children.Count == TableHarness.VisibleColumns(table).Length),
            "every realized row carries one cell per visible column");

        // The row's own cells now start with the size the moved column renders.
        await Task.Delay(120);
        table.UpdateLayout();
        string[] texts = Proof.Descendants<TextBlock>(rows[0]).Select(t => t.Text).ToArray();
        Proof.Note("F03 row 0 cell text after the move: " + string.Join(" | ", texts));
        Assert.AreEqual(
            "6400000000", texts[0], "the row's leading cell is now the moved 'size' column");
    });

    // ================================================================ F04 resize and fit

    /// <summary>
    /// The separator gesture, driven through the strip's own three steps. The drag moves a guide
    /// and no width at all; the release applies exactly one.
    /// </summary>
    [TestMethod]
    public Task F04_ASeparatorDragResizesOneColumnAndReportsOnce() => TestHost.RunAsync(async () =>
    {
        Table table = await Downloads.LoadAsync();
        Header.Strip strip = Downloads.Strip(table);

        int events = 0;
        table.LayoutChanged += (_, kind) =>
        {
            Assert.AreEqual(LayoutChange.Resize, kind);
            events++;
        };

        Assert.IsFalse((bool)Proof.Call(strip, "BeginResizeAt", 75d, 1u)!, "mid-header is not a grip");
        Assert.IsTrue((bool)Proof.Call(strip, "BeginResizeAt", 150d, 1u)!, "the trailing edge is");

        Proof.Call(strip, "TrackResize", 210d);
        Proof.Note($"F04 after tracking to 210: guide={Preview(strip)} " +
                   $"name={TableHarness.ResolvedWidth(table, "name")} " +
                   $"progress={TableHarness.ResolvedWidth(table, "progress")} events={events}");
        Assert.AreEqual(210d, Preview(strip), "the guide follows the pointer");
        Assert.AreEqual(150d, TableHarness.ResolvedWidth(table, "name"), "and no width has moved");
        Assert.AreEqual(220d, TableHarness.ResolvedWidth(table, "progress"), "the neighbour is untouched");
        Assert.AreEqual(0, events, "a movement is not a persistence event");

        // Section 10 clamps to the column's own MinWidth of 90 and to nothing else.
        Proof.Call(strip, "TrackResize", 10d);
        Assert.AreEqual(90d, Preview(strip), "the guide stops at MinWidth 90");

        Proof.Call(strip, "TrackResize", 260d);
        Proof.Call(strip, "CompleteResize");
        Proof.Note($"F04 completed: name={TableHarness.ResolvedWidth(table, "name")} events={events}");
        Assert.AreEqual(260d, TableHarness.ResolvedWidth(table, "name"), "the release applies it");
        Assert.AreEqual(1, events, "one gesture, one report");
        Assert.AreEqual(260d, table.Layout.Widths["name"]);
    });

    /// <summary>Escape leaves the width alone, because the drag never applied one.</summary>
    [TestMethod]
    public Task F04_EscapeDuringAResizeLeavesTheWidthAlone() => TestHost.RunAsync(async () =>
    {
        Table table = await Downloads.LoadAsync();
        Header.Strip strip = Downloads.Strip(table);
        int events = 0;
        table.LayoutChanged += (_, _) => events++;

        Proof.Call(strip, "BeginResizeAt", 150d, 1u);
        Proof.Call(strip, "TrackResize", 300d);
        Assert.AreEqual(300d, Preview(strip), "the guide had moved");

        Proof.Call(strip, "CancelGesture");
        Proof.Note($"F04 after Escape: name={TableHarness.ResolvedWidth(table, "name")} events={events}");
        Assert.AreEqual(150d, TableHarness.ResolvedWidth(table, "name"), "and no width ever did");
        Assert.AreEqual(0, events);
    });

    /// <summary>The width the release would apply. Nothing else in the gesture changes a width.</summary>
    private static double Preview(Header.Strip strip) =>
        Proof.Field<double>(strip, "_previewWidth");

    /// <summary>
    /// The two lines a double-click on a separator runs: resolve the separator, fit that column.
    /// </summary>
    [TestMethod]
    public Task F04_ADoubleClickOnASeparatorFitsThatColumn() => TestHost.RunAsync(async () =>
    {
        Table table = await Downloads.LoadAsync();
        Header.Strip strip = Downloads.Strip(table);

        List<LayoutChange> kinds = new();
        table.LayoutChanged += (_, kind) => kinds.Add(kind);

        object? separator = Proof.Call(strip, "SeparatorNear", 150d);
        Assert.IsNotNull(separator, "x=150 is the separator after 'name'");
        table.Fit(table.Columns.Single(c => c.Id == "name"));

        double fitted = TableHarness.ResolvedWidth(table, "name");
        Proof.Note($"F04 fit 'name': 150 -> {fitted}, kinds={string.Join(",", kinds)}");
        Assert.AreNotEqual(150d, fitted, "the fit changed the width");
        Assert.IsTrue(fitted >= 90, "and stayed inside the column's own minimum");
        CollectionAssert.AreEqual(new[] { LayoutChange.Fit }, kinds);
    });

    [TestMethod]
    public Task F04_FitVisibleColumnsReportsTheWholeOperationOnce() => TestHost.RunAsync(async () =>
    {
        Table table = await Downloads.LoadAsync();
        double[] before = TableHarness.VisibleColumns(table).Select(v => v.Width).ToArray();

        int events = 0;
        table.LayoutChanged += (_, kind) =>
        {
            Assert.AreEqual(LayoutChange.Fit, kind);
            events++;
        };

        table.FitColumns();

        double[] after = TableHarness.VisibleColumns(table).Select(v => v.Width).ToArray();
        Proof.Note("F04 fit-visible before: " + string.Join(", ", before));
        Proof.Note("F04 fit-visible after:  " + string.Join(", ", after));
        Assert.AreEqual(1, events, "seven columns, one report");
        Assert.IsTrue(before.Zip(after).Any(p => p.First != p.Second), "something changed");
    });

    // ================================================================ F05 header menu

    /// <summary>
    /// Section 12's menu, opened the way a right-click opens it, then read off the open popup.
    /// </summary>
    [TestMethod]
    public Task F05_TheHeaderMenuOpensWithACheckableItemPerColumnAndTheCommands() =>
        TestHost.RunAsync(async () =>
        {
            Table table = await Downloads.LoadAsync();
            Header.Strip strip = Downloads.Strip(table);
            Header.Cell name =
                Downloads.HeaderCells(table).Single(c => ColumnId(c) == "name");

            // The strip answers ContextRequested by opening this menu. Both halves are checked:
            // that the request opens a popup, and what the menu it opens holds.
            Assert.IsTrue((bool)Proof.Call(strip, "ShowMenu", name, (Point?)null)!);
            await Task.Delay(120);
            int popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(table.XamlRoot).Count;
            Proof.Note($"F05 ShowMenu opened {popups} popup(s)");
            Assert.AreEqual(1, popups, "the request opened a flyout");
            CloseMenu(table);

            MenuFlyout menu = MenuFor(table, "name");
            List<MenuFlyoutItemBase> items = menu.Items.ToList();
            Proof.Note("F05 menu: " + string.Join(" | ", items.Select(Proof.Describe)));

            CollectionAssert.AreEqual(
                new[]
                {
                    "Hide column “Name”", "---", "Fit column “Name”", "Fit visible columns",
                    "---", "Move left", "Move right", "---",
                    "Name", "Progress", "Status", "Queue", "ETA", "Speed", "Peers", "Size",
                    "Ratio", "Added", "Completed on",
                },
                items.Select(Proof.Label).ToArray());

            // Everything after the last separator: three of the commands now carry a column's
            // name themselves, so they can no longer be told from the column entries by name.
            List<MenuFlyoutItem> toggles = items
                .Skip(items.FindLastIndex(item => item is MenuFlyoutSeparator) + 1)
                .OfType<MenuFlyoutItem>()
                .ToList();
            Proof.Note("F05 column list: " + string.Join(" | ", toggles.Select(Proof.Describe)));

            Assert.AreEqual(11, toggles.Count, "one checkable item per declared column");
            CollectionAssert.AreEqual(
                new[]
                {
                    "Name", "Progress", "Status", "Queue", "ETA", "Speed", "Peers", "Size",
                    "Ratio", "Added", "Completed on",
                },
                toggles.Select(t => t.Text).ToArray());
            CollectionAssert.AreEqual(
                new[] { true, true, true, true, false, true, true, true, false, false, false },
                toggles.Select(t => t.Icon is not null).ToArray(),
                "checked exactly for the seven visible columns");

            Assert.IsFalse(
                items.Single(i => Proof.Label(i) == "Move left").IsEnabled,
                "'name' is leftmost, so Move left is disabled");
            Assert.IsTrue(items.Single(i => Proof.Label(i) == "Move right").IsEnabled);
        });

    /// <summary>Toggling a column in the menu hides it and shows it again in its own place.</summary>
    [TestMethod]
    public Task F05_TheColumnToggleHidesAndShowsAColumnInPlace() => TestHost.RunAsync(async () =>
    {
        Table table = await Downloads.LoadAsync();
        Header.Strip strip = Downloads.Strip(table);
        Header.Cell name = Downloads.HeaderCells(table).Single(c => ColumnId(c) == "name");

        // Widen 'status' first, so the show can be checked to keep the resolved width.
        Proof.Call(strip, "BeginResizeAt", 480d, 1u);
        Proof.Call(strip, "TrackResize", 520d);
        Proof.Call(strip, "CompleteResize");
        Assert.AreEqual(150d, TableHarness.ResolvedWidth(table, "status"));

        await InvokeMenuCommand(table, name, "name", "Status");
        table.UpdateLayout();
        string[] hidden = Downloads.HeaderCells(table).Select(c => ColumnId(c)).ToArray();
        Proof.Note("F05 after hiding status: " + string.Join(", ", hidden));
        CollectionAssert.AreEqual(
            new[] { "name", "progress", "queue", "speed", "peers", "size" }, hidden);
        Assert.AreEqual(6, Downloads.RowPanels(table)[0].Children.Count, "rows lost the cell too");

        await InvokeMenuCommand(table, name, "name", "Status");
        table.UpdateLayout();
        string[] shown = Downloads.HeaderCells(table).Select(c => ColumnId(c)).ToArray();
        Proof.Note("F05 after showing status: " + string.Join(", ", shown) +
                   $" width={TableHarness.ResolvedWidth(table, "status")}");
        CollectionAssert.AreEqual(
            new[] { "name", "progress", "status", "queue", "speed", "peers", "size" }, shown,
            "it comes back in its own place");
        Assert.AreEqual(150d, TableHarness.ResolvedWidth(table, "status"), "keeping its width");
    });

    /// <summary>The menu's own commands, invoked through their automation peers.</summary>
    [TestMethod]
    public Task F05_HideThisColumnAndTheMoveCommandsRunFromTheMenu() => TestHost.RunAsync(async () =>
    {
        Table table = await Downloads.LoadAsync();

        Header.Cell queue = Downloads.HeaderCells(table).Single(c => ColumnId(c) == "queue");
        await InvokeMenuCommand(table, queue, "queue", "Move left");
        table.UpdateLayout();
        string[] moved = Downloads.HeaderCells(table).Select(c => ColumnId(c)).ToArray();
        Proof.Note("F05 after 'Move left' on queue: " + string.Join(", ", moved));
        CollectionAssert.AreEqual(
            new[] { "name", "progress", "queue", "status", "speed", "peers", "size" }, moved);

        Header.Cell status = Downloads.HeaderCells(table).Single(c => ColumnId(c) == "status");
        await InvokeMenuCommand(table, status, "status", "Hide column “Status”");
        table.UpdateLayout();
        string[] hidden = Downloads.HeaderCells(table).Select(c => ColumnId(c)).ToArray();
        Proof.Note("F05 after hiding the status column: " + string.Join(", ", hidden));
        CollectionAssert.AreEqual(
            new[] { "name", "progress", "queue", "speed", "peers", "size" }, hidden);
    });

    /// <summary>The last visible column cannot be hidden, so the menu cannot empty the table.</summary>
    [TestMethod]
    public Task F05_TheLastVisibleColumnsToggleIsDisabled() => TestHost.RunAsync(async () =>
    {
        Table table = await Downloads.LoadAsync();
        Header.Strip strip = Downloads.Strip(table);

        foreach (string id in new[] { "progress", "status", "queue", "speed", "peers", "size" })
        {
            table.GetType();
            SetVisibility(table, id, false);
        }

        table.UpdateLayout();
        _ = strip;
        Header.Cell only = Downloads.HeaderCells(table).Single();
        Assert.AreEqual("name", ColumnId(only));

        List<MenuFlyoutItemBase> items = MenuFor(table, "name").Items.ToList();
        MenuFlyoutItem nameToggle =
            items.OfType<MenuFlyoutItem>().Single(t => t.Text == "Name");

        Proof.Note($"F05 last visible column: Hide enabled=" +
                   $"{items.Single(i => Proof.Label(i) == "Hide column “Name”").IsEnabled}, " +
                   $"Name toggle enabled={nameToggle.IsEnabled}");
        Assert.IsFalse(nameToggle.IsEnabled);
        Assert.IsFalse(items.Single(i => Proof.Label(i) == "Hide column “Name”").IsEnabled);
    });

    // ================================================================ F06 layout persistence

    /// <summary>
    /// Order, visibility, widths and sort taken out of one table, carried through JSON the way a
    /// restart would carry them, and restored into a second table built from the same schema.
    /// </summary>
    [TestMethod]
    public Task F06_OrderVisibilityWidthsAndSortSurviveASerializedRoundTrip() =>
        TestHost.RunAsync(async () =>
        {
            Table first = await Downloads.LoadAsync();
            Header.Strip strip = Downloads.Strip(first);

            MoveColumn(first, Column(first, "size"), 0);
            SetVisibility(first, "peers", false);
            SetVisibility(first, "ratio", true);
            Proof.Call(strip, "BeginResizeAt", TableHarness.VisibleColumns(first)
                .First(v => v.Id == "size").Width, 1u);
            Proof.Call(strip, "TrackResize", 137d);
            Proof.Call(strip, "CompleteResize");
            ActivateSort(strip, Downloads.HeaderCells(first).Single(c => ColumnId(c) == "queue"));
            ActivateSort(strip, Downloads.HeaderCells(first).Single(c => ColumnId(c) == "queue"));

            ColumnLayout saved = first.Layout;
            string json = JsonSerializer.Serialize(saved);
            Proof.Note("F06 saved layout: " + json);

            ColumnLayout reloaded = JsonSerializer.Deserialize<ColumnLayout>(json)!;

            Table second = Downloads.Build();
            second.ItemsSource = Downloads.Rows();
            second.Layout = reloaded;
            await TableHarness.LoadAsync(second);
            second.UpdateLayout();

            ColumnLayout restored = second.Layout;
            Proof.Note("F06 restored order:      " + string.Join(", ", restored.Order));
            Proof.Note("F06 restored visible:    " + string.Join(
                ", ", TableHarness.VisibleColumns(second).Select(v => v.Id)));
            Proof.Note("F06 restored widths:     " + string.Join(
                ", ", restored.Widths.Select(w => $"{w.Key}={w.Value}")));
            Proof.Note($"F06 restored sort:       {restored.SortColumnId} {restored.SortDirection}");

            CollectionAssert.AreEqual(saved.Order.ToArray(), restored.Order.ToArray());
            CollectionAssert.AreEqual(
                saved.Visibility.OrderBy(v => v.Key).ToArray(),
                restored.Visibility.OrderBy(v => v.Key).ToArray());
            CollectionAssert.AreEqual(
                saved.Widths.OrderBy(v => v.Key).ToArray(),
                restored.Widths.OrderBy(v => v.Key).ToArray());
            Assert.AreEqual("queue", restored.SortColumnId);
            Assert.AreEqual(SortDirection.Descending, restored.SortDirection);

            CollectionAssert.AreEqual(
                Downloads.QueueAscending.Reverse().ToArray(),
                Downloads.ViewNameArray(second),
                "the restored sort produced the restored view order");

            Assert.AreEqual("size", TableHarness.VisibleColumns(second)[0].Id);
            Assert.IsFalse(TableHarness.IsVisible(second, "peers"));
            Assert.IsTrue(TableHarness.IsVisible(second, "ratio"));
        });

    // ================================================================ F07 pointer selection

    /// <summary>
    /// Section 13's three pointer rules, at the point the arbiter applies them once it has resolved
    /// the row and the modifiers.
    /// </summary>
    [TestMethod]
    public Task F07_PlainClickReplacesCtrlTogglesShiftTakesTheRange() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(8, height: 400);

        h.Click(h[1]);
        Proof.Note("F07 plain click row 1: " + string.Join(",", h.SelectedKeys()));
        CollectionAssert.AreEqual(new[] { "k1" }, h.SelectedKeys());
        CollectionAssert.AreEqual(new[] { "k1" }, h.ContainerSelectedKeys());

        h.Click(h[4], ctrl: true);
        Proof.Note("F07 ctrl-click row 4: " + string.Join(",", h.SelectedKeys()));
        CollectionAssert.AreEqual(new[] { "k1", "k4" }, h.SelectedKeys());

        h.Click(h[4], ctrl: true);
        Proof.Note("F07 ctrl-click row 4 again: " + string.Join(",", h.SelectedKeys()));
        CollectionAssert.AreEqual(new[] { "k1" }, h.SelectedKeys(), "Ctrl toggles off");

        h.Click(h[4], ctrl: true);
        h.Click(h[6], shift: true);
        Proof.Note("F07 shift-click row 6: " + string.Join(",", h.SelectedKeys()));
        CollectionAssert.AreEqual(
            new[] { "k4", "k5", "k6" }, h.SelectedKeys(), "the range runs from the k4 anchor");
        CollectionAssert.AreEqual(new[] { "k4", "k5", "k6" }, h.ContainerSelectedKeys());

        h.Click(h[2]);
        Proof.Note("F07 plain click row 2: " + string.Join(",", h.SelectedKeys()));
        CollectionAssert.AreEqual(new[] { "k2" }, h.SelectedKeys(), "a plain click replaces");
    });

    /// <summary>A press on a selected row defers its click, so a drag keeps the whole packet.</summary>
    [TestMethod]
    public Task F07_APressOnASelectedRowDefersUntilRelease() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(8, height: 400);
        h.Table.Selection = new(new object[] { h[1], h[2], h[3] }, h[1]);

        Gesture.Press(h, row: h[2], item: h[2]);
        Proof.Note("F07 after press on selected row: " + string.Join(",", h.SelectedKeys()));
        CollectionAssert.AreEqual(new[] { "k1", "k2", "k3" }, h.SelectedKeys());

        Proof.Call(h.Table, "DispatchClick");
        Proof.Note("F07 after release: " + string.Join(",", h.SelectedKeys()));
        CollectionAssert.AreEqual(new[] { "k2" }, h.SelectedKeys());
    });

    // ================================================================ F08 keyboard

    /// <summary>Every key of section 13, through the actions the key switch dispatches to.</summary>
    [TestMethod]
    public Task F08_TheWholeKeyboardSetMovesSelectionAndCurrent() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(40, height: 400);

        Assert.IsTrue(h.MoveBy(1, extend: false));
        Proof.Note($"F08 Down from nothing: current={h.CurrentKey()} selected={string.Join(",", h.SelectedKeys())}");
        Assert.AreEqual("k0", h.CurrentKey());

        h.MoveBy(1, false);
        h.MoveBy(1, false);
        Assert.AreEqual("k2", h.CurrentKey());

        h.MoveBy(1, extend: true);
        Proof.Note("F08 Shift+Down: " + string.Join(",", h.SelectedKeys()));
        CollectionAssert.AreEqual(new[] { "k2", "k3" }, h.SelectedKeys());

        h.MoveBy(-1, extend: true);
        Proof.Note("F08 Shift+Up: " + string.Join(",", h.SelectedKeys()));
        CollectionAssert.AreEqual(new[] { "k2" }, h.SelectedKeys());

        h.MoveToEdge(first: false, extend: false);
        Proof.Note($"F08 End: current={h.CurrentKey()}");
        Assert.AreEqual("k39", h.CurrentKey());

        h.MoveToEdge(first: true, extend: false);
        Assert.AreEqual("k0", h.CurrentKey());

        h.MoveToEdge(first: false, extend: true);
        Proof.Note($"F08 Shift+End selected {h.SelectedKeys().Length} rows");
        Assert.AreEqual(40, h.SelectedKeys().Length);

        h.MoveToEdge(first: true, extend: false);
        h.MoveBy(5, false);
        h.MoveToEdge(first: true, extend: true);
        Proof.Note("F08 Shift+Home: " + string.Join(",", h.SelectedKeys()));
        CollectionAssert.AreEqual(new[] { "k0", "k1", "k2", "k3", "k4", "k5" }, h.SelectedKeys());

        int page = h.RowsPerPage();
        h.Table.Selection = Selection.Empty;
        h.MoveBy(1, false);
        h.MoveBy(page, false);
        Proof.Note($"F08 PageDown ({page} rows/page): current={h.CurrentKey()}");
        Assert.AreEqual("k" + page, h.CurrentKey());
        h.MoveBy(-page, false);
        Assert.AreEqual("k0", h.CurrentKey());

        Assert.IsTrue(h.SelectAll());
        Proof.Note($"F08 Ctrl+A selected {h.SelectedKeys().Length} rows");
        Assert.AreEqual(40, h.SelectedKeys().Length);

        h.Table.Selection = new(new object[] { h[7] }, h[7]);
        Assert.IsTrue(h.InvokeCurrent());
        Proof.Note($"F08 Enter invoked: {string.Join(",", h.Invoked)}");
        CollectionAssert.AreEqual(new object[] { h[7] }, h.Invoked);
    });

    /// <summary>
    /// The gate the key handler applies: the table's keys run from the passive row surface only,
    /// so an editor in a cell keeps its own arrows.
    /// </summary>
    [TestMethod]
    public Task F08_TheKeyGateRefusesWhileACellEditorHasFocus() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6, height: 300);
        ListView list = h.HostedList();

        Control row = (Control)list.ContainerFromItem(h[1]);
        row.Focus(FocusState.Programmatic);
        await Task.Delay(60);
        Proof.Note($"F08 focus on a row container: RowSurfaceHasFocus={h.RowSurfaceHasFocus()}");
        Assert.IsTrue(h.RowSurfaceHasFocus());

        TextBox editor = new() { Width = 60 };
        TestHost.RootPanel.Children.Add(editor);
        editor.Focus(FocusState.Programmatic);
        await Task.Delay(60);
        Proof.Note($"F08 focus in a TextBox: RowSurfaceHasFocus={h.RowSurfaceHasFocus()}");
        Assert.IsFalse(h.RowSurfaceHasFocus(), "the table's keys stand down");
    });

    // ================================================================ F09 marquee

    /// <summary>
    /// Section 14's rectangle: a drag from empty row surface, the overlay it draws, the rows it
    /// covers, and Escape putting the selection back.
    /// </summary>
    [TestMethod]
    public Task F09_AMarqueeFromEmptySurfaceSelectsTheRowsItCovers() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(
            6, configure: t => t.IsMarqueeEnabled = true, height: 400);
        ListView list = h.HostedList();
        FrameworkElement overlay = Overlay(h.Table);

        Assert.AreEqual(Visibility.Collapsed, overlay.Visibility, "idle: no rectangle");

        double rowHeight = ((FrameworkElement)list.ContainerFromItem(h[0])).ActualHeight;
        Proof.Note($"F09 realized row height = {rowHeight}");

        Gesture.PressEmpty(h, new Point(10, rowHeight * 4.5));
        Assert.IsTrue((bool)Proof.Call(h.Table, "CanCommitGesture")!, "the threshold may commit");

        // What CommitGesture dispatches to once the threshold is crossed. Its one other step is
        // ListView.CapturePointer, which needs a real Pointer this session cannot produce.
        Proof.Call(h.Table, "BeginMarquee", list);

        Assert.AreEqual(
            "Marquee", Proof.Field<object>(h.Table, "_gesture").ToString(), "the gesture committed");

        // Sweep up across rows 1..4.
        object marquee = Proof.Field<object>(h.Table, "_marquee");
        Proof.Call(marquee, "Track", new Point(180, rowHeight * 1.5));

        Proof.Note($"F09 overlay: visibility={overlay.Visibility} " +
                   $"x={Proof.TranslateX(overlay)} y={Proof.TranslateY(overlay)} " +
                   $"{overlay.Width}x{overlay.Height}");
        Assert.AreEqual(Visibility.Visible, overlay.Visibility, "the rectangle is drawn");
        Assert.AreEqual(170d, overlay.Width, 0.01);
        Assert.AreEqual(rowHeight * 3, overlay.Height, 0.01);

        Proof.Note("F09 selected by sweep: " + string.Join(",", h.SelectedKeys()));
        CollectionAssert.AreEqual(new[] { "k1", "k2", "k3", "k4" }, h.SelectedKeys());
        CollectionAssert.AreEqual(new[] { "k1", "k2", "k3", "k4" }, h.ContainerSelectedKeys());

        // Shrink it back over rows 3..4 only.
        Proof.Call(marquee, "Track", new Point(180, rowHeight * 3.5));
        Proof.Note("F09 after shrinking: " + string.Join(",", h.SelectedKeys()));
        CollectionAssert.AreEqual(new[] { "k3", "k4" }, h.SelectedKeys());

        Assert.IsTrue((bool)Proof.Call(h.Table, "CancelCommittedGesture")!, "Escape ends it");
        Proof.Note("F09 after Escape: " + string.Join(",", h.SelectedKeys()));
        Assert.AreEqual(0, h.SelectedKeys().Length, "the selection it started from comes back");
        Assert.AreEqual(Visibility.Collapsed, overlay.Visibility);
    });

    /// <summary>
    /// The marquee is on by default, because a rectangle is a selection gesture and every table
    /// has a selection; it goes only when the host withdraws it or the mode allows one row.
    /// </summary>
    [TestMethod]
    public Task F09_AMarqueeIsOnByDefaultAndRefusedWhenWithdrawn() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6, height: 300);
        Assert.IsTrue(h.Table.IsMarqueeEnabled, "on by default");

        Gesture.PressEmpty(h, new Point(10, 10));
        Assert.IsTrue((bool)Proof.Call(h.Table, "CanCommitGesture")!);

        h.Table.IsMarqueeEnabled = false;
        Assert.IsFalse((bool)Proof.Call(h.Table, "CanCommitGesture")!);

        h.Table.IsMarqueeEnabled = true;
        h.Table.SelectionMode = ListViewSelectionMode.Single;
        Proof.Call(h.Table, "SyncSelectionPolicy");
        Proof.Note("F09 marquee in Single mode allowed = " +
                   (bool)Proof.Call(h.Table, "CanCommitGesture")!);
        Assert.IsFalse((bool)Proof.Call(h.Table, "CanCommitGesture")!);
    });

    /// <summary>
    /// Section 14: a press on a row the table would not drag becomes the rectangle at the
    /// threshold, not a dead press. Nothing competes for the gesture there, and the pointer has
    /// said so already, showing the arrow where a draggable row shows the move cursor.
    /// </summary>
    [TestMethod]
    public Task F09_APressOnARowTheTableWouldNotDragBecomesTheMarquee() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6, height: 400);
        ListView list = h.HostedList();
        double rowHeight = ((FrameworkElement)list.ContainerFromItem(h[0])).ActualHeight;

        // Reordering is off by default, so no row can be dragged and the press selects the row as
        // a click would.
        Assert.IsFalse((bool)Proof.Call(h.Table, "CanBeginRowDrag", h[2])!);
        Gesture.Press(h, row: h[2], item: h[2], originY: rowHeight * 2.5);
        CollectionAssert.AreEqual(
            new[] { "k2" }, h.SelectedKeys(), "a press on a row that cannot be dragged selects it");
        Assert.AreEqual(
            "Marquee",
            Proof.Call(h.Table, "GestureAtThreshold")!.ToString(),
            "at the threshold the press becomes the rectangle");

        Proof.Call(h.Table, "BeginMarquee", list);
        object marquee = Proof.Field<object>(h.Table, "_marquee");
        Proof.Call(marquee, "Track", new Point(10, rowHeight * 4.5));
        Proof.Note("F09 sweep from a row: " + string.Join(",", h.SelectedKeys()));
        CollectionAssert.AreEqual(
            new[] { "k2", "k3", "k4" },
            h.SelectedKeys(),
            "the rectangle covers the row it started on and the rows swept below it");

        Assert.IsTrue((bool)Proof.Call(h.Table, "CancelCommittedGesture")!, "Escape ends it");
        Assert.AreEqual(
            0, h.SelectedKeys().Length, "and the selection the gesture started from comes back");

        // Once reordering is on the same press is the drag: the rule never guesses between the two.
        h.Table.CanReorder = true;
        Gesture.Press(h, row: h[2], item: h[2], originY: rowHeight * 2.5);
        Assert.AreEqual("RowDrag", Proof.Call(h.Table, "GestureAtThreshold")!.ToString());
        Proof.Call(h.Table, "CancelGesture");
    });

    // ================================================================ F10 row context menu

    /// <summary>
    /// Section 15: a context request on a row selects it, names the placement target, and reaches
    /// the host with the whole packet.
    /// </summary>
    [TestMethod]
    public Task F10_ARowContextRequestReachesTheHostWithTheRowAndThePacket() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(8, height: 400);
            ListView list = h.HostedList();

            List<ItemContextRequestedEventArgs> requests = new();
            h.Table.ItemContextRequested += (_, e) => requests.Add(e);

            // A request on an unselected row replaces the selection with it.
            FrameworkElement container = (FrameworkElement)list.ContainerFromItem(h[5]);
            Assert.IsTrue((bool)Proof.Call(
                h.Table, "RequestRowContext", h[5], container, (Point?)new Point(12, 9))!);

            ItemContextRequestedEventArgs first = requests.Single();
            Proof.Note($"F10 request on row 5: item={first.Item} " +
                       $"packet={string.Join(",", first.SelectedItems)} " +
                       $"target={first.Target.GetType().Name} point={first.Position}");
            Assert.AreSame(h[5], first.Item);
            CollectionAssert.AreEqual(new object[] { h[5] }, first.SelectedItems.ToArray());
            Assert.AreSame(container, first.Target);
            Assert.AreEqual(new Point(12, 9), first.Position);

            // A request inside an existing packet keeps the packet.
            h.Table.Selection = new(new object[] { h[1], h[2], h[3] }, h[2]);
            requests.Clear();
            Proof.Call(
                h.Table,
                "RequestRowContext",
                h[2],
                (FrameworkElement)list.ContainerFromItem(h[2]),
                (Point?)null);

            Proof.Note("F10 request inside a packet: " +
                       string.Join(",", requests.Single().SelectedItems));
            CollectionAssert.AreEqual(
                new object[] { h[1], h[2], h[3] }, requests.Single().SelectedItems.ToArray());
        });

    /// <summary>A row the host marked non-interactive asks for nothing.</summary>
    [TestMethod]
    public Task F10_ANonInteractiveRowRaisesNoContextRequest() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(
            6, t => t.Schema<Row>().CanInteract(row => row.Interactive), height: 300);
        h[3].Interactive = false;
        h.Table.RefreshView();

        int requests = 0;
        h.Table.ItemContextRequested += (_, _) => requests++;

        FrameworkElement container =
            (FrameworkElement)h.HostedList().ContainerFromItem(h[3]);
        bool handled = (bool)Proof.Call(
            h.Table, "RequestRowContext", h[3], container, (Point?)null)!;

        Proof.Note($"F10 non-interactive row: handled={handled} requests={requests}");
        Assert.IsFalse(handled);
        Assert.AreEqual(0, requests);
    });

    // ================================================================ F11 row drag reorder

    /// <summary>
    /// Section 16: a drag of the selected packet, the insertion marker it draws, and the request it
    /// ends with. The table changes no order itself.
    /// </summary>
    [TestMethod]
    public Task F11_DraggingASelectedRowMovesTheWholePacket() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(
            8, configure: t => t.CanReorder = true, height: 400);

        List<ReorderRequestedEventArgs> requests = new();
        h.Table.ReorderRequested += (_, e) => requests.Add(e);

        ListView list = h.HostedList();
        FrameworkElement marker = InsertionMarker(h.Table);
        double rowHeight = ((FrameworkElement)list.ContainerFromItem(h[0])).ActualHeight;

        Assert.AreEqual(Visibility.Collapsed, marker.Visibility);

        h.Table.Selection = new(new object[] { h[1], h[2], h[3] }, h[1]);
        ObservableCollection<Row> rows = h.Rows;

        Gesture.Press(h, row: h[2], item: h[2], originY: rowHeight * 2.5);
        Assert.AreEqual(
            "RowDrag",
            Proof.Call(h.Table, "GestureAtThreshold")!.ToString(),
            "a press on a row becomes the drag at the threshold");
        Proof.Call(h.Table, "BeginRowDrag", list, h[2]);
        Assert.AreEqual("RowDrag", Proof.Field<object>(h.Table, "_gesture").ToString());

        CollectionAssert.AreEqual(
            new object[] { h[1], h[2], h[3] },
            Proof.Field<IReadOnlyList<object>>(h.Table, "_movingPacket").ToArray(),
            "the packet resolved at the press is the whole selection");

        // Drag down to the boundary before row 6.
        object drag = Proof.Field<object>(h.Table, "_rowDrag");
        int boundary = (int)Proof.Call(drag, "Track", rowHeight * 5.6)!;
        Proof.Note($"F11 marker: visibility={marker.Visibility} y={Proof.TranslateY(marker)} " +
                   $"boundary={boundary} (rowHeight {rowHeight})");
        Assert.AreEqual(Visibility.Visible, marker.Visibility, "the insertion marker is drawn");
        Assert.AreEqual(6, boundary);

        Proof.Call(h.Table, "CompleteRowDrag", rowHeight * 5.6);

        ReorderRequestedEventArgs request = requests.Single();
        Proof.Note($"F11 request: moving={string.Join(",", request.Items)} " +
                   $"before={request.Before}");
        CollectionAssert.AreEqual(
            new object[] { h[1], h[2], h[3] }, request.Items.ToArray());
        Assert.AreSame(h[6], request.Before);

        CollectionAssert.AreEqual(
            new object[] { rows[0], rows[1], rows[2], rows[3], rows[4], rows[5], rows[6], rows[7] },
            rows.ToArray(),
            "the table moved nothing itself; the request is the whole of it");
        Assert.AreEqual(Visibility.Collapsed, marker.Visibility);

        // Apply the request the way the queue host would, and check it lands where it asked.
        List<Row> moving = request.Items.Cast<Row>().ToList();
        foreach (Row row in moving)
        {
            rows.Remove(row);
        }

        int at = rows.IndexOf((Row)request.Before!);
        for (int i = 0; i < moving.Count; i++)
        {
            rows.Insert(at + i, moving[i]);
        }

        Proof.Note("F11 host applied: " + string.Join(",", rows.Select(r => r.Key)));
        CollectionAssert.AreEqual(
            new[] { "k0", "k4", "k5", "k1", "k2", "k3", "k6", "k7" },
            rows.Select(r => r.Key).ToArray());
    });

    /// <summary>A drag of an unselected row moves that row alone and leaves the selection standing.</summary>
    [TestMethod]
    public Task F11_DraggingAnUnselectedRowMovesThatRowAlone() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(
            8,
            configure: t =>
            {
                t.CanReorder = true;
                t.IsMarqueeEnabled = true;
            },
            height: 400);

        List<ReorderRequestedEventArgs> requests = new();
        h.Table.ReorderRequested += (_, e) => requests.Add(e);

        h.Table.Selection = new(new object[] { h[0], h[1] }, h[0]);
        double rowHeight =
            ((FrameworkElement)h.HostedList().ContainerFromItem(h[0])).ActualHeight;

        Gesture.Press(h, row: h[5], item: h[5], originY: rowHeight * 5.5);
        CollectionAssert.AreEqual(
            new[] { "k0", "k1" }, h.SelectedKeys(), "the press defers, so the packet stands");
        Assert.AreEqual(
            "RowDrag",
            Proof.Call(h.Table, "GestureAtThreshold")!.ToString(),
            "a press on a row is a drag at the threshold, never a marquee, whatever the host enabled");

        Proof.Call(h.Table, "BeginRowDrag", h.HostedList(), h[5]);
        Proof.Call(h.Table, "CompleteRowDrag", rowHeight * 0.2);

        Proof.Note($"F11 unselected drag: moving={string.Join(",", requests.Single().Items)} " +
                   $"before={requests.Single().Before} " +
                   $"selection={string.Join(",", h.SelectedKeys())}");
        CollectionAssert.AreEqual(new object[] { h[5] }, requests.Single().Items.ToArray());
        Assert.AreSame(h[0], requests.Single().Before);
        CollectionAssert.AreEqual(new[] { "k0", "k1" }, h.SelectedKeys(), "selection untouched");
    });

    /// <summary>
    /// The flag is the only owner of the gesture. It is off by default, because a reorder is a
    /// domain request and means something only where the host owns an order; and subscribing to
    /// <c>ReorderRequested</c> no longer turns it on behind the flag's back, which used to make
    /// an event subscription load-bearing behaviour that nothing in the API announced.
    /// </summary>
    [TestMethod]
    public Task F11_RowDragIsRefusedWithoutTheHostsOptIn() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(6, height: 300);

        Assert.IsFalse((bool)Proof.Call(h.Table, "CanBeginRowDrag", h[2])!, "off by default");

        h.Table.ReorderRequested += (_, _) => { };
        Assert.IsFalse(
            (bool)Proof.Call(h.Table, "CanBeginRowDrag", h[2])!,
            "a handler alone does not turn it on");

        h.Table.CanReorder = true;
        Proof.Note("F11 with the opt-in: " + (bool)Proof.Call(h.Table, "CanBeginRowDrag", h[2])!);
        Assert.IsTrue((bool)Proof.Call(h.Table, "CanBeginRowDrag", h[2])!);
    });

    /// <summary>
    /// Section 16: the drag is offered only while the view shows the row order. Sorted by another
    /// column, a boundary between two rows is a place in that sort, so the table withholds the drag
    /// and a drag from a row is section 14's sweep instead. Sorted by the row-order column either
    /// way, the drag is offered and the request is reported in row order, so a host that applies it
    /// in that order puts the packet where it was dropped.
    /// </summary>
    [TestMethod]
    public Task F11_TheDragFollowsTheRowOrderColumnThroughTheSort() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(
            8,
            configure: t =>
            {
                // Column a defines the row order: ascending by key, which is the source order.
                // Column b sorts the other way round, so its order is not the row order.
                t.Columns[0].DefinesRowOrder = true;
                t.Schema<Row>()
                    .Sort(t.Columns[0], row => row.Key)
                    .Sort(t.Columns[1], row => -row.Rank);
                t.CanReorder = true;
            },
            height: 400);

        List<ReorderRequestedEventArgs> requests = new();
        h.Table.ReorderRequested += (_, e) => requests.Add(e);
        ListView list = h.HostedList();
        double rowHeight = ((FrameworkElement)list.ContainerFromItem(h[0])).ActualHeight;

        Assert.IsTrue(
            (bool)Proof.Call(h.Table, "CanBeginRowDrag", h[2])!, "unsorted: the view is the row order");

        ColumnLayout unsorted = h.Table.Layout;
        h.Table.Layout = unsorted with { SortColumnId = "b" };
        h.Table.UpdateLayout();
        Assert.IsFalse(
            (bool)Proof.Call(h.Table, "CanBeginRowDrag", h[2])!,
            "sorted by another column: the drag is withheld");
        Gesture.Press(h, row: h[2], item: h[2], originY: rowHeight * 5.5);
        Assert.AreEqual(
            "Marquee",
            Proof.Call(h.Table, "GestureAtThreshold")!.ToString(),
            "and a drag from a row sweeps instead");
        Proof.Call(h.Table, "CancelGesture");

        h.Table.Layout = unsorted with { SortColumnId = "a" };
        h.Table.UpdateLayout();
        Assert.IsTrue(
            (bool)Proof.Call(h.Table, "CanBeginRowDrag", h[2])!,
            "sorted by the row-order column upward: offered");

        h.Table.Layout =
            unsorted with { SortColumnId = "a", SortDirection = SortDirection.Descending };
        h.Table.UpdateLayout();
        Assert.IsTrue(
            (bool)Proof.Call(h.Table, "CanBeginRowDrag", h[2])!, "and downward: offered");
        CollectionAssert.AreEqual(
            new[] { "k7", "k6", "k5", "k4", "k3", "k2", "k1", "k0" },
            list.Items.Cast<Row>().Select(r => r.Key).ToArray(),
            "the view runs opposite to the row order");

        // Drop k5 and k4, selected and so moving as one packet, between k7 and k6: the boundary
        // before view index 1.
        h.Table.Selection = new(new object[] { h[4], h[5] }, h[5]);
        Gesture.Press(h, row: h[5], item: h[5], originY: rowHeight * 2.5);
        Proof.Call(h.Table, "BeginRowDrag", list, h[5]);
        Proof.Call(h.Table, "CompleteRowDrag", rowHeight * 1.2);

        ReorderRequestedEventArgs request = requests.Single();
        Proof.Note($"F11 descending request: moving={string.Join(",", request.Items)} " +
                   $"before={request.Before}");
        CollectionAssert.AreEqual(
            new object[] { h[4], h[5] },
            request.Items.ToArray(),
            "the packet is reported in row order, not in the order it stands on screen");
        Assert.AreSame(
            h[7], request.Before, "and goes before the row above the boundary, in row order");

        // Applied in row order, the packet lands exactly where it was dropped once the view is
        // read downward again: k7, k5, k4, k6.
        List<Row> order = h.Rows.ToList();
        order.Remove(h[4]);
        order.Remove(h[5]);
        order.InsertRange(order.IndexOf(h[7]), new[] { h[4], h[5] });
        CollectionAssert.AreEqual(
            new[] { "k0", "k1", "k2", "k3", "k6", "k4", "k5", "k7" },
            order.Select(r => r.Key).ToArray());

        // A drop right beside the block, on the side that is next in row order, changes nothing:
        // the boundary before view index 2 is between k6 and k5, and k5 already follows k6.
        h.Table.Selection = new(new object[] { h[5] }, h[5]);
        Gesture.Press(h, row: h[5], item: h[5], originY: rowHeight * 2.5);
        Proof.Call(h.Table, "BeginRowDrag", list, h[5]);
        Proof.Call(h.Table, "CompleteRowDrag", rowHeight * 1.6);
        Assert.AreEqual(1, requests.Count, "a placement that keeps the order raises nothing");
    });

    /// <summary>
    /// A sort that stops showing the row order ends a live drag even when it leaves every row
    /// where it was. The drop would be refused, so a drag that can end in nothing must not keep
    /// its marker up until the release.
    /// </summary>
    [TestMethod]
    public Task F11_ASortThatHidesTheRowOrderEndsALiveDragEvenWhenNothingMoves() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(
                6,
                configure: t =>
                {
                    // Both columns sort ascending by key, which is the source order, so sorting by
                    // b moves no row; only a is the row order.
                    Schema<Row> schema = t.Schema<Row>();
                    foreach (Column column in t.Columns)
                    {
                        schema.Sort(column, row => row.Key);
                    }

                    t.Columns[0].DefinesRowOrder = true;
                    t.CanReorder = true;
                },
                height: 400);
            h.Table.ReorderRequested += (_, _) => { };
            ListView list = h.HostedList();
            FrameworkElement marker = InsertionMarker(h.Table);
            double rowHeight = ((FrameworkElement)list.ContainerFromItem(h[0])).ActualHeight;

            Gesture.Press(h, row: h[2], item: h[2], originY: rowHeight * 2.5);
            Proof.Call(h.Table, "BeginRowDrag", list, h[2]);
            Assert.AreEqual(Visibility.Visible, marker.Visibility, "the drag is live");

            h.Table.Layout = h.Table.Layout with { SortColumnId = "b" };
            CollectionAssert.AreEqual(
                new[] { "k0", "k1", "k2", "k3", "k4", "k5" },
                list.Items.Cast<Row>().Select(r => r.Key).ToArray(),
                "the sort moved nothing");
            Proof.Note("F11 after a sort that hides the row order: gesture=" +
                       Proof.Field<object>(h.Table, "_gesture"));
            Assert.AreEqual(
                "None", Proof.Field<object>(h.Table, "_gesture").ToString(), "and still ended the drag");
            Assert.AreEqual(Visibility.Collapsed, marker.Visibility);
        });

    /// <summary>
    /// Section 9: hiding the sorted column clears the sort, and section 18 refuses a restored sort
    /// on a hidden column. The header is the only place a sort shows or is changed, so a sort by a
    /// hidden column would withhold section 16's drag with nothing on screen to explain it.
    /// </summary>
    [TestMethod]
    public Task F11_HidingTheSortedColumnClearsTheSortAndOffersTheDragAgain() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(
                6,
                configure: t =>
                {
                    t.Columns[0].DefinesRowOrder = true;
                    t.Schema<Row>()
                        .Sort(t.Columns[0], row => row.Key)
                        .Sort(t.Columns[1], row => -row.Rank);
                    t.CanReorder = true;
                },
                height: 400);
            h.Table.ReorderRequested += (_, _) => { };
            List<LayoutChange> changes = new();
            h.Table.LayoutChanged += (_, kind) => changes.Add(kind);
            ListView list = h.HostedList();

            ColumnLayout unsorted = h.Table.Layout;
            h.Table.Layout = unsorted with { SortColumnId = "b" };
            Assert.IsFalse(
                (bool)Proof.Call(h.Table, "CanBeginRowDrag", h[2])!, "sorted by b: withheld");
            Assert.AreEqual("k5", ((Row)list.Items[0]).Key, "and the view runs by b");

            SetVisibility(h.Table, "b", false);

            LayoutChange change = changes.Single();
            Proof.Note($"F11 after hiding the sorted column: kind={change} " +
                       $"sort={h.Table.Layout.SortColumnId ?? "none"} " +
                       $"first row={((Row)list.Items[0]).Key}");
            Assert.AreEqual(LayoutChange.Visibility, change);
            Assert.IsNull(h.Table.Layout.SortColumnId, "the one change leaves no sort behind");
            Assert.AreEqual("k0", ((Row)list.Items[0]).Key, "the view is back in natural order");
            Assert.IsTrue(
                (bool)Proof.Call(h.Table, "CanBeginRowDrag", h[2])!, "and the drag is offered again");

            // A restored layout that hides the column it sorts by keeps the visibility and drops
            // the sort, the same rule from the other direction.
            h.Table.Layout = unsorted with
            {
                SortColumnId = "b",
                Visibility = new Dictionary<string, bool> { ["b"] = false },
            };
            Assert.IsNull(h.Table.Layout.SortColumnId, "a sort on a hidden column is refused");
            Assert.AreEqual("k0", ((Row)list.Items[0]).Key);
        });

    /// <summary>
    /// Section 5.3: selection belongs to the row, not to its container. Rows selected, scrolled far
    /// enough out of the list to lose their containers, and scrolled back are selected still, in
    /// the table's model and on the containers the list gives them again.
    /// </summary>
    [TestMethod]
    public Task F11_SelectedRowsScrolledOutOfTheListComeBackSelected() => TestHost.RunAsync(async () =>
    {
        SelectionHarness h = await SelectionHarness.LoadAsync(200, height: 220);
        ListView list = h.HostedList();
        ScrollViewer scroller = (ScrollViewer)Proof.Call(h.Table, "InnerScrollViewer")!;

        h.Table.Selection = new(new object[] { h[2], h[3], h[4], h[5], h[6] }, h[2]);
        CollectionAssert.AreEqual(new[] { "k2", "k3", "k4", "k5", "k6" }, h.ContainerSelectedKeys());

        scroller.ChangeView(null, scroller.ScrollableHeight, null, disableAnimation: true);
        h.Table.UpdateLayout();
        Proof.Note($"F11 scrolled to {scroller.VerticalOffset:0} of {scroller.ScrollableHeight:0}; " +
                   $"container for k2 = {(list.ContainerFromItem(h[2]) is null ? "gone" : "kept")}");
        Assert.IsNull(list.ContainerFromItem(h[2]), "the selected rows left the realized range");
        CollectionAssert.AreEqual(
            new[] { "k2", "k3", "k4", "k5", "k6" }, h.SelectedKeys(), "the model kept them");

        scroller.ChangeView(null, 0, null, disableAnimation: true);
        h.Table.UpdateLayout();
        CollectionAssert.AreEqual(new[] { "k2", "k3", "k4", "k5", "k6" }, h.SelectedKeys());
        CollectionAssert.AreEqual(
            new[] { "k2", "k3", "k4", "k5", "k6" },
            h.ContainerSelectedKeys(),
            "and the containers they were given again show it");
        Assert.IsTrue(((ListViewItem)list.ContainerFromItem(h[4])).IsSelected);
    });

    // ================================================================ F12 row activation

    /// <summary>
    /// What an activation raises: the row acted on, the packet standing at the time, and nothing at
    /// all for a row the host marked non-interactive. A double-click and Enter share this contract;
    /// only the Enter path can be reached without a pointer.
    /// </summary>
    [TestMethod]
    public Task F12_ActivationRaisesItemInvokedWithTheRowAndThePacket() =>
        TestHost.RunAsync(async () =>
        {
            SelectionHarness h = await SelectionHarness.LoadAsync(
                8, t => t.Schema<Row>().CanInteract(row => row.Interactive), height: 400);

            List<ItemInvokedEventArgs> invoked = new();
            h.Table.ItemInvoked += (_, e) => invoked.Add(e);

            h.Table.Selection = new(new object[] { h[2], h[3], h[4] }, h[3]);
            Assert.IsTrue(h.InvokeCurrent());

            Proof.Note($"F12 invoked item={invoked.Single().Item} " +
                       $"packet={string.Join(",", invoked.Single().SelectedItems)}");
            Assert.AreSame(h[3], invoked.Single().Item);
            CollectionAssert.AreEqual(
                new object[] { h[2], h[3], h[4] }, invoked.Single().SelectedItems.ToArray());

            // The hit test both activation paths use to resolve their row, over a realized
            // container and over the passive cells panel inside it.
            ListView list = h.HostedList();
            DependencyObject container = (DependencyObject)list.ContainerFromItem(h[3]);
            DependencyObject cells = Proof.Descendant<CellsPanel>(container)!;
            Proof.Note($"F12 hit test: container={h.HitTest(container)} cells={h.HitTest(cells)}");
            Assert.AreEqual("Row", h.HitTest(container));
            Assert.AreEqual("Row", h.HitTest(cells));

            // A non-interactive row is refused by the same gate both activation paths use.
            invoked.Clear();
            h.Table.Selection = new(new object[] { h[5] }, h[5]);
            h[5].Interactive = false;

            bool activated = h.InvokeCurrent();
            Proof.Note($"F12 activating a non-interactive current row: " +
                       $"handled={activated} events={invoked.Count}");
            Assert.IsFalse(activated);
            Assert.AreEqual(0, invoked.Count);
        });

    // ================================================================ F13 presentations

    [TestMethod]
    public Task F13_LoadingEmptyAndNoResultsEachShowTheirOwnContent() => TestHost.RunAsync(async () =>
    {
        Table table = Downloads.Build();
        table.LoadingContent = "LOADING";
        table.EmptyContent = "EMPTY";
        table.NoResultsContent = "NO RESULTS";
        table.Placeholder = Placeholder.Loading;
        table.ItemsSource = new ObservableCollection<Download>();

        await TableHarness.LoadAsync(table);
        table.UpdateLayout();

        ContentPresenter layer = Proof.Descendants<ContentPresenter>(table)
            .Single(p => p.Content is string s
                && s is "LOADING" or "EMPTY" or "NO RESULTS");

        Proof.Note($"F13 Placeholder=Loading -> '{layer.Content}' {layer.Visibility}");
        Assert.AreEqual("LOADING", layer.Content);
        Assert.AreEqual(Visibility.Visible, layer.Visibility);

        table.Placeholder = Placeholder.Empty;
        Proof.Note($"F13 Placeholder=Empty -> '{layer.Content}' {layer.Visibility}");
        Assert.AreEqual("EMPTY", layer.Content);

        table.Placeholder = Placeholder.NoResults;
        Proof.Note($"F13 Placeholder=NoResults -> '{layer.Content}' {layer.Visibility}");
        Assert.AreEqual("NO RESULTS", layer.Content);

        table.ItemsSource = Downloads.Rows();
        table.UpdateLayout();
        await Task.Delay(60);
        Proof.Note($"F13 with rows -> content={layer.Content ?? "(null)"} {layer.Visibility}");
        Assert.AreEqual(Visibility.Collapsed, layer.Visibility, "rows win over every presentation");
    });

    // ================================================================ helpers

    private static (string, Visibility, string) Glyph(Header.Cell cell)
    {
        FontIcon icon = Proof.Descendant<FontIcon>(cell)
            ?? throw new AssertFailedException("The header cell realized no sort glyph.");
        return (
            icon.Visibility == Visibility.Visible ? icon.Glyph : string.Empty,
            icon.Visibility,
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetItemStatus(cell) ?? string.Empty);
    }

    /// <summary>The id of the column this realized header cell shows. The property is internal.</summary>
    private static string ColumnId(Header.Cell cell) =>
        ((Column)cell.GetType()
            .GetProperty("Column", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(cell)!).Id!;

    private static bool ActivateSort(Header.Strip strip, Header.Cell cell) =>
        (bool)Proof.Call(strip, "ActivateSortFrom", cell)!;

    /// <summary>The internal <c>ResolvedColumn</c>, which the test assembly cannot name.</summary>
    private static object Column(Table table, string id) => TableHarness.ResolvedColumn(table, id);

    private static bool MoveColumn(Table table, object column, int boundary) =>
        (bool)Proof.Call(table, "MoveColumnTo", column, boundary, null)!;

    private static void SetVisibility(Table table, string id, bool visible) =>
        Proof.Call(table, "SetColumnVisibility", Column(table, id), visible);

    private static FrameworkElement Marker(Header.Strip strip) =>
        Proof.Field<FrameworkElement>(strip, "_marker");

    private static FrameworkElement Overlay(Table table) =>
        Proof.Field<FrameworkElement>(table, "_marqueeOverlay");

    private static FrameworkElement InsertionMarker(Table table) =>
        Proof.Field<FrameworkElement>(table, "_rowInsertionMarker");

    /// <summary>
    /// The menu the strip builds for this column — the very call <c>ShowMenu</c> makes. Reading the
    /// flyout's own items is exact; reading the popup's visual tree depends on realization timing.
    /// </summary>
    private static MenuFlyout MenuFor(Table table, string? columnId)
    {
        Type type = typeof(Table).Assembly.GetType("Syno.TableView.Header.Menu")!;
        return (MenuFlyout)type
            .GetMethod("Create", System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, new[] { table, columnId is null ? null : Column(table, columnId) })!;
    }

    private static void CloseMenu(Table table)
    {
        foreach (Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(table.XamlRoot))
        {
            popup.IsOpen = false;
        }
    }

    /// <summary>
    /// Show the column's menu on its header and invoke one item through that item's own automation
    /// peer, which is what raises its <c>Click</c>.
    /// </summary>
    private static async Task InvokeMenuCommand(
        Table table, Header.Cell at, string columnId, string command)
    {
        MenuFlyout menu = MenuFor(table, columnId);
        MenuFlyoutItemBase item = FindItem(menu, command);
        Assert.IsTrue(item.IsEnabled, $"'{command}' is enabled");

        menu.ShowAt(at);
        await Task.Delay(120);
        Proof.InvokeMenuItem(item);
        CloseMenu(table);
        await Task.Delay(60);
    }

    private static MenuFlyoutItemBase FindItem(MenuFlyout menu, string label)
    {
        foreach (MenuFlyoutItemBase item in menu.Items)
        {
            if (Proof.Label(item) == label)
            {
                return item;
            }

            if (item is MenuFlyoutSubItem sub)
            {
                foreach (MenuFlyoutItemBase child in sub.Items)
                {
                    if (Proof.Label(child) == label)
                    {
                        return child;
                    }
                }
            }
        }

        throw new AssertFailedException($"The menu has no item '{label}'.");
    }

    /// <summary>Sets the arbiter's press state exactly as <c>OnRowsPointerPressed</c> sets it.</summary>
    private static class Gesture
    {
        internal static void Press(
            SelectionHarness h, Row row, object item, double originY = 0, bool ctrl = false,
            bool shift = false)
        {
            _ = row;
            Arm(h, item, new Point(10, originY), ctrl, shift);
        }

        internal static void PressEmpty(SelectionHarness h, Point origin) =>
            Arm(h, null, origin, false, false);

        private static void Arm(
            SelectionHarness h, object? item, Point origin, bool ctrl, bool shift)
        {
            Proof.Call(h.Table, "SyncSelectionPolicy");
            Proof.SetField(h.Table, "_gesture", Proof.Parse(h.Table, "_gesture", "Pressed"));
            Proof.SetField(h.Table, "_gesturePointerId", 1u);
            Proof.SetField(h.Table, "_gestureOrigin", origin);
            Proof.SetField(h.Table, "_gestureItem", item);
            Proof.SetField(h.Table, "_gestureCtrl", ctrl);
            Proof.SetField(h.Table, "_gestureShift", shift);
            Proof.SetField(h.Table, "_gestureSelection", h.Table.Selection.Items);

            bool couldDrag = item is not null
                && (bool)Proof.Call(h.Table, "CanBeginRowDrag", item)!;
            Proof.SetField(h.Table, "_gestureCouldDrag", couldDrag);

            // The control's own press step, over the fields set above. Never a copy of its rule: a
            // harness that can agree with itself while disagreeing with the control is a suite that
            // stays green through a regression.
            Proof.Call(h.Table, "ApplyPress");
        }
    }
}
