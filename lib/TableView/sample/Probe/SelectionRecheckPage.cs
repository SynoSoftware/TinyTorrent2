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
using Windows.UI;

namespace Syno.TableViewSample.Probe;

/// <summary>
/// Independent re-run of the selection probe's questions Q13..Q18. Written from scratch so
/// that a method error in that probe cannot be reproduced by reusing its code.
/// </summary>
public sealed partial class SelectionRecheckPage : Page
{
    private readonly StringBuilder _log = new();
    private double _scale = 1.0;
    private bool _finished;
    private IntPtr _hwnd,
        _site;
    private static readonly string Out = System.IO.Path.Combine(
        AppContext.BaseDirectory,
        "selection-recheck-results.txt"
    );

    public SelectionRecheckPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void W(string s) => _log.AppendLine(s);

    private void Sec(string s)
    {
        W("");
        W(new string('=', 92));
        W(s);
        W(new string('=', 92));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var wd = DispatcherQueue.CreateTimer();
        wd.Interval = TimeSpan.FromSeconds(900);
        wd.IsRepeating = false;
        wd.Tick += (_, _) =>
        {
            W("WATCHDOG fired, run incomplete.");
            Finish();
        };
        wd.Start();

        _scale = XamlRoot?.RasterizationScale ?? 1.0;
        RequestedTheme = ElementTheme.Light;
        W($"selection recheck started {DateTime.Now:O}  RasterizationScale={_scale}");
        try
        {
            var w = MainWindow.Instance!;
            w.AppWindow.ResizeClient(
                new Windows.Graphics.SizeInt32((int)(960 * _scale), (int)(800 * _scale))
            );
            w.AppWindow.Move(new Windows.Graphics.PointInt32(60, 60));
            w.Activate();
        }
        catch (Exception ex)
        {
            W("resize/move failed " + ex.Message);
        }
        await Settle(700);

        try
        {
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance!);
        }
        catch { }
        foreach (var k in ChildWindows(_hwnd))
            if (ClassOf(k) == "InputSiteWindowClass")
                _site = k;
        W(
            $"top-level HWND 0x{_hwnd.ToInt64():X}   InputSiteWindowClass HWND 0x{_site.ToInt64():X}"
        );

        await G("ENV", EnvCheck);
        await G("Q13/Q14 input", InputCapability);
        await G("Q14k", KeyboardRange);
        await G("Q15", Contrast);
        await G("Q16", HeaderRegistration);
        await G("Q17", CollectionChanges);
        await G("Q18", ResetScroll);

