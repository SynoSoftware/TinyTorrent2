using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

namespace Synapse_Sample;

/// <summary>
/// Round 3: independent re-check of round 2's method-critical points.
/// V1 visual-state instantiation, V2 reset instance identity, V3 content inset
/// without an ItemContainerStyle, V4 measure counting with construction counters,
/// V5 leaves three ListViews on screen for an out-of-process UIA client.
/// </summary>
public sealed partial class Round3Page : Page
{
    private readonly StringBuilder _log = new();
    private double _scale = 1.0;
    private bool _finished;
    private static readonly Color Grey = Color.FromArgb(255, 128, 128, 128);
    private static readonly string Out = System.IO.Path.Combine(AppContext.BaseDirectory, "round3-results.txt");
    private static readonly string Ready = System.IO.Path.Combine(AppContext.BaseDirectory, "round3-uia-ready.txt");

    public Round3Page() { InitializeComponent(); Loaded += OnLoaded; }

    private void W(string s) => _log.AppendLine(s);
    private void Section(string s) { W(""); W("========================================================"); W(s); W("========================================================"); }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try { File.Delete(Ready); } catch { }
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(420);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { W("WATCHDOG"); Finish(); };
        timer.Start();

        _scale = XamlRoot?.RasterizationScale ?? 1.0;
        RequestedTheme = ElementTheme.Light;
        W($"round3 started {DateTime.Now:O}  RasterizationScale={_scale}");
        SizeWindow();
        await Settle(500);

        await G("V1", V1_StatesInstantiation);
        await G("V2", V2_ResetIdentity);
        await G("V3", V3_InsetNoStyle);
        await G("V4", V4_MeasureCounters);
        await G("V6", V6_AccentBar);
        await G("V5", V5_UiaSetup);

