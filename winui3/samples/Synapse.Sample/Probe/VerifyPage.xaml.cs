using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

namespace Synapse_Sample;

/// <summary>
/// Independent verification of the probe's Q1a and Q5 answers.
/// Q1a: forces an OPAQUE SelectedBackground so a drawn selection cannot hide in a 3.5% overlay.
/// Q5 : hooks PointerWheelChanged on EVERY realized row, not one chosen container.
/// </summary>
public sealed partial class VerifyPage : Page
{
    private readonly StringBuilder _log = new();
    private readonly List<string> _wheel = new();
    private double _scale = 1.0;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _finished;

    private static readonly Color Grey = Color.FromArgb(255, 128, 128, 128);

    public VerifyPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void W(string s) => _log.AppendLine(s);
    private void Section(string s) { W(""); W("=================================================================="); W(s); W("=================================================================="); }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(240);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { W("\n!!! WATCHDOG FIRED !!!"); Finish(); };
        timer.Start();

        _scale = XamlRoot?.RasterizationScale ?? 1.0;
        RequestedTheme = ElementTheme.Light;
        W($"verify started {DateTime.Now:O}   RasterizationScale={_scale}");

        SizeWindow();
        await Settle(500);
        ReportWindow();

        await Guarded("V1 opaque-brush selection", V1Async);
        await Guarded("V2 hook-every-row wheel", V2Async);
        await Guarded("V3 row cancels the scroll", V3Async);