        wd.Stop();
        Finish();
    }

    private async Task G(string n, Func<Task> b)
    {
        try
        {
            await b();
        }
        catch (Exception ex)
        {
            W($"*** {n} THREW {ex.GetType().Name}: {ex.Message}");
            W(ex.StackTrace ?? "");
        }
    }

    private void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        W($"selection recheck finished {DateTime.Now:O}");
        try
        {
            File.WriteAllText(Out, _log.ToString());
        }
        catch { }
        Application.Current.Exit();
    }

    private async Task Settle(int ms = 250)
    {
        Host.UpdateLayout();
        await Task.Delay(ms);
        Host.UpdateLayout();
        await Task.Delay(80);
    }

    private async Task ShowAsync(UIElement el, int ms = 500)
    {
        Host.Children.Clear();
        RowPanel.Live.Clear();
        if (el is FrameworkElement fe)
        {
            fe.HorizontalAlignment = HorizontalAlignment.Left;
            fe.VerticalAlignment = VerticalAlignment.Top;
        }
        Host.Children.Add(el);
        await Settle(ms);
    }

    private static List<Item> MakeItems(int n)
    {
        var l = new List<Item>(n);
        for (int i = 0; i < n; i++)
            l.Add(new Item { Index = i, Name = "item " + i });
        return l;
    }

    private static T? Descendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t)
                return t;
            var r = Descendant<T>(c);
            if (r is not null)
                return r;
        }
        return null;
    }

    private static Point Origin(UIElement el, UIElement rel) =>
        el.TransformToVisual(rel).TransformPoint(new Point(0, 0));

    // ---------------------------------------------------------------- capture
    private sealed class Shot
    {
        public byte[] Px = Array.Empty<byte>();
        public int PW,
            PH;
        public double DW,
            DH;
        public bool Ok;
        public string Err = "";
    }

    private static async Task<Shot> CaptureAsync(FrameworkElement el)
    {
        var s = new Shot { DW = el.ActualWidth, DH = el.ActualHeight };
        try
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(el);
            var buf = await rtb.GetPixelsAsync();
            s.Px = new byte[buf.Length];
            using (var rd = Windows.Storage.Streams.DataReader.FromBuffer(buf))
                rd.ReadBytes(s.Px);
            s.PW = rtb.PixelWidth;
            s.PH = rtb.PixelHeight;
            s.Ok = s.Px.Length >= 4;
            if (!s.Ok)
                s.Err = "empty buffer";
        }
        catch (Exception ex)
        {
            s.Err = ex.GetType().Name + " " + ex.Message;
        }
        return s;
    }

    private static Color At(Shot s, double dipX, double dipY)
    {
        if (!s.Ok || s.DW <= 0 || s.DH <= 0)
            return Colors.Transparent;
        int x = (int)Math.Round(dipX * s.PW / s.DW);
        int y = (int)Math.Round(dipY * s.PH / s.DH);
        x = Math.Clamp(x, 0, s.PW - 1);
        y = Math.Clamp(y, 0, s.PH - 1);
        int i = (y * s.PW + x) * 4;
        return Color.FromArgb(s.Px[i + 3], s.Px[i + 2], s.Px[i + 1], s.Px[i + 0]);
    }

    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    // WCAG 2.x relative luminance and contrast ratio, from measured sRGB.
    private static double Lin(double c) =>
        c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static double Lum(Color c) =>
        0.2126 * Lin(c.R / 255.0) + 0.7152 * Lin(c.G / 255.0) + 0.0722 * Lin(c.B / 255.0);

    private static double Ratio(Color a, Color b)
    {
        double la = Lum(a),
            lb = Lum(b);
        double hi = Math.Max(la, lb),
            lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    // ---------------------------------------------------------------- interop
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr h);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, char[] b, int n);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr p, EnumChildProc cb, IntPtr l);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr h, ref POINT p);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr h, ref POINT p);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint f, bool inh, uint acc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(
        IntPtr h,
        int i,
        byte[] b,
        int len,
        out int need
    );

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr h);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint c, uint t);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int vk);

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] s);

    [DllImport("user32.dll")]
    private static extern bool SetKeyboardState(byte[] s);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X,
            Y;
    }

    private delegate bool EnumChildProc(IntPtr h, IntPtr p);

    private const int WM_LBUTTONDOWN = 0x0201,
        WM_LBUTTONUP = 0x0202,
        WM_MOUSEMOVE = 0x0200,
        WM_MOUSEWHEEL = 0x020A;
    private const int WM_KEYDOWN = 0x0100,
        WM_KEYUP = 0x0101;
    private const int MK_LBUTTON = 0x0001,
        MK_SHIFT = 0x0004,
        MK_CONTROL = 0x0008;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002,
        MOUSEEVENTF_LEFTUP = 0x0004;
    private const byte VK_SHIFT = 0x10,
        VK_CONTROL = 0x11,
        VK_SPACE = 0x20,
        VK_DOWN = 0x28,
        VK_A = 0x41;

    private static string ClassOf(IntPtr h)
    {
        var b = new char[256];
        int n = GetClassName(h, b, b.Length);
        return n > 0 ? new string(b, 0, n) : "(none)";
    }

    private static List<IntPtr> ChildWindows(IntPtr p)
    {
        var l = new List<IntPtr>();
        EnumChildWindows(
            p,
            (h, _) =>
            {
                l.Add(h);
                return true;
            },
            IntPtr.Zero
        );
        return l;
    }

    private static string DesktopName()
    {
        var d = OpenInputDesktop(0, false, 0x0001);
        if (d == IntPtr.Zero)
            return $"OpenInputDesktop FAILED (err {Marshal.GetLastWin32Error()})";
        var buf = new byte[256];
        bool ok = GetUserObjectInformation(d, 2, buf, buf.Length, out int need);
        CloseDesktop(d);
        return ok ? Encoding.Unicode.GetString(buf, 0, Math.Max(0, need - 2)) : "unreadable";
    }

    /// <summary>DIP point inside an element to a physical screen point.</summary>
    private POINT ScreenPointOf(UIElement el, double dx, double dy)
    {
        var w = el.TransformToVisual(null).TransformPoint(new Point(dx, dy));
        var p = new POINT { X = (int)Math.Round(w.X * _scale), Y = (int)Math.Round(w.Y * _scale) };
        ClientToScreen(_hwnd, ref p);
        return p;
    }

    private async Task EnvCheck()
    {
        Sec("ENVIRONMENT. Whether real pointer input can reach this window at all.");
        W($"input desktop name = '{DesktopName()}'");
        W($"LogonUI processes = {System.Diagnostics.Process.GetProcessesByName("LogonUI").Length}");
        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint fgpid);
        W(
            $"GetForegroundWindow = 0x{fg.ToInt64():X} class='{ClassOf(fg)}' pid={fgpid}   our pid={Environment.ProcessId}"
        );
        GetCursorPos(out var cur);
        W($"cursor at ({cur.X},{cur.Y})");
        await Task.CompletedTask;
    }

    // ================================================================= Q13 / Q14
    private async Task InputCapability()
    {
        Sec(
            "Q13 / Q14. Pointer semantics under Multiple.\n"
                + "    Step 1 proves whether any click route reaches a plain Button.\n"
                + "    The table questions only run if a route is proven."
        );

        int clicks = 0;
        string order = "";
        var btn = new Button
        {
            Content = "target",
            Width = 260,
            Height = 64,
            Margin = new Thickness(24),
        };
        btn.Click += (_, _) => clicks++;
        btn.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => order += "pressed "),
            true
        );
        await ShowAsync(btn, 500);
        SetForegroundWindow(_hwnd);
        await Settle(300);

        var pt = ScreenPointOf(btn, 130, 32);
        var over = WindowFromPoint(pt);
        GetWindowThreadProcessId(over, out uint opid);
        bool ours = opid == (uint)Environment.ProcessId;
        W($"button centre screen point ({pt.X},{pt.Y})");
        W(
            $"WindowFromPoint there = 0x{over.ToInt64():X} class='{ClassOf(over)}' pid={opid}  isOurs={ours}"
        );

        // Route A. Real injection. Only when the point is genuinely over our own window,
        // otherwise the click would land in another application.
        if (ours)
        {
            GetCursorPos(out var saved);
            clicks = 0;
            order = "";
            SetCursorPos(pt.X, pt.Y);
            await Task.Delay(150);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(90);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            await Settle(350);
            W($"route A real injection: PointerPressed='{order.Trim()}' Click count={clicks}");
            SetCursorPos(saved.X, saved.Y);
        }
        else
            W(
                "route A real injection: SKIPPED. The point is not over our window, so a real click would hit another application."
            );

        // Positive control: is the app reachable by any message at all?
        var inner = new Border
        {
            Width = 300,
            Height = 2000,
            Background = new SolidColorBrush(Colors.LightGray),
        };
        var sv = new ScrollViewer
        {
            Width = 320,
            Height = 200,
            Content = inner,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var btn2 = new Button
        {
            Content = "target2",
            Width = 220,
            Height = 56,
            Margin = new Thickness(0, 230, 0, 0),
        };
        int clicks2 = 0;
        btn2.Click += (_, _) => clicks2++;
        var g = new Grid { Width = 700, Height = 320 };
        g.Children.Add(sv);
        g.Children.Add(btn2);
        await ShowAsync(g, 500);

        var wp = ScreenPointOf(sv, 160, 100);
        double v0 = sv.VerticalOffset;
        SendMessage(
            _site,
            WM_MOUSEWHEEL,
            (IntPtr)(-120 << 16),
            (IntPtr)((wp.Y << 16) | (wp.X & 0xFFFF))
        );
        await Settle(400);
        W(
            $"positive control WM_MOUSEWHEEL to input site: VerticalOffset {v0} -> {sv.VerticalOffset}  "
                + (sv.VerticalOffset != v0 ? "MESSAGES ARE DELIVERED" : "no effect")
        );

        // Route B. Synthetic mouse-button messages. lParam is CLIENT space of the receiving window.
        var bp = ScreenPointOf(btn2, 110, 28);
        var targets = new List<(string, IntPtr)>
        {
            ("InputSiteWindowClass", _site),
            ("top-level", _hwnd),
        };
        foreach (var k in ChildWindows(_hwnd))
            if (ClassOf(k).Contains("DesktopChildSiteBridge"))
                targets.Add(("DesktopChildSiteBridge", k));
        bool msgRoute = false;
        foreach (var (name, t) in targets)
        {
            if (t == IntPtr.Zero)
                continue;
            foreach (bool post in new[] { false, true })
            {
                clicks2 = 0;
                var p = bp;
                ScreenToClient(t, ref p);
                IntPtr lp = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
                if (post)
                {
                    PostMessage(t, WM_MOUSEMOVE, IntPtr.Zero, lp);
                    PostMessage(t, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lp);
                    await Task.Delay(90);
                    PostMessage(t, WM_LBUTTONUP, IntPtr.Zero, lp);
                }
                else
                {
                    SendMessage(t, WM_MOUSEMOVE, IntPtr.Zero, lp);
                    SendMessage(t, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lp);
                    await Task.Delay(90);
                    SendMessage(t, WM_LBUTTONUP, IntPtr.Zero, lp);
                }
                await Settle(300);
                W(
                    $"route B {(post ? "Post" : "Send")} WM_LBUTTONDOWN/UP to {name} at client ({p.X},{p.Y}): Click count={clicks2}"
                );
                if (clicks2 > 0)
                    msgRoute = true;
            }
        }

        W("");
        W(
            msgRoute
                ? "A click route was proven. Q13 and Q14 now run."
                : "NO CLICK ROUTE REACHES THE APP. Q13 and Q14 cannot be measured in this session."
        );
        if (!msgRoute)
            return;

        await Q13Body();
    }

    /// <summary>Only reached when a click route is proven.</summary>
    private async Task Q13Body()
    {
        var src = MakeItems(10);
        string trace = "";
        var lv = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            IsMultiSelectCheckBoxEnabled = false,
            ItemsSource = src,
            ItemTemplate = (DataTemplate)Resources["R4PlainRow"],
            ItemContainerStyle = (Style)Resources["R4RowStyle"],
            ItemsPanel = (ItemsPanelTemplate)Resources["R4Isp"],
            Width = 600,
            Height = 380,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
        };
        bool swallow = false;
        lv.ContainerContentChanging += (s, e) =>
        {
            if (e.ItemContainer.ContentTemplateRoot is FrameworkElement root)
            {
                root.PointerPressed -= OnRowPressed;
                root.PointerPressed += OnRowPressed;
            }
        };
        void OnRowPressed(object s, PointerRoutedEventArgs e)
        {
            trace += $"content({((FrameworkElement)s).DataContext}) ";
            if (swallow)
                e.Handled = true;
        }

        await ShowAsync(lv, 600);
        // Hook every realized container, not one.
        var conts = new List<ListViewItem>();
        for (int i = 0; i < src.Count; i++)
            if (lv.ContainerFromIndex(i) is ListViewItem c)
            {
                conts.Add(c);
                c.AddHandler(
                    UIElement.PointerPressedEvent,
                    new PointerEventHandler(
                        (s2, _) => trace += $"container({lv.IndexFromContainer((ListViewItem)s2)}) "
                    ),
                    true
                );
                if (c.ContentTemplateRoot is FrameworkElement r)
                {
                    r.PointerPressed -= OnRowPressed;
                    r.PointerPressed += OnRowPressed;
                }
            }
        W($"realized containers hooked = {conts.Count}");

        async Task Click(int index, bool ctrl, bool shift)
        {
            var c = (ListViewItem)lv.ContainerFromIndex(index);
            var p = ScreenPointOf(c, c.ActualWidth / 2, c.ActualHeight / 2);
            var t = _site;
            ScreenToClient(t, ref p);
            IntPtr lp = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
            int mk = MK_LBUTTON | (ctrl ? MK_CONTROL : 0) | (shift ? MK_SHIFT : 0);
            SendMessage(t, WM_MOUSEMOVE, IntPtr.Zero, lp);
            SendMessage(t, WM_LBUTTONDOWN, (IntPtr)mk, lp);
            await Task.Delay(90);
            SendMessage(t, WM_LBUTTONUP, (IntPtr)(mk & ~MK_LBUTTON), lp);
            await Settle(350);
        }

        string Sel() =>
            "[" + string.Join(",", lv.SelectedItems.Cast<Item>().Select(p => p.Index)) + "]";

        trace = "";
        swallow = false;
        await Click(3, false, false);
        W($"Q13 a) plain click on row 3: event order = '{trace.Trim()}'  SelectedItems={Sel()}");

        lv.SelectedItems.Clear();
        await Settle(200);
        trace = "";
        swallow = true;
        await Click(4, false, false);
        W(
            $"Q13 b) click on row 4 with e.Handled=true on the row content: order='{trace.Trim()}' SelectedItems={Sel()}"
        );
        swallow = false;

        lv.SelectedItems.Clear();
        await Settle(200);
        trace = "";
        await Click(2, false, false);
        string s1 = Sel();
        await Click(7, false, true);
        W($"Q14 a) click row 2 then Shift+click row 7: after first {s1}, after shift {Sel()}");

        lv.SelectedItems.Clear();
        await Settle(200);
        await Click(2, false, false);
        await Click(7, true, false);
        W($"Q14 b) click row 2 then Ctrl+click row 7: SelectedItems={Sel()}");
    }

    // ================================================================= Q14 keyboard substitute
    private async Task KeyboardRange()
    {
        Sec(
            "Q14 keyboard substitute. Posted key messages do not set the thread key state, so the\n"
                + "    modifier is written directly into this UI thread's key state table with SetKeyboardState\n"
                + "    before the key is posted. Whether that reaches WinUI is measured, not assumed."
        );

        var src = MakeItems(10);
        var lv = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            IsMultiSelectCheckBoxEnabled = false,
            ItemsSource = src,
            ItemTemplate = (DataTemplate)Resources["R4PlainRow"],
            ItemContainerStyle = (Style)Resources["R4RowStyle"],
            ItemsPanel = (ItemsPanelTemplate)Resources["R4Isp"],
            Width = 600,
            Height = 380,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
        };
        string seen = "";
        lv.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(
                (_, e) =>
                {
                    var st = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                        Windows.System.VirtualKey.Shift
                    );
                    var ct = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                        Windows.System.VirtualKey.Control
                    );
                    seen +=
                        $"[{e.Key} shift={st} ctrl={ct} gks={GetKeyState(VK_SHIFT):X4}/{GetKeyState(VK_CONTROL):X4}] ";
                }
            ),
            true
        );
        await ShowAsync(lv, 600);

        string Sel() =>
            "[" + string.Join(",", lv.SelectedItems.Cast<Item>().Select(p => p.Index)) + "]";

        void SetMod(byte vk, bool down)
        {
            var st = new byte[256];
            GetKeyboardState(st);
            st[vk] = down ? (byte)0x80 : (byte)0x00;
            SetKeyboardState(st);
        }

        async Task Key(byte vk, bool shift = false, bool ctrl = false)
        {
            if (shift)
                SetMod(VK_SHIFT, true);
            if (ctrl)
                SetMod(VK_CONTROL, true);
            uint scan = MapVirtualKey(vk, 0);
            PostMessage(_site, WM_KEYDOWN, (IntPtr)vk, (IntPtr)(int)(1 | (scan << 16)));
            await Task.Delay(120);
            PostMessage(
                _site,
                WM_KEYUP,
                (IntPtr)vk,
                (IntPtr)(int)(1 | (scan << 16) | (1 << 30) | (1 << 31))
            );
            await Task.Delay(120);
            if (shift)
                SetMod(VK_SHIFT, false);
            if (ctrl)
                SetMod(VK_CONTROL, false);
            await Settle(250);
        }

        var c2 = lv.ContainerFromIndex(2) as ListViewItem;
        c2?.Focus(FocusState.Keyboard);
        await Settle(300);
        W(
            $"focus placed on container 2: {(c2 is not null ? c2.FocusState.ToString() : "no container")}"
        );

        seen = "";
        await Key(VK_SPACE);
        W($"Space on focused row 2: SelectedItems={Sel()}  keydown trace={seen.Trim()}");

        seen = "";
        await Key(VK_DOWN, shift: true);
        await Key(VK_DOWN, shift: true);
        await Key(VK_DOWN, shift: true);
        W($"three Shift+Down: SelectedItems={Sel()}");
        W($"    keydown trace = {seen.Trim()}");
        W(
            "    if shift=Down appears in the trace the modifier reached WinUI; if shift=None it did not."
        );

        // Discriminator. Select row 2, then walk focus down to row 6 with PLAIN Down,
        // which must not change the selection, then press Shift+Down once.
        // An anchored range gives 2..7. A per-row add gives 2 and 7 only.
        lv.SelectedItems.Clear();
        await Settle(200);
        (lv.ContainerFromIndex(2) as ListViewItem)?.Focus(FocusState.Keyboard);
        await Settle(200);
        string Foc()
        {
            var f = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (f is not null)
            {
                if (f is ListViewItem li)
                    return "row " + lv.IndexFromContainer(li);
                f = VisualTreeHelper.GetParent(f);
            }
            return "(not a row)";
        }

        W("anchor test. Selection and focus are both read after every key.");
        await Key(VK_SPACE);
        W($"    Space          -> sel {Sel()} focus {Foc()}");
        for (int i = 0; i < 4; i++)
        {
            await Key(VK_DOWN);
            W($"    plain Down     -> sel {Sel()} focus {Foc()}");
        }
        await Key(VK_DOWN, shift: true);
        W($"    Shift+Down     -> sel {Sel()} focus {Foc()}");
        await Key(VK_DOWN, shift: true);
        W($"    Shift+Down     -> sel {Sel()} focus {Foc()}");
        await Key(
            0x26 /*VK_UP*/
            ,
            shift: true
        );
        W($"    Shift+Up       -> sel {Sel()} focus {Foc()}");
        await Key(0x26, shift: true);
        W($"    Shift+Up       -> sel {Sel()} focus {Foc()}");

        W("second anchor test, no plain Down in between.");
        lv.SelectedItems.Clear();
        await Settle(200);
        (lv.ContainerFromIndex(2) as ListViewItem)?.Focus(FocusState.Keyboard);
        await Settle(200);
        await Key(VK_SPACE);
        W($"    Space          -> sel {Sel()} focus {Foc()}");
        for (int i = 0; i < 3; i++)
        {
            await Key(VK_DOWN, shift: true);
            W($"    Shift+Down     -> sel {Sel()} focus {Foc()}");
        }
        for (int i = 0; i < 2; i++)
        {
            await Key(0x26, shift: true);
            W($"    Shift+Up       -> sel {Sel()} focus {Foc()}");
        }

        lv.SelectedItems.Clear();
        await Settle(200);
        (lv.ContainerFromIndex(2) as ListViewItem)?.Focus(FocusState.Keyboard);
        await Settle(200);
        seen = "";
        await Key(VK_A, ctrl: true);
        W($"Ctrl+A: SelectedItems count={lv.SelectedItems.Count}  trace={seen.Trim()}");
    }

    // ================================================================= Q15
    private async Task Contrast()
    {
        Sec(
            "Q15. Contrast of the selection cue and the focus cue, measured from RenderTargetBitmap\n"
                + "    pixels of the opaque page host. WCAG 2.x relative luminance, (L1+0.05)/(L2+0.05)."
        );

        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            RequestedTheme = theme;
            await Settle(500);

            var src = MakeItems(6);
            var lv = new ListView
            {
                SelectionMode = ListViewSelectionMode.Multiple,
                IsMultiSelectCheckBoxEnabled = false,
                ItemsSource = src,
                ItemTemplate = (DataTemplate)Resources["R4PlainRow"],
                ItemContainerStyle = (Style)Resources["R4RowStyle"],
                ItemsPanel = (ItemsPanelTemplate)Resources["R4Isp"],
                Width = 600,
                Height = 300,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
            };
            await ShowAsync(lv, 600);

            var c1 = lv.ContainerFromIndex(1) as ListViewItem;
            var c2 = lv.ContainerFromIndex(2) as ListViewItem;
            if (c1 is null || c2 is null)
            {
                W("no containers");
                continue;
            }
            W("");
            W($"---- theme {theme} ----");
            W(
                $"container 1 = {c1.ActualWidth}x{c1.ActualHeight} Padding={c1.Padding} MinHeight={c1.MinHeight}"
            );

            double y1 = Origin(c1, Host).Y,
                y2 = Origin(c2, Host).Y;
            double h = c1.ActualHeight;
            double midY1 = y1 + h / 2,
                midY2 = y2 + h / 2;

            var before = await CaptureAsync(Host);
            if (!before.Ok)
            {
                W("capture failed " + before.Err);
                continue;
            }
            W($"capture {before.PW}x{before.PH} px for {before.DW}x{before.DH} DIP");

            c1.IsSelected = true;
            await Settle(500);
            var after = await CaptureAsync(Host);

            // sample well right of the text so the pixel is container chrome only
            double sx = 420;
            var unselNow = At(after, sx, midY2); // row 2, still unselected, same capture
            var selNow = At(after, sx, midY1); // row 1, selected
            var unselBefore = At(before, sx, midY1); // row 1 before selection
            W($"row 1 before selection = {Hex(unselBefore)}");
            W($"row 1 selected         = {Hex(selNow)}");
            W($"row 2 unselected, same frame = {Hex(unselNow)}");
            W(
                $"contrast selected vs adjacent unselected row (same frame) = {Ratio(selNow, unselNow):0.000}:1"
            );
            W(
                $"contrast selected vs the same row before selection        = {Ratio(selNow, unselBefore):0.000}:1"
            );

            // strongest changed pixel anywhere across the row
            double bestR = 0;
            string bestS = "";
            int changed = 0;
            for (double x = 0; x < Math.Min(600, before.DW); x += 0.5)
            for (double dy = 1; dy < h - 1; dy += 1)
            {
                var a = At(before, x, y1 + dy);
                var b = At(after, x, y1 + dy);
                if (a.R == b.R && a.G == b.G && a.B == b.B)
                    continue;
                changed++;
                double r = Ratio(a, b);
                if (r > bestR)
                {
                    bestR = r;
                    bestS = $"x={x} y={y1 + dy:0} {Hex(a)} -> {Hex(b)}";
                }
            }
            W(
                $"pixels that changed when the row became selected = {changed}; strongest change {bestR:0.000}:1  ({bestS})"
            );

            // horizontal scan across the selected row, to see whether an accent bar is drawn
            var sb = new StringBuilder();
            string last = "";
            for (double x = 0; x < 40; x += 0.5)
            {
                var c = At(after, x, midY1);
                if (Hex(c) != last)
                {
                    sb.Append($" x{x:0.#}={Hex(c)}");
                    last = Hex(c);
                }
            }
            W($"left edge scan of the selected row, x 0..40: {sb}");

            // focus cue
            c1.IsSelected = false;
            await Settle(300);
            var noFocus = await CaptureAsync(Host);
            c1.Focus(FocusState.Keyboard);
            await Settle(400);
            var withFocus = await CaptureAsync(Host);
            double fBest = 0;
            string fStr = "";
            int fChanged = 0;
            for (double x = 0; x < Math.Min(600, before.DW); x += 0.5)
            for (double dy = 0; dy < h; dy += 0.5)
            {
                var a = At(noFocus, x, y1 + dy);
                var b = At(withFocus, x, y1 + dy);
                if (a.R == b.R && a.G == b.G && a.B == b.B)
                    continue;
                fChanged++;
                double r = Ratio(a, b);
                if (r > fBest)
                {
                    fBest = r;
                    fStr = $"x={x} y={y1 + dy:0} {Hex(a)} -> {Hex(b)}";
                }
            }
            W($"focus cue: pixels changed={fChanged}; strongest {fBest:0.000}:1  ({fStr})");

            // selected AND focused together
            c1.IsSelected = true;
            await Settle(400);
            var selFocus = await CaptureAsync(Host);
            W($"selected+focused body colour at x=420 = {Hex(At(selFocus, sx, midY1))}");
        }
        RequestedTheme = ElementTheme.Light;
        await Settle(300);
    }

    // ================================================================= Q16
    private async Task HeaderRegistration()
    {
        Sec(
            "Q16. Header panel as a sibling of the ListView, sharing one table-owned offset.\n"
                + "    Header and row cell origins are both transformed into the SAME ancestor, the outer Grid,\n"
                + "    and cross-checked against a pixel scan of one capture."
        );

        foreach (
            var (label, count) in new (string, int)[] { ("6 items", 6), ("10000 items", 10000) }
        )
        {
            RowPanel.SharedOffset = 0;
            var grid = new Grid { Width = 800, Height = 300 };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            );

            var header = new RowPanel();
            for (int i = 0; i < RowPanel.ColumnCount; i++)
                header.Children.Add(
                    new Border
                    {
                        Background = new SolidColorBrush(
                            i % 2 == 0
                                ? Color.FromArgb(255, 40, 40, 40)
                                : Color.FromArgb(255, 220, 220, 220)
                        ),
                        Child = new TextBlock
                        {
                            Margin = new Thickness(4),
                            Text = "h" + i,
                            Foreground = new SolidColorBrush(
                                i % 2 == 0 ? Colors.White : Colors.Black
                            ),
                        },
                    }
                );
            var headerHost = new Grid
            {
                Height = 28,
                Background = new SolidColorBrush(Colors.White),
            };
            headerHost.Children.Add(header);
            headerHost.Clip = new RectangleGeometry { Rect = new Rect(0, 0, 800, 28) };
            Grid.SetRow(headerHost, 0);
            grid.Children.Add(headerHost);

            var lv = new ListView
            {
                SelectionMode = ListViewSelectionMode.Multiple,
                IsMultiSelectCheckBoxEnabled = false,
                ItemsSource = MakeItems(count),
                ItemTemplate = (DataTemplate)Resources["R4CellRow"],
                ItemContainerStyle = (Style)Resources["R4RowStyle"],
                ItemsPanel = (ItemsPanelTemplate)Resources["R4Isp"],
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
            };
            ScrollViewer.SetHorizontalScrollMode(lv, ScrollMode.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(lv, ScrollBarVisibility.Disabled);
            Grid.SetRow(lv, 1);
            grid.Children.Add(lv);
            await ShowAsync(grid, 700);

            var sv = Descendant<ScrollViewer>(lv);
            var c0 = lv.ContainerFromIndex(0) as ListViewItem;
            var rp = c0?.ContentTemplateRoot as RowPanel;
            W("");
            W($"---- {label} ----");
            if (rp is null)
            {
                W("row panel not found");
                continue;
            }
            W(
                $"inner ScrollViewer Viewport={sv?.ViewportWidth:0.##} Extent={sv?.ExtentWidth:0.##} Scrollable={sv?.ScrollableWidth:0.##} "
                    + $"VScrollBar={sv?.ComputedVerticalScrollBarVisibility}"
            );
            W(
                $"container 0 = {c0!.ActualWidth}x{c0.ActualHeight}  header panel width={header.ActualWidth} arranged={header.LastArrangeFinal.Width}  row panel arranged={rp.LastArrangeFinal.Width}"
            );

            foreach (double off in new double[] { 0, 1200 })
            {
                RowPanel.SharedOffset = off;
                foreach (var p in RowPanel.Live)
                    p.InvalidateArrange();
                header.InvalidateArrange();
                await Settle(400);

                var hx = new List<double>();
                var rx = new List<double>();
                for (int i = 0; i < RowPanel.ColumnCount; i++)
                {
                    hx.Add(Origin(header.Children[i], grid).X);
                    rx.Add(Origin(rp.Children[i], grid).X);
                }
                W($"  offset {off}");
                W(
                    $"    header cell x (in grid space) = {string.Join(" ", hx.Select(v => v.ToString("0.###")))}"
                );
                W(
                    $"    row    cell x (in grid space) = {string.Join(" ", rx.Select(v => v.ToString("0.###")))}"
                );
                W(
                    $"    delta row - header            = {string.Join(" ", hx.Select((v, i) => (rx[i] - v).ToString("0.###")))}"
                );

                var shot = await CaptureAsync(grid);
                if (shot.Ok)
                {
                    double rowY = Origin(rp, grid).Y + rp.ActualHeight / 2;
                    W($"    header pixel edges y=14 : {Edges(shot, 14)}");
                    W($"    row 0  pixel edges y={rowY:0} : {Edges(shot, rowY)}");
                }
            }
        }
        RowPanel.SharedOffset = 0;
    }

    private static string Edges(Shot s, double dipY)
    {
        var sb = new StringBuilder();
        string last = "";
        for (double x = 0; x <= 800; x += 0.5)
        {
            var c = At(s, x, dipY);
            string t = Hex(c);
            if (t != last)
            {
                sb.Append($" {x:0.#}");
                last = t;
            }
        }
        return sb.Length == 0 ? "(none)" : sb.ToString();
    }

    // ================================================================= Q17
    private async Task CollectionChanges()
    {
        Sec(
            "Q17. Incremental collection changes with rows 2, 5 and 9 selected.\n"
                + "    The NotifyCollectionChangedAction actually raised is logged for every operation,\n"
                + "    so a Reset masquerading as an incremental change would be visible.\n"
                + "    Survival is judged by object reference, not by index."
        );

        async Task Run(string name, Action<ObservableCollection<Item>> op)
        {
            var src = new ObservableCollection<Item>(MakeItems(12));
            var actions = new List<string>();
            var lv = new ListView
            {
                SelectionMode = ListViewSelectionMode.Multiple,
                IsMultiSelectCheckBoxEnabled = false,
                ItemsSource = src,
                ItemTemplate = (DataTemplate)Resources["R4PlainRow"],
                ItemContainerStyle = (Style)Resources["R4RowStyle"],
                ItemsPanel = (ItemsPanelTemplate)Resources["R4Isp"],
                Width = 600,
                Height = 420,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
            };
            int ev = 0,
                add = 0,
                rem = 0;
            lv.SelectionChanged += (_, e) =>
            {
                ev++;
                add += e.AddedItems.Count;
                rem += e.RemovedItems.Count;
            };
            await ShowAsync(lv, 450);

            var a = src[2];
            var b = src[5];
            var c = src[9];
            lv.SelectedItems.Add(a);
            lv.SelectedItems.Add(b);
            lv.SelectedItems.Add(c);
            await Settle(300);
            string beforeSel = string.Join(
                " ",
                lv.SelectedItems.Cast<Item>().Select(p => $"id{p.Index}@{src.IndexOf(p)}")
            );
            ev = 0;
            add = 0;
            rem = 0;

            NotifyCollectionChangedEventHandler h = (_, e) =>
                actions.Add(
                    $"{e.Action}(old={e.OldStartingIndex},new={e.NewStartingIndex},oldItems={e.OldItems?.Count ?? 0},newItems={e.NewItems?.Count ?? 0})"
                );
            src.CollectionChanged += h;
            op(src);
            src.CollectionChanged -= h;
            await Settle(550);

            var still = new List<string>();
            foreach (var (item, nm) in new[] { (a, "id2"), (b, "id5"), (c, "id9") })
                still.Add(
                    nm
                        + (
                            lv.SelectedItems.Any(o => ReferenceEquals(o, item))
                                ? "=kept"
                                : (
                                    src.Contains(item)
                                        ? "=LOST(still in source)"
                                        : "=removed from source"
                                )
                        )
                );

            W("");
            W($"  {name}");
            W(
                $"    INotifyCollectionChanged raised: {(actions.Count == 0 ? "(none)" : string.Join("  ", actions))}"
            );
            W(
                $"    source count {src.Count}   ListView Items count {lv.Items.Count}   {(src.Count == lv.Items.Count ? "in sync" : "OUT OF SYNC")}"
            );
            W($"    BEFORE SelectedItems = {beforeSel}");
            W(
                $"    AFTER  SelectedItems({lv.SelectedItems.Count}) = {string.Join(" ", lv.SelectedItems.Cast<Item>().Select(p => $"id{p.Index}@{src.IndexOf(p)}"))}   SelectedIndex={lv.SelectedIndex}"
            );
            W($"    by reference: {string.Join("  ", still)}");
            W($"    SelectionChanged: {ev} event(s), {add} added, {rem} removed");
            var vis = new List<string>();
            for (int i = 0; i < Math.Min(src.Count, 14); i++)
                if (lv.ContainerFromIndex(i) is ListViewItem ci && ci.IsSelected)
                    vis.Add(i.ToString());
            W($"    containers with IsSelected=true at indices [{string.Join(",", vis)}]");
        }

        await Run("Add at the end", s => s.Add(new Item { Index = 100, Name = "item 100" }));
        await Run("Insert(0, new)", s => s.Insert(0, new Item { Index = 101, Name = "item 101" }));
        await Run("RemoveAt(0), an unselected row", s => s.RemoveAt(0));
        await Run("RemoveAt(5), a selected row", s => s.RemoveAt(5));
        await Run("Move(9, 0), a selected row to the top", s => s.Move(9, 0));
        await Run("Move(0, 11), an unselected row past the selection", s => s.Move(0, 11));
        await Run("Move(2, 3), a selected row down by one", s => s.Move(2, 3));
        await Run("Move(3, 2), an unselected row up by one", s => s.Move(3, 2));
        await Run(
            "Replace src[0], an unselected row",
            s => s[0] = new Item { Index = 200, Name = "item 200" }
        );
        await Run(
            "Replace src[5], a selected row",
            s => s[5] = new Item { Index = 205, Name = "item 205" }
        );
        await Run(
            "Clear() then re-add the SAME references",
            s =>
            {
                var cp = s.ToList();
                s.Clear();
                foreach (var i in cp)
                    s.Add(i);
            }
        );
        await Run(
            "Sort as 11 Move calls that reverse the list",
            s =>
            {
                for (int i = 0; i < s.Count; i++)
                    s.Move(s.Count - 1, i);
            }
        );
        await Run(
            "Remove+Insert instead of Move, a selected row to the top",
            s =>
            {
                var it = s[9];
                s.RemoveAt(9);
                s.Insert(0, it);
            }
        );
    }

    // ================================================================= Q18
    private sealed class SnapshotCollection : ObservableCollection<Item>
    {
        public void ReplaceAll(IEnumerable<Item> items)
        {
            Items.Clear();
            foreach (var i in items)
                Items.Add(i);
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(
                new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)
            );
        }
    }

    private async Task ResetScroll()
    {
        Sec("Q18. Does a single Reset move the vertical scroll position, and is focus the cause?");

        async Task Case(string name, bool sameInstances, bool focusOutside)
        {
            var items = MakeItems(10000);
            var src = new SnapshotCollection();
            foreach (var i in items)
                src.Add(i);

            var outsideBtn = new Button
            {
                Content = "outside",
                Width = 120,
                Height = 40,
            };
            var lv = new ListView
            {
                SelectionMode = ListViewSelectionMode.Multiple,
                IsMultiSelectCheckBoxEnabled = false,
                ItemsSource = src,
                ItemTemplate = (DataTemplate)Resources["R4PlainRow"],
                ItemContainerStyle = (Style)Resources["R4RowStyle"],
                ItemsPanel = (ItemsPanelTemplate)Resources["R4Isp"],
                Width = 600,
                Height = 380,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
            };
            var panel = new StackPanel { Orientation = Orientation.Vertical };
            panel.Children.Add(outsideBtn);
            panel.Children.Add(lv);
            await ShowAsync(panel, 600);

            lv.ScrollIntoView(src[3674]);
            await Settle(500);
            lv.ScrollIntoView(src[3674]);
            await Settle(600);

            var sv = Descendant<ScrollViewer>(lv);
            var isp = Descendant<ItemsStackPanel>(lv);
            if (focusOutside)
                outsideBtn.Focus(FocusState.Keyboard);
            else
                (lv.ContainerFromIndex(isp?.FirstVisibleIndex + 2 ?? 0) as ListViewItem)?.Focus(
                    FocusState.Keyboard
                );
            await Settle(400);

            string FocusNow()
            {
                var f = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
                while (f is not null)
                {
                    if (f is ListViewItem li)
                        return $"ListViewItem index {lv.IndexFromContainer(li)}";
                    if (f is Button)
                        return "the outside Button";
                    f = VisualTreeHelper.GetParent(f);
                }
                return "(none)";
            }

            double v0 = sv?.VerticalOffset ?? -1;
            int first0 = isp?.FirstVisibleIndex ?? -1;
            string f0 = FocusNow();

            var next = sameInstances ? items.ToList() : MakeItems(10000);
            src.ReplaceAll(next);
            await Settle(300);
            double v1mid = sv?.VerticalOffset ?? -1;
            await Settle(900);

            W("");
            W($"  {name}");
            W(
                $"    before: VerticalOffset={v0:0.#} FirstVisibleIndex={first0} focus={f0} ExtentHeight={sv?.ExtentHeight:0.#}"
            );
            W($"    right after the Reset: VerticalOffset={v1mid:0.#}");
            W(
                $"    settled: VerticalOffset={sv?.VerticalOffset:0.#} FirstVisibleIndex={isp?.FirstVisibleIndex} focus={FocusNow()}"
            );
        }

        await Case("Reset with NEW instances, focus inside the list", false, false);
        await Case("Reset with the SAME instances, focus inside the list", true, false);
        await Case("Reset with NEW instances, focus on a Button OUTSIDE the list", false, true);
        await Case("Reset with the SAME instances, focus on a Button OUTSIDE the list", true, true);
    }
}
