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

namespace Syno.TableViewSample.Probe;

public sealed partial class ChromePage : Page
{
    private readonly StringBuilder _log = new();
    private readonly List<string> _wheel = new();
    private double _scale = 1.0;
    private bool _finished;
    private IntPtr _hwnd = IntPtr.Zero;
    private string _hoverEvidence = "";

    // Mid grey: a 6 % white overlay lands near 136, a 6 % black overlay near 120,
    // so either kind of default selection fill is measurable against it.
    private static readonly Color Grey = Color.FromArgb(255, 128, 128, 128);

    public ChromePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void W(string s) => _log.AppendLine(s);

    private void Section(string s)
    {
        _log.AppendLine();
        _log.AppendLine("==================================================================");
        _log.AppendLine(s);
        _log.AppendLine("==================================================================");
    }

    // ------------------------------------------------------------- entrypoint

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(240);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            W("\n!!! WATCHDOG FIRED - run did not complete in 240 s !!!");
            Finish();
        };
        timer.Start();

        _scale = XamlRoot?.RasterizationScale ?? 1.0;
        RequestedTheme = ElementTheme.Light;

        W($"chrome probe started {DateTime.Now:O}");
        W($"RasterizationScale = {_scale}");
        W(
            $"Page.ActualTheme   = {ActualTheme}   Application.RequestedTheme = {Application.Current.RequestedTheme}"
        );
        W(
            "Host viewport = 800 x 400 DIP. Chrome tests use a mid-grey ListView background (BGRA 128,128,128,255)."
        );

        SizeWindow();
        await Settle(500);
        ReportWindow();

        await Guarded("Q1a/Q1b", Q1Async);
        await Guarded("Q4", Q4Async);
        await Guarded("Q2+Q3+Q6 (ItemsStackPanel)", () => TableAsync(useVsp: false));
        await Guarded("Q2+Q3+Q6 (VirtualizingStackPanel)", () => TableAsync(useVsp: true));
        await Guarded("Q5", Q5Async);

        timer.Stop();
        Finish();
    }

    private async Task Guarded(string name, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            W($"\n*** {name} THREW: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        W($"\nchrome probe finished {DateTime.Now:O}");

        var targets = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "chrome-results.txt"),
            Path.Combine(Path.GetTempPath(), "chrome-results.txt"),
        };
        var text = _log.ToString();
        foreach (var t in targets)
        {
            try
            {
                File.WriteAllText(t, text);
            }
            catch
            { /* reported by absence */
            }
        }
        Application.Current.Exit();
    }

    private void SizeWindow()
    {
        try
        {
            var w = MainWindow.Instance;
            if (w is null)
            {
                W("MainWindow.Instance is null; window not resized");
                return;
            }
            int px = (int)Math.Ceiling(940 * _scale);
            int py = (int)Math.Ceiling(560 * _scale);
            w.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(px, py));
            w.Activate();
            _hwnd = Win32Interop.GetWindowFromWindowId(w.AppWindow.Id);
            W($"window client resized to {px} x {py} physical px; HWND = 0x{_hwnd.ToInt64():X}");
        }
        catch (Exception ex)
        {
            W("resize failed: " + ex.Message);
        }
    }

    private void ReportWindow()
    {
        try
        {
            GetWindowRect(_hwnd, out var r);
            W($"window screen rect = ({r.Left},{r.Top})-({r.Right},{r.Bottom})");
            var origin = new POINT { X = 0, Y = 0 };
            ClientToScreen(_hwnd, ref origin);
            W($"client origin in screen px = ({origin.X},{origin.Y})");
            W(
                $"foreground HWND = 0x{GetForegroundWindow().ToInt64():X} (ours = 0x{_hwnd.ToInt64():X})"
            );
            var mid = new POINT { X = origin.X + 40, Y = origin.Y + 200 };
            var wf = WindowFromPoint(mid);
            W($"WindowFromPoint(client+40,+200) = 0x{wf.ToInt64():X} class = {ClassOf(wf)}");
        }
        catch (Exception ex)
        {
            W("window report failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------- utilities

    private async Task Settle(int ms = 250)
    {
        Host.UpdateLayout();
        await Task.Delay(ms);
        Host.UpdateLayout();
        await Task.Delay(60);
    }

    private async Task ShowAsync(UIElement el, int ms = 350)
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
        var list = new List<Item>(n);
        for (int i = 0; i < n; i++)
            list.Add(new Item { Index = i, Name = "item " + i });
        return list;
    }

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t)
                return t;
            var r = FindDescendant<T>(c);
            if (r is not null)
                return r;
        }
        return null;
    }

    private static string StatesOf(Control c)
    {
        if (VisualTreeHelper.GetChildrenCount(c) == 0)
            return "no template root";
        var root = VisualTreeHelper.GetChild(c, 0) as FrameworkElement;
        if (root is null)
            return "template root not a FrameworkElement";
        var groups = VisualStateManager.GetVisualStateGroups(root);
        if (groups is null || groups.Count == 0)
            return $"templateRoot={root.GetType().Name}; VisualStateGroups: none instantiated";
        var parts = new List<string>();
        foreach (var g in groups)
            parts.Add($"{g.Name}={(g.CurrentState?.Name ?? "(null)")}");
        return $"templateRoot={root.GetType().Name}; " + string.Join(" | ", parts);
    }

    // ---------------------------------------------------------- pixel capture

    private sealed class Shot
    {
        public int W,
            H;
        public byte[] Px = Array.Empty<byte>();
        public bool Ok;
        public string Error = "";

        public override string ToString() => Ok ? $"{W}x{H}" : "FAILED " + Error;
    }

    private async Task<Shot> CaptureAsync(UIElement el)
    {
        var shot = new Shot();
        try
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(el);
            var buf = await rtb.GetPixelsAsync();
            var bytes = new byte[buf.Length];
            DataReader.FromBuffer(buf).ReadBytes(bytes);
            shot.W = rtb.PixelWidth;
            shot.H = rtb.PixelHeight;
            shot.Px = bytes;
            shot.Ok = true;
        }
        catch (Exception ex)
        {
            shot.Error = ex.GetType().Name + ": " + ex.Message;
        }
        return shot;
    }

    private string Pixel(Shot s, double dipX, double dipY)
    {
        if (!s.Ok)
            return "capture-failed";
        int x = (int)Math.Round(dipX * _scale),
            y = (int)Math.Round(dipY * _scale);
        if (x < 0 || y < 0 || x >= s.W || y >= s.H)
            return $"outside capture ({x},{y}) of {s.W}x{s.H}";
        int i = (y * s.W + x) * 4;
        return $"BGRA({s.Px[i]},{s.Px[i + 1]},{s.Px[i + 2]},{s.Px[i + 3]})";
    }

    private string Mean(Shot s, double x0, double y0, double w, double h)
    {
        if (!s.Ok)
            return "capture-failed";
        long b = 0,
            g = 0,
            r = 0,
            a = 0;
        int n = 0;
        int px0 = (int)(x0 * _scale),
            py0 = (int)(y0 * _scale);
        int px1 = (int)((x0 + w) * _scale),
            py1 = (int)((y0 + h) * _scale);
        for (int y = py0; y < py1; y++)
        for (int x = px0; x < px1; x++)
        {
            if (x < 0 || y < 0 || x >= s.W || y >= s.H)
                continue;
            int i = (y * s.W + x) * 4;
            b += s.Px[i];
            g += s.Px[i + 1];
            r += s.Px[i + 2];
            a += s.Px[i + 3];
            n++;
        }
        if (n == 0)
            return "no pixels in range";
        return $"mean BGRA({(double)b / n:0.00},{(double)g / n:0.00},{(double)r / n:0.00},{(double)a / n:0.00}) over {n}px";
    }

    private string Scan(Shot s, double dipY, double x0, double x1)
    {
        if (!s.Ok)
            return "capture-failed";
        int y = (int)Math.Round(dipY * _scale);
        var sb = new StringBuilder();
        string last = "";
        for (double x = x0; x <= x1; x += 1)
        {
            int px = (int)Math.Round(x * _scale);
            if (px < 0 || px >= s.W || y < 0 || y >= s.H)
                continue;
            int i = (y * s.W + px) * 4;
            var c = $"({s.Px[i]},{s.Px[i + 1]},{s.Px[i + 2]},{s.Px[i + 3]})";
            if (c != last)
            {
                sb.Append($" x{x:0}={c}");
                last = c;
            }
        }
        return sb.Length == 0 ? "(nothing)" : sb.ToString();
    }

    private static Point Origin(UIElement child, UIElement ancestor) =>
        child.TransformToVisual(ancestor).TransformPoint(new Point(0, 0));

    private ListView MakeChromeList(ListViewSelectionMode mode, Style? itemStyle)
    {
        var lv = new ListView
        {
            SelectionMode = mode,
            ItemsSource = MakeItems(6),
            ItemTemplate = (DataTemplate)Resources["SimpleTemplate"],
            ItemContainerStyle = itemStyle,
            Background = new SolidColorBrush(Grey),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Width = 800,
            Height = 400,
        };
        return lv;
    }

    /// <summary>Everything that says whether the row is drawn as selected / hovered / focused.</summary>
    private void Chrome(Shot s, ListViewItem c, ListView lv, string label)
    {
        var o = Origin(c, lv);
        double h = c.ActualHeight;
        W($"  {label}");
        W($"    states       = {StatesOf(c)}");
        W($"    whole row    = {Mean(s, o.X, o.Y + 1, 780, h - 2)}");
        W($"    left 12 DIP  = {Mean(s, o.X, o.Y + 1, 12, h - 2)}");
        W($"    empty right  = {Mean(s, o.X + 300, o.Y + 1, 400, h - 2)}");
        W($"    scan y=mid   ={Scan(s, o.Y + h / 2, o.X, o.X + 60)}");
        W($"    scan y=top+1 ={Scan(s, o.Y + 1, o.X, o.X + 60)}");
    }

    // ================================================================== Q1a/Q1b

    private async Task Q1Async()
    {
        Section(
            "Q1a  Does ListViewItem draw selected chrome when SelectionMode=None and the code sets\n"
                + "     ListViewItem.IsSelected = true directly?\n"
                + "Q1b  Does the container's automation peer report IsSelected in that configuration?"
        );

        // ---- configuration under test: SelectionMode = None
        var lv = MakeChromeList(ListViewSelectionMode.None, null);
        await ShowAsync(lv);

        var c0 = lv.ContainerFromIndex(0) as ListViewItem;
        var c1 = lv.ContainerFromIndex(1) as ListViewItem;
        if (c0 is null || c1 is null)
        {
            W("containers not realized; abort Q1");
            return;
        }

        W($"ListView.SelectionMode = {lv.SelectionMode}");
        W(
            $"container type = {c0.GetType().FullName}, size {c0.ActualWidth:0.##}x{c0.ActualHeight:0.##}"
        );
        var pres = FindDescendant<ListViewItemPresenter>(c0);
        if (pres is not null)
        {
            W($"presenter = {pres.GetType().FullName}");
            W($"  SelectedBackground        = {Describe(pres.SelectedBackground)}");
            W($"  PointerOverBackground     = {Describe(pres.PointerOverBackground)}");
            W($"  SelectionIndicatorMode    = {pres.SelectionIndicatorMode}");
            W($"  SelectionIndicatorBrush   = {Describe(pres.SelectionIndicatorBrush)}");
        }

        W("");
        W("--- BEFORE: nothing selected ---");
        var before = await CaptureAsync(lv);
        W($"capture {before}");
        Chrome(before, c0, lv, "item0 (untouched)");

        W("");
        W("--- setting c0.IsSelected = true while SelectionMode = None ---");
        c0.IsSelected = true;
        await Settle();
        W($"c0.IsSelected read back      = {c0.IsSelected}");
        W($"lv.SelectedIndex             = {lv.SelectedIndex}");
        W(
            $"lv.SelectedItem is item0     = {ReferenceEquals(lv.SelectedItem, ((List<Item>)lv.ItemsSource)[0])}"
        );
        W($"lv.SelectedItems.Count       = {lv.SelectedItems.Count}");
        var after = await CaptureAsync(lv);
        W($"capture {after}");
        Chrome(after, c0, lv, "item0 (IsSelected=true, SelectionMode=None)");
        Chrome(after, c1, lv, "item1 (control, never touched)");

        W("");
        W("--- Q1b automation, SelectionMode=None, c0.IsSelected=true ---");
        ReportSelectionAutomation(lv, 0);

        // ---- reference configuration: SelectionMode = Single, genuine selection
        Section(
            "Q1a/Q1b REFERENCE  identical list, SelectionMode=Single, genuine selection.\n"
                + "This calibrates what 'drawn as selected' looks like in this capture."
        );
        var lv2 = MakeChromeList(ListViewSelectionMode.Single, null);
        await ShowAsync(lv2);
        lv2.SelectedIndex = 0;
        await Settle();
        var d0 = lv2.ContainerFromIndex(0) as ListViewItem;
        var d1 = lv2.ContainerFromIndex(1) as ListViewItem;
        if (d0 is null || d1 is null)
        {
            W("reference containers not realized");
            return;
        }
        W($"d0.IsSelected = {d0.IsSelected}");
        var refShot = await CaptureAsync(lv2);
        W($"capture {refShot}");
        Chrome(refShot, d0, lv2, "item0 (genuinely selected)");
        Chrome(refShot, d1, lv2, "item1 (unselected)");
        W("");
        W("--- automation, SelectionMode=Single, item0 genuinely selected ---");
        ReportSelectionAutomation(lv2, 0);

        // ---- Multiple, the design's fallback
        Section("Q1a/Q1b REFERENCE 2  SelectionMode=Multiple (the design's section 5.4 fallback)");
        var lv3 = MakeChromeList(ListViewSelectionMode.Multiple, null);
        await ShowAsync(lv3);
        lv3.SelectedIndex = 0;
        await Settle();
        var e0 = lv3.ContainerFromIndex(0) as ListViewItem;
        if (e0 is null)
        {
            W("containers not realized");
            return;
        }
        var s3 = await CaptureAsync(lv3);
        W($"capture {s3}");
        Chrome(s3, e0, lv3, "item0 (selected, Multiple)");
        ReportSelectionAutomation(lv3, 0);
    }

    private static string Describe(Brush? b) =>
        b switch
        {
            null => "(null)",
            SolidColorBrush s => $"SolidColorBrush {s.Color} opacity {s.Opacity}",
            _ => b.GetType().Name,
        };

    private void ReportSelectionAutomation(ListView lv, int index)
    {
        var container = lv.ContainerFromIndex(index) as ListViewItem;
        if (container is null)
        {
            W("  container not realized");
            return;
        }

        // (a) the container's own peer
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(container);
        W($"  container peer = {peer?.GetType().FullName ?? "(null)"}");
        if (peer is not null)
        {
            W(
                $"    implements: {string.Join(", ", peer.GetType().GetInterfaces().Select(i => i.Name))}"
            );
            W(
                $"    direct cast to ISelectionItemProvider: {(peer is ISelectionItemProvider ? "ok" : "null")}"
            );
            if (peer is ISelectionItemProvider dsp)
                SafeIsSelected("    container-peer direct", dsp);
            var sup = new List<string>();
            foreach (PatternInterface pi in Enum.GetValues<PatternInterface>())
            {
                object? o = null;
                try
                {
                    o = peer.GetPattern(pi);
                }
                catch { }
                if (o is not null)
                    sup.Add($"{pi}->{o.GetType().Name}");
            }
            W($"    GetPattern non-null: {(sup.Count == 0 ? "(none)" : string.Join(", ", sup))}");
            if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider psp)
                SafeIsSelected("    container-peer GetPattern", psp);
        }

        // (b) the ListView peer and the item (data) peers it exposes to UIA
        var lvPeer = FrameworkElementAutomationPeer.CreatePeerForElement(lv);
        W($"  ListView peer = {lvPeer?.GetType().FullName ?? "(null)"}");
        if (lvPeer is null)
            return;
        var selPat = lvPeer.GetPattern(PatternInterface.Selection) as ISelectionProvider;
        if (selPat is null)
            W("    ListView Selection pattern = null");
        else
        {
            var sel = selPat.GetSelection();
            W(
                $"    ListView ISelectionProvider: CanSelectMultiple={selPat.CanSelectMultiple} "
                    + $"IsSelectionRequired={selPat.IsSelectionRequired} GetSelection().Length={sel?.Length ?? -1}"
            );
        }

        IList<AutomationPeer>? kids = null;
        try
        {
            kids = lvPeer.GetChildren();
        }
        catch (Exception ex)
        {
            W("    GetChildren threw " + ex.Message);
        }
        W($"    ListView peer children = {kids?.Count ?? -1}");
        if (kids is not null && kids.Count > index)
        {
            var item = kids[index];
            W($"    item peer[{index}] = {item.GetType().FullName}");
            W(
                $"      implements: {string.Join(", ", item.GetType().GetInterfaces().Select(i => i.Name))}"
            );
            if (item is ISelectionItemProvider isp)
                SafeIsSelected($"      item-peer[{index}] direct", isp);
            else
                W($"      item-peer[{index}] direct cast to ISelectionItemProvider = null");
            if (item.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider ipat)
                SafeIsSelected($"      item-peer[{index}] GetPattern", ipat);
            else
                W($"      item-peer[{index}] GetPattern(SelectionItem) = null");
        }
    }

    private void SafeIsSelected(string label, ISelectionItemProvider p)
    {
        try
        {
            W($"{label}.IsSelected = {p.IsSelected}");
        }
        catch (Exception ex)
        {
            W($"{label}.IsSelected threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ====================================================================== Q4

    private async Task Q4Async()
    {
        Section(
            "Q4  Does the default ListViewItem style still draw selection, hover and focus chrome\n"
                + "    when the ItemContainerStyle sets Padding=0?\n"
                + "    Run under SelectionMode=None (direct IsSelected) and under Multiple / Single (real selection)."
        );

        foreach (
            var mode in new[]
            {
                ListViewSelectionMode.None,
                ListViewSelectionMode.Multiple,
                ListViewSelectionMode.Single,
            }
        )
        {
            W("");
            W(
                $"---------------- SelectionMode = {mode}, ItemContainerStyle Padding=0 ----------------"
            );
            var lv = MakeChromeList(mode, (Style)Resources["PaddingZeroItemStyle"]);
            await ShowAsync(lv);

            var c0 = lv.ContainerFromIndex(0) as ListViewItem;
            var c2 = lv.ContainerFromIndex(2) as ListViewItem;
            var c3 = lv.ContainerFromIndex(3) as ListViewItem;
            var c4 = lv.ContainerFromIndex(4) as ListViewItem;
            if (c0 is null || c2 is null || c3 is null || c4 is null)
            {
                W("containers not realized");
                continue;
            }

            W($"container Padding = {c0.Padding}, ActualHeight = {c0.ActualHeight:0.##}");
            var cp = FindDescendant<ContentPresenter>(c0);
            if (cp is not null)
            {
                var o = Origin(cp, c0);
                W(
                    $"ContentPresenter inside container: offset {o.X:0.##},{o.Y:0.##} size {cp.ActualWidth:0.##}x{cp.ActualHeight:0.##}"
                );
            }

            var baseline = await CaptureAsync(lv);
            W($"baseline capture {baseline}");
            Chrome(baseline, c0, lv, "item0 baseline (nothing applied)");

            if (mode == ListViewSelectionMode.None)
                c0.IsSelected = true;
            else
                lv.SelectedIndex = 0;
            VisualStateManager.GoToState(c2, "PointerOver", false);
            bool focused = c3.Focus(FocusState.Keyboard);
            await Settle();

            var shot = await CaptureAsync(lv);
            W($"capture {shot}");
            Chrome(
                shot,
                c0,
                lv,
                mode == ListViewSelectionMode.None
                    ? "item0 SELECTION (IsSelected=true, SelectionMode=None)"
                    : "item0 SELECTION (real selection)"
            );
            Chrome(shot, c2, lv, "item2 HOVER (VisualStateManager.GoToState PointerOver, forced)");
            Chrome(
                shot,
                c3,
                lv,
                $"item3 FOCUS (Focus(Keyboard)={focused}, FocusState={c3.FocusState})"
            );
            Chrome(shot, c4, lv, "item4 CONTROL (untouched)");

            // Real pointer hover, if the desktop session accepts injected input.
            _hoverEvidence = "no PointerEntered / PointerMoved reached item4";
            c4.PointerEntered += (_, _) => _hoverEvidence = "PointerEntered fired on item4";
            c4.PointerMoved += (_, _) => _hoverEvidence = "PointerMoved fired on item4";
            var o4 = Origin(c4, lv);
            var inWin = lv.TransformToVisual(null)
                .TransformPoint(new Point(60, o4.Y + c4.ActualHeight / 2));
            var sp = new POINT
            {
                X = (int)Math.Round(inWin.X * _scale),
                Y = (int)Math.Round(inWin.Y * _scale),
            };
            ClientToScreen(_hwnd, ref sp);
            GetCursorPos(out var saved);
            SetForegroundWindow(_hwnd);
            SetCursorPos(sp.X, sp.Y);
            await Settle(400);
            W(
                $"  REAL HOVER attempt over item4 at screen ({sp.X},{sp.Y}); WindowFromPoint class = {ClassOf(WindowFromPoint(sp))}"
            );
            W(
                $"    item4 states after real hover = {StatesOf(c4)}   pointer evidence: {_hoverEvidence}"
            );
            var hoverShot = await CaptureAsync(lv);
            Chrome(hoverShot, c4, lv, "item4 after REAL hover");
            SetCursorPos(saved.X, saved.Y);
            await Task.Delay(150);
        }
    }

    // ============================================================ Q2 + Q3 + Q5 + Q6

    private async Task TableAsync(bool useVsp)
    {
        string panelName = useVsp ? "VirtualizingStackPanel" : "ItemsStackPanel";
        Section(
            $"Q2  Custom Panel root measuring to 2000 DIP in an 800 DIP viewport, inner ScrollViewer\n"
                + $"    horizontal axis DISABLED, panel subtracts a table-owned offset in ArrangeOverride.\n"
                + $"Q3  Virtualization with 10,000 items.\n"
                + $"    ItemsPanel = {panelName}"
        );

        RowPanel.SharedOffset = 0;

        var lv = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            ItemsSource = MakeItems(10000),
            ItemTemplate = (DataTemplate)Resources["RowTemplate"],
            ItemContainerStyle = (Style)Resources["TableItemStyle"],
            ItemsPanel = (ItemsPanelTemplate)Resources[useVsp ? "VspPanel" : "IspPanel"],
            Background = new SolidColorBrush(Colors.White),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Width = 800,
            Height = 400,
        };
        ScrollViewer.SetHorizontalScrollMode(lv, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(lv, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(lv, ScrollMode.Enabled);
        ScrollViewer.SetVerticalScrollBarVisibility(lv, ScrollBarVisibility.Auto);

        await ShowAsync(lv, 800);

        var sv = FindDescendant<ScrollViewer>(lv);
        W($"inner ScrollViewer found = {sv is not null}");
        if (sv is not null)
        {
            W(
                $"  HorizontalScrollMode={sv.HorizontalScrollMode} HorizontalScrollBarVisibility={sv.HorizontalScrollBarVisibility}"
            );
            W(
                $"  ExtentWidth={sv.ExtentWidth:0.##} ViewportWidth={sv.ViewportWidth:0.##} HorizontalOffset={sv.HorizontalOffset:0.##} ScrollableWidth={sv.ScrollableWidth:0.##}"
            );
            W(
                $"  ExtentHeight={sv.ExtentHeight:0.##} ViewportHeight={sv.ViewportHeight:0.##} VerticalOffset={sv.VerticalOffset:0.##}"
            );
        }

        W($"ItemsPanelRoot = {lv.ItemsPanelRoot?.GetType().FullName ?? "(null)"}");
        W($"Q3  realized containers with 10,000 items = {lv.ItemsPanelRoot?.Children.Count ?? -1}");

        var c0 = lv.ContainerFromIndex(0) as ListViewItem;
        var row0 = c0 is null ? null : FindDescendant<RowPanel>(c0);
        if (c0 is null || row0 is null)
        {
            W("container 0 or RowPanel missing; cannot continue");
            return;
        }

        W("");
        W("--- Q2 arrangement, table-owned offset = 0 ---");
        W(
            $"container0 ActualWidth={c0.ActualWidth:0.##} ActualHeight={c0.ActualHeight:0.##} Padding={c0.Padding}"
        );
        W(
            $"RowPanel measure available = {row0.LastMeasureAvailable.Width:0.##} x {row0.LastMeasureAvailable.Height}"
        );
        W(
            $"RowPanel measure returned  = {row0.LastMeasureReturned.Width:0.##} x {row0.LastMeasureReturned.Height:0.##}  (clamped DesiredSize.Width = {row0.DesiredSize.Width:0.##})"
        );
        W(
            $"RowPanel arrange finalSize = {row0.LastArrangeFinal.Width:0.##} x {row0.LastArrangeFinal.Height:0.##}"
        );
        W(
            $"RowPanel ActualWidth = {row0.ActualWidth:0.##}, measures {row0.MeasureCount}, arranges {row0.ArrangeCount}"
        );
        DumpCells(row0, lv, "offset=0");

        var shot0 = await CaptureAsync(lv);
        double rowY = Origin(c0, lv).Y + c0.ActualHeight / 2;
        W($"capture {shot0}");
        W(
            "cell colours: c0 #101080 c1 #20A0FF c2 #30C030 c3 #FFD000 c4 #FF6000 c5 #E00040 c6 #9000C0 c7 #00C0C0 c8 #80FF00 c9 #404040"
        );
        W($"pixel x=100 (cell centre, expect c0 BGRA(128,16,16,255)) = {Pixel(shot0, 100, rowY)}");
        W($"pixel x=300 (expect c1 BGRA(255,160,32,255))             = {Pixel(shot0, 300, rowY)}");
        W($"pixel x=500 (expect c2 BGRA(48,192,48,255))              = {Pixel(shot0, 500, rowY)}");
        W($"pixel x=700 (expect c3 BGRA(0,208,255,255))              = {Pixel(shot0, 700, rowY)}");

        W("");
        W("--- Q2 arrangement, table-owned offset changed to 1200 ---");
        RowPanel.SharedOffset = 1200;
        RowPanel.InvalidateAll();
        await Settle(350);

        var c0b = lv.ContainerFromIndex(0) as ListViewItem;
        var row0b = c0b is null ? null : FindDescendant<RowPanel>(c0b);
        if (row0b is null || c0b is null)
            W("container 0 gone after offset change");
        else
        {
            W(
                $"RowPanel arrange finalSize = {row0b.LastArrangeFinal.Width:0.##} x {row0b.LastArrangeFinal.Height:0.##}, measures {row0b.MeasureCount}, arranges {row0b.ArrangeCount}"
            );
            DumpCells(row0b, lv, "offset=1200");
            var shot1 = await CaptureAsync(lv);
            double y2 = Origin(c0b, lv).Y + c0b.ActualHeight / 2;
            W($"capture {shot1}");
            W($"pixel x=100 (expect c6 BGRA(192,0,144,255)) = {Pixel(shot1, 100, y2)}");
            W($"pixel x=300 (expect c7 BGRA(192,192,0,255)) = {Pixel(shot1, 300, y2)}");
            W($"pixel x=500 (expect c8 BGRA(0,255,128,255)) = {Pixel(shot1, 500, y2)}");
            W($"pixel x=700 (expect c9 BGRA(64,64,64,255))  = {Pixel(shot1, 700, y2)}");

            // Overflow check: the Host is 900 DIP wide, the ListView only 800.
            // If the 2000 DIP row painted outside the ListView, it would show up at x=850.
            var hostShot = await CaptureAsync(Host);
            double hostY = Origin(c0b, Host).Y + c0b.ActualHeight / 2;
            W($"host capture {hostShot} (Host is 900 DIP wide, ListView 800)");
            W($"  Host pixel x=750 (inside the ListView)  = {Pixel(hostShot, 750, hostY)}");
            W(
                $"  Host pixel x=850 (outside the ListView; white here means the row does not paint past the viewport) = {Pixel(hostShot, 850, hostY)}"
            );
        }
        if (sv is not null)
            W(
                $"ScrollViewer after offset change: HOffset={sv.HorizontalOffset:0.##} ExtentWidth={sv.ExtentWidth:0.##} ScrollableWidth={sv.ScrollableWidth:0.##} VOffset={sv.VerticalOffset:0.##}"
            );

        W("");
        W("--- Q3 virtualization across a scroll ---");
        int mid = lv.ItemsPanelRoot?.Children.Count ?? -1;
        sv?.ChangeView(null, useVsp ? 4000 : 100000, null, true);
        await Settle(600);
        W(
            $"after ChangeView: VerticalOffset={sv?.VerticalOffset:0.##}, realized containers = {lv.ItemsPanelRoot?.Children.Count ?? -1} (was {mid})"
        );
        sv?.ChangeView(null, 0, null, true);
        await Settle(500);
        W($"back at top: realized containers = {lv.ItemsPanelRoot?.Children.Count ?? -1}");

        await Guarded("Q6 " + panelName, () => Q6Async(lv, sv, panelName));
    }

    private void DumpCells(RowPanel row, ListView lv, string label)
    {
        for (int i = 0; i < row.LastArrangeRects.Count; i++)
        {
            var r = row.LastArrangeRects[i];
            var child = row.Children[i] as FrameworkElement;
            string live = "-";
            if (child is not null)
            {
                var p = Origin(child, lv);
                live =
                    $"x-in-ListView={p.X:0.##} size={child.ActualWidth:0.##}x{child.ActualHeight:0.##}";
            }
            W(
                $"  [{label}] cell c{i} arranged x={r.X:0.##} w={r.Width:0.##} h={r.Height:0.##}   {live}"
            );
        }
    }

    // ====================================================================== Q5

    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int MK_SHIFT = 0x0004;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x01000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_SHIFT = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X,
            Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left,
            Top,
            Right,
            Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr h, ref POINT p);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr h, ref POINT p);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr h);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr h, out RECT r);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint f, int dx, int dy, int data, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, char[] b, int max);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr h, EnumChildProc cb, IntPtr p);

    [DllImport("user32.dll")]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr h);

    private delegate bool EnumChildProc(IntPtr h, IntPtr p);

    private static List<IntPtr> ChildWindows(IntPtr parent)
    {
        var list = new List<IntPtr>();
        EnumChildWindows(
            parent,
            (h, _) =>
            {
                list.Add(h);
                return true;
            },
            IntPtr.Zero
        );
        return list;
    }

    private static string ClassOf(IntPtr h)
    {
        var buf = new char[256];
        int n = GetClassName(h, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : "(none)";
    }

    private void HookWheel(UIElement el, string who)
    {
        el.PointerWheelChanged += (s, e) =>
            RecordWheel(who + ".normal(handledEventsToo:false)", e, el);
        el.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(
                (s, e) => RecordWheel(who + ".AddHandler(handledEventsToo:true)", e, el)
            ),
            true
        );
    }

    private void RecordWheel(string who, PointerRoutedEventArgs e, UIElement el)
    {
        var p = e.GetCurrentPoint(el);
        _wheel.Add(
            $"      {who}: Handled-at-entry={e.Handled} delta={p.Properties.MouseWheelDelta} "
                + $"isHorizontalWheel={p.Properties.IsHorizontalMouseWheel} keyModifiers={e.KeyModifiers} "
                + $"originalSource={(e.OriginalSource?.GetType().Name ?? "(null)")}"
        );
    }

    /// <summary>Screen pixel for a DIP point inside <paramref name="el"/>.</summary>
    private POINT ScreenPoint(UIElement el, double dipX, double dipY)
    {
        var inWin = el.TransformToVisual(null).TransformPoint(new Point(dipX, dipY));
        var p = new POINT
        {
            X = (int)Math.Round(inWin.X * _scale),
            Y = (int)Math.Round(inWin.Y * _scale),
        };
        ClientToScreen(_hwnd, ref p);
        return p;
    }

    private async Task Q5Async()
    {
        Section(
            "Q5  Do Shift+wheel and horizontal-wheel events reach a handler on the row surface,\n"
                + "    or does the inner ScrollViewer consume them first?\n"
                + "    Handlers attached BOTH normally AND with AddHandler(..., handledEventsToo: true).\n"
                + "    Two controls run first with the same delivery mechanism, so the ListView result can be\n"
                + "    attributed to the ScrollViewer rather than to the delivery mechanism."
        );

        var kids = ChildWindows(_hwnd);
        W($"child windows of our top-level HWND: {kids.Count}");
        foreach (var k in kids)
            W($"    0x{k.ToInt64():X} class = {ClassOf(k)}");
        IntPtr site = IntPtr.Zero;
        foreach (var k in kids)
            if (ClassOf(k) == "InputSiteWindowClass")
                site = k;
        if (site == IntPtr.Zero)
        {
            W("no InputSiteWindowClass child found; Q5 cannot be measured");
            return;
        }
        W($"wheel messages are delivered to the input site HWND 0x{site.ToInt64():X}");
        W(
            $"foreground HWND = 0x{GetForegroundWindow().ToInt64():X} (ours = 0x{_hwnd.ToInt64():X})"
        );
        var inputDesk = OpenInputDesktop(0, false, 0x0001);
        W($"OpenInputDesktop = {(inputDesk == IntPtr.Zero ? "FAILED" : "ok")}");
        if (inputDesk != IntPtr.Zero)
            CloseDesktop(inputDesk);

        async Task Trial(string name, POINT sp, ScrollViewer? sv, int msg, int delta, bool shift)
        {
            _wheel.Clear();
            double h0 = sv?.HorizontalOffset ?? -1,
                v0 = sv?.VerticalOffset ?? -1;
            IntPtr lparam = (IntPtr)((sp.Y << 16) | (sp.X & 0xFFFF));
            int wp = (delta << 16) | (shift ? MK_SHIFT : 0);
            if (shift)
                keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
            SendMessage(site, msg, (IntPtr)wp, lparam);
            if (shift)
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            await Settle(400);
            W("");
            W($"  {name}   (screen point {sp.X},{sp.Y})");
            W($"    handlers fired: {_wheel.Count}");
            foreach (var s in _wheel)
                W(s);
            if (sv is not null)
                W(
                    $"    ScrollViewer  H {h0:0.##} -> {sv.HorizontalOffset:0.##}   V {v0:0.##} -> {sv.VerticalOffset:0.##}"
                );
        }

        // ---------------- CONTROL 1: no ScrollViewer anywhere ----------------
        W("");
        W("---------------- CONTROL 1: plain nested Borders, no ScrollViewer ----------------");
        var outer = new Border
        {
            Width = 800,
            Height = 400,
            Background = new SolidColorBrush(Grey),
        };
        var innerB = new Border
        {
            Width = 400,
            Height = 200,
            Background = new SolidColorBrush(Colors.SteelBlue),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        outer.Child = innerB;
        HookWheel(innerB, "control1.innerBorder(leaf)");
        HookWheel(outer, "control1.outerBorder(ancestor)");
        await ShowAsync(outer);
        await Trial(
            "C1 vertical wheel -120 over the inner Border",
            ScreenPoint(innerB, 200, 100),
            null,
            WM_MOUSEWHEEL,
            -120,
            false
        );

        // ---------------- CONTROL 2: leaf inside a plain ScrollViewer ----------------
        W("");
        W("---------------- CONTROL 2: a leaf Border inside a plain ScrollViewer ----------------");
        var sv2 = new ScrollViewer
        {
            Width = 800,
            Height = 400,
            VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var tall = new Border
        {
            Width = 800,
            Height = 3000,
            Background = new SolidColorBrush(Colors.SteelBlue),
        };
        sv2.Content = tall;
        HookWheel(tall, "control2.contentInsideScrollViewer(leaf)");
        HookWheel(sv2, "control2.scrollViewer");
        await ShowAsync(sv2);
        await Trial(
            "C2 vertical wheel -120 over the content inside the ScrollViewer",
            ScreenPoint(sv2, 200, 200),
            sv2,
            WM_MOUSEWHEEL,
            -120,
            false
        );

        // ---------------- THE REAL CASE: the table ListView ----------------
        W("");
        W(
            "---------------- THE TABLE: ListView, horizontal axis disabled, custom row panel ----------------"
        );
        RowPanel.SharedOffset = 0;
        var lv = new ListView
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
        ScrollViewer.SetHorizontalScrollMode(lv, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(lv, ScrollBarVisibility.Disabled);
        await ShowAsync(lv, 800);

        var sv3 = FindDescendant<ScrollViewer>(lv);
        var c0 = lv.ContainerFromIndex(2) as ListViewItem;
        var row = c0 is null ? null : FindDescendant<RowPanel>(c0);
        if (c0 is null || row is null)
        {
            W("row container not realized; abort");
            return;
        }
        HookWheel(row, "rowPanel(the row surface)");
        HookWheel(c0, "listViewItem(the container)");
        HookWheel(lv, "listView");
        if (sv3 is not null)
            HookWheel(sv3, "innerScrollViewer");

        double y = Origin(c0, lv).Y + c0.ActualHeight / 2;
        var target = ScreenPoint(lv, 120, y);
        W($"target row: container index 2, ListView-relative point (120,{y:0.#}) DIP");

        await Trial("T1 vertical wheel -120, no modifier", target, sv3, WM_MOUSEWHEEL, -120, false);
        await Trial(
            "T2 vertical wheel -120 with Shift (keybd_event VK_SHIFT held + MK_SHIFT in wParam)",
            target,
            sv3,
            WM_MOUSEWHEEL,
            -120,
            true
        );
        await Trial(
            "T3 horizontal wheel +120 (WM_MOUSEHWHEEL)",
            target,
            sv3,
            WM_MOUSEHWHEEL,
            120,
            false
        );
        await Trial(
            "T4 horizontal wheel -120 (WM_MOUSEHWHEEL)",
            target,
            sv3,
            WM_MOUSEHWHEEL,
            -120,
            false
        );
    }

    private async Task Q6Async(ListView lv, ScrollViewer? sv, string panelName)
    {
        Section(
            $"Q6  ListViewBase.ScrollIntoView with the horizontal axis disabled and a table-owned\n"
                + $"    offset of 900 DIP in play. Target: item 7500 of 10,000.  ItemsPanel = {panelName}"
        );

        sv?.ChangeView(null, 0, null, true);
        await Settle(400);
        RowPanel.SharedOffset = 900;
        RowPanel.InvalidateAll();
        await Settle(400);

        var items = (List<Item>)lv.ItemsSource;
        W($"table-owned offset = {RowPanel.SharedOffset}");
        if (sv is not null)
            W(
                $"before: VOffset={sv.VerticalOffset:0.##} HOffset={sv.HorizontalOffset:0.##} ExtentW={sv.ExtentWidth:0.##} "
                    + $"ExtentH={sv.ExtentHeight:0.##} ViewportW={sv.ViewportWidth:0.##} ScrollableWidth={sv.ScrollableWidth:0.##}"
            );
        var rBefore = FindRow(lv, 0);
        if (rBefore is not null && rBefore.LastArrangeRects.Count == 10)
            W(
                $"before: row0 cells arranged c0 x={rBefore.LastArrangeRects[0].X:0.##} .. c9 x={rBefore.LastArrangeRects[9].X:0.##}"
            );

        lv.ScrollIntoView(items[7500]);
        await Settle(900);
        var cFirst = lv.ContainerFromItem(items[7500]) as ListViewItem;
        if (sv is not null)
            W(
                $"first reading, 0.9 s after the call: VOffset={sv.VerticalOffset:0.##} ExtentH={sv.ExtentHeight:0.##}"
                    + (
                        cFirst is null
                            ? ", container not realized"
                            : $", container y={Origin(cFirst, lv).Y:0.##}"
                    )
            );
        // ItemsStackPanel corrects its estimated extent asynchronously; give it time to finish.
        await Settle(2000);

        if (sv is not null)
            W(
                $"after : VOffset={sv.VerticalOffset:0.##} HOffset={sv.HorizontalOffset:0.##} ExtentH={sv.ExtentHeight:0.##} "
                    + $"ExtentW={sv.ExtentWidth:0.##} ScrollableWidth={sv.ScrollableWidth:0.##}"
            );
        W($"realized containers = {lv.ItemsPanelRoot?.Children.Count ?? -1}");

        var cAfter = lv.ContainerFromItem(items[7500]) as ListViewItem;
        W(
            $"ContainerFromItem(items[7500]) = {(cAfter is null ? "null (not realized)" : "realized")}"
        );
        if (cAfter is not null)
        {
            var p = Origin(cAfter, lv);
            W(
                $"item 7500 container in ListView at {p.X:0.##},{p.Y:0.##} size {cAfter.ActualWidth:0.##}x{cAfter.ActualHeight:0.##} "
                    + $"(viewport is 800x400, so y in [0,400) means it is on screen)"
            );
            var rAfter = FindDescendant<RowPanel>(cAfter);
            if (rAfter is not null && rAfter.LastArrangeRects.Count == 10)
            {
                W(
                    $"after : arrange finalSize = {rAfter.LastArrangeFinal.Width:0.##} x {rAfter.LastArrangeFinal.Height:0.##}"
                );
                W(
                    $"after : row cells c0 x={rAfter.LastArrangeRects[0].X:0.##} .. c9 x={rAfter.LastArrangeRects[9].X:0.##}  "
                        + $"(table offset still {RowPanel.SharedOffset})"
                );
                var shot = await CaptureAsync(lv);
                double y = p.Y + cAfter.ActualHeight / 2;
                W($"capture {shot}");
                W(
                    $"  with offset 900, x=100 falls in c5 (1000-900=100..300): expect BGRA(64,0,224,255) = {Pixel(shot, 200, y)}"
                );
                W(
                    $"  x=600 falls in c7 (1400-900=500..700): expect BGRA(192,192,0,255) = {Pixel(shot, 600, y)}"
                );
            }
        }

        W("");
        W("--- calling ScrollIntoView(items[7500]) a second time ---");
        lv.ScrollIntoView(items[7500]);
        await Settle(1200);
        var cSecond = lv.ContainerFromItem(items[7500]) as ListViewItem;
        if (sv is not null)
            W(
                $"after 2nd call: VOffset={sv.VerticalOffset:0.##} HOffset={sv.HorizontalOffset:0.##} ExtentH={sv.ExtentHeight:0.##}"
                    + (
                        cSecond is null
                            ? ", container not realized"
                            : $", container y={Origin(cSecond, lv).Y:0.##}"
                    )
            );

        W("");
        W("--- same call with the table offset back at 0 ---");
        RowPanel.SharedOffset = 0;
        RowPanel.InvalidateAll();
        await Settle(350);
        lv.ScrollIntoView(items[0]);
        await Settle(700);
        if (sv is not null)
            W(
                $"after ScrollIntoView(item 0): VOffset={sv.VerticalOffset:0.##} HOffset={sv.HorizontalOffset:0.##}"
            );
        W($"realized containers = {lv.ItemsPanelRoot?.Children.Count ?? -1}");
        var r0 = FindRow(lv, 0);
        if (r0 is not null && r0.LastArrangeRects.Count == 10)
            W(
                $"row0 cells c0 x={r0.LastArrangeRects[0].X:0.##} .. c9 x={r0.LastArrangeRects[9].X:0.##}"
            );
    }

    private static RowPanel? FindRow(ListView lv, int index)
    {
        var c = lv.ContainerFromIndex(index) as ListViewItem;
        return c is null ? null : FindDescendant<RowPanel>(c);
    }
}
