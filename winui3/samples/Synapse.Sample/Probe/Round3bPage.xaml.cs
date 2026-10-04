using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

namespace Synapse_Sample;

/// <summary>
/// Probe round 3, questions Q13..Q18.
/// Q13 pointer semantics under Multiple, Q14 native Shift range, Q15 measured contrast,
/// Q16 header/row cell registration, Q17 incremental collection changes, Q18 focus relocation.
/// </summary>
public sealed partial class Round3bPage : Page
{
    private readonly StringBuilder _log = new();
    private double _scale = 1.0;
    private bool _finished;
    private IntPtr _hwnd;
    private IntPtr _site;
    private static readonly string Out = System.IO.Path.Combine(AppContext.BaseDirectory, "q13-q18-results.txt");

    public Round3bPage() { InitializeComponent(); Loaded += OnLoaded; }

    private void W(string s) => _log.AppendLine(s);

    private void Section(string s)
    { W(""); W("========================================================"); W(s); W("========================================================"); }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(600);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { W("WATCHDOG fired: the run did not complete."); Finish(); };
        timer.Start();

        _scale = XamlRoot?.RasterizationScale ?? 1.0;
        RequestedTheme = ElementTheme.Light;
        W($"round3b (Q13..Q18) started {DateTime.Now:O}  RasterizationScale={_scale}");
        SizeWindow();
        await Settle(600);