        timer.Stop();
        Finish();
    }

    private async Task Guarded(string name, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex) { W($"\n*** {name} THREW: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        W($"\nverify finished {DateTime.Now:O}");
        var text = _log.ToString();
        foreach (var t in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "verify-results.txt"),
            Path.Combine(Path.GetTempPath(), "verify-results.txt"),
        })
        { try { File.WriteAllText(t, text); } catch { } }
        Application.Current.Exit();
    }

    private void SizeWindow()
    {
        try
        {
            var w = MainWindow.Instance;
            if (w is null) { W("MainWindow.Instance null"); return; }
            w.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)Math.Ceiling(940 * _scale), (int)Math.Ceiling(560 * _scale)));
            w.Activate();
            _hwnd = Win32Interop.GetWindowFromWindowId(w.AppWindow.Id);
            W($"HWND = 0x{_hwnd.ToInt64():X}");
        }
        catch (Exception ex) { W("resize failed: " + ex.Message); }
    }

    private void ReportWindow()
    {
        GetWindowRect(_hwnd, out var r);
        W($"window screen rect = ({r.Left},{r.Top})-({r.Right},{r.Bottom})");
        var o = new POINT { X = 0, Y = 0 };
        ClientToScreen(_hwnd, ref o);
        W($"client origin screen px = ({o.X},{o.Y})");
        W($"foreground HWND = 0x{GetForegroundWindow().ToInt64():X}");
        GetCursorPos(out var cur);
        W($"REAL cursor position = ({cur.X},{cur.Y})  WindowFromPoint there = {ClassOf(WindowFromPoint(cur))}");
        var mid = new POINT { X = o.X + 40, Y = o.Y + 200 };
        W($"WindowFromPoint(client+40,+200) = {ClassOf(WindowFromPoint(mid))}");
    }

    private async Task Settle(int ms = 250)
    {
        Host.UpdateLayout();
        await Task.Delay(ms);
        Host.UpdateLayout();
        await Task.Delay(60);
    }

    private async Task ShowAsync(UIElement el, int ms = 400)
    {
        Host.Children.Clear();
        RowPanel.Live.Clear();
        if (el is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Left; fe.VerticalAlignment = VerticalAlignment.Top; }
        Host.Children.Add(el);
        await Settle(ms);
    }

    private static List<ProbeItem> MakeItems(int n)
    {
        var l = new List<ProbeItem>(n);
        for (int i = 0; i < n; i++) l.Add(new ProbeItem { Index = i, Name = "item " + i });
        return l;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            var r = FindDescendant<T>(c);
            if (r is not null) return r;
        }
        return null;
    }

    private static void FindAll<T>(DependencyObject root, List<T> into) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) into.Add(t);
            FindAll(c, into);
        }
    }

    // ---------------------------------------------------------- pixel capture

    private sealed class Shot
    {
        public int W, H;
        public byte[] Px = Array.Empty<byte>();
        public bool Ok;
        public string Error = "";
        public override string ToString() => Ok ? $"{W}x{H}" : "FAILED " + Error;
    }

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
        catch (Exception ex) { s.Error = ex.GetType().Name + ": " + ex.Message; }
        return s;
    }

    private string Mean(Shot s, double x0, double y0, double w, double h)
    {
        if (!s.Ok) return "capture-failed";
        long b = 0, g = 0, r = 0; int n = 0;
        for (int y = (int)(y0 * _scale); y < (int)((y0 + h) * _scale); y++)
            for (int x = (int)(x0 * _scale); x < (int)((x0 + w) * _scale); x++)
            {
                if (x < 0 || y < 0 || x >= s.W || y >= s.H) continue;
                int i = (y * s.W + x) * 4;
                b += s.Px[i]; g += s.Px[i + 1]; r += s.Px[i + 2]; n++;
            }
        return n == 0 ? "no px" : $"mean BGR({(double)b / n:0.00},{(double)g / n:0.00},{(double)r / n:0.00}) over {n}px";
    }

    private static Point Origin(UIElement child, UIElement ancestor) =>
        child.TransformToVisual(ancestor).TransformPoint(new Point(0, 0));

    private static string StatesOf(Control c)
    {
        if (VisualTreeHelper.GetChildrenCount(c) == 0) return "no template root";
        if (VisualTreeHelper.GetChild(c, 0) is not FrameworkElement root) return "root not FE";
        var groups = VisualStateManager.GetVisualStateGroups(root);
        if (groups is null || groups.Count == 0) return "VisualStateGroups: none instantiated";
        var parts = new List<string>();
        foreach (var g in groups) parts.Add($"{g.Name}={(g.CurrentState?.Name ?? "(null)")}");
        return string.Join(" | ", parts);
    }

    // ====================================================================== V1
    // Q1a hardened. The stock SelectedBackground is #09000000 - a 3.5 % overlay.
    // Force it OPAQUE RED on every container. If SelectionMode=None ever painted a
    // selected fill, the row would go solid red. Nothing subtle can hide.

    private async Task V1Async()
    {
        Section("V1  Q1a hardened: OPAQUE SelectedBackground.\n" +
                "    ListViewItemPresenter.SelectedBackground = opaque RED (255,0,0),\n" +
                "    SelectedPointerOverBackground / SelectedPressedBackground also red,\n" +
                "    SelectionIndicatorBrush = opaque LIME. Grey list background 128,128,128.\n" +
                "    If a selected fill is drawn at all, the row mean goes to R=255 G=0 B=0.");

        foreach (var mode in new[] { ListViewSelectionMode.None, ListViewSelectionMode.Single })
        {
            W("");
            W($"---------------- SelectionMode = {mode} ----------------");
            var lv = new ListView
            {
                SelectionMode = mode,
                ItemsSource = MakeItems(6),
                ItemTemplate = (DataTemplate)Resources["SimpleTemplate"],
                Background = new SolidColorBrush(Grey),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Width = 800,
                Height = 400,
            };
            await ShowAsync(lv);

            var presenters = new List<ListViewItemPresenter>();
            if (lv.ItemsPanelRoot is not null) FindAll(lv.ItemsPanelRoot, presenters);
            W($"presenters found = {presenters.Count}");
            var red = new SolidColorBrush(Color.FromArgb(255, 255, 0, 0));
            var lime = new SolidColorBrush(Color.FromArgb(255, 0, 255, 0));
            foreach (var p in presenters)
            {
                p.SelectedBackground = red;
                p.SelectedPointerOverBackground = red;
                p.SelectedPressedBackground = red;
                p.SelectionIndicatorBrush = lime;
                p.CheckBrush = lime;
            }
            await Settle(300);

            var c0 = lv.ContainerFromIndex(0) as ListViewItem;
            var c1 = lv.ContainerFromIndex(1) as ListViewItem;
            if (c0 is null || c1 is null) { W("containers not realized"); continue; }
            var p0 = FindDescendant<ListViewItemPresenter>(c0);
            W($"c0 presenter SelectedBackground now = {(p0?.SelectedBackground as SolidColorBrush)?.Color.ToString() ?? "?"}");

            var before = await CaptureAsync(lv);
            var o0 = Origin(c0, lv);
            W($"BEFORE  c0 {Mean(before, o0.X, o0.Y + 1, 780, c0.ActualHeight - 2)}   states: {StatesOf(c0)}");

            if (mode == ListViewSelectionMode.None) c0.IsSelected = true; else lv.SelectedIndex = 0;
            await Settle(350);

            var after = await CaptureAsync(lv);
            var o0b = Origin(c0, lv);
            var o1 = Origin(c1, lv);
            W($"c0.IsSelected={c0.IsSelected}  lv.SelectedIndex={lv.SelectedIndex}  lv.SelectedItems.Count={lv.SelectedItems.Count}");
            W($"AFTER   c0 {Mean(after, o0b.X, o0b.Y + 1, 780, c0.ActualHeight - 2)}   states: {StatesOf(c0)}");
            W($"AFTER   c1 (control, untouched) {Mean(after, o1.X, o1.Y + 1, 780, c1.ActualHeight - 2)}   states: {StatesOf(c1)}");
            W($"AFTER   c0 left 12 DIP {Mean(after, o0b.X, o0b.Y + 1, 12, c0.ActualHeight - 2)}");
        }
    }

    // ====================================================================== V2
    // Q5 decisive. Hook PointerWheelChanged on EVERY realized row and container,
    // plus every level of the ListView template, so "no handler fired on the row"
    // cannot be an artefact of hooking the wrong container.

    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_MOUSEHWHEEL = 0x020E;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, char[] b, int max);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr h, EnumChildProc cb, IntPtr p);
    private delegate bool EnumChildProc(IntPtr h, IntPtr p);

    private static string ClassOf(IntPtr h)
    {
        var buf = new char[256];
        int n = GetClassName(h, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : "(none)";
    }

    private static List<IntPtr> ChildWindows(IntPtr parent)
    {
        var list = new List<IntPtr>();
        EnumChildWindows(parent, (h, _) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    private ListView _lv = null!;

    /// <summary>Walks up from the original source and names the realized row it belongs to.</summary>
    private string RowOf(object? originalSource)
    {
        var d = originalSource as DependencyObject;
        while (d is not null)
        {
            if (d is ListViewItem lvi)
            {
                int idx = _lv.IndexFromContainer(lvi);
                return $"inside ListViewItem index {idx}";
            }
            d = VisualTreeHelper.GetParent(d);
        }
        return "not inside any ListViewItem";
    }

    private void Hook(UIElement el, string who)
    {
        el.PointerWheelChanged += (s, e) => Rec(who + " [+=]", e, el);
        el.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler((s, e) => Rec(who + " [AddHandler true]", e, el)), true);
    }

    private void Rec(string who, PointerRoutedEventArgs e, UIElement el)
    {
        var p = e.GetCurrentPoint(el);
        _wheel.Add($"      {who}: Handled-at-entry={e.Handled} delta={p.Properties.MouseWheelDelta} " +
                   $"horiz={p.Properties.IsHorizontalMouseWheel} mods={e.KeyModifiers} " +
                   $"pos-in-el=({p.Position.X:0.#},{p.Position.Y:0.#}) " +
                   $"originalSource={(e.OriginalSource?.GetType().Name ?? "null")} -> {RowOf(e.OriginalSource)}");
    }

    private async Task V2Async()
    {
        Section("V2  Q5 decisive: PointerWheelChanged hooked on EVERY realized ListViewItem and\n" +
                "    EVERY realized RowPanel, plus ItemsStackPanel, ItemsPresenter, ScrollViewer,\n" +
                "    ListView, Host and Page. Both registrations on each.\n" +
                "    If the wheel routes through the row surface at all, some row handler MUST fire.");

        var kids = ChildWindows(_hwnd);
        IntPtr site = IntPtr.Zero;
        foreach (var k in kids) if (ClassOf(k) == "InputSiteWindowClass") site = k;
        W($"input site HWND = 0x{site.ToInt64():X}");
        if (site == IntPtr.Zero) { W("no input site; cannot run V2"); return; }

        RowPanel.SharedOffset = 0;
        _lv = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            ItemsSource = MakeItems(10000),
            ItemTemplate = (DataTemplate)Resources["RowTemplate"],
            ItemContainerStyle = (Style)Resources["TableItemStyle"],
            ItemsPanel = (ItemsPanelTemplate)Resources["IspPanel"],
            Background = new SolidColorBrush(Colors.White),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Width = 800,
            Height = 400,
        };
        ScrollViewer.SetHorizontalScrollMode(_lv, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_lv, ScrollBarVisibility.Disabled);
        await ShowAsync(_lv, 900);

        var sv = FindDescendant<ScrollViewer>(_lv);
        var ip = FindDescendant<ItemsPresenter>(_lv);
        var isp = _lv.ItemsPanelRoot;

        var containers = new List<ListViewItem>();
        if (isp is not null) FindAll(isp, containers);
        var rows = new List<RowPanel>();
        if (isp is not null) FindAll(isp, rows);

        W($"realized ListViewItem containers = {containers.Count}");
        W($"realized RowPanel row surfaces   = {rows.Count}");
        foreach (var c in containers) Hook(c, $"ListViewItem[{_lv.IndexFromContainer(c)}]");
        foreach (var r in rows) Hook(r, "RowPanel");
        // Also hook the Border and TextBlock leaves of the first few rows.
        int leafCount = 0;
        foreach (var r in rows)
        {
            foreach (var ch in r.Children)
            {
                if (leafCount >= 40) break;
                Hook(ch, "cellBorder");
                if (ch is Border bb && bb.Child is UIElement tb) { Hook(tb, "cellTextBlock"); }
                leafCount++;
            }
            if (leafCount >= 40) break;
        }
        W($"leaf cell elements hooked = {leafCount}");
        if (isp is not null) Hook(isp, "ItemsStackPanel");
        if (ip is not null) Hook(ip, "ItemsPresenter");
        if (sv is not null) Hook(sv, "innerScrollViewer");
        Hook(_lv, "listView");
        Hook(Host, "Host(Grid outside the ListView)");
        Hook(this, "Page");

        var c2 = _lv.ContainerFromIndex(2) as ListViewItem;
        if (c2 is null) { W("container 2 missing"); return; }
        double y = Origin(c2, _lv).Y + c2.ActualHeight / 2;

        POINT ScreenPoint(UIElement el, double dx, double dy)
        {
            var w = el.TransformToVisual(null).TransformPoint(new Point(dx, dy));
            var p = new POINT { X = (int)Math.Round(w.X * _scale), Y = (int)Math.Round(w.Y * _scale) };
            ClientToScreen(_hwnd, ref p);
            return p;
        }

        var overRow = ScreenPoint(_lv, 120, y);
        var overHostOutside = ScreenPoint(Host, 860, 200);   // right of the 800 DIP ListView
        W($"screen point over row index 2 = ({overRow.X},{overRow.Y})");
        W($"screen point over Host but OUTSIDE the ListView = ({overHostOutside.X},{overHostOutside.Y})");

        async Task Trial(string name, POINT lp, int msg, int delta, bool moveCursorFirst)
        {
            _wheel.Clear();
            double h0 = sv?.HorizontalOffset ?? -1, v0 = sv?.VerticalOffset ?? -1;
            GetCursorPos(out var saved);
            if (moveCursorFirst) { SetCursorPos(lp.X, lp.Y); await Task.Delay(250); }
            IntPtr lparam = (IntPtr)((lp.Y << 16) | (lp.X & 0xFFFF));
            SendMessage(site, msg, (IntPtr)(delta << 16), lparam);
            await Settle(450);
            W("");
            W($"  {name}");
            W($"    lParam screen point = ({lp.X},{lp.Y})   cursor moved first = {moveCursorFirst}");
            GetCursorPos(out var now);
            W($"    real cursor during send = ({now.X},{now.Y})");
            W($"    handlers fired: {_wheel.Count}");
            foreach (var s in _wheel) W(s);
            if (sv is not null) W($"    ScrollViewer  H {h0:0.##} -> {sv.HorizontalOffset:0.##}   V {v0:0.##} -> {sv.VerticalOffset:0.##}");
            if (moveCursorFirst) SetCursorPos(saved.X, saved.Y);
        }

        await Trial("A. vertical wheel -120, lParam over row 2, cursor NOT moved", overRow, WM_MOUSEWHEEL, -120, false);
        await Trial("B. vertical wheel -120, lParam over row 2, cursor MOVED there first", overRow, WM_MOUSEWHEEL, -120, true);
        await Trial("C. vertical wheel -120, lParam OUTSIDE the ListView (over Host)", overHostOutside, WM_MOUSEWHEEL, -120, false);
        await Trial("D. horizontal wheel +120, lParam over row 2, cursor MOVED there first", overRow, WM_MOUSEHWHEEL, 120, true);

        W("");
        W("--- summary of which hooked levels ever fired ---");
    }

    // ====================================================================== V3
    // The actionable question. If the row surface sees the wheel unhandled (V2 says it
    // does), can the row set e.Handled = true and stop the inner ScrollViewer scrolling?
    // Also: does the Shift modifier survive a synthetic WM_MOUSEWHEEL?

    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr extra);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    private const int MK_SHIFT = 0x0004;
    private const byte VK_SHIFT = 0x10;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private async Task V3Async()
    {
        Section("V3  Can the ROW cancel the scroll?\n" +
                "    Every realized RowPanel gets a PointerWheelChanged handler that sets e.Handled = true.\n" +
                "    If the ScrollViewer still scrolls, the row cannot own the wheel.\n" +
                "    lParam is now built in the INPUT SITE's client coordinates, which is what V2 showed\n" +
                "    the site actually reads (the earlier probe passed screen coordinates).");

        var kids = ChildWindows(_hwnd);
        IntPtr site = IntPtr.Zero;
        foreach (var k in kids) if (ClassOf(k) == "InputSiteWindowClass") site = k;
        if (site == IntPtr.Zero) { W("no input site"); return; }

        RowPanel.SharedOffset = 0;
        _lv = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            ItemsSource = MakeItems(10000),
            ItemTemplate = (DataTemplate)Resources["RowTemplate"],
            ItemContainerStyle = (Style)Resources["TableItemStyle"],
            ItemsPanel = (ItemsPanelTemplate)Resources["IspPanel"],
            Background = new SolidColorBrush(Colors.White),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Width = 800,
            Height = 400,
        };
        ScrollViewer.SetHorizontalScrollMode(_lv, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_lv, ScrollBarVisibility.Disabled);
        await ShowAsync(_lv, 900);

        var sv = FindDescendant<ScrollViewer>(_lv);
        var isp = _lv.ItemsPanelRoot;
        var rows = new List<RowPanel>();
        if (isp is not null) FindAll(isp, rows);
        W($"realized RowPanel surfaces = {rows.Count}");

        bool cancel = false;
        int rowFires = 0;
        double tableOffset = 0;
        foreach (var r in rows)
        {
            var rr = r;
            rr.PointerWheelChanged += (s, e) =>
            {
                var p = e.GetCurrentPoint(rr);
                rowFires++;
                _wheel.Add($"      RowPanel handler: Handled-at-entry={e.Handled} delta={p.Properties.MouseWheelDelta} " +
                           $"horiz={p.Properties.IsHorizontalMouseWheel} argsMods={e.KeyModifiers} " +
                           $"GetKeyState(SHIFT)&0x8000={(GetKeyState(VK_SHIFT) & 0x8000) != 0}");
                if (cancel)
                {
                    // What the table would do: move its own horizontal offset, then claim the event.
                    tableOffset += p.Properties.MouseWheelDelta;
                    e.Handled = true;
                }
            };
        }

        // lParam in the input site's own client space, which is what the site reads.
        POINT SitePoint(UIElement el, double dx, double dy)
        {
            var w = el.TransformToVisual(null).TransformPoint(new Point(dx, dy));
            var p = new POINT { X = (int)Math.Round(w.X * _scale), Y = (int)Math.Round(w.Y * _scale) };
            ClientToScreen(_hwnd, ref p);
            ScreenToClient(site, ref p);
            return p;
        }

        var c2 = _lv.ContainerFromIndex(2) as ListViewItem;
        if (c2 is null) { W("container 2 missing"); return; }
        double y = Origin(c2, _lv).Y + c2.ActualHeight / 2;
        var pt = SitePoint(_lv, 120, y);
        W($"lParam (input-site client px) targeting ListView point (120,{y:0.#}) DIP = ({pt.X},{pt.Y})");

        async Task Trial(string name, int msg, int delta, bool shift, bool doCancel)
        {
            _wheel.Clear(); rowFires = 0; cancel = doCancel;
            double v0 = sv?.VerticalOffset ?? -1, h0 = sv?.HorizontalOffset ?? -1;
            double t0 = tableOffset;
            IntPtr lparam = (IntPtr)((pt.Y << 16) | (pt.X & 0xFFFF));
            int wp = (delta << 16) | (shift ? MK_SHIFT : 0);
            if (shift) { keybd_event(VK_SHIFT, 0x2A, 0, UIntPtr.Zero); await Task.Delay(120); }
            SendMessage(site, msg, (IntPtr)wp, lparam);
            await Settle(450);
            if (shift) { keybd_event(VK_SHIFT, 0x2A, KEYEVENTF_KEYUP, UIntPtr.Zero); await Task.Delay(80); }
            W("");
            W($"  {name}   (row sets Handled = {doCancel})");
            W($"    row handler fires: {rowFires}");
            foreach (var s in _wheel) W(s);
            if (sv is not null)
                W($"    ScrollViewer  V {v0:0.##} -> {sv.VerticalOffset:0.##}   H {h0:0.##} -> {sv.HorizontalOffset:0.##}");
            W($"    table-owned offset {t0:0.##} -> {tableOffset:0.##}");
        }

        await Trial("R1 vertical -120, row does NOT cancel (baseline)", WM_MOUSEWHEEL, -120, false, false);
        await Trial("R2 vertical -120, row CANCELS", WM_MOUSEWHEEL, -120, false, true);
        await Trial("R3 vertical -120, row does NOT cancel (baseline again)", WM_MOUSEWHEEL, -120, false, false);
        await Trial("R4 horizontal +120 (WM_MOUSEHWHEEL), row does NOT cancel", WM_MOUSEHWHEEL, 120, false, false);
        await Trial("R5 horizontal +120 (WM_MOUSEHWHEEL), row CANCELS", WM_MOUSEHWHEEL, 120, false, true);
        await Trial("R6 vertical -120 WITH Shift, row does NOT cancel", WM_MOUSEWHEEL, -120, true, false);
        await Trial("R7 vertical -120 WITH Shift, row CANCELS", WM_MOUSEWHEEL, -120, true, true);
    }
}
