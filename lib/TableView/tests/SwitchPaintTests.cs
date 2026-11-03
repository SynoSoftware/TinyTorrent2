using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace Syno.TableView.Tests;

/// <summary>
/// A row whose first cell is one solid color: its role in the change, and for a staying row its
/// own. A staying row's cell has a top and a bottom edge in two other shades of its color.
/// </summary>
public sealed class PaintRow
{
    internal PaintRow(int id) => Id = id;

    public int Id { get; }
    public int Rank { get; internal set; }
    public SolidColorBrush Brush { get; internal set; } = new(Microsoft.UI.Colors.Gray);
    public SolidColorBrush Top { get; internal set; } = new(Microsoft.UI.Colors.Gray);
    public SolidColorBrush Bottom { get; internal set; } = new(Microsoft.UI.Colors.Gray);
}

/// <summary>
/// Records how a 200-row table paints a source change, frame by frame as the compositor presents
/// the window, so the list's composition animations are included. It asserts no result. For the
/// whole column it reports blank frames, the frames before the rows settle, and any whole-list
/// Reset. For each row on screen both before and after the change it reports whether the row kept
/// its container, whether the list was told to remove it, and whether it dimmed in any frame: a row
/// that slides keeps every pixel of its color, and one removed and inserted again fades.
/// </summary>
[TestClass]
public class SwitchPaintTests
{
    private static readonly Windows.UI.Color LeavingColor = new() { A = 255, R = 220, G = 40, B = 30 };

    /// <summary>Arriving rows share green and blue, and red tells them apart like staying rows.</summary>
    private const int ArrivingGreen = 170, ArrivingBlue = 40;

    /// <summary>Staying rows share green and blue, and red tells them apart, for the 40 top rows.</summary>
    private const int StayingGreen = 60, StayingBlue = 220, Shades = 40;

    /// <summary>A staying row's top edge changes its green and its bottom edge its blue.</summary>
    private const byte TopGreen = 150, BottomBlue = 120;

    private static Windows.UI.Color StayingColor(int id) =>
        new() { A = 255, R = (byte)(10 + 5 * (id % Shades)), G = StayingGreen, B = StayingBlue };

    private static Windows.UI.Color ArrivingColor(int id) =>
        new() { A = 255, R = (byte)(10 + 5 * (id % Shades)), G = ArrivingGreen, B = ArrivingBlue };

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// <paramref name="Before"/> and <paramref name="After"/> list row ids in source order;
    /// <paramref name="Change"/> takes the source from one to the other. With
    /// <paramref name="ScrollTo"/>, the list is scrolled to put that row at its top and a row on
    /// screen takes keyboard focus first.
    /// </summary>
    private sealed record Scenario(string Name, int[] Before, int[] After, Func<Context, Task> Change,
        int Selected = -1, bool Sorted = false, int RecordMs = 1000, int ScrollTo = 0);

    private sealed class Context(Table table, ObservableCollection<PaintRow> source, Dictionary<int, PaintRow> rows)
    {
        internal Table Table => table;
        internal ObservableCollection<PaintRow> Source => source;
        internal Dictionary<int, PaintRow> Rows => rows;

        internal int IndexOf(int id) => source.Select(row => row.Id).ToList().IndexOf(id);

        /// <summary>Remove what <paramref name="after"/> lacks, highest first, then insert what it adds, lowest first.</summary>
        internal void Notify(int[] after)
        {
            for (int index = source.Count - 1; index >= 0; index--)
            {
                if (!after.Contains(source[index].Id))
                {
                    source.RemoveAt(index);
                }
            }
            for (int index = 0; index < after.Length; index++)
            {
                if (index >= source.Count || source[index].Id != after[index])
                {
                    source.Insert(index, rows[after[index]]);
                }
            }
        }
    }

    private static int[] Ids(int from, int to, Func<int, bool>? keep = null) =>
        Enumerable.Range(from, to - from).Where(id => keep?.Invoke(id) != false).ToArray();