        try { _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance!); } catch { }
        foreach (var k in ChildWindows(_hwnd)) if (ClassOf(k) == "InputSiteWindowClass") _site = k;
        W($"top-level HWND = 0x{_hwnd.ToInt64():X}   InputSiteWindowClass HWND = 0x{_site.ToInt64():X}");

        await G("input calibration", CalibrateInput);
        await G("delivery", Q13x_Delivery);
        await G("Q13", Q13_PointerSemantics);
        await G("Q14", Q14_NativeShiftRange);
        await G("Q14k", Q14k_KeyboardSelection);
        await G("Q15", Q15_Contrast);
        await G("Q16", Q16_HeaderRegistration);
        await G("Q17", Q17_IncrementalChanges);
        await G("Q18", Q18_FocusRelocationScroll);

        timer.Stop();
        Finish();
    }

    private async Task G(string n, Func<Task> b)
    { try { await b(); } catch (Exception ex) { W($"*** {n} THREW {ex.GetType().Name}: {ex.Message}"); W(ex.StackTrace ?? ""); } }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        W($"round3b finished {DateTime.Now:O}");
        try { File.WriteAllText(Out, _log.ToString()); } catch { }
        Application.Current.Exit();
    }

    private void SizeWindow()
    {
        try
        {
            var w = MainWindow.Instance;
            if (w is null) return;
            w.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(940 * _scale), (int)(760 * _scale)));
            w.Activate();
        }
        catch (Exception ex) { W("resize failed " + ex.Message); }
    }

    private async Task Settle(int ms = 250)
    { Host.UpdateLayout(); await Task.Delay(ms); Host.UpdateLayout(); await Task.Delay(60); }

    private async Task ShowAsync(UIElement el, int ms = 500)
    {
        Host.Children.Clear();
        RowPanel.Live.Clear();
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
    { int n = VisualTreeHelper.GetChildrenCount(root); for (int i = 0; i < n; i++) { var c = VisualTreeHelper.GetChild(root, i); if (c is T t) into.Add(t); FindAll(c, into); } }

    private static Point Origin(UIElement c, UIElement a) => c.TransformToVisual(a).TransformPoint(new Point(0, 0));

    private static string Sel(ListView lv)
    {
        if (lv.SelectedItems.Count == 0) return "(none)";
        var parts = new List<string>();
        foreach (var o in lv.SelectedItems)
            parts.Add(o is ProbeItem p ? $"id{p.Index}@{lv.Items.IndexOf(o)}" : o?.ToString() ?? "null");
        return string.Join(" ", parts);
    }

    // ------------------------------------------------------------ win32
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void mouse_event(uint f, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr extra);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, char[] b, int max);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr h, EnumChildProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr h, int index, byte[] info, int len, out int needed);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    private delegate bool EnumChildProc(IntPtr h, IntPtr p);

    /// <summary>Brings our window to the front and reports whether it worked.</summary>
    private string ForceForeground()
    {
        ShowWindow(_hwnd, 9);                                   // SW_RESTORE
        SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);  // TOPMOST, NOMOVE|NOSIZE|SHOWWINDOW
        uint ourThread = GetCurrentThreadId();
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = fgThread != 0 && fgThread != ourThread && AttachThreadInput(ourThread, fgThread, true);
        BringWindowToTop(_hwnd);
        SetForegroundWindow(_hwnd);
        SetActiveWindow(_hwnd);
        if (attached) AttachThreadInput(ourThread, fgThread, false);
        GetWindowRect(_hwnd, out var r);
        return $"foreground-is-ours={GetForegroundWindow() == _hwnd} visible={IsWindowVisible(_hwnd)} minimized={IsIconic(_hwnd)} " +
               $"rect=({r.Left},{r.Top})-({r.Right},{r.Bottom}) attachedToForegroundThread={attached}";
    }

    /// <summary>True when the given screen point is over a window owned by this process.</summary>
    private bool PointIsOurs(POINT p, out string what)
    {
        var h = WindowFromPoint(p);
        GetWindowThreadProcessId(h, out uint pid);
        uint ours = (uint)Environment.ProcessId;
        what = $"WindowFromPoint = 0x{h.ToInt64():X} class='{ClassOf(h)}' pid={pid} (ours={ours})";
        return pid == ours;
    }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_SHIFT = 0x10, VK_CONTROL = 0x11;
    private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_MOUSEMOVE = 0x0200;
    private const int MK_LBUTTON = 0x0001, MK_SHIFT = 0x0004, MK_CONTROL = 0x0008;

    private static string ClassOf(IntPtr h)
    { var b = new char[256]; int n = GetClassName(h, b, b.Length); return n > 0 ? new string(b, 0, n) : "(none)"; }

    private static List<IntPtr> ChildWindows(IntPtr p)
    { var l = new List<IntPtr>(); EnumChildWindows(p, (h, _) => { l.Add(h); return true; }, IntPtr.Zero); return l; }

    private POINT ScreenPointOf(UIElement el, double dx, double dy)
    {
        var w = el.TransformToVisual(null).TransformPoint(new Point(dx, dy));
        var p = new POINT { X = (int)Math.Round(w.X * _scale), Y = (int)Math.Round(w.Y * _scale) };
        ClientToScreen(_hwnd, ref p);
        return p;
    }

    /// <summary>Which delivery mechanism the calibration proved. Null until calibration runs.</summary>
    private string _route = "none";

    private string _lastGuard = "";

    private async Task RealClick(POINT screen, bool ctrl, bool shift)
    {
        ForceForeground();
        await Task.Delay(150);
        // Never inject a click into a window that is not ours.
        if (!PointIsOurs(screen, out string what)) { _lastGuard = "SKIPPED, the point is not over our window: " + what; return; }
        _lastGuard = what;
        SetCursorPos(screen.X, screen.Y);
        await Task.Delay(150);
        if (ctrl) keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        if (shift) keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
        if (ctrl || shift) await Task.Delay(120);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(90);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(150);
        if (shift) keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (ctrl) keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        await Task.Delay(250);
    }

    private async Task MessageClick(POINT screen, bool ctrl, bool shift)
    {
        // WM_LBUTTONDOWN lParam is in the CLIENT space of the receiving window,
        // which is the input site window, not the top level window.
        var p = screen;
        ScreenToClient(_site, ref p);
        IntPtr lp = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
        int mk = MK_LBUTTON | (ctrl ? MK_CONTROL : 0) | (shift ? MK_SHIFT : 0);
        if (ctrl) keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        if (shift) keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
        SendMessage(_site, WM_MOUSEMOVE, (IntPtr)(mk & ~MK_LBUTTON), lp);
        await Task.Delay(80);
        SendMessage(_site, WM_LBUTTONDOWN, (IntPtr)mk, lp);
        await Task.Delay(90);
        SendMessage(_site, WM_LBUTTONUP, (IntPtr)(mk & ~MK_LBUTTON), lp);
        if (shift) keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (ctrl) keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        await Task.Delay(300);
    }

    private async Task ClickAsync(POINT screen, bool ctrl = false, bool shift = false)
    {
        if (_route == "message") await MessageClick(screen, ctrl, shift);
        else await RealClick(screen, ctrl, shift);
        await Settle(200);
    }

    // ================================================================= calibration
    private async Task CalibrateInput()
    {
        Section("INPUT CALIBRATION\n" +
                "    A Button is clicked by both delivery mechanisms before any table question runs,\n" +
                "    so a null result later cannot be blamed on undelivered input.");

        var d = OpenInputDesktop(0, false, 0x0100);
        W($"OpenInputDesktop = {(d != IntPtr.Zero ? "ok (this session owns the input desktop)" : "FAILED (session may be locked)")}");
        if (d != IntPtr.Zero) CloseDesktop(d);

        int clicks = 0; string mods = "", seen = "";
        var btn = new Button { Content = "target", Width = 240, Height = 60, Margin = new Thickness(20) };
        btn.Click += (_, _) => clicks++;
        btn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) =>
        { seen += "pressed "; mods = e.KeyModifiers.ToString(); }), true);
        await ShowAsync(btn, 500);

        try { MainWindow.Instance!.AppWindow.Move(new Windows.Graphics.PointInt32(80, 80)); } catch { }
        await Settle(300);
        W("window activation: " + ForceForeground());
        await Settle(300);
        W("window activation, second attempt: " + ForceForeground());

        var pt = ScreenPointOf(btn, 120, 30);
        W($"button centre in screen coordinates = ({pt.X},{pt.Y})");
        PointIsOurs(pt, out string over);
        W("    " + over);
        GetCursorPos(out var saved);

        clicks = 0; seen = ""; mods = "";
        await RealClick(pt, false, false);
        await Settle(250);
        W($"real input (SetCursorPos + mouse_event): pointer events='{seen.Trim()}' Click count={clicks}   guard: {_lastGuard}");
        bool real = clicks > 0;

        bool msg = false;
        foreach (var (name, target) in new (string, IntPtr)[] { ("InputSiteWindowClass", _site), ("top-level window", _hwnd) })
        {
            if (msg || target == IntPtr.Zero) continue;
            clicks = 0; seen = ""; mods = "";
            var save = _site; _site = target;
            await MessageClick(pt, false, false);
            _site = save;
            await Settle(250);
            W($"posted WM_LBUTTONDOWN/UP to {name}: pointer events='{seen.Trim()}' Click count={clicks}");
            msg = clicks > 0;
        }

        _route = real ? "real" : (msg ? "message" : "none");
        W($"chosen delivery mechanism = {_route}");

        if (_route != "none")
        {
            clicks = 0; seen = ""; mods = "";
            await ClickAsync(pt, ctrl: true, shift: false);
            W($"Ctrl held during the click: KeyModifiers seen by the handler = '{mods}' (Control means the modifier reached WinUI)");
            clicks = 0; seen = ""; mods = "";
            await ClickAsync(pt, ctrl: false, shift: true);
            W($"Shift held during the click: KeyModifiers seen by the handler = '{mods}'");
        }
        SetCursorPos(saved.X, saved.Y);
        W($"foreground window is ours = {GetForegroundWindow() == _hwnd}");
    }

    // ================================================================= delivery investigation
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_MOUSEWHEEL = 0x020A;
    private const int WM_POINTERDOWN = 0x0246, WM_POINTERUP = 0x0247;
    private string _keyRoute = "none";

    private static string DesktopName()
    {
        var d = OpenInputDesktop(0, false, 0x0001);   // DESKTOP_READOBJECTS
        if (d == IntPtr.Zero) return "OpenInputDesktop failed (error " + Marshal.GetLastWin32Error() + ")";
        var buf = new byte[256];
        bool ok = GetUserObjectInformation(d, 2, buf, buf.Length, out int need);   // UOI_NAME
        CloseDesktop(d);
        return ok ? System.Text.Encoding.Unicode.GetString(buf, 0, Math.Max(0, need - 2)) : "unreadable";
    }

    private void PostKey(IntPtr target, byte vk, bool down)
    {
        uint scan = MapVirtualKey(vk, 0);
        int l = down ? (int)(1 | (scan << 16)) : (int)(1 | (scan << 16) | (1 << 30) | (1 << 31));
        PostMessage(target, down ? WM_KEYDOWN : WM_KEYUP, (IntPtr)vk, (IntPtr)l);
    }

    private async Task KeyPress(byte vk, bool ctrl = false, bool shift = false)
    {
        var t = _site;
        if (ctrl) PostKey(t, VK_CONTROL, true);
        if (shift) PostKey(t, VK_SHIFT, true);
        if (ctrl || shift) await Task.Delay(60);
        PostKey(t, vk, true);
        await Task.Delay(60);
        PostKey(t, vk, false);
        await Task.Delay(60);
        if (shift) PostKey(t, VK_SHIFT, false);
        if (ctrl) PostKey(t, VK_CONTROL, false);
        await Task.Delay(200);
    }

    private async Task Q13x_Delivery()
    {
        Section("INPUT DELIVERY INVESTIGATION. Which synthetic routes still reach the app, measured one at a time, with a wheel message as the positive control.");

        W($"input desktop name = '{DesktopName()}'  (Winlogon means the session is LOCKED)");
        W($"LogonUI processes running = {System.Diagnostics.Process.GetProcessesByName("LogonUI").Length}");
        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint fgpid);
        W($"foreground window 0x{fg.ToInt64():X} class='{ClassOf(fg)}' pid={fgpid}");
        W("child windows of our top-level window:");
        foreach (var k in ChildWindows(_hwnd)) W($"    0x{k.ToInt64():X} class={ClassOf(k)}");

        // ---- positive control: does a wheel message still scroll while locked?
        var inner = new Border { Width = 300, Height = 2000, Background = new SolidColorBrush(Colors.LightGray) };
        var sv = new ScrollViewer { Width = 320, Height = 200, Content = inner, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var btn = new Button { Content = "key target", Width = 200, Height = 48, Margin = new Thickness(0, 220, 0, 0) };
        int clicks = 0; var keys = new List<string>();
        btn.Click += (_, _) => clicks++;
        btn.KeyDown += (s2, e2) => keys.Add($"KeyDown {e2.Key} mods={Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)}");
        var panel = new Grid();
        panel.Children.Add(sv); panel.Children.Add(btn);
        await ShowAsync(panel, 600);

        var wheelPt = ScreenPointOf(sv, 160, 100);
        double v0 = sv.VerticalOffset;
        SendMessage(_site, WM_MOUSEWHEEL, (IntPtr)(-120 << 16), (IntPtr)((wheelPt.Y << 16) | (wheelPt.X & 0xFFFF)));
        await Settle(400);
        W($"positive control, WM_MOUSEWHEEL to the input site: ScrollViewer VerticalOffset {v0} -> {sv.VerticalOffset}  " +
          $"{(sv.VerticalOffset != v0 ? "MESSAGE DELIVERY WORKS" : "no effect")}");

        // ---- mouse button messages, three targets, Send and Post
        var btnPt = ScreenPointOf(btn, 100, 24);
        var targets = new List<(string, IntPtr)> { ("InputSiteWindowClass", _site), ("top-level window", _hwnd) };
        foreach (var k in ChildWindows(_hwnd))
            if (ClassOf(k).Contains("DesktopChildSiteBridge")) targets.Add(("DesktopChildSiteBridge", k));
        foreach (var (name, t) in targets)
        {
            foreach (bool post in new[] { false, true })
            {
                clicks = 0;
                var p = btnPt; ScreenToClient(t, ref p);
                IntPtr lp = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
                if (post)
                { PostMessage(t, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lp); await Task.Delay(80); PostMessage(t, WM_LBUTTONUP, IntPtr.Zero, lp); }
                else
                { SendMessage(t, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lp); await Task.Delay(80); SendMessage(t, WM_LBUTTONUP, IntPtr.Zero, lp); }
                await Settle(300);
                W($"WM_LBUTTONDOWN/UP {(post ? "Post" : "Send")} to {name} at client ({p.X},{p.Y}): Button.Click count = {clicks}");
            }
        }
        // ---- WM_POINTERDOWN, pointer id 1
        clicks = 0;
        var pp = btnPt;
        SendMessage(_site, WM_POINTERDOWN, (IntPtr)1, (IntPtr)((pp.Y << 16) | (pp.X & 0xFFFF)));
        await Task.Delay(80);
        SendMessage(_site, WM_POINTERUP, (IntPtr)1, (IntPtr)((pp.Y << 16) | (pp.X & 0xFFFF)));
        await Settle(300);
        W($"WM_POINTERDOWN/UP to the input site with pointer id 1: Button.Click count = {clicks}");

        // ---- keyboard messages
        btn.Focus(FocusState.Keyboard);
        await Settle(300);
        keys.Clear(); clicks = 0;
        await KeyPress(0x20);                      // VK_SPACE, which invokes a focused Button
        W($"posted WM_KEYDOWN/UP VK_SPACE to the input site: KeyDown events={keys.Count} [{string.Join("; ", keys)}] Button.Click count={clicks}");
        _keyRoute = (keys.Count > 0 || clicks > 0) ? "post-to-input-site" : "none";
        W($"keyboard delivery route = {_keyRoute}");
    }

    // ================================================================= Q13 / Q14 shared rig
    private ListView _lv = null!;
    private readonly List<string> _seq = new();
    private bool _cancelPressed;
    private bool _cancelReleased;

    private ListView BuildClickList(int n)
    {
        var lv = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            IsMultiSelectCheckBoxEnabled = false,
            ItemsSource = MakeItems(n),
            ItemTemplate = (DataTemplate)Resources["PlainRowTemplate"],
            ItemContainerStyle = (Style)Resources["DesignRowStyle"],
            ItemsPanel = (ItemsPanelTemplate)Resources["IspPanel3b"],
            Width = 600,
            Height = 420,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
        };
        ScrollViewer.SetHorizontalScrollMode(lv, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(lv, ScrollBarVisibility.Disabled);
        lv.SelectionChanged += (s, e) =>
            _seq.Add($"      SelectionChanged  +{e.AddedItems.Count} -{e.RemovedItems.Count}  now: {Sel(lv)}");
        lv.ItemClick += (s, e) => _seq.Add("      ItemClick");
        return lv;
    }

    private void HookRows(ListView lv)
    {
        for (int i = 0; i < lv.Items.Count; i++)
        {
            if (lv.ContainerFromIndex(i) is not ListViewItem c) continue;
            int idx = i;
            if (c.ContentTemplateRoot is UIElement root)
            {
                root.PointerPressed += (s, e) =>
                {
                    _seq.Add($"      row content PointerPressed  row={idx}  Handled-at-entry={e.Handled}  mods={e.KeyModifiers}  selection now: {Sel(lv)}");
                    if (_cancelPressed) { e.Handled = true; _seq.Add("        -> handler set e.Handled = true"); }
                };
                root.PointerReleased += (s, e) =>
                {
                    _seq.Add($"      row content PointerReleased row={idx}  Handled-at-entry={e.Handled}  selection now: {Sel(lv)}");
                    if (_cancelReleased) { e.Handled = true; _seq.Add("        -> handler set e.Handled = true"); }
                };
                root.Tapped += (s, e) => _seq.Add($"      row content Tapped          row={idx}  Handled-at-entry={e.Handled}  selection now: {Sel(lv)}");
            }
            c.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) =>
                _seq.Add($"      CONTAINER PointerPressed (handledEventsToo) row={idx} Handled-at-entry={e.Handled} selection now: {Sel(lv)}")), true);
        }
    }

    private POINT RowPoint(ListView lv, int index)
    {
        var c = (ListViewItem)lv.ContainerFromIndex(index);
        var o = Origin(c, lv);
        return ScreenPointOf(lv, o.X + 300, o.Y + c.ActualHeight / 2);
    }

    private async Task Q13_PointerSemantics()
    {
        Section("Q13  Pointer semantics under SelectionMode=Multiple, IsMultiSelectCheckBoxEnabled=False.\n" +
                "    Does a handler on the ROW CONTENT see PointerPressed before the container acts,\n" +
                "    and does e.Handled = true there suppress the native toggle?");

        _lv = BuildClickList(12);
        await ShowAsync(_lv, 700);
        HookRows(_lv);
        W($"realized containers = {_lv.Items.Count}, all hooked. row height = {((ListViewItem)_lv.ContainerFromIndex(0)).ActualHeight} DIP");

        var items = (List<ProbeItem>)_lv.ItemsSource;

        async Task Trial(string name, int row, bool cancelPressed, bool cancelReleased, bool ctrl, bool shift)
        {
            _lv.SelectedItems.Clear();
            _lv.SelectedItems.Add(items[0]);
            _lv.SelectedItems.Add(items[3]);
            await Settle(200);
            _cancelPressed = cancelPressed; _cancelReleased = cancelReleased;
            _seq.Clear();
            W("");
            W($"  {name}");
            W($"    BEFORE  SelectedItems({_lv.SelectedItems.Count}) = {Sel(_lv)}   SelectedIndex={_lv.SelectedIndex}");
            var p = RowPoint(_lv, row);
            W($"    clicking row {row} at screen ({p.X},{p.Y}) ctrl={ctrl} shift={shift} route={_route}");
            await ClickAsync(p, ctrl, shift);
            W($"    injection guard: {_lastGuard}");
            W("    event order as it happened:");
            if (_seq.Count == 0) W("      (no events at all)");
            foreach (var s in _seq) W(s);
            W($"    AFTER   SelectedItems({_lv.SelectedItems.Count}) = {Sel(_lv)}   SelectedIndex={_lv.SelectedIndex}");
            _cancelPressed = false; _cancelReleased = false;
        }

        await Trial("A. plain click on row 5, NO cancelling handler", 5, false, false, false, false);
        await Trial("B. plain click on row 5, row handler sets e.Handled = true on PointerPressed", 5, true, false, false, false);
        await Trial("C. plain click on row 5, row handler sets e.Handled = true on Pressed AND Released", 5, true, true, false, false);
        await Trial("D. plain click on row 7, no cancelling handler, second click on an ALREADY selected row",
                    3, false, false, false, false);
    }

    private async Task Q14_NativeShiftRange()
    {
        Section("Q14  Does Multiple range natively on Shift+click? Click row 2, then Shift+click row 7.\n" +
                "    Then the same with Ctrl, and with Ctrl+Shift.");

        _lv = BuildClickList(12);
        await ShowAsync(_lv, 700);
        HookRows(_lv);
        _cancelPressed = false; _cancelReleased = false;

        async Task Pair(string name, bool ctrl, bool shift)
        {
            _lv.SelectedItems.Clear();
            await Settle(200);
            W("");
            W($"  {name}");
            _seq.Clear();
            await ClickAsync(RowPoint(_lv, 2));
            W($"    injection guard: {_lastGuard}");
            W($"    after a plain click on row 2: SelectedItems({_lv.SelectedItems.Count}) = {Sel(_lv)}");
            _seq.Clear();
            await ClickAsync(RowPoint(_lv, 7), ctrl, shift);
            foreach (var s in _seq) W(s);
            W($"    after the second click on row 7 (ctrl={ctrl} shift={shift}): SelectedItems({_lv.SelectedItems.Count}) = {Sel(_lv)}");
        }

        await Pair("Shift+click row 7", false, true);
        await Pair("Ctrl+click row 7", true, false);
        await Pair("Ctrl+Shift+click row 7", true, true);
        await Pair("plain click row 7 (control case)", false, false);
    }

    private async Task Q14k_KeyboardSelection()
    {
        Section("Q14 supplementary: keyboard selection semantics under Multiple. Clicks cannot be injected on a locked session, but posted key messages can be. If Shift+Arrow extends a range, Multiple keeps an anchor the table cannot read.");

        if (_keyRoute == "none") { W("  keyboard messages are not delivered either; nothing measured."); return; }

        _lv = BuildClickList(12);
        await ShowAsync(_lv, 700);
        HookRows(_lv);
        _cancelPressed = false; _cancelReleased = false;

        // Prove whether a posted key message carries the modifier at all.
        _lv.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((s2, e2) =>
        {
            var ks = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
            var kc = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            _seq.Add($"KeyDown key={e2.Key} handled-at-entry={e2.Handled} InputKeyboardSource Shift={ks} Control={kc} " +
                     $"GetKeyState(VK_SHIFT)=0x{(ushort)GetKeyState(VK_SHIFT):X4} GetKeyState(VK_CONTROL)=0x{(ushort)GetKeyState(VK_CONTROL):X4}");
        }), true);

        var c2 = (ListViewItem)_lv.ContainerFromIndex(2);
        c2.Focus(FocusState.Keyboard);
        await Settle(300);
        W($"  focus start = {FocusDesc(_lv)}   SelectedItems = {Sel(_lv)}");

        async Task Key(string name, byte vk, bool ctrl = false, bool shift = false)
        {
            _seq.Clear();
            await KeyPress(vk, ctrl, shift);
            W($"    {name}: SelectedItems({_lv.SelectedItems.Count}) = {Sel(_lv)}   focus = {FocusDesc(_lv)}");
            foreach (var e in _seq) W("      " + e);
        }

        await Key("Space on row 2", 0x20);
        await Key("Down", 0x28);
        await Key("Shift+Down", 0x28, false, true);
        await Key("Shift+Down", 0x28, false, true);
        await Key("Shift+Down", 0x28, false, true);
        _lv.SelectedItems.Clear(); await Settle(200);
        W($"    selection cleared: {Sel(_lv)}");
        await Key("Space (anchor at the focused row)", 0x20);
        await Key("Shift+Down after Space", 0x28, false, true);
        await Key("Ctrl+Down", 0x28, true, false);
        await Key("Ctrl+Space", 0x20, true, false);
        await Key("Shift+Up", 0x26, false, true);
        await Key("Ctrl+A", 0x41, true, false);
    }

    // ================================================================= capture
    private sealed class Shot
    { public int W, H; public byte[] Px = Array.Empty<byte>(); public bool Ok; public string Err = ""; public override string ToString() => Ok ? $"{W}x{H}" : "FAILED " + Err; }

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

    private Color At(Shot s, double dipX, double dipY)
    {
        int x = (int)Math.Round(dipX * _scale), y = (int)Math.Round(dipY * _scale);
        if (!s.Ok || x < 0 || y < 0 || x >= s.W || y >= s.H) return Color.FromArgb(0, 0, 0, 0);
        int i = (y * s.W + x) * 4;
        return Color.FromArgb(s.Px[i + 3], s.Px[i + 2], s.Px[i + 1], s.Px[i]);
    }

    private static double Lin(double c) { c /= 255.0; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
    private static double Lum(Color c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    private static double Ratio(Color a, Color b)
    { double la = Lum(a), lb = Lum(b); if (la < lb) (la, lb) = (lb, la); return (la + 0.05) / (lb + 0.05); }
    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Most common colour in a DIP rectangle, so anti-aliased text cannot dominate.</summary>
    private (Color colour, double share) Mode(Shot s, double x0, double y0, double w, double h)
    {
        var counts = new Dictionary<uint, int>();
        int total = 0;
        for (int py = (int)Math.Round(y0 * _scale); py < (int)Math.Round((y0 + h) * _scale); py++)
            for (int px = (int)Math.Round(x0 * _scale); px < (int)Math.Round((x0 + w) * _scale); px++)
            {
                if (px < 0 || py < 0 || px >= s.W || py >= s.H) continue;
                int i = (py * s.W + px) * 4;
                uint key = (uint)(s.Px[i] | (s.Px[i + 1] << 8) | (s.Px[i + 2] << 16));
                counts.TryGetValue(key, out int c); counts[key] = c + 1; total++;
            }
        if (total == 0) return (Color.FromArgb(0, 0, 0, 0), 0);
        var best = counts.OrderByDescending(k => k.Value).First();
        var col = Color.FromArgb(255, (byte)((best.Key >> 16) & 0xFF), (byte)((best.Key >> 8) & 0xFF), (byte)(best.Key & 0xFF));
        return (col, (double)best.Value / total);
    }

    private async Task Q15_Contrast()
    {
        Section("Q15  Measured contrast of the selection and focus cues, using the design's actual\n" +
                "    container style (BasedOn DefaultListViewItemStyle, Stretch, Padding 0, no MinHeight/MinWidth).");

        SetForegroundWindow(_hwnd);
        await Task.Delay(200);

        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            RequestedTheme = theme;
            await Settle(400);

            var items = MakeItems(8);
            var lv = new ListView
            {
                SelectionMode = ListViewSelectionMode.Multiple,
                IsMultiSelectCheckBoxEnabled = false,
                ItemsSource = items,
                ItemTemplate = (DataTemplate)Resources["PlainRowTemplate"],
                ItemContainerStyle = (Style)Resources["DesignRowStyle"],
                ItemsPanel = (ItemsPanelTemplate)Resources["IspPanel3b"],
                Width = 600,
                Height = 400,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
            };
            ScrollViewer.SetHorizontalScrollMode(lv, ScrollMode.Disabled);
            await ShowAsync(lv, 700);

            var c2 = (ListViewItem)lv.ContainerFromIndex(2);
            var c4 = (ListViewItem)lv.ContainerFromIndex(4);
            double h = c2.ActualHeight;
            double y2 = Origin(c2, Host).Y, y4 = Origin(c4, Host).Y;
            double lvx0 = Origin(lv, Host).X;
            W("");
            W($"---- {theme} ---- container size = {c2.ActualWidth}x{c2.ActualHeight}  Padding={c2.Padding}  MinHeight={c2.MinHeight}");

            // Every capture is of Host, which has an opaque theme background. Capturing the
            // ListView alone yields alpha 0 everywhere, because its own background is transparent.
            var baseShot = await CaptureAsync(Host);
            var unsel = Mode(baseShot, lvx0 + 200, y2 + 4, 300, h - 8);
            W($"    capture {baseShot}   unselected row 2 body colour = {Hex(unsel.colour)} ({unsel.share:P0} of the sampled pixels)");

            lv.SelectedItems.Add(items[2]);
            await Settle(450);
            var selShot = await CaptureAsync(Host);
            var selBody = Mode(selShot, lvx0 + 200, y2 + 4, 300, h - 8);
            var unselBody = Mode(selShot, lvx0 + 200, y4 + 4, 300, h - 8);
            W($"    selected row 2 body colour   = {Hex(selBody.colour)} ({selBody.share:P0})");
            W($"    unselected row 4 body colour = {Hex(unselBody.colour)} ({unselBody.share:P0})");
            W($"    WCAG contrast selected vs unselected = {Ratio(selBody.colour, unselBody.colour):0.00}:1   " +
              $"{(Ratio(selBody.colour, unselBody.colour) >= 3.0 ? "PASSES" : "FAILS")} the 3:1 requirement");

            // Any other pixel the selection changed: scan the whole selected row for the
            // largest difference from the same pixel in the unselected capture.
            double biggest = 0; Color ba = default, bb = default; double bx = -1, by = -1;
            double lvx = lvx0;
            for (double x = lvx; x < lvx + 596; x += 1)
                for (double yy = y2; yy < y2 + h; yy += 1)
                {
                    var before = At(baseShot, x, yy);
                    var after = At(selShot, x, yy);
                    double r = Ratio(before, after);
                    if (r > biggest) { biggest = r; ba = before; bb = after; bx = x; by = yy; }
                }
            W($"    strongest single-pixel change caused by selecting the row: at ({bx:0},{by:0}) " +
              $"{Hex(ba)} -> {Hex(bb)} ratio {biggest:0.00}:1");
            W($"    horizontal scan across the selected row: {ColorChanges(selShot, y2 + h / 2, lvx, lvx + 80)}");

            // ---- focus cue on an UNSELECTED row
            lv.SelectedItems.Clear();
            await Settle(300);
            var noFocus = await CaptureAsync(Host);
            bool got = c4.Focus(FocusState.Keyboard);
            await Settle(450);
            var withFocus = await CaptureAsync(Host);
            W($"    Focus(Keyboard) on container 4 returned {got}; container FocusState={c4.FocusState}");

            double fBest = 0; Color fa = default, fb = default; double fx = -1, fy = -1;
            int changed = 0;
            double top = y4 - 4, bot = y4 + h + 4;
            for (double x = lvx; x < lvx + 596; x += 1)
                for (double yy = top; yy < bot; yy += 1)
                {
                    var a = At(noFocus, x, yy); var b = At(withFocus, x, yy);
                    if (a.R != b.R || a.G != b.G || a.B != b.B) changed++;
                    double r = Ratio(a, b);
                    if (r > fBest) { fBest = r; fa = a; fb = b; fx = x; fy = yy; }
                }
            W($"    pixels changed by focusing the row = {changed}");
            W($"    strongest focus-cue pixel: at ({fx:0},{fy:0}) background {Hex(fa)} -> focus cue {Hex(fb)}  " +
              $"contrast {fBest:0.00}:1  {(fBest >= 3.0 ? "PASSES" : "FAILS")} the 3:1 requirement");
            if (changed > 0)
            {
                var probe = new List<string>();
                for (double yy = top; yy < bot; yy += 1)
                {
                    var a = At(noFocus, lvx + 300, yy); var b = At(withFocus, lvx + 300, yy);
                    if (a.R != b.R || a.G != b.G || a.B != b.B)
                        probe.Add($"y={yy - y4:0} {Hex(a)}->{Hex(b)}");
                }
                W($"    vertical slice at x=300 in the row through the focused row: {(probe.Count == 0 ? "(no change on this column)" : string.Join("  ", probe))}");
            }

            // ---- focus cue on a SELECTED row (current + selected together)
            lv.SelectedItems.Add(items[4]);
            await Settle(400);
            var selFocus = await CaptureAsync(Host);
            var selFocusBody = Mode(selFocus, lvx0 + 200, y4 + 4, 300, h - 8);
            W($"    selected AND focused row 4 body colour = {Hex(selFocusBody.colour)}  " +
              $"vs unselected {Hex(unselBody.colour)} ratio {Ratio(selFocusBody.colour, unselBody.colour):0.00}:1");
        }

        RequestedTheme = ElementTheme.Light;
        await Settle(300);

        W("");
        W("---- Windows contrast theme ----");
        var acc = new Windows.UI.ViewManagement.AccessibilitySettings();
        W($"    AccessibilitySettings.HighContrast = {acc.HighContrast}  scheme='{(acc.HighContrast ? acc.HighContrastScheme : "(none)")}'");
        W("    A contrast theme was NOT switched on: that is a system-wide Windows setting and this");
        W("    harness does not change system settings. The rendered numbers above are therefore");
        W("    Light and Dark only. What can be read without changing the setting are the system");
        W("    colours the HighContrast theme dictionary binds to:");
        foreach (var key in new[] { "SystemColorHighlightColor", "SystemColorHighlightTextColor", "SystemColorWindowColor",
                                    "SystemColorWindowTextColor", "SystemColorButtonFaceColor", "SystemColorButtonTextColor",
                                    "SystemColorHotlightColor", "SystemColorGrayTextColor" })
        {
            object? v = null;
            try { v = Application.Current.Resources[key]; } catch { }
            if (v is Color cc) W($"      {key,-32} = {Hex(cc)}");
            else W($"      {key,-32} = (not resolvable: {v?.GetType().Name ?? "missing"})");
        }
        try
        {
            W($"      GetSysColor(COLOR_HIGHLIGHT=13)     = {SysHex(13)}");
            W($"      GetSysColor(COLOR_WINDOW=5)         = {SysHex(5)}");
            W($"      GetSysColor(COLOR_WINDOWTEXT=8)     = {SysHex(8)}");
            W($"      GetSysColor(COLOR_HIGHLIGHTTEXT=14) = {SysHex(14)}");
            var hl = SysColor(13); var win = SysColor(5);
            W($"      ratio highlight vs window = {Ratio(hl, win):0.00}:1 (these are the CURRENT system colours, not a contrast theme)");
        }
        catch (Exception ex) { W("      GetSysColor failed " + ex.Message); }
    }

    private static Color SysColor(int i)
    { uint v = GetSysColor(i); return Color.FromArgb(255, (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF)); }
    private static string SysHex(int i) => Hex(SysColor(i));

    // ================================================================= Q16
    private async Task Q16_HeaderRegistration()
    {
        Section("Q16  Header panel as a SIBLING of the ListView, same panel type, same table-owned offset.\n" +
                "    Do the header cell boundaries line up with the row cell boundaries at offset 0 and 1200?");

        foreach (var (label, count, sbv) in new (string, int, ScrollBarVisibility)[]
        {
            ("6 items, no vertical scrollbar needed, ScrollBarVisibility=Auto", 6, ScrollBarVisibility.Auto),
            ("10000 items, vertical scrollbar needed, ScrollBarVisibility=Auto", 10000, ScrollBarVisibility.Auto),
            ("10000 items, ScrollBarVisibility=Visible", 10000, ScrollBarVisibility.Visible),
        })
        {
            RowPanel.SharedOffset = 0;
            var grid = new Grid { Width = 800, Height = 300 };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new RowPanel();
            for (int i = 0; i < RowPanel.ColumnCount; i++)
                header.Children.Add(new Border
                {
                    Background = new SolidColorBrush(i % 2 == 0 ? Color.FromArgb(255, 60, 60, 60) : Color.FromArgb(255, 200, 200, 200)),
                    Child = new TextBlock { Margin = new Thickness(4), Text = "h" + i, Foreground = new SolidColorBrush(i % 2 == 0 ? Colors.White : Colors.Black) }
                });
            var headerHost = new Grid { Height = 28, Background = new SolidColorBrush(Colors.White) };
            headerHost.Children.Add(header);
            headerHost.Clip = new RectangleGeometry { Rect = new Rect(0, 0, 800, 28) };
            Grid.SetRow(headerHost, 0);
            grid.Children.Add(headerHost);

            var lv = new ListView
            {
                SelectionMode = ListViewSelectionMode.Multiple,
                IsMultiSelectCheckBoxEnabled = false,
                ItemsSource = MakeItems(count),
                ItemTemplate = (DataTemplate)Resources["CellRowTemplate"],
                ItemContainerStyle = (Style)Resources["DesignRowStyle"],
                ItemsPanel = (ItemsPanelTemplate)Resources["IspPanel3b"],
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
            };
            ScrollViewer.SetHorizontalScrollMode(lv, ScrollMode.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(lv, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(lv, sbv);
            Grid.SetRow(lv, 1);
            grid.Children.Add(lv);

            await ShowAsync(grid, 800);

            W("");
            W($"---- {label} ----");
            var sv = FindDescendant<ScrollViewer>(lv);
            var c0 = lv.ContainerFromIndex(0) as ListViewItem;
            var rp = c0?.ContentTemplateRoot as RowPanel;
            if (rp is null) { W("    row panel not found"); continue; }

            W($"    ListView actual width = {lv.ActualWidth}  header host width = {headerHost.ActualWidth}");
            W($"    inner ScrollViewer ViewportWidth = {sv?.ViewportWidth:0.##} ExtentWidth = {sv?.ExtentWidth:0.##} " +
              $"ScrollableWidth = {sv?.ScrollableWidth:0.##} ComputedVerticalScrollBarVisibility = {sv?.ComputedVerticalScrollBarVisibility}");
            W($"    header panel arrange width = {header.LastArrangeFinal.Width}   row panel arrange width = {rp.LastArrangeFinal.Width}");
            W($"    container 0 size = {c0!.ActualWidth}x{c0.ActualHeight}  Padding={c0.Padding}  BorderThickness={c0.BorderThickness}");
            W($"    row panel x in the Grid = {Origin(rp, grid).X:0.###}   header panel x in the Grid = {Origin(header, grid).X:0.###}");

            foreach (double off in new double[] { 0, 1200 })
            {
                RowPanel.SharedOffset = off;
                foreach (var p in RowPanel.Live) p.InvalidateArrange();
                header.InvalidateArrange();
                await Settle(350);

                var hx = new List<double>(); var rx = new List<double>();
                for (int i = 0; i < RowPanel.ColumnCount; i++)
                {
                    hx.Add(Origin(header.Children[i], grid).X);
                    rx.Add(Origin(rp.Children[i], grid).X);
                }
                W($"    offset {off}:");
                W($"      header cell x = {string.Join(" ", hx.Select(v => v.ToString("0.###")))}");
                W($"      row    cell x = {string.Join(" ", rx.Select(v => v.ToString("0.###")))}");
                W($"      delta row-header = {string.Join(" ", hx.Select((v, i) => (rx[i] - v).ToString("0.###")))}");

                var shot = await CaptureAsync(grid);
                W($"      pixel scan, header strip y=14: {ColorChanges(shot, 14, 0, 800)}");
                double rowY = Origin(rp, grid).Y + rp.ActualHeight / 2;
                W($"      pixel scan, row 0     y={rowY:0}: {ColorChanges(shot, rowY, 0, 800)}");
            }
        }
        RowPanel.SharedOffset = 0;
    }

    private string ColorChanges(Shot s, double dipY, double x0, double x1)
    {
        if (!s.Ok) return "capture failed " + s.Err;
        var sb = new StringBuilder(); string last = "";
        for (double x = x0; x <= x1; x += 0.5)
        {
            var c = At(s, x, dipY);
            string t = Hex(c);
            if (t != last) { sb.Append($" x{x:0.#}={t}"); last = t; }
        }
        return sb.Length == 0 ? "(nothing)" : sb.ToString();
    }

    // ================================================================= Q17
    private async Task Q17_IncrementalChanges()
    {
        Section("Q17  Incremental collection changes under Multiple with rows 2, 5 and 9 selected.\n" +
                "    Add, Remove, Move and Replace, each on a freshly seeded list.");

        async Task Run(string name, Action<ObservableCollection<ProbeItem>> op)
        {
            var src = new ObservableCollection<ProbeItem>(MakeItems(12));
            var lv = new ListView
            {
                SelectionMode = ListViewSelectionMode.Multiple,
                IsMultiSelectCheckBoxEnabled = false,
                ItemsSource = src,
                ItemTemplate = (DataTemplate)Resources["PlainRowTemplate"],
                ItemContainerStyle = (Style)Resources["DesignRowStyle"],
                ItemsPanel = (ItemsPanelTemplate)Resources["IspPanel3b"],
                Width = 600,
                Height = 420,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
            };
            int events = 0, added = 0, removed = 0;
            lv.SelectionChanged += (s, e) => { events++; added += e.AddedItems.Count; removed += e.RemovedItems.Count; };
            await ShowAsync(lv, 550);
            lv.SelectedItems.Add(src[2]); lv.SelectedItems.Add(src[5]); lv.SelectedItems.Add(src[9]);
            await Settle(300);
            var before = Sel(lv);
            var beforeRefs = lv.SelectedItems.Cast<ProbeItem>().Select(p => p.Index).OrderBy(i => i).ToList();
            events = 0; added = 0; removed = 0;
            var f = (ListViewItem?)lv.ContainerFromIndex(0);
            op(src);
            await Settle(500);
            W("");
            W($"  {name}");
            W($"    BEFORE SelectedItems = {before}   (id N @ index M)");
            W($"    AFTER  SelectedItems({lv.SelectedItems.Count}) = {Sel(lv)}   SelectedIndex={lv.SelectedIndex}");
            var afterRefs = lv.SelectedItems.Cast<ProbeItem>().Select(p => p.Index).OrderBy(i => i).ToList();
            W($"    selected ids before = [{string.Join(",", beforeRefs)}]  after = [{string.Join(",", afterRefs)}]  " +
              $"{(beforeRefs.SequenceEqual(afterRefs) ? "IDENTICAL set of items" : "the set of items CHANGED")}");
            W($"    SelectionChanged during the operation: {events} event(s), {added} added, {removed} removed");
            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            string fd = "(none)";
            while (focused is not null)
            { if (focused is ListViewItem li) { fd = $"ListViewItem index {lv.IndexFromContainer(li)} FocusState={li.FocusState}"; break; } focused = VisualTreeHelper.GetParent(focused); }
            W($"    focus after = {fd}");
            var visualSel = new List<string>();
            for (int i = 0; i < Math.Min(src.Count, 14); i++)
                if (lv.ContainerFromIndex(i) is ListViewItem ci && ci.IsSelected) visualSel.Add(i.ToString());
            W($"    containers reporting IsSelected=true at indices: [{string.Join(",", visualSel)}]");
        }

        await Run("Add: src.Add(new item) appended at the end", s => s.Add(new ProbeItem { Index = 100, Name = "item 100" }));
        await Run("Add: src.Insert(0, new item) in front of every selected row", s => s.Insert(0, new ProbeItem { Index = 101, Name = "item 101" }));
        await Run("Remove: src.RemoveAt(0), an UNSELECTED row before the selection", s => s.RemoveAt(0));
        await Run("Remove: src.RemoveAt(5), a SELECTED row", s => s.RemoveAt(5));
        await Run("Move: src.Move(9, 0), a selected row to the top", s => s.Move(9, 0));
        await Run("Move: src.Move(0, 11), an unselected row past the selection", s => s.Move(0, 11));
        await Run("Move: src.Move(2, 3), a selected row down by one", s => s.Move(2, 3));
        await Run("Replace: src[5] = new item, replacing a SELECTED row", s => s[5] = new ProbeItem { Index = 105, Name = "item 105" });
        await Run("Replace: src[0] = new item, replacing an UNSELECTED row", s => s[0] = new ProbeItem { Index = 100, Name = "item 100b" });
        await Run("Reset: Clear() then re-add the SAME references in the same order", s =>
        {
            var copy = s.ToList();
            s.Clear();
            foreach (var i in copy) s.Add(i);
        });

        W("");
        W("  A sort produces a run of Move operations. The same list is sorted by reversing it with Move calls:");
        await Run("Sort simulation: 11 Move calls that reverse the list", s =>
        {
            for (int i = 0; i < s.Count; i++) s.Move(s.Count - 1, i);
        });
    }

    // ================================================================= Q18
    /// <summary>ObservableCollection that swaps its whole content and raises exactly one Reset.</summary>
    private sealed class SnapshotCollection : ObservableCollection<ProbeItem>
    {
        public void ReplaceAll(IEnumerable<ProbeItem> items)
        {
            Items.Clear();
            foreach (var i in items) Items.Add(i);
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    private async Task Q18_FocusRelocationScroll()
    {
        Section("Q18  Does the Reset-driven focus relocation scroll the view?\n" +
                "    10000 items, scrolled to about row 3674, then a Reset.");

        var src = new SnapshotCollection();
        foreach (var i in MakeItems(10000)) src.Add(i);
        var lv = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            IsMultiSelectCheckBoxEnabled = false,
            ItemsSource = src,
            ItemTemplate = (DataTemplate)Resources["PlainRowTemplate"],
            ItemContainerStyle = (Style)Resources["DesignRowStyle"],
            ItemsPanel = (ItemsPanelTemplate)Resources["IspPanel3b"],
            Width = 600,
            Height = 400,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
        };
        ScrollViewer.SetHorizontalScrollMode(lv, ScrollMode.Disabled);
        await ShowAsync(lv, 800);
        var sv = FindDescendant<ScrollViewer>(lv)!;
        var actions = new List<string>();
        src.CollectionChanged += (s2, e2) => actions.Add(e2.Action.ToString());

        lv.ScrollIntoView(src[3674]);
        await Settle(600);
        lv.ScrollIntoView(src[3674]);
        await Settle(600);
        W($"  after two ScrollIntoView(item 3674): VerticalOffset={sv.VerticalOffset:0.##} " +
          $"ScrollableHeight={sv.ScrollableHeight:0.##} ExtentHeight={sv.ExtentHeight:0.##}");

        var firstRealized = FirstRealizedIndex(lv);
        W($"  first realized container index = {firstRealized}");
        if (lv.ContainerFromIndex(firstRealized + 2) is ListViewItem cf)
        { cf.Focus(FocusState.Keyboard); await Settle(300); W($"  focus put on container index {firstRealized + 2}, FocusState={cf.FocusState}"); }
        lv.SelectedItems.Add(src[3674]);
        await Settle(300);

        double v0 = sv.VerticalOffset;
        W("");
        W("  --- Reset by ReplaceAll: one Reset event, brand new item instances ---");
        W($"    BEFORE VerticalOffset = {v0:0.##}   SelectedItems={lv.SelectedItems.Count}");
        actions.Clear();
        src.ReplaceAll(MakeItems(10000));
        await Task.Delay(60);
        W($"    collection events raised = [{string.Join(",", actions)}]");
        W($"    immediately after the Reset call (no layout pass yet) VerticalOffset = {sv.VerticalOffset:0.##}");
        await Settle(900);
        W($"    AFTER  VerticalOffset = {sv.VerticalOffset:0.##}   ExtentHeight={sv.ExtentHeight:0.##}   SelectedItems={lv.SelectedItems.Count}");
        W($"    first realized container index now = {FirstRealizedIndex(lv)}");
        W($"    focus now = {FocusDesc(lv)}");

        // second reset, this time with the SAME instances
        lv.ScrollIntoView(src[3674]); await Settle(500);
        lv.ScrollIntoView(src[3674]); await Settle(500);
        double v1 = sv.VerticalOffset;
        var same = src.ToList();
        W("");
        W("  --- Reset by ReplaceAll with the SAME instances in the same order ---");
        W($"    BEFORE VerticalOffset = {v1:0.##}");
        actions.Clear();
        src.ReplaceAll(same);
        await Settle(900);
        W($"    collection events raised = [{string.Join(",", actions)}]");
        W($"    AFTER  VerticalOffset = {sv.VerticalOffset:0.##}   first realized index = {FirstRealizedIndex(lv)}");
        W($"    focus now = {FocusDesc(lv)}");
        actions.Clear();
        src.ReplaceAll(src.ToList());
        await Settle(900);
        W($"    repeated once more: events=[{string.Join(",", actions)}] VerticalOffset = {sv.VerticalOffset:0.##} " +
          $"first realized index = {FirstRealizedIndex(lv)}");

        // third: Clear() + re-add, the shape used in round 2
        lv.ScrollIntoView(src[3674]); await Settle(500);
        lv.ScrollIntoView(src[3674]); await Settle(500);
        double v2 = sv.VerticalOffset;
        W("");
        W("  --- Clear() then re-add all 10000 items one by one ---");
        W($"    BEFORE VerticalOffset = {v2:0.##}");
        var copy = src.ToList();
        src.Clear();
        await Task.Delay(80);
        W($"    right after Clear(): VerticalOffset = {sv.VerticalOffset:0.##} Items={lv.Items.Count}");
        foreach (var i in copy) src.Add(i);
        await Settle(1200);
        W($"    AFTER  VerticalOffset = {sv.VerticalOffset:0.##}   first realized index = {FirstRealizedIndex(lv)}   Items={lv.Items.Count}");
        W($"    focus now = {FocusDesc(lv)}");

        // does the view come back if nothing has focus at all?
        lv.ScrollIntoView(src[3674]); await Settle(500);
        lv.ScrollIntoView(src[3674]); await Settle(500);
        double v3 = sv.VerticalOffset;
        var btn = new Button { Content = "elsewhere", Width = 120 };
        Host.Children.Add(btn);
        btn.Focus(FocusState.Programmatic);
        await Settle(300);
        W("");
        W("  --- same Reset, but focus is on a Button outside the ListView ---");
        W($"    BEFORE VerticalOffset = {v3:0.##}  focus = {FocusDesc(lv)}");
        src.ReplaceAll(MakeItems(10000));
        await Settle(900);
        W($"    AFTER  VerticalOffset = {sv.VerticalOffset:0.##}   first realized index = {FirstRealizedIndex(lv)}");
        W($"    focus now = {FocusDesc(lv)}");
    }

    private static int FirstRealizedIndex(ListView lv)
    {
        var isp = lv.ItemsPanelRoot as ItemsStackPanel;
        return isp?.FirstVisibleIndex ?? -1;
    }

    private string FocusDesc(ListView lv)
    {
        var f = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        string type = f?.GetType().Name ?? "(null)";
        var d = f;
        while (d is not null)
        { if (d is ListViewItem li) return $"{type} inside ListViewItem index {lv.IndexFromContainer(li)} FocusState={li.FocusState}"; d = VisualTreeHelper.GetParent(d); }
        return $"{type}, not inside any ListViewItem";
    }
}