        timer.Stop();
        Finish();
    }

    private async Task G(string n, Func<Task> b)
    { try { await b(); } catch (Exception ex) { W($"*** {n} THREW {ex.GetType().Name}: {ex.Message}"); W(ex.StackTrace ?? ""); } }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        W($"round3 finished {DateTime.Now:O}");
        try { File.WriteAllText(Out, _log.ToString()); } catch { }
        Application.Current.Exit();
    }

    private void SizeWindow()
    {
        try
        {
            var w = MainWindow.Instance;
            if (w is null) return;
            w.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(940 * _scale), (int)(700 * _scale)));
            w.Activate();
        }
        catch (Exception ex) { W("resize failed " + ex.Message); }
    }

    private async Task Settle(int ms = 250)
    { Host.UpdateLayout(); await Task.Delay(ms); Host.UpdateLayout(); await Task.Delay(60); }

    private async Task ShowAsync(UIElement el, int ms = 450)
    {
        Host.Children.Clear();
        Row3Panel.Live.Clear();
        if (el is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Left; fe.VerticalAlignment = VerticalAlignment.Top; }
        Host.Children.Add(el);
        await Settle(ms);
    }

    private static List<ProbeItem> MakeItems(int n)
    { var l = new List<ProbeItem>(n); for (int i = 0; i < n; i++) l.Add(new ProbeItem { Index = i, Name = "item " + i }); return l; }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        { var c = VisualTreeHelper.GetChild(root, i); if (c is T t) return t; var r = FindDescendant<T>(c); if (r is not null) return r; }
        return null;
    }

    private static void FindAll<T>(DependencyObject root, List<T> into) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++) { var c = VisualTreeHelper.GetChild(root, i); if (c is T t) into.Add(t); FindAll(c, into); }
    }

    private static Point Origin(UIElement c, UIElement a) => c.TransformToVisual(a).TransformPoint(new Point(0, 0));

    // ------------------------------------------------------------ capture
    private sealed class Shot { public int W, H; public byte[] Px = Array.Empty<byte>(); public bool Ok; public string Err = ""; public override string ToString() => Ok ? $"{W}x{H}" : "FAILED " + Err; }

    private async Task<Shot> CaptureAsync(UIElement el)
    {
        var s = new Shot();
        try
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(el);
            var buf = await rtb.GetPixelsAsync();
            var bytes = new byte[buf.Length];
            DataReader.FromBuffer(buf).ReadBytes(bytes);
            s.W = rtb.PixelWidth; s.H = rtb.PixelHeight; s.Px = bytes; s.Ok = true;
        }
        catch (Exception ex) { s.Err = ex.GetType().Name + ": " + ex.Message; }
        return s;
    }

    private static bool IsBlue(byte b, byte g, byte r) => b > 180 && g < 80 && r < 80;

    private double FirstBlueX(Shot s, double y0, double h, double w = 140)
    {
        if (!s.Ok) return double.NaN;
        for (double dx = 0; dx <= w; dx += 0.5)
        {
            int px = (int)Math.Round(dx * _scale);
            if (px < 0 || px >= s.W) continue;
            for (int py = (int)Math.Round(y0 * _scale); py < (int)Math.Round((y0 + h) * _scale); py++)
            {
                if (py < 0 || py >= s.H) continue;
                int i = (py * s.W + px) * 4;
                if (IsBlue(s.Px[i], s.Px[i + 1], s.Px[i + 2])) return dx;
            }
        }
        return double.NaN;
    }

    private string RawScan(Shot s, double dipY, double x0, double x1)
    {
        if (!s.Ok) return "capture-failed";
        int y = (int)Math.Round(dipY * _scale);
        var sb = new StringBuilder(); string last = "";
        for (double x = x0; x <= x1; x += 1)
        {
            int px = (int)Math.Round(x * _scale);
            if (px < 0 || px >= s.W || y < 0 || y >= s.H) continue;
            int i = (y * s.W + px) * 4;
            var c = $"({s.Px[i]},{s.Px[i + 1]},{s.Px[i + 2]})";
            if (c != last) { sb.Append($" x{x:0}={c}"); last = c; }
        }
        return sb.Length == 0 ? "(nothing)" : sb.ToString();
    }

    private static string GroupsOf(ListViewItem c)
    {
        if (VisualTreeHelper.GetChildrenCount(c) == 0) return "no template root";
        if (VisualTreeHelper.GetChild(c, 0) is not FrameworkElement root) return "root not FE";
        var g = VisualStateManager.GetVisualStateGroups(root);
        if (g is null || g.Count == 0) return "0 groups";
        var parts = new List<string>();
        foreach (var grp in g)
            parts.Add($"'{grp.Name}' current='{grp.CurrentState?.Name ?? "(null)"}' states({grp.States.Count}): {string.Join(", ", grp.States.Select(x => x.Name))}");
        return $"{g.Count} group(s) -> " + string.Join(" | ", parts);
    }

    // ================================================= V1
    private async Task V1_StatesInstantiation()
    {
        Section("V1  Are the container visual state groups really absent without an ItemContainerStyle,\n" +
                "    or merely not instantiated yet? Read, force with GoToState, read again.");

        foreach (var (label, mode, style) in new (string, ListViewSelectionMode, string?)[]
        {
            ("SelectionMode=None, NO ItemContainerStyle", ListViewSelectionMode.None, null),
            ("SelectionMode=Multiple, NO ItemContainerStyle", ListViewSelectionMode.Multiple, null),
            ("SelectionMode=Multiple, TightItemStyle3", ListViewSelectionMode.Multiple, "TightItemStyle3"),
        })
        {
            W("");
            W($"---- {label} ----");
            var lv = new ListView
            {
                SelectionMode = mode,
                ItemsSource = MakeItems(6),
                ItemTemplate = (DataTemplate)Resources["MarkerTemplate"],
                ItemContainerStyle = style is null ? null : (Style)Resources[style],
                Background = new SolidColorBrush(Grey),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Width = 800,
                Height = 300,
            };
            await ShowAsync(lv);
            if (lv.ContainerFromIndex(0) is not ListViewItem c) { W("  container 0 not realized"); continue; }
            var root = VisualTreeHelper.GetChildrenCount(c) > 0 ? VisualTreeHelper.GetChild(c, 0) as FrameworkElement : null;
            W($"  template root = {root?.GetType().FullName ?? "(none)"}");
            W($"  BEFORE any GoToState: {GroupsOf(c)}");

            bool r1 = VisualStateManager.GoToState(c, "Selected", false);
            await Settle(150);
            W($"  GoToState(container, Selected) = {r1}");
            W($"  AFTER GoToState on the CONTAINER: {GroupsOf(c)}");

            if (root is Control rc)
            {
                bool r2 = VisualStateManager.GoToState(rc, "Selected", false);
                W($"  GoToState(templateRoot, Selected) = {r2}");
            }
            await Settle(150);
            W($"  AFTER GoToState on the TEMPLATE ROOT: {GroupsOf(c)}");

            if (mode != ListViewSelectionMode.None)
            {
                var src = (List<ProbeItem>)lv.ItemsSource;
                if (mode == ListViewSelectionMode.Multiple) lv.SelectedItems.Add(src[0]); else lv.SelectedIndex = 0;
            }
            else { c.IsSelected = true; }
            await Settle(250);
            W($"  AFTER a real selection change: {GroupsOf(c)}");

            if (lv.ContainerFromIndex(1) is ListViewItem c1)
                W($"  container 1, never touched: {GroupsOf(c1)}");
        }
    }

    // ================================================= V2
    private sealed class R3Item
    {
        public int Id { get; init; }
        public int Gen { get; init; }
        public override string ToString() => $"#{Id} gen{Gen}";
    }

    private async Task V2_ResetIdentity()
    {
        Section("V2  Collection Reset under Multiple. Instance identity asserted, not assumed.");

        var coll = new ObservableCollection<R3Item>();
        for (int i = 0; i < 12; i++) coll.Add(new R3Item { Id = i, Gen = 1 });
        var gen1 = coll.ToList();

        var lv = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            ItemsSource = coll,
            ItemTemplate = (DataTemplate)Resources["TextTemplate"],
            ItemContainerStyle = (Style)Resources["TightItemStyle3"],
            Background = new SolidColorBrush(Grey),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Width = 800,
            Height = 380,
        };
        int ev = 0, add = 0, rem = 0;
        lv.SelectionChanged += (_, e2) => { ev++; add += e2.AddedItems.Count; rem += e2.RemovedItems.Count; };
        await ShowAsync(lv, 500);

        lv.SelectedItems.Add(gen1[2]); lv.SelectedItems.Add(gen1[5]); lv.SelectedItems.Add(gen1[9]);
        await Settle(250);
        (lv.ContainerFromIndex(5) as ListViewItem)?.Focus(FocusState.Keyboard);
        await Settle(250);
        W($"  BEFORE reset: Items={lv.Items.Count} SelectedItems={lv.SelectedItems.Count} SelectedIndex={lv.SelectedIndex}");
        W($"    selected ids = {string.Join(",", lv.SelectedItems.Select(o => ((R3Item)o).Id))}");
        W($"    focus = {Desc(FocusManager.GetFocusedElement(XamlRoot), lv)}");

        ev = 0; add = 0; rem = 0;
        coll.Clear();
        await Settle(250);
        W($"  right after Clear(): SelectedItems={lv.SelectedItems.Count} SelectedIndex={lv.SelectedIndex} Items={lv.Items.Count}");
        var gen2 = new List<R3Item>();
        for (int i = 0; i < 12; i++) { var it = new R3Item { Id = i, Gen = 2 }; gen2.Add(it); coll.Add(it); }
        await Settle(500);

        int sameRef = 0;
        for (int i = 0; i < 12; i++) if (ReferenceEquals(gen1[i], gen2[i])) sameRef++;
        W("");
        W($"  IDENTITY ASSERTION: ReferenceEquals(gen1[i], gen2[i]) true for {sameRef} of 12; 0 means the re-added items are genuinely new objects");
        W($"    gen1[2] hash={gen1[2].GetHashCode()}  gen2[2] hash={gen2[2].GetHashCode()}");
        W($"    any shared reference between gen1 and gen2 = {gen1.Any(x => gen2.Any(y => ReferenceEquals(x, y)))}");
        W($"  AFTER reset with NEW instances: Items={lv.Items.Count} SelectedItems={lv.SelectedItems.Count} SelectedIndex={lv.SelectedIndex}");
        W($"    selected = {(lv.SelectedItems.Count == 0 ? "(none)" : string.Join(",", lv.SelectedItems.Select(o => o.ToString())))}");
        W($"    SelectionChanged during reset: {ev} events, {add} added, {rem} removed");
        W($"    focus = {Desc(FocusManager.GetFocusedElement(XamlRoot), lv)}");
        for (int i = 0; i < Math.Min(12, lv.Items.Count); i++)
            if (lv.ContainerFromIndex(i) is ListViewItem cc && (cc.FocusState != FocusState.Unfocused || cc.IsSelected))
                W($"    container[{i}] IsSelected={cc.IsSelected} FocusState={cc.FocusState}");

        W("");
        W("  ---- second reset: Clear() then re-add the SAME references, same order ----");
        lv.SelectedItems.Clear(); await Settle(200);
        lv.SelectedItems.Add(gen2[2]); lv.SelectedItems.Add(gen2[5]); lv.SelectedItems.Add(gen2[9]);
        await Settle(300);
        W($"    before: SelectedItems={lv.SelectedItems.Count} ids={string.Join(",", lv.SelectedItems.Select(o => ((R3Item)o).Id))}");
        ev = 0; add = 0; rem = 0;
        var same = coll.ToList();
        coll.Clear(); await Settle(200);
        foreach (var it in same) coll.Add(it);
        await Settle(500);
        int stillSame = 0;
        for (int i = 0; i < 12; i++) if (ReferenceEquals(same[i], coll[i])) stillSame++;
        W($"    IDENTITY ASSERTION: re-added item is the same reference for {stillSame} of 12; 12 means identity was preserved");
        W($"    after: SelectedItems={lv.SelectedItems.Count} SelectedIndex={lv.SelectedIndex}");
        W($"      selected = {(lv.SelectedItems.Count == 0 ? "(none)" : string.Join(",", lv.SelectedItems.Select(o => o.ToString())))}");
        W($"      SelectionChanged: {ev} events, {add} added, {rem} removed");
        W($"      focus = {Desc(FocusManager.GetFocusedElement(XamlRoot), lv)}");

        W("");
        W("  ---- re-applying the selection after the reset ----");
        foreach (var id in new[] { 2, 5, 9 }) { var it = coll.FirstOrDefault(x => x.Id == id); if (it is not null) lv.SelectedItems.Add(it); }
        await Settle(300);
        W($"    after re-apply: SelectedItems={lv.SelectedItems.Count} ids={string.Join(",", lv.SelectedItems.Select(o => ((R3Item)o).Id))}");
    }

    private string Desc(object? o, ListView lv)
    {
        if (o is null) return "null";
        if (o is ListViewItem i) return $"ListViewItem index {lv.IndexFromContainer(i)} content={i.Content} FocusState={i.FocusState} IsSelected={i.IsSelected}";
        return o.GetType().Name;
    }

    // ================================================= V3
    private async Task V3_InsetNoStyle()
    {
        Section("V3  Content inset measured by pixels with NO ItemContainerStyle at all, so the\n" +
                "    result does not depend on round 2's TightItemStyle.");

        foreach (var (label, mode, cb) in new (string, ListViewSelectionMode, bool?)[]
        {
            ("None", ListViewSelectionMode.None, null),
            ("Single", ListViewSelectionMode.Single, null),
            ("Multiple, check box default", ListViewSelectionMode.Multiple, null),
            ("Multiple, IsMultiSelectCheckBoxEnabled=False", ListViewSelectionMode.Multiple, false),
        })
        {
            var lv = new ListView
            {
                SelectionMode = mode,
                ItemsSource = MakeItems(6),
                ItemTemplate = (DataTemplate)Resources["MarkerTemplate"],
                Background = new SolidColorBrush(Grey),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Width = 800,
                Height = 300,
            };
            if (cb.HasValue) lv.IsMultiSelectCheckBoxEnabled = cb.Value;
            await ShowAsync(lv);
            if (lv.ContainerFromIndex(0) is not ListViewItem c) { W($"  {label}: not realized"); continue; }
            var shot = await CaptureAsync(lv);
            var o = Origin(c, lv);
            double h = c.ActualHeight;
            W("");
            W($"  ---- {label} ---- no ItemContainerStyle; container Padding={c.Padding} size={c.ActualWidth:0.##}x{h:0.##} origin=({o.X:0.##},{o.Y:0.##})");
            W($"    capture={shot}  first BLUE x = {F(FirstBlueX(shot, o.Y + 1, h - 2))} DIP from the ListView left edge");
            W($"    raw scan y=mid x0..48 ={RawScan(shot, o.Y + h / 2, 0, 48)}");

            if (mode == ListViewSelectionMode.None) c.IsSelected = true;
            else if (mode == ListViewSelectionMode.Single) lv.SelectedIndex = 0;
            else lv.SelectedItems.Add(((List<ProbeItem>)lv.ItemsSource)[0]);
            await Settle(350);
            var shot2 = await CaptureAsync(lv);
            W($"    SELECTED: first BLUE x = {F(FirstBlueX(shot2, o.Y + 1, h - 2))} DIP   groups={GroupsOf(c)}");
            W($"    SELECTED raw scan y=mid x0..48 ={RawScan(shot2, o.Y + h / 2, 0, 48)}");
        }
    }

    private static string F(double d) => double.IsNaN(d) ? "not found" : d.ToString("0.#");

    // ================================================= V4
    private async Task V4_MeasureCounters()
    {
        Section("V4  Offset change by InvalidateArrange alone. MeasureOverride counted per panel,\n" +
                "    plus a construction counter so a panel made during the change cannot hide a measure.");

        Row3Panel.SharedOffset = 0;
        Row3Panel.Constructed = 0;

        var lv = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            IsMultiSelectCheckBoxEnabled = false,
            ItemsSource = MakeItems(10000),
            ItemTemplate = (DataTemplate)Resources["RowTemplate"],
            ItemContainerStyle = (Style)Resources["TightItemStyle3"],
            ItemsPanel = (ItemsPanelTemplate)Resources["IspPanel3"],
            Background = new SolidColorBrush(Colors.White),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Width = 800,
            Height = 400,
        };
        ScrollViewer.SetHorizontalScrollMode(lv, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(lv, ScrollBarVisibility.Disabled);
        await ShowAsync(lv, 900);

        var sv = FindDescendant<ScrollViewer>(lv);
        W($"  realized containers = {lv.ItemsPanelRoot?.Children.Count ?? -1}  panel={lv.ItemsPanelRoot?.GetType().Name}");
        W($"  Row3Panel objects constructed so far = {Row3Panel.Constructed}");
        if (sv is not null) W($"  ExtentWidth={sv.ExtentWidth:0.##} ViewportWidth={sv.ViewportWidth:0.##} HOffset={sv.HorizontalOffset:0.##} ScrollableWidth={sv.ScrollableWidth:0.##}");

        var panels = new List<Row3Panel>();
        if (lv.ItemsPanelRoot is not null) FindAll(lv.ItemsPanelRoot, panels);
        int mBefore = panels.Sum(p => p.MeasureCount), aBefore = panels.Sum(p => p.ArrangeCount);
        int ctorBefore = Row3Panel.Constructed;
        W($"  BEFORE: panels={panels.Count} sum(MeasureCount)={mBefore} sum(ArrangeCount)={aBefore} constructed={ctorBefore}");

        Row3Panel.SharedOffset = 1200;
        foreach (var p in panels) p.InvalidateArrange();
        await Settle(450);

        int mAfter = panels.Sum(p => p.MeasureCount), aAfter = panels.Sum(p => p.ArrangeCount);
        var panelsNow = new List<Row3Panel>();
        if (lv.ItemsPanelRoot is not null) FindAll(lv.ItemsPanelRoot, panelsNow);
        int newPanels = panelsNow.Count(p => !panels.Contains(p));
        W($"  AFTER InvalidateArrange alone: sum(MeasureCount)={mAfter} delta={mAfter - mBefore}   sum(ArrangeCount)={aAfter} delta={aAfter - aBefore}");
        W($"    per-panel MeasureCount distinct values = {string.Join(",", panels.Select(p => p.MeasureCount).Distinct().OrderBy(x => x))}");
        W($"    panels constructed during the change = {Row3Panel.Constructed - ctorBefore}; live panels not in the before-list = {newPanels}");
        var c0 = lv.ContainerFromIndex(0) as ListViewItem;
        var r0 = c0 is null ? null : FindDescendant<Row3Panel>(c0);
        if (r0 is not null)
            W($"    row 0 arrange rects x = {string.Join(" ", r0.LastArrangeRects.Take(4).Select(r => r.X.ToString("0.#")))}");
        var shot = await CaptureAsync(lv);
        if (c0 is not null)
        {
            double y = Origin(c0, lv).Y + c0.ActualHeight / 2;
            W($"    pixel x=100 expect c6 BGR(192,0,144) = {Px(shot, 100, y)}");
            W($"    pixel x=700 expect c9 BGR(64,64,64)  = {Px(shot, 700, y)}");
        }
        if (sv is not null) W($"    after: HOffset={sv.HorizontalOffset:0.##} ExtentWidth={sv.ExtentWidth:0.##} ScrollableWidth={sv.ScrollableWidth:0.##}");

        W("");
        W("  --- scroll to the real end of the extent, offset still 1200 ---");
        double target = sv?.ScrollableHeight ?? 0;
        W($"    ScrollableHeight={target:0.##}; round 2 asked ChangeView for 100000 and labelled it the end");
        sv?.ChangeView(null, target, null, true);
        await Settle(900);
        var panels3 = new List<Row3Panel>();
        if (lv.ItemsPanelRoot is not null) FindAll(lv.ItemsPanelRoot, panels3);
        int fresh = panels3.Count(p => !panelsNow.Contains(p));
        W($"    VerticalOffset={sv?.VerticalOffset:0.##} realized={lv.ItemsPanelRoot?.Children.Count ?? -1}");
        W($"    live panels={panels3.Count}, not seen before the scroll={fresh}, total constructed={Row3Panel.Constructed}");
        if (panels3.Count > 0)
        {
            var p0 = panels3[0];
            W($"    first live panel arrange rects x = {string.Join(" ", p0.LastArrangeRects.Take(4).Select(r => r.X.ToString("0.#")))}");
            if (p0.Children.Count > 6)
                W($"    cell0 x-in-ListView={Origin(p0.Children[0], lv).X:0.##}  cell6 x-in-ListView={Origin(p0.Children[6], lv).X:0.##}");
        }
        var shot2 = await CaptureAsync(lv);
        W($"    pixel (100,200) = {Px(shot2, 100, 200)}  c6 BGR(192,0,144) means recycled rows carry the offset");
    }

    private string Px(Shot s, double dx, double dy)
    {
        if (!s.Ok) return "capture-failed";
        int x = (int)Math.Round(dx * _scale), y = (int)Math.Round(dy * _scale);
        if (x < 0 || y < 0 || x >= s.W || y >= s.H) return "out of range";
        int i = (y * s.W + x) * 4;
        return $"BGR({s.Px[i]},{s.Px[i + 1]},{s.Px[i + 2]})";
    }

    // ================================================= V6
    private async Task V6_AccentBar()
    {
        Section("V6  Selection chrome in the left gutter, with and without an ItemContainerStyle.\n" +
                "    Round 2 reported 'no accent selection bar is drawn at all' and that Multiple+False\n" +
                "    is byte-for-byte the same as Single. Both cases are measured here side by side.");

        foreach (var styleName in new string?[] { null, "TightItemStyle3" })
        {
            foreach (var (label, mode, cb) in new (string, ListViewSelectionMode, bool?)[]
            {
                ("Single", ListViewSelectionMode.Single, null),
                ("Multiple, check box False", ListViewSelectionMode.Multiple, false),
            })
            {
                var lv = new ListView
                {
                    SelectionMode = mode,
                    ItemsSource = MakeItems(6),
                    ItemTemplate = (DataTemplate)Resources["MarkerTemplate"],
                    ItemContainerStyle = styleName is null ? null : (Style)Resources[styleName],
                    Background = new SolidColorBrush(Grey),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    Width = 800,
                    Height = 300,
                };
                if (cb.HasValue) lv.IsMultiSelectCheckBoxEnabled = cb.Value;
                await ShowAsync(lv);
                if (lv.ContainerFromIndex(0) is not ListViewItem c) { W("  not realized"); continue; }
                if (mode == ListViewSelectionMode.Single) lv.SelectedIndex = 0;
                else lv.SelectedItems.Add(((List<ProbeItem>)lv.ItemsSource)[0]);
                await Settle(400);
                var shot = await CaptureAsync(lv);
                var o = Origin(c, lv);
                double h = c.ActualHeight;
                W("");
                W($"  ---- {label}, ItemContainerStyle={(styleName ?? "(none)")} ---- container {c.ActualWidth:0.##}x{h:0.##} Padding={c.Padding}");
                W($"    accent BGR(192,103,0)-like pixels in the left 20 DIP: {AccentRange(shot, o.Y + 1, h - 2, 20)}");
                W($"    selected row raw scan y=mid x0..20 ={RawScan(shot, o.Y + h / 2, 0, 20)}");
                W($"    unselected row 2 raw scan y=mid x0..20 ={RawScan(shot, o.Y + h * 2 + h / 2, 0, 20)}");
                W($"    groups = {GroupsOf(c)}");
            }
        }
    }

    private string AccentRange(Shot s, double y0, double hh, double w)
    {
        if (!s.Ok) return "capture-failed";
        double first = double.NaN, last = double.NaN;
        for (double dx = 0; dx <= w; dx += 0.5)
        {
            int px = (int)Math.Round(dx * _scale);
            if (px < 0 || px >= s.W) continue;
            bool hit = false;
            for (int py = (int)Math.Round(y0 * _scale); py < (int)Math.Round((y0 + hh) * _scale); py++)
            {
                if (py < 0 || py >= s.H) continue;
                int i = (py * s.W + px) * 4;
                byte b = s.Px[i], g = s.Px[i + 1], r = s.Px[i + 2];
                if (b > 140 && g > 55 && g < 175 && r < 95) { hit = true; break; }
            }
            if (hit) { if (double.IsNaN(first)) first = dx; last = dx; }
        }
        return double.IsNaN(first) ? "none" : $"x {first:0.#} .. {last:0.#} DIP";
    }

    // ================================================= V5
    private async Task V5_UiaSetup()
    {
        Section("V5  Three ListViews left on screen for an OUT-OF-PROCESS UIA client.\n" +
                "    LvA = custom container peer + custom ListView peer, round 2 configuration\n" +
                "    LvB = custom container peer, STOCK ListView peer, the decisive case\n" +
                "    LvC = stock ListView\n" +
                "    Round 2 only asked the in-process helpers.");

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        ListView Build(ListView lv, string id)
        {
            lv.SelectionMode = ListViewSelectionMode.Multiple;
            lv.IsMultiSelectCheckBoxEnabled = false;
            lv.ItemsSource = MakeItems(6);
            lv.ItemTemplate = (DataTemplate)Resources["MarkerTemplate"];
            lv.ItemContainerStyle = (Style)Resources["TightItemStyle3"];
            lv.Background = new SolidColorBrush(Grey);
            lv.BorderThickness = new Thickness(0);
            lv.Padding = new Thickness(0);
            lv.Width = 280; lv.Height = 300;
            AutomationProperties.SetAutomationId(lv, id);
            panel.Children.Add(lv);
            return lv;
        }

        var a = Build(new ProbeListView(), "LvA");
        var b = Build(new ProbeListViewStockPeer(), "LvB");
        var c = Build(new ListView(), "LvC");

        await ShowAsync(panel, 800);
        foreach (var lv in new[] { a, b, c })
            lv.SelectedItems.Add(((List<ProbeItem>)lv.ItemsSource)[0]);
        await Settle(500);

        foreach (var (lv, name) in new[] { (a, "LvA"), (b, "LvB"), (c, "LvC") })
        {
            var c0 = lv.ContainerFromIndex(0) as UIElement;
            W("");
            W($"  {name}: ListView type={lv.GetType().Name} container type={c0?.GetType().Name}");
            var cp = c0 is null ? null : FrameworkElementAutomationPeer.FromElement(c0);
            W($"    in-process FromElement(container) = {cp?.GetType().Name ?? "null"}  ClassName={(cp is null ? "-" : Try(() => cp.GetClassName()))}");
            var lp = FrameworkElementAutomationPeer.FromElement(lv);
            W($"    in-process FromElement(ListView)  = {lp?.GetType().Name ?? "null"}");
            W($"    SelectedItems={lv.SelectedItems.Count} container0.IsSelected={(c0 as ListViewItem)?.IsSelected}");
        }
        W($"  ProbeItemPeer instances created = {ProbeItemPeer.Created}");

        var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        W("");
        W($"  process id = {pid}. Waiting up to 120 s for the out-of-process UIA client.");
        try { File.WriteAllText(Ready, pid.ToString()); } catch (Exception ex) { W("  ready file failed: " + ex.Message); }

        for (int i = 0; i < 120; i++)
        {
            await Task.Delay(1000);
            if (File.Exists(Ready + ".done")) { W($"  client signalled done after {i + 1} s."); break; }
        }
        try { File.Delete(Ready); } catch { }
        try { File.Delete(Ready + ".done"); } catch { }
    }

    private static string Try(Func<string> f) { try { return f(); } catch (Exception ex) { return "threw " + ex.GetType().Name; } }
}

/// <summary>Custom containers and custom container peer, but the STOCK ListView peer.</summary>
public partial class ProbeListViewStockPeer : ListView
{
    protected override DependencyObject GetContainerForItemOverride() => new ProbeListViewItem();
    protected override bool IsItemItsOwnContainerOverride(object item) => item is ProbeListViewItem;
}

/// <summary>Round 3 row panel, with a construction counter.</summary>
public sealed class Row3Panel : Panel
{
    public const double ColumnWidth = 200;
    public static double SharedOffset;
    public static int Constructed;
    public static readonly List<Row3Panel> Live = new();

    public Size LastMeasureAvailable, LastMeasureReturned, LastArrangeFinal;
    public readonly List<Rect> LastArrangeRects = new();
    public int MeasureCount, ArrangeCount;

    public Row3Panel()
    {
        Constructed++;
        Loaded += (_, _) => { if (!Live.Contains(this)) Live.Add(this); };
        Unloaded += (_, _) => Live.Remove(this);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureCount++;
        LastMeasureAvailable = availableSize;
        double h = 0;
        foreach (var ch in Children) { ch.Measure(new Size(ColumnWidth, double.PositiveInfinity)); h = Math.Max(h, ch.DesiredSize.Height); }
        if (h <= 0) h = 28;
        LastMeasureReturned = new Size(ColumnWidth * Children.Count, h);
        return LastMeasureReturned;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        ArrangeCount++;
        LastArrangeFinal = finalSize;
        LastArrangeRects.Clear();
        for (int i = 0; i < Children.Count; i++)
        {
            var r = new Rect(i * ColumnWidth - SharedOffset, 0, ColumnWidth, finalSize.Height);
            Children[i].Arrange(r);
            LastArrangeRects.Add(r);
        }
        return finalSize;
    }
}