    private static readonly Scenario[] Scenarios =
    {
        // A refresh: every fourth row leaves, the selected one among them, and the rest slide up.
        new("churn-selected-leaves", Ids(0, 200), Ids(0, 200, id => id % 4 != 3),
            c => Assign(c, Ids(0, 200, id => id % 4 != 3)), Selected: 3),
        // A different source: no row stays.
        new("switch", Ids(0, 200), Ids(1000, 1200), c => Assign(c, Ids(1000, 1200))),
        new("switch-selected-leaves", Ids(0, 200), Ids(1000, 1200), c => Assign(c, Ids(1000, 1200)), Selected: 3),
        // The same changes as notifications: three on-screen rows leave, then three arrive.
        new("notify-leave", Ids(0, 200), Ids(0, 200, id => id is not (3 or 7 or 11)),
            c => Notify(c, Ids(0, 200, id => id is not (3 or 7 or 11)))),
        new("notify-arrive", Ids(0, 200, id => id is not (3 or 7 or 11)), Ids(0, 200),
            c => Notify(c, Ids(0, 200))),
        // A same-identity Replace of an on-screen row.
        new("notify-replace", Ids(0, 200), Ids(0, 200), c =>
        {
            PaintRow old = c.Rows[5];
            c.Source[c.IndexOf(5)] = new PaintRow(5) { Rank = old.Rank, Brush = old.Brush, Top = old.Top, Bottom = old.Bottom };
            return Task.CompletedTask;
        }),
    };

    /// <summary>
    /// Source changes scrolled halfway down, with a focused row; see <see cref="ScrolledSwitchPaint"/>.
    /// A different source starts at its first row, and a list that shares rows with the one before
    /// keeps its place.
    /// </summary>
    private static readonly Scenario[] Scrolled =
    {
        new("switch-scrolled", Ids(0, 200), Ids(1000, 1200), c => Assign(c, Ids(1000, 1200)), Selected: 103,
            ScrollTo: 100),
        new("churn-scrolled", Ids(0, 200), Ids(0, 200, id => id % 4 != 3),
            c => Assign(c, Ids(0, 200, id => id % 4 != 3)), Selected: 101, ScrollTo: 100),
        // A host that restores a saved position after assigning a different source, as Library does.
        new("switch-scrolled-restore", Ids(0, 200), Ids(1000, 1200), c =>
        {
            Task assigned = Assign(c, Ids(1000, 1200));
            c.Table.ScrollTo(1500);
            return assigned;
        }, Selected: 103, ScrollTo: 100),
    };

    /// <summary>Changes that reorder rows on screen; see <see cref="ReorderPaint"/>.</summary>
    private static readonly Scenario[] Reorders =
    {
        // A host Move of an on-screen row.
        new("notify-move", Ids(0, 200), MoveIds(Ids(0, 200), 2, 8), c =>
        {
            c.Source.Move(2, 8);
            return Task.CompletedTask;
        }),
        // Under a live sort, inside the settle interval: one row leaves and one arrives at once,
        // and two rows whose values swapped keep their places until the settle, then trade them.
        new("sorted-interval-then-settle", Ids(0, 200), Ids(0, 200, id => id != 5).Append(1000).ToArray(), c =>
        {
            c.Source.RemoveAt(c.IndexOf(5));
            c.Source.Add(c.Rows[1000]);
            (c.Rows[2].Rank, c.Rows[9].Rank) = (c.Rows[9].Rank, c.Rows[2].Rank);
            c.Table.RefreshView();
            return Task.CompletedTask;
        }, Sorted: true, RecordMs: 4500),
    };

    private static int[] MoveIds(int[] ids, int from, int to)
    {
        List<int> moved = ids.ToList();
        int id = moved[from];
        moved.RemoveAt(from);
        moved.Insert(to, id);
        return moved.ToArray();
    }

    private static Task Assign(Context context, int[] after)
    {
        context.Table.ItemsSource = after.Select(id => context.Rows[id]).ToList();
        return Task.CompletedTask;
    }

    private static Task Notify(Context context, int[] after)
    {
        context.Notify(after);
        return Task.CompletedTask;
    }

    [TestMethod]
    [TestCategory("Measurement")]
    public Task SourceSwitchPaint() => RecordAsync(Scenarios);

    /// <summary>The scenarios in <see cref="Scrolled"/>; a test of its own to stay inside the host's time limit.</summary>
    [TestMethod]
    [TestCategory("Measurement")]
    public Task ScrolledSwitchPaint() => RecordAsync(Scrolled);

    /// <summary>The scenarios in <see cref="Reorders"/>; a test of its own to stay inside the host's time limit.</summary>
    [TestMethod]
    [TestCategory("Measurement")]
    public Task ReorderPaint() => RecordAsync(Reorders);

    private Task RecordAsync(Scenario[] scenarios) =>
        TestHost.RunAsync(async () =>
        {
            Window window = TestHost.Window;
            double scale = TestHost.RootPanel.XamlRoot.RasterizationScale;
            window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(820 * scale), (int)(620 * scale)));
            await Task.Delay(500);
            using Recorder recorder = new(WinRT.Interop.WindowNative.GetWindowHandle(window));
            await Task.Delay(500);
            foreach (Scenario scenario in scenarios)
            {
                TestContext.WriteLine(await RunAsync(scenario, recorder, scale));
            }
            if (recorder.Error is { } error)
            {
                TestContext.WriteLine("capture error: " + error);
            }
        });

    private static async Task<string> RunAsync(Scenario scenario, Recorder recorder, double scale)
    {
        Dictionary<int, PaintRow> rows = scenario.Before.Concat(scenario.After).Distinct()
            .ToDictionary(id => id, id => new PaintRow(id) { Rank = id * 10 });
        if (rows.ContainsKey(1000) && scenario.Sorted)
        {
            // The arriving row sorts between rows 7 and 8.
            rows[1000].Rank = 75;
        }
        foreach (PaintRow row in rows.Values)
        {
            bool was = scenario.Before.Contains(row.Id), stays = scenario.After.Contains(row.Id);
            row.Brush = new SolidColorBrush(was && stays ? StayingColor(row.Id) : was ? LeavingColor : ArrivingColor(row.Id));
            row.Top = was && stays ? new SolidColorBrush(StayingColor(row.Id) with { G = TopGreen }) : row.Brush;
            row.Bottom = was && stays ? new SolidColorBrush(StayingColor(row.Id) with { B = BottomBlue }) : row.Brush;
        }

        Table table = new() { Width = 800, Height = 560 };
        Column role = new()
        {
            Id = "role", DisplayName = "Role", Width = 120,
            CellTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>"
                + "<Border Background='{Binding Brush}' Width='100' Height='30'><Grid>"
                + "<Border Height='5' VerticalAlignment='Top' Background='{Binding Top}'/>"
                + "<Border Height='5' VerticalAlignment='Bottom' Background='{Binding Bottom}'/>"
                + "</Grid></Border></DataTemplate>"),
        };
        table.Columns.Add(role);
        foreach (string id in new[] { "b", "c", "d", "e", "f" })
        {
            table.Columns.Add(TestData.Column(id, 120));
        }
        Schema<PaintRow> schema = table.Schema<PaintRow>().Key(row => row.Id);
        if (scenario.Sorted)
        {
            schema.SortKey(role, row => row.Rank);
        }
        ObservableCollection<PaintRow> source = new(scenario.Before.Select(id => rows[id]));
        table.ItemsSource = source;
        Grid host = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.White), RequestedTheme = ElementTheme.Light };
        host.Children.Add(table);
        TaskCompletionSource loaded = new();
        table.Loaded += (_, _) => loaded.TrySetResult();
        TestHost.RootPanel.Children.Add(host);
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (scenario.Sorted)
        {
            table.SortInterval = TimeSpan.FromSeconds(3);
            table.Sort = new(role);
        }
        // Past the entrance animation and the list's one-second fast-mutation window.
        await Task.Delay(1500);
        if (scenario.Selected >= 0)
        {
            table.Selection = new Selection(new object[] { rows[scenario.Selected] });
            await Task.Delay(300);
        }

        ListView surface = Descendants(table).OfType<ListView>().First();
        Rect bounds = surface.TransformToVisual(null).TransformBounds(new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
        FrameworkElement first = (FrameworkElement)surface.ContainerFromIndex(0);
        Border cell = Descendants(first).OfType<Border>()
            .First(border => border.DataContext is PaintRow row && ReferenceEquals(border.Background, row.Brush));
        Rect cellBounds = cell.TransformToVisual(null).TransformBounds(new Rect(0, 0, cell.ActualWidth, cell.ActualHeight));
        // Down the middle of every row's role cell, from the first row to the foot of the list.
        recorder.SetColumn(cellBounds.X + cellBounds.Width / 2, cellBounds.Y, bounds.Bottom - cellBounds.Y, scale);
        int rowPixels = (int)Math.Round(first.ActualHeight * scale);
        ScrollViewer scroller = Descendants(surface).OfType<ScrollViewer>().First();
        if (scenario.ScrollTo > 0)
        {
            scroller.ChangeView(null, scenario.ScrollTo * first.ActualHeight, null, disableAnimation: true);
            await Task.Delay(500);
            ((Control)surface.ContainerFromIndex(scenario.ScrollTo + 2)).Focus(FocusState.Keyboard);
            await Task.Delay(200);
        }
        double offset = scroller.VerticalOffset;
        Dictionary<int, ListViewItem> before = OnScreen(surface);

        Dictionary<string, int> notices = new();
        IObservableVector<object> view = (IObservableVector<object>)surface.ItemsSource;
        // The view as the list was told it: the row each removal took.
        List<int> told = view.Cast<PaintRow>().Select(row => row.Id).ToList();
        HashSet<int> removed = new();
        void OnVector(IObservableVector<object> sender, IVectorChangedEventArgs e)
        {
            string kind = e.CollectionChange.ToString();
            notices[kind] = notices.GetValueOrDefault(kind) + 1;
            int index = (int)e.Index;
            switch (e.CollectionChange)
            {
                case CollectionChange.ItemInserted:
                    told.Insert(index, ((PaintRow)sender[index]).Id);
                    break;
                case CollectionChange.ItemRemoved:
                    removed.Add(told[index]);
                    told.RemoveAt(index);
                    break;
                case CollectionChange.ItemChanged:
                    told[index] = ((PaintRow)sender[index]).Id;
                    break;
            }
        }
        view.VectorChanged += OnVector;

        recorder.Start();
        await Task.Delay(150);
        double start = Now();
        Context context = new(table, source, rows);
        await scenario.Change(context);
        await Task.Delay(scenario.RecordMs);
        (Shot? pre, List<Shot> shots) = recorder.Stop(start);
        view.VectorChanged -= OnVector;
        Dictionary<int, ListViewItem> after = OnScreen(surface);
        string state = $"offset {offset:0} -> {scroller.VerticalOffset:0}, selected "
            + $"[{string.Join(",", table.Selection.Items.Cast<PaintRow>().Select(row => row.Id))}], focus "
            + DescribeFocus(table, surface) + ", " + DescribeAutomation(surface);
        TestHost.RootPanel.Children.Remove(host);
        await Task.Delay(200);

        return $"{scenario.Name}: {Describe(pre, shots, rowPixels)}; "
            + $"{DescribeRows(before, after, removed, pre, shots)}; {DescribeMoves(pre, shots)}; "
            + $"{state}; notices "
            + (notices.Count == 0 ? "none" : string.Join(", ", notices.Select(n => $"{n.Key} {n.Value}")));
    }

    /// <summary>The focused row, or the focused element's type when no row has focus.</summary>
    private static string DescribeFocus(Table table, ListView surface) =>
        Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(table.XamlRoot) switch
        {
            ListViewItem container when surface.ItemFromContainer(container) is PaintRow row => $"row {row.Id}",
            null => "none",
            object element => element.GetType().Name,
        };

    /// <summary>How many item peers the list's automation peer has, and the rows of the first three.</summary>
    private static string DescribeAutomation(ListView surface)
    {
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(surface);
        IList<Microsoft.UI.Xaml.Automation.Peers.AutomationPeer> children = peer.GetChildren();
        IEnumerable<string> first = children.Take(3).Select(child =>
            child is Microsoft.UI.Xaml.Automation.Peers.ItemAutomationPeer { Item: PaintRow row } ? row.Id.ToString() : "?");
        return $"automation children {children.Count} [{string.Join(",", first)}]";
    }

    /// <summary>The container of each row whose container lies wholly inside the list's viewport.</summary>
    private static Dictionary<int, ListViewItem> OnScreen(ListView surface)
    {
        Dictionary<int, ListViewItem> shown = new();
        foreach (UIElement child in surface.ItemsPanelRoot.Children)
        {
            if (child is not ListViewItem container || surface.ItemFromContainer(container) is not PaintRow row)
            {
                continue;
            }
            Rect at = container.TransformToVisual(surface)
                .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            if (at.Top >= 0 && at.Bottom <= surface.ActualHeight)
            {
                shown[row.Id] = container;
            }
        }
        return shown;
    }

    /// <summary>
    /// Every row on screen both before and after: the ones that kept their container, the ones the
    /// list was told to remove, and the ones whose own color fell below half in some frame, into
    /// colors no row has. A fade blends the row with the background; a row that slides over
    /// another covers it with its own color, which is not a dimmed frame.
    /// </summary>
    private static string DescribeRows(Dictionary<int, ListViewItem> before, Dictionary<int, ListViewItem> after,
        HashSet<int> removed, Shot? pre, List<Shot> shots)
    {
        List<int> staying = before.Keys.Where(after.ContainsKey).Order().ToList();
        List<int> replaced = staying.Where(id => !ReferenceEquals(before[id], after[id])).ToList();
        List<int> reinserted = staying.Where(removed.Contains).ToList();
        List<int> dimmed = pre is null ? new() : staying.Where(id => id < Shades
            && pre.Rows.GetValueOrDefault(id) > 0
            && shots.Any(shot => shot.Rows.GetValueOrDefault(id) * 2 < pre.Rows[id]
                && shot.Other * 2 >= pre.Rows[id] - shot.Rows.GetValueOrDefault(id))).ToList();
        return $"rows on screen before and after {staying.Count}, new container {List(replaced)}, "
            + $"removed and inserted {List(reinserted)}, dimmed in a frame {List(dimmed)}";

        static string List(List<int> ids) => ids.Count == 0 ? "0" : $"{ids.Count} [{string.Join(",", ids)}]";
    }

    /// <summary>
    /// Every top row that moved in some frame: the top of its cell in each frame where it changed,
    /// how many frames showed it away from both its first and last places, and its largest step in
    /// one frame. A row that slides passes through places between; a row that jumps takes the whole
    /// distance in one frame. The place is read from the cell's edges, so a row that another covers
    /// still has one. The recorder keeps every presented frame, so recorded frames next to each other
    /// are screen frames next to each other. A frame where neither edge shows whole has no place: the
    /// move across it is divided by the frames it spans and marked *.
    /// </summary>
    private static string DescribeMoves(Shot? pre, List<Shot> shots)
    {
        if (pre is null || shots.Count == 0 || !pre.Tops.Keys.Any(pre.Bottoms.ContainsKey))
        {
            return "moved rows: no frames";
        }
        int edgePixels = pre.Tops.Values.Max(edge => edge.Count);
        int rowPixels = pre.Tops.Keys.Where(pre.Bottoms.ContainsKey).Max(id => pre.Bottoms[id].Last - pre.Tops[id].First + 1);
        List<string> moves = new();
        foreach (int id in pre.Rows.Keys.Order())
        {
            double from = pre.Place(id, edgePixels, rowPixels);
            List<(int Frame, double At, double Y)> path = shots
                .Select((shot, frame) => (Frame: frame, shot.At, Y: shot.Place(id, edgePixels, rowPixels)))
                .Where(point => !double.IsNaN(point.Y)).ToList();
            if (double.IsNaN(from) || path.All(point => Math.Abs(point.Y - from) < 2))
            {
                continue;
            }
            double to = path[^1].Y;
            int between = path.Count(point => Math.Abs(point.Y - from) > 1 && Math.Abs(point.Y - to) > 1);
            int unplaced = shots.Count - path.Count;
            double step = 0;
            double stepAt = 0;
            double previous = from;
            int previousFrame = -1;
            List<string> changes = new();
            foreach (var (frame, at, y) in path)
            {
                int frames = frame - previousFrame;
                if (Math.Abs(y - previous) > 0.5)
                {
                    changes.Add(frames == 1 ? $"+{at:0}:{y:0}" : $"+{at:0}:{y:0}*{frames}");
                    if (Math.Abs(y - previous) / frames > step)
                    {
                        step = Math.Abs(y - previous) / frames;
                        stepAt = at;
                    }
                }
                previous = y;
                previousFrame = frame;
            }
            string gaps = unplaced == 0 ? "" : $", {unplaced} frames with neither edge whole (*n: a move across n frames)";
            moves.Add($"row {id} {from:0}->{to:0}px, {between} frames between, "
                + $"largest step {step:0}px at +{stepAt:0}ms{gaps} [{string.Join(" ", changes)}]");
        }
        return moves.Count == 0 ? "moved rows 0" : $"moved rows {moves.Count}: {string.Join("; ", moves)}";
    }

    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (DependencyObject below in Descendants(child))
            {
                yield return below;
            }
        }
    }

    /// <summary>
    /// Counts the column's pixels by role in each frame. A blank frame shows more background than
    /// the frames before and after the change by at least one row: rows missing, as in a flash or
    /// a cleared list. The rows have settled at the first frame equal to the last one.
    /// </summary>
    private static string Describe(Shot? pre, List<Shot> shots, int rowPixels)
    {
        if (pre is null || shots.Count == 0)
        {
            return $"no pixels (frame before: {(pre is null ? "none" : "yes")}, frames after: {shots.Count})";
        }
        Shot last = shots[^1];
        int baseline = Math.Max(pre.Background, last.Background);
        List<Shot> blanks = shots.Where(shot => shot.Background > baseline + rowPixels).ToList();
        int blank = blanks.Count;
        int settled = shots.FindIndex(shot => shot.SameAs(last));
        List<Shot> changing = shots.Take(settled + 1).Where(shot => !shot.SameAs(pre)).ToList();
        // Long recordings keep only the frames that differ from the one before.
        List<Shot> path = shots.Take(Math.Min(shots.Count, settled + 2))
            .Where((shot, index) => index == 0 || !shot.SameAs(shots[index - 1])).ToList();
        string steps = string.Join(" | ", path
            .Select(shot => $"+{shot.At:0}ms s{shot.Staying} l{shot.Leaving} a{shot.Arriving} bg{shot.Background} o{shot.Other}"
                + (shot.ArrivingOrder.Count == 0 ? "" : $" a-top {string.Join(",", shot.ArrivingOrder.Take(3))}")));
        string colours = pre.Staying + pre.Leaving + pre.Arriving > 0 ? ""
            : $"no role colour in the column, which shows {pre.Commonest}; ";
        return colours + $"before s{pre.Staying} l{pre.Leaving} a{pre.Arriving} bg{pre.Background}; "
            + $"frames {shots.Count}, changing frames until settled {changing.Count}, blank frames {blank}, "
            + $"most background {(shots.Max(shot => shot.Background) - baseline) / (double)rowPixels:0.0} rows over baseline; "
            + $"row {rowPixels}px; path {steps}"
            + string.Concat(blanks.Select(shot => "; " + DescribeBlank(pre, shot, baseline)));
    }

    /// <summary>
    /// What a blank frame lacks, row by row: the background over the baseline, and each top row
    /// that shows fewer of its pixels than before the change. A row that slides over another
    /// covers it, so the two together show one row's pixels less.
    /// </summary>
    private static string DescribeBlank(Shot pre, Shot shot, int baseline)
    {
        IEnumerable<string> lacking = pre.Rows.Keys.Order()
            .Where(id => shot.Rows.GetValueOrDefault(id) < pre.Rows[id] - 2)
            .Select(id => $"{id}:{shot.Rows.GetValueOrDefault(id)}/{pre.Rows[id]}@{(shot.Rows.ContainsKey(id) ? shot.Centre(id) : double.NaN):0}");
        return $"blank +{shot.At:0}ms background {shot.Background - baseline}px over baseline, l{shot.Leaving} a{shot.Arriving} o{shot.Other}, "
            + $"rows short [{string.Join(" ", lacking)}]";
    }

    private sealed class Shot
    {
        public double At;
        public int Staying, Leaving, Arriving, Background, Other;
        public Dictionary<int, int> Rows = new();

        /// <summary>The arriving rows' shades (id modulo <see cref="Shades"/>), top first.</summary>
        public List<int> ArrivingOrder = new();

        /// <summary>The sum of each top row's pixel positions down the column, for <see cref="Centre"/>.</summary>
        public Dictionary<int, long> Positions = new();
        public string Commonest = "";

        /// <summary>The middle of <paramref name="id"/>'s pixels down the column.</summary>
        public double Centre(int id) => Rows.TryGetValue(id, out int count) ? Positions[id] / (double)count : double.NaN;

        /// <summary>Each staying row's top edge: its first pixel and how many pixels show.</summary>
        public Dictionary<int, (int First, int Count)> Tops = new();

        /// <summary>Each staying row's bottom edge: its last pixel and how many pixels show.</summary>
        public Dictionary<int, (int Last, int Count)> Bottoms = new();

        /// <summary>
        /// The first pixel of <paramref name="id"/>'s cell, read from an edge that shows whole, or
        /// NaN when neither does. A row that covers this one hides one edge and the middle; both
        /// edges are hidden only when rows lie almost exactly on top of each other.
        /// </summary>
        public double Place(int id, int edgePixels, int rowPixels) =>
            Tops.TryGetValue(id, out var top) && top.Count >= edgePixels - 2 ? top.First
            : Bottoms.TryGetValue(id, out var bottom) && bottom.Count >= edgePixels - 2 ? bottom.Last - (rowPixels - 1)
            : double.NaN;

        public bool SameAs(Shot other) =>
            Math.Abs(Staying - other.Staying) + Math.Abs(Leaving - other.Leaving)
            + Math.Abs(Arriving - other.Arriving) + Math.Abs(Background - other.Background) <= 4
            && Rows.All(row => Math.Abs(row.Value - other.Rows.GetValueOrDefault(row.Key)) <= 2)
            && ArrivingOrder.Take(3).SequenceEqual(other.ArrivingOrder.Take(3));

        public static Shot Classify(byte[] column, double at)
        {
            Shot shot = new() { At = at };
            for (int i = 0; i + 3 < column.Length; i += 4)
            {
                int b = column[i], g = column[i + 1], r = column[i + 2];
                int y = i / 4;
                bool top = Math.Abs(g - TopGreen) <= 3 && Math.Abs(b - StayingBlue) <= 3;
                bool bottom = Math.Abs(g - StayingGreen) <= 3 && Math.Abs(b - BottomBlue) <= 3;
                // A row between pixels blends its edge with its middle; that pixel is still the row's.
                bool own = Math.Abs(b - StayingBlue) <= 3 && g >= StayingGreen - 3 && g <= TopGreen + 3
                    || Math.Abs(g - StayingGreen) <= 3 && b >= BottomBlue - 3 && b <= StayingBlue + 3;
                if (own
                    && (r - 10 + 2) / 5 is int shade && shade >= 0 && shade < Shades && Math.Abs(r - 10 - 5 * shade) <= 2)
                {
                    shot.Staying++;
                    shot.Rows[shade] = shot.Rows.GetValueOrDefault(shade) + 1;
                    shot.Positions[shade] = shot.Positions.GetValueOrDefault(shade) + y;
                    if (top)
                    {
                        shot.Tops[shade] = shot.Tops.TryGetValue(shade, out var edge) ? (edge.First, edge.Count + 1) : (y, 1);
                    }
                    if (bottom)
                    {
                        shot.Bottoms[shade] = (y, shot.Bottoms.GetValueOrDefault(shade).Count + 1);
                    }
                }
                else if (Near(LeavingColor))
                {
                    shot.Leaving++;
                }
                else if (Math.Abs(g - ArrivingGreen) + Math.Abs(b - ArrivingBlue) < 30)
                {
                    shot.Arriving++;
                    if ((r - 10 + 2) / 5 is int arriving && arriving >= 0 && arriving < Shades
                        && Math.Abs(r - 10 - 5 * arriving) <= 2 && !shot.ArrivingOrder.Contains(arriving))
                    {
                        shot.ArrivingOrder.Add(arriving);
                    }
                }
                else if (r > 235 && g > 235 && b > 235)
                {
                    shot.Background++;
                }
                else
                {
                    shot.Other++;
                }

                bool Near(Windows.UI.Color c) => Math.Abs(c.R - r) + Math.Abs(c.G - g) + Math.Abs(c.B - b) < 30;
            }
            shot.Commonest = string.Join(", ", Enumerable.Range(0, column.Length / 4)
                .GroupBy(i => (R: column[i * 4 + 2], G: column[i * 4 + 1], B: column[i * 4]))
                .OrderByDescending(group => group.Count()).Take(4)
                .Select(group => $"rgb({group.Key.R},{group.Key.G},{group.Key.B}) x{group.Count()}"));
            return shot;
        }
    }

    /// <summary>Keeps one pixel column of each frame the compositor presents for the window.</summary>
    private sealed class Recorder : IDisposable
    {
        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr levels, uint count, uint sdk, out IntPtr device, out int level, out IntPtr context);

        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private const int ExtendedFrameBounds = 9;

        private readonly IntPtr _hwnd;
        private readonly IDirect3DDevice _device;
        private readonly Direct3D11CaptureFramePool _pool;
        private readonly GraphicsCaptureSession _session;
        private readonly object _gate = new();
        private Windows.Graphics.SizeInt32 _size;
        private (int X, int Y, int Height)? _column;
        private (byte[] Pixels, int Width, int Height)? _latest;
        private Shot? _pre;
        private List<Shot>? _shots;

        /// <summary>The first failure to read a frame, which arrives on the capture thread.</summary>
        public string? Error { get; private set; }

        public Recorder(IntPtr hwnd)
        {
            _hwnd = hwnd;
            const uint BgraSupport = 0x20, SdkVersion = 7;
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, BgraSupport, IntPtr.Zero, 0,
                SdkVersion, out IntPtr d3d, out _, out IntPtr context));
            Guid dxgi = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in dxgi, out IntPtr dxgiDevice));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr inspectable));
            _device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
            Marshal.Release(dxgiDevice);
            Marshal.Release(context);
            Marshal.Release(d3d);

            GraphicsCaptureItem item = GraphicsCaptureItem.TryCreateFromWindowId(new Windows.UI.WindowId((ulong)hwnd.ToInt64()))
                ?? throw new InvalidOperationException("The window cannot be captured.");
            _size = item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            _pool.FrameArrived += OnFrame;
            _session = _pool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;
            _session.StartCapture();
        }

        /// <summary>A column at window DIP <paramref name="x"/>, from <paramref name="top"/> down <paramref name="height"/>.</summary>
        public void SetColumn(double x, double top, double height, double scale)
        {
            // Capture frames include the window's border; the client area starts at this offset.
            POINT client = default;
            ClientToScreen(_hwnd, ref client);
            DwmGetWindowAttribute(_hwnd, ExtendedFrameBounds, out RECT frame, Marshal.SizeOf<RECT>());
            lock (_gate)
            {
                _column = (client.X - frame.Left + (int)Math.Round(x * scale),
                    client.Y - frame.Top + (int)Math.Round(top * scale), (int)Math.Round(height * scale));
            }
        }

        /// <summary>
        /// Start keeping frames. The frame before the change is the latest one presented, because the
        /// compositor presents a frame only when the window's content changes.
        /// </summary>
        public void Start()
        {
            lock (_gate)
            {
                _pre = _latest is { } latest && _column is { } column
                    ? Shot.Classify(Strip(latest.Pixels, latest.Width, latest.Height, column), 0) : null;
                _shots = new();
            }
        }

        /// <summary>The frame before the change, and every frame after <paramref name="start"/>, timed from it.</summary>
        public (Shot? Pre, List<Shot> Shots) Stop(double start)
        {
            lock (_gate)
            {
                List<Shot> shots = _shots ?? new();
                _shots = null;
                Shot? pre = shots.LastOrDefault(shot => shot.At < start) ?? _pre;
                List<Shot> after = shots.Where(shot => shot.At >= start).ToList();
                foreach (Shot shot in after)
                {
                    shot.At -= start;
                }
                return (pre, after);
            }
        }

        private void OnFrame(Direct3D11CaptureFramePool pool, object args)
        {
            try
            {
                using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }
                double at = Now();
                if (frame.ContentSize.Width != _size.Width || frame.ContentSize.Height != _size.Height)
                {
                    _size = frame.ContentSize;
                    pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
                    return;
                }
                using SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface).AsTask().GetAwaiter().GetResult();
                int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
                Windows.Storage.Streams.Buffer buffer = new((uint)(width * height * 4));
                bitmap.CopyToBuffer(buffer);
                byte[] pixels = buffer.ToArray();
                lock (_gate)
                {
                    _latest = (pixels, width, height);
                    if (_shots is not null && _column is { } column)
                    {
                        _shots.Add(Shot.Classify(Strip(pixels, width, height, column), at));
                    }
                }
            }
            catch (Exception error)
            {
                Error ??= error.Message;
            }
        }

        private static byte[] Strip(byte[] pixels, int width, int height, (int X, int Y, int Height) column)
        {
            byte[] strip = new byte[column.Height * 4];
            for (int y = 0; y < column.Height && column.Y + y < height && column.X < width; y++)
            {
                Array.Copy(pixels, ((column.Y + y) * width + column.X) * 4, strip, y * 4, 4);
            }
            return strip;
        }

        public void Dispose()
        {
            _session.Dispose();
            _pool.Dispose();
        }
    }
}
