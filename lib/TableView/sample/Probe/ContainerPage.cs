using System.Collections.ObjectModel;
using System.Reflection;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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

/// <summary>
/// Container measurements: Q7 (multi-select check box and the content inset),
/// Q8 (removing the inset without retemplating), Q9 (collection Reset),
/// Q10 (automation peer replacement), Q11 (visual states),
/// Q12 (geometry re-run in the surviving mode).
/// </summary>
public sealed partial class ContainerPage : Page
{
    private readonly StringBuilder _log = new();
    private double _scale = 1.0;
    private bool _finished;

    private static readonly Color Grey = Color.FromArgb(255, 128, 128, 128);

    /// <summary>Content-origin x measured under SelectionMode=None. The register baseline.</summary>
    private double _baselineMarkerX = double.NaN;

    /// <summary>Set by Q8 when a fix removes the inset. Applied again in Q12.</summary>
    private string _fixName = "(none found)";
    private Action<ListView>? _fixBeforeShow;
    private Action<ListView>? _fixAfterRealize;

    public ContainerPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void W(string s) => _log.AppendLine(s);

    private void Section(string s)
    {
        W("");
        W("==================================================================");
        W(s);
        W("==================================================================");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(420);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { W("\n!!! WATCHDOG FIRED !!!"); Finish(); };
        timer.Start();

        _scale = XamlRoot?.RasterizationScale ?? 1.0;
        RequestedTheme = ElementTheme.Light;
        W($"container probe started {DateTime.Now:O}   RasterizationScale={_scale}");
        W($"Application.RequestedTheme={Application.Current.RequestedTheme}  Page.ActualTheme={ActualTheme}");
        W("ListView background is mid grey BGRA(128,128,128,255) so a 3.5% black overlay is visible.");
        W("Content template starts with a 6 DIP pure blue (#FF0000FF) marker at content x = 0.");

        SizeWindow();
        await Settle(500);

        await Guarded("Q7", Q7Async);
        await Guarded("Q7b", Q7bAsync);
        await Guarded("Q8", Q8Async);
        await Guarded("Q9", Q9Async);
        await Guarded("Q10", Q10Async);
        await Guarded("Q11", Q11Async);
        await Guarded("Q12", Q12Async);

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
        W($"\ncontainer probe finished {DateTime.Now:O}");
        var text = _log.ToString();
        foreach (var t in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "container-results.txt"),
            Path.Combine(Path.GetTempPath(), "container-results.txt"),
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
            w.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(
                (int)Math.Ceiling(940 * _scale), (int)Math.Ceiling(560 * _scale)));
            w.Activate();
        }
        catch (Exception ex) { W("resize failed: " + ex.Message); }
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
        for (int i = 0; i < n; i++) l.Add(new Item { Index = i, Name = "item " + i });
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

    private static Point Origin(UIElement child, UIElement ancestor) =>
        child.TransformToVisual(ancestor).TransformPoint(new Point(0, 0));

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

    private string Scan(Shot s, double dipY, double x0, double x1)
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
            if (c != last) { sb.Append($" x{x - x0:0}={c}"); last = c; }
        }
        return sb.Length == 0 ? "(nothing)" : sb.ToString();
    }

    private static bool IsInk(byte b, byte g, byte r) =>
        Math.Abs(b - 128) > 6 || Math.Abs(g - 128) > 6 || Math.Abs(r - 128) > 6;

    private static bool IsBlue(byte b, byte g, byte r) => b > 180 && g < 80 && r < 80;

    /// <summary>Accent #0067C0 arrives as BGR(192,103,0).</summary>
    private static bool IsAccent(byte b, byte g, byte r) => b > 140 && g > 55 && g < 175 && r < 95;

    /// <summary>
    /// First column, in DIP relative to <paramref name="xDip0"/>, that contains any pixel
    /// matching the predicate anywhere in the row's vertical band.
    /// </summary>
    private double FirstMatchX(Shot s, double xDip0, double yDip0, double wDip, double hDip,
                               Func<byte, byte, byte, bool> pred)
    {
        if (!s.Ok) return double.NaN;
        for (double dx = 0; dx <= wDip; dx += 0.5)
        {
            int px = (int)Math.Round((xDip0 + dx) * _scale);
            if (px < 0 || px >= s.W) continue;
            for (int py = (int)Math.Round(yDip0 * _scale); py < (int)Math.Round((yDip0 + hDip) * _scale); py++)
            {
                if (py < 0 || py >= s.H) continue;
                int i = (py * s.W + px) * 4;
                if (pred(s.Px[i], s.Px[i + 1], s.Px[i + 2])) return dx;
            }
        }
        return double.NaN;
    }

    private string MatchRange(Shot s, double xDip0, double yDip0, double wDip, double hDip,
                              Func<byte, byte, byte, bool> pred)
    {
        double first = double.NaN, last = double.NaN;
        if (!s.Ok) return "capture-failed";
        for (double dx = 0; dx <= wDip; dx += 0.5)
        {
            int px = (int)Math.Round((xDip0 + dx) * _scale);
            if (px < 0 || px >= s.W) continue;
            bool hit = false;
            for (int py = (int)Math.Round(yDip0 * _scale); py < (int)Math.Round((yDip0 + hDip) * _scale); py++)
            {
                if (py < 0 || py >= s.H) continue;
                int i = (py * s.W + px) * 4;
                if (pred(s.Px[i], s.Px[i + 1], s.Px[i + 2])) { hit = true; break; }
            }
            if (hit) { if (double.IsNaN(first)) first = dx; last = dx; }
        }
        return double.IsNaN(first) ? "none" : $"x {first:0.#} .. {last:0.#} DIP";
    }

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

    // ---------------------------------------------------------- reflection helpers

    private static bool TrySetProp(object target, string prop, object? value, out string note)
    {
        var pi = target.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
        if (pi is null) { note = "no such property"; return false; }
        if (!pi.CanWrite) { note = "property has no setter"; return false; }
        if (pi.PropertyType.IsEnum && value is string es)
        {
            try { value = Enum.Parse(pi.PropertyType, es); }
            catch (Exception ex) { note = $"cannot parse '{es}' as {pi.PropertyType.FullName}: {ex.Message}"; return false; }
        }
        try { pi.SetValue(target, value); note = "set ok"; return true; }
        catch (Exception ex) { note = ex.GetType().Name + ": " + ex.Message; return false; }
    }

    private static object? GetProp(object target, string prop)
    {
        var pi = target.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance);
        try { return pi?.GetValue(target); } catch { return null; }
    }

    private static DependencyProperty? DpOf(Type t, string name)
    {
        var f = t.GetField(name + "Property", BindingFlags.Public | BindingFlags.Static);
        return f?.GetValue(null) as DependencyProperty;
    }

    private static object? EnumValue(string typeName, string member)
    {
        var t = typeof(ListViewItemPresenter).Assembly.GetType(typeName);
        if (t is null || !t.IsEnum) return null;
        try { return Enum.Parse(t, member); } catch { return null; }
    }

    // ====================================================================== shared measure

    private sealed class RowMeasure
    {
        public double MarkerX = double.NaN;
        public double InkX = double.NaN;
        public string Accent = "";
        public string RowMean = "";
        public string States = "";
        public string Scan = "";
        public double ContainerW, ContainerH;
        public string ContainerBox = "";
    }

    private async Task<RowMeasure> MeasureRow(ListView lv, int index, string label)
    {
        var m = new RowMeasure();
        if (lv.ContainerFromIndex(index) is not ListViewItem c)
        {
            W($"    {label}: container {index} not realized");
            return m;
        }
        var shot = await CaptureAsync(lv);
        var o = Origin(c, lv);
        double h = c.ActualHeight;
        m.ContainerW = c.ActualWidth; m.ContainerH = h;
        m.ContainerBox = $"origin-in-ListView=({o.X:0.##},{o.Y:0.##}) size={c.ActualWidth:0.##}x{h:0.##} " +
                         $"Margin={c.Margin} Padding={c.Padding}";
        // Scan from the ListView's own left edge (x = 0), not the container origin,
        // so a negative container Margin cannot hide the shift.
        m.MarkerX = FirstMatchX(shot, 0, o.Y + 1, 120, h - 2, IsBlue);
        m.InkX = FirstMatchX(shot, 0, o.Y + 1, 120, h - 2, IsInk);
        m.Accent = MatchRange(shot, 0, o.Y + 1, 120, h - 2, IsAccent);
        m.RowMean = Mean(shot, 0, o.Y + 1, 780, h - 2);
        m.States = StatesOf(c);
        m.Scan = Scan(shot, o.Y + h / 2, 0, 60);
        W($"    {label}");
        W($"      {m.ContainerBox}");
        W($"      first BLUE marker x = {Fmt(m.MarkerX)}   first INK x = {Fmt(m.InkX)}   (DIP from ListView left edge)");
        W($"      accent-coloured pixels: {m.Accent}");
        W($"      row mean = {m.RowMean}");
        W($"      states = {m.States}");
        W($"      scan y=mid (x from ListView left) ={m.Scan}");
        return m;
    }

    private static string Fmt(double d) => double.IsNaN(d) ? "not found" : d.ToString("0.#");

    private ListView MakeMarkerList(ListViewSelectionMode mode, bool? multiCheckBox, Style? itemStyle)
    {
        var lv = new ListView
        {
            SelectionMode = mode,
            ItemsSource = MakeItems(6),
            ItemTemplate = (DataTemplate)Resources["MarkerTemplate"],
            ItemContainerStyle = itemStyle ?? (Style)Resources["TightItemStyle"],
            Background = new SolidColorBrush(Grey),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Width = 800,
            Height = 300,
        };
        if (multiCheckBox.HasValue) lv.IsMultiSelectCheckBoxEnabled = multiCheckBox.Value;
        return lv;
    }

    private void SelectFirst(ListView lv)
    {
        if (lv.SelectionMode == ListViewSelectionMode.None)
        {
            if (lv.ContainerFromIndex(0) is ListViewItem c) c.IsSelected = true;
        }
        else if (lv.SelectionMode == ListViewSelectionMode.Single)
        {
            lv.SelectedIndex = 0;
        }
        else
        {
            var src = (List<Item>)lv.ItemsSource;
            lv.SelectedItems.Add(src[0]);
        }
    }

    // ====================================================================== Q7

    private async Task Q7Async()
    {
        Section("Q7  Does ListView.IsMultiSelectCheckBoxEnabled=False remove the 28 DIP content\n" +
                "    inset under SelectionMode=Multiple?\n" +
                "    Four configurations. Each measures the content origin by pixel scan, then\n" +
                "    selects row 0 and measures again.");

        var cases = new (string Label, ListViewSelectionMode Mode, bool? Cb)[]
        {
            ("A  SelectionMode=None", ListViewSelectionMode.None, null),
            ("B  SelectionMode=Single", ListViewSelectionMode.Single, null),
            ("C  SelectionMode=Multiple, IsMultiSelectCheckBoxEnabled left at its default",
                ListViewSelectionMode.Multiple, null),
            ("D  SelectionMode=Multiple, IsMultiSelectCheckBoxEnabled = False",
                ListViewSelectionMode.Multiple, false),
        };

        foreach (var (label, mode, cb) in cases)
        {
            W("");
            W($"---------------- {label} ----------------");
            var lv = MakeMarkerList(mode, cb, null);
            await ShowAsync(lv, 450);
            W($"  lv.SelectionMode={lv.SelectionMode}  lv.IsMultiSelectCheckBoxEnabled={lv.IsMultiSelectCheckBoxEnabled}");

            var before = await MeasureRow(lv, 0, "BEFORE anything is selected (row 0)");
            if (mode == ListViewSelectionMode.None && double.IsNaN(_baselineMarkerX))
                _baselineMarkerX = before.MarkerX;

            SelectFirst(lv);
            await Settle(350);
            W($"  after selecting row 0: SelectedItems.Count={lv.SelectedItems.Count} SelectedIndex={lv.SelectedIndex}" +
              $"  container.IsSelected={(lv.ContainerFromIndex(0) as ListViewItem)?.IsSelected}");
            var after = await MeasureRow(lv, 0, "AFTER row 0 is selected");
            await MeasureRow(lv, 1, "row 1, never touched (control)");

            W($"  DELTA row 0 mean before -> after : {before.RowMean}  ->  {after.RowMean}");
            W($"  content origin before={Fmt(before.MarkerX)} after={Fmt(after.MarkerX)}");
        }
        W("");
        W($"Q7 baseline content origin under SelectionMode=None = {Fmt(_baselineMarkerX)} DIP");
    }

    // ====================================================================== Q7b

    private async Task Q7bAsync()
    {
        Section("Q7b  Where the container's own left-hand chrome sits.\n" +
                "     Same measurement, but the content template starts with 60 DIP of empty space,\n" +
                "     so the selection indicator, the check box and the focus rectangle are not\n" +
                "     painted over by the content marker. Content origin = markerX - 60.");

        foreach (var (label, mode, cb) in new (string, ListViewSelectionMode, bool?)[]
        {
            ("Single", ListViewSelectionMode.Single, null),
            ("Multiple, IsMultiSelectCheckBoxEnabled default", ListViewSelectionMode.Multiple, null),
            ("Multiple, IsMultiSelectCheckBoxEnabled = False", ListViewSelectionMode.Multiple, false),
        })
        {
            W("");
            W($"---------------- {label} ----------------");
            var lv = MakeMarkerList(mode, cb, null);
            lv.ItemTemplate = (DataTemplate)Resources["GutterTemplate"];
            await ShowAsync(lv, 450);

            await GutterRow(lv, 0, "row 0, nothing selected");

            SelectFirst(lv);
            await Settle(350);
            await GutterRow(lv, 0, "row 0, SELECTED");

            var c2 = lv.ContainerFromIndex(2) as ListViewItem;
            bool ok = c2 is not null && c2.Focus(FocusState.Keyboard);
            await Settle(350);
            W($"  row2.Focus(Keyboard)={ok} FocusState={(c2?.FocusState.ToString() ?? "?")} IsSelected={c2?.IsSelected}");
            await GutterRow(lv, 2, "row 2, keyboard FOCUS, not selected");
        }
    }

    private async Task GutterRow(ListView lv, int index, string label)
    {
        if (lv.ContainerFromIndex(index) is not ListViewItem c) { W($"    {label}: not realized"); return; }
        var shot = await CaptureAsync(lv);
        var o = Origin(c, lv);
        double h = c.ActualHeight;
        double marker = FirstMatchX(shot, 0, o.Y + 1, 140, h - 2, IsBlue);
        W($"    {label}");
        W($"      blue marker x = {Fmt(marker)}  -> content origin = {(double.IsNaN(marker) ? "not found" : (marker - 60).ToString("0.#"))} DIP");
        W($"      accent (#0067C0-like) pixels in the left 60 DIP: {MatchRange(shot, 0, o.Y + 1, 59, h - 2, IsAccent)}");
        W($"      any ink in the left 60 DIP: {MatchRange(shot, 0, o.Y + 1, 59, h - 2, IsInk)}");
        W($"      scan y=mid, x 0..60 ={Scan(shot, o.Y + h / 2, 0, 60)}");
        W($"      scan y=top+1, x 0..60 ={Scan(shot, o.Y + 1, 0, 60)}");
        W($"      row mean = {Mean(shot, 0, o.Y + 1, 780, h - 2)}   states = {StatesOf(c)}");
    }

    // ====================================================================== Q8

    private async Task Q8Async()
    {
        Section("Q8  If the inset survives, can it be removed without retemplating the container?\n" +
                "    First enumerate what ListViewItemPresenter actually offers, then try each route\n" +
                "    and re-measure the content origin under SelectionMode=Multiple.");

        // --- enumerate the type
        var t = typeof(ListViewItemPresenter);
        W($"  type = {t.FullName}");
        W($"  base chain = {string.Join(" -> ", Chain(t))}");
        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .OrderBy(p => p.Name).ToList();
        W($"  declared public instance properties = {props.Count}");
        foreach (var p in props)
        {
            var dp = DpOf(t, p.Name);
            W($"    {p.Name,-38} {p.PropertyType.Name,-32} " +
              $"{(p.CanWrite ? "settable" : "read-only")}  " +
              $"{(dp is not null ? "DependencyProperty present -> reachable from a Style setter" : "no DependencyProperty -> NOT reachable from a Style setter")}");
        }
        var inherited = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(p => p.DeclaringType != t).Select(p => p.Name).OrderBy(x => x).ToList();
        W($"  inherited public instance properties = {string.Join(", ", inherited)}");

        // Also enumerate ListViewItem, which is the type an ItemContainerStyle can target.
        W("");
        var ti = typeof(ListViewItem);
        var tips = ti.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .OrderBy(p => p.Name).Select(p => p.Name).ToList();
        W($"  ListViewItem declared public instance properties = {(tips.Count == 0 ? "(none)" : string.Join(", ", tips))}");
        var tibase = typeof(ListViewItem).BaseType;
        W($"  ListViewItem base = {tibase?.FullName}");
        if (tibase is not null)
            W($"    its declared properties = {string.Join(", ", tibase.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => p.Name).OrderBy(x => x))}");

        // --- route 1: implicit Style targeting ListViewItemPresenter
        var presenterCandidates = new (string Prop, string Desc, Func<object?> Value)[]
        {
            ("CheckMode", "CheckMode = Hidden", () => "Hidden"),
            ("CheckMode", "CheckMode = Inline", () => "Inline"),
            ("CheckMode", "CheckMode = Overlay", () => "Overlay"),
            ("SelectionCheckMarkVisualEnabled", "SelectionCheckMarkVisualEnabled = False", () => false),
            ("CheckBoxBrush", "CheckBoxBrush = Transparent",
                () => new SolidColorBrush(Colors.Transparent)),
            ("ContentMargin", "ContentMargin = 0", () => new Thickness(0)),
            ("ContentMargin", "ContentMargin = -28,0,0,0", () => new Thickness(-28, 0, 0, 0)),
        };

        foreach (var (prop, desc, val) in presenterCandidates)
        {
            var dp = DpOf(t, prop);
            var value = val();
            W("");
            W($"---------------- implicit Style TargetType=ListViewItemPresenter, {desc} ----------------");
            if (dp is null) { W($"  ListViewItemPresenter.{prop}Property does not exist; a Style setter cannot reach it. SKIPPED."); continue; }
            if (value is null) { W($"  value for {desc} could not be constructed. SKIPPED."); continue; }

            var lv = MakeMarkerList(ListViewSelectionMode.Multiple, null, null);
            try
            {
                var st = new Style(t);
                st.Setters.Add(new Setter(dp, value));
                lv.Resources[t] = st;
            }
            catch (Exception ex) { W("  building/attaching the Style threw " + ex.GetType().Name + ": " + ex.Message); continue; }

            await ShowAsync(lv, 450);
            var p0 = lv.ContainerFromIndex(0) is ListViewItem cc ? FindDescendant<ListViewItemPresenter>(cc) : null;
            W($"  presenter found = {p0 is not null}; presenter.{prop} reads back = {(p0 is null ? "?" : GetProp(p0, prop)?.ToString() ?? "null")}");
            var m = await MeasureRow(lv, 0, "row 0, nothing selected");
            SelectFirst(lv);
            await Settle(300);
            var ms = await MeasureRow(lv, 0, "row 0, selected");
            NoteFix($"implicit Style TargetType=ListViewItemPresenter, {desc}", m.MarkerX,
                lv2 => { var st = new Style(t); st.Setters.Add(new Setter(dp, val()!)); lv2.Resources[t] = st; }, null);
        }

        // --- route 2: set the same properties directly on realized presenters
        foreach (var (prop, desc, val) in presenterCandidates)
        {
            W("");
            W($"---------------- direct set on every realized ListViewItemPresenter, {desc} ----------------");
            var value = val();
            if (value is null) { W("  value could not be constructed. SKIPPED."); continue; }
            var lv = MakeMarkerList(ListViewSelectionMode.Multiple, null, null);
            await ShowAsync(lv, 450);
            var presenters = new List<ListViewItemPresenter>();
            if (lv.ItemsPanelRoot is not null) FindAll(lv.ItemsPanelRoot, presenters);
            string note = "no presenters";
            int okCount = 0;
            var pit = typeof(ListViewItemPresenter).GetProperty(prop);
            if (pit is not null && pit.PropertyType.IsEnum)
                W($"  {prop} is enum {pit.PropertyType.FullName} with members: {string.Join(", ", Enum.GetNames(pit.PropertyType))}");
            if (presenters.Count > 0)
                W($"  {prop} reads back BEFORE the set = {GetProp(presenters[0], prop)?.ToString() ?? "null"}");
            foreach (var p in presenters) { if (TrySetProp(p, prop, value, out note)) okCount++; }
            W($"  presenters={presenters.Count} set-ok={okCount} note={note}");
            if (presenters.Count > 0)
                W($"  {prop} reads back AFTER the set = {GetProp(presenters[0], prop)?.ToString() ?? "null"}");
            foreach (var p in presenters) p.InvalidateMeasure();
            await Settle(350);
            await MeasureRow(lv, 0, "row 0, nothing selected");
        }

        // --- route 3: negative Margin on the container
        W("");
        W("---------------- ItemContainerStyle Margin = -28,0,0,0 ----------------");
        {
            var st = new Style(typeof(ListViewItem));
            st.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(-28, 0, 0, 0)));
            st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            st.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            st.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
            st.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
            var lv = MakeMarkerList(ListViewSelectionMode.Multiple, null, st);
            await ShowAsync(lv, 450);
            var m = await MeasureRow(lv, 0, "row 0, nothing selected");
            SelectFirst(lv);
            await Settle(300);
            await MeasureRow(lv, 0, "row 0, selected");
            NoteFix("ItemContainerStyle Margin = -28,0,0,0", m.MarkerX, lv2 => { }, null);
        }

        // --- route 4: negative Padding on the container
        W("");
        W("---------------- ItemContainerStyle Padding = -28,0,0,0 ----------------");
        {
            var st = new Style(typeof(ListViewItem));
            st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(-28, 0, 0, 0)));
            st.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            st.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
            st.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
            var lv = MakeMarkerList(ListViewSelectionMode.Multiple, null, st);
            await ShowAsync(lv, 450);
            var m = await MeasureRow(lv, 0, "row 0, nothing selected");
            SelectFirst(lv);
            await Settle(300);
            await MeasureRow(lv, 0, "row 0, selected");
        }

        // --- route 5: IsMultiSelectCheckBoxEnabled=False combined with negative Margin
        W("");
        W("---------------- IsMultiSelectCheckBoxEnabled=False AND container Margin -28 ----------------");
        {
            var st = new Style(typeof(ListViewItem));
            st.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(-28, 0, 0, 0)));
            st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            st.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            st.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
            st.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
            var lv = MakeMarkerList(ListViewSelectionMode.Multiple, false, st);
            await ShowAsync(lv, 450);
            await MeasureRow(lv, 0, "row 0, nothing selected");
            SelectFirst(lv);
            await Settle(300);
            await MeasureRow(lv, 0, "row 0, selected");
        }

        W("");
        W($"Q8 route recorded for the Q12 re-run: {_fixName}");
    }

    private static IEnumerable<string> Chain(Type t)
    {
        var list = new List<string>();
        for (var x = t; x is not null; x = x.BaseType) list.Add(x.Name);
        return list;
    }

    private void NoteFix(string name, double markerX, Action<ListView> before, Action<ListView>? after)
    {
        if (_fixBeforeShow is not null) return;
        if (double.IsNaN(markerX) || double.IsNaN(_baselineMarkerX)) return;
        if (Math.Abs(markerX - _baselineMarkerX) <= 1.0)
        {
            _fixName = name;
            _fixBeforeShow = before;
            _fixAfterRealize = after;
            W($"  >>> this route puts the content origin back at the SelectionMode=None baseline ({Fmt(markerX)} vs {Fmt(_baselineMarkerX)}).");
        }
    }

    // ====================================================================== Q9

    private sealed class ResetItem
    {
        public int Id { get; init; }
        public int Gen { get; init; }
        public override string ToString() => $"#{Id} gen{Gen}";
    }

    private async Task Q9Async()
    {
        Section("Q9  What does a collection Reset do to selection and focus, under Multiple?\n" +
                "    ObservableCollection of 12. Rows 2, 5 and 9 selected through the ListView API.\n" +
                "    Keyboard focus put on row 5. Then Clear() plus re-add.");

        var coll = new ObservableCollection<ResetItem>();
        for (int i = 0; i < 12; i++) coll.Add(new ResetItem { Id = i, Gen = 1 });
        var gen1 = coll.ToList();

        var lv = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            ItemsSource = coll,
            ItemTemplate = (DataTemplate)Resources["TextTemplate"],
            ItemContainerStyle = (Style)Resources["TightItemStyle"],
            Background = new SolidColorBrush(Grey),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Width = 800,
            Height = 380,
        };

        int selChanged = 0, addedTotal = 0, removedTotal = 0;
        lv.SelectionChanged += (s, e) => { selChanged++; addedTotal += e.AddedItems.Count; removedTotal += e.RemovedItems.Count; };

        await ShowAsync(lv, 500);

        lv.SelectedItems.Add(gen1[2]);
        lv.SelectedItems.Add(gen1[5]);
        lv.SelectedItems.Add(gen1[9]);
        await Settle(250);

        var c5 = lv.ContainerFromIndex(5) as ListViewItem;
        bool focusOk = c5 is not null && c5.Focus(FocusState.Keyboard);
        await Settle(250);
        W($"  Focus(FocusState.Keyboard) on the row-5 container returned {focusOk}");

        DumpSel(lv, coll, gen1, null, "BEFORE the reset");
        W($"  SelectionChanged so far: {selChanged} events, {addedTotal} added, {removedTotal} removed");

        // ---------------- reset with NEW but equivalent instances ----------------
        W("");
        W("---- coll.Clear() then re-add 12 NEW instances with the same Id values, same order ----");
        selChanged = 0; addedTotal = 0; removedTotal = 0;
        coll.Clear();
        await Settle(250);
        W($"  immediately after Clear(): SelectedItems.Count={lv.SelectedItems.Count} SelectedIndex={lv.SelectedIndex} " +
          $"Items.Count={lv.Items.Count}");
        W($"  focused element right after Clear() = {Describe(FocusManager.GetFocusedElement(XamlRoot))}");

        var gen2 = new List<ResetItem>();
        for (int i = 0; i < 12; i++) { var it = new ResetItem { Id = i, Gen = 2 }; gen2.Add(it); coll.Add(it); }
        await Settle(500);

        DumpSel(lv, coll, gen1, gen2, "AFTER Clear() + re-add of NEW equivalent instances");
        W($"  SelectionChanged during the reset: {selChanged} events, {addedTotal} added, {removedTotal} removed");

        // ---------------- reset with the SAME instances in a different order ----------------
        W("");
        W("---- restore a known selection, then Clear() and re-add the SAME instances shuffled ----");
        lv.SelectedItems.Clear();
        await Settle(200);
        lv.SelectedItems.Add(gen2[2]);
        lv.SelectedItems.Add(gen2[5]);
        lv.SelectedItems.Add(gen2[9]);
        var cc5 = lv.ContainerFromIndex(5) as ListViewItem;
        cc5?.Focus(FocusState.Keyboard);
        await Settle(300);
        DumpSel(lv, coll, gen1, gen2, "BEFORE the second reset");

        selChanged = 0; addedTotal = 0; removedTotal = 0;
        var shuffled = new List<ResetItem> { gen2[9], gen2[0], gen2[5], gen2[11], gen2[2], gen2[1],
                                             gen2[3], gen2[4], gen2[6], gen2[7], gen2[8], gen2[10] };
        coll.Clear();
        await Settle(200);
        foreach (var it in shuffled) coll.Add(it);
        await Settle(500);

        W("  new order by Id: " + string.Join(",", coll.Select(x => x.Id)));
        DumpSel(lv, coll, gen1, gen2, "AFTER Clear() + re-add of the SAME instances in a different order");
        W($"  SelectionChanged during the second reset: {selChanged} events, {addedTotal} added, {removedTotal} removed");
    }

    private void DumpSel(ListView lv, ObservableCollection<ResetItem> coll,
                         List<ResetItem> gen1, List<ResetItem>? gen2, string label)
    {
        W($"  {label}");
        W($"    Items.Count={lv.Items.Count}  SelectedItems.Count={lv.SelectedItems.Count}  SelectedIndex={lv.SelectedIndex}");
        var idx = new List<string>();
        foreach (var o in lv.SelectedItems)
        {
            var it = o as ResetItem;
            int i = it is null ? -1 : coll.IndexOf(it);
            string origin = it is null ? "?" :
                gen1.Contains(it) ? "gen1 instance" : (gen2 is not null && gen2.Contains(it) ? "gen2 instance" : "unknown instance");
            idx.Add($"index {i} ({it}) [{origin}]");
        }
        W($"    selected: {(idx.Count == 0 ? "(none)" : string.Join("; ", idx))}");
        try
        {
            var ranges = lv.SelectedRanges;
            W($"    SelectedRanges = {(ranges.Count == 0 ? "(none)" : string.Join(", ", ranges.Select(r => $"[{r.FirstIndex}..{r.LastIndex}]")))}");
        }
        catch (Exception ex) { W("    SelectedRanges threw " + ex.Message); }

        var fe = FocusManager.GetFocusedElement(XamlRoot);
        W($"    FocusManager.GetFocusedElement = {Describe(fe)}");
        for (int i = 0; i < Math.Min(12, lv.Items.Count); i++)
        {
            if (lv.ContainerFromIndex(i) is ListViewItem c && (c.FocusState != FocusState.Unfocused || c.IsSelected))
                W($"    container[{i}]: IsSelected={c.IsSelected} FocusState={c.FocusState} states={StatesOf(c)}");
        }
    }

    private string Describe(object? o)
    {
        if (o is null) return "null";
        if (o is ListViewItem lvi)
        {
            var lv = FindAncestor<ListView>(lvi);
            int i = lv?.IndexFromContainer(lvi) ?? -2;
            return $"ListViewItem index {i} (content {lvi.Content}) FocusState={lvi.FocusState}";
        }
        if (o is FrameworkElement f) return $"{o.GetType().Name} name='{f.Name}'";
        return o.GetType().Name;
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    // ====================================================================== Q10

    private async Task Q10Async()
    {
        Section("Q10  Can the item's automation peer be replaced or corrected?\n" +
                "     A ListView subclass supplies its own containers, its own container peer and its\n" +
                "     own ListView peer. The question is which peer UIA is handed.");

        // What hooks exist at all?
        W("  overridable automation hooks, by reflection:");
        foreach (var (type, name) in new[]
        {
            (typeof(UIElement), "OnCreateAutomationPeer"),
            (typeof(ItemsControlAutomationPeer), "OnCreateItemAutomationPeer"),
            (typeof(AutomationPeer), "GetChildrenCore"),
            (typeof(AutomationPeer), "GetPatternCore"),
            (typeof(AutomationPeer), "GetClassNameCore"),
        })
        {
            var mi = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            W($"    {type.Name}.{name}: {(mi is null ? "does not exist" : $"exists, virtual={mi.IsVirtual}, final={mi.IsFinal}")}");
        }

        var lv = new PeerList
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            ItemsSource = MakeItems(6),
            ItemTemplate = (DataTemplate)Resources["MarkerTemplate"],
            ItemContainerStyle = (Style)Resources["TightItemStyle"],
            Background = new SolidColorBrush(Grey),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Width = 800,
            Height = 300,
        };
        await ShowAsync(lv, 500);

        var c0 = lv.ContainerFromIndex(0);
        W("");
        W($"  container type actually generated = {c0?.GetType().FullName ?? "(null)"}");
        W($"  PeerItem.OnCreateAutomationPeer calls seen = {PeerItem.PeerCreated}");
        W($"  PeerList.OnCreateAutomationPeer calls seen     = {PeerList.PeerCreated}");

        if (c0 is UIElement ce)
        {
            var cp = FrameworkElementAutomationPeer.FromElement(ce);
            W($"  FrameworkElementAutomationPeer.FromElement(container) = {cp?.GetType().FullName ?? "null"}");
            if (cp is not null)
            {
                W($"    is our ItemPeer = {cp is ItemPeer}");
                W($"    ClassName = {SafeCall(() => cp.GetClassName())}");
                var pat = SafeObj(() => cp.GetPattern(PatternInterface.SelectionItem));
                W($"    GetPattern(SelectionItem) = {(pat is null ? "null" : pat.GetType().FullName)}");
                if (pat is ISelectionItemProvider sip)
                    W($"    ISelectionItemProvider.IsSelected = {SafeCall(() => sip.IsSelected.ToString())}");
            }
        }

        // select row 0, then look again
        var src = (List<Item>)lv.ItemsSource;
        lv.SelectedItems.Add(src[0]);
        await Settle(300);

        var lvPeer = FrameworkElementAutomationPeer.FromElement(lv);
        W("");
        W($"  ListView peer = {lvPeer?.GetType().FullName ?? "null"}   is our ListPeer = {lvPeer is ListPeer}");
        if (lvPeer is not null)
        {
            var sel = SafeObj(() => lvPeer.GetPattern(PatternInterface.Selection));
            W($"    ListView GetPattern(Selection) = {(sel is null ? "null" : sel.GetType().FullName)}");
            var kids = SafeObj(() => lvPeer.GetChildren()) as IList<AutomationPeer>;
            W($"    GetChildren() count = {(kids is null ? "threw/null" : kids.Count.ToString())}");
            if (kids is not null)
            {
                for (int i = 0; i < Math.Min(3, kids.Count); i++)
                {
                    var k = kids[i];
                    var pat = SafeObj(() => k.GetPattern(PatternInterface.SelectionItem));
                    string isSel = "n/a";
                    if (pat is ISelectionItemProvider sp) isSel = SafeCall(() => sp.IsSelected.ToString());
                    else if (k is ISelectionItemProvider dsp) isSel = "direct-cast " + SafeCall(() => dsp.IsSelected.ToString());
                    W($"      child[{i}] = {k.GetType().FullName}");
                    W($"        is ItemPeer={k is ItemPeer}  ClassName={SafeCall(() => k.GetClassName())}");
                    W($"        GetPattern(SelectionItem)={(pat is null ? "null" : pat.GetType().FullName)}  IsSelected={isSel}");
                }
            }
        }

        // Does the ListView peer's GetChildren override actually get used?
        W("");
        W($"  ListPeer.GetChildrenCore calls seen = {ListPeer.ChildrenCoreCalls}");
        W($"  ItemPeer.GetPatternCore calls seen      = {ItemPeer.PatternCoreCalls}");
        W($"  ItemPeer instances created             = {ItemPeer.Created}");

        // For comparison: a stock ListView in the same configuration.
        W("");
        W("  ---- stock ListView, same configuration, for comparison ----");
        var stock = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            ItemsSource = MakeItems(6),
            ItemTemplate = (DataTemplate)Resources["MarkerTemplate"],
            ItemContainerStyle = (Style)Resources["TightItemStyle"],
            Width = 800,
            Height = 300,
        };
        await ShowAsync(stock, 400);
        var sc0 = stock.ContainerFromIndex(0) as UIElement;
        var scp = sc0 is null ? null : FrameworkElementAutomationPeer.FromElement(sc0);
        W($"    container peer = {scp?.GetType().FullName ?? "null"}");
        var sPeer = FrameworkElementAutomationPeer.FromElement(stock);
        var sKids = sPeer is null ? null : SafeObj(() => sPeer.GetChildren()) as IList<AutomationPeer>;
        W($"    ListView peer = {sPeer?.GetType().FullName ?? "null"}  children = {(sKids is null ? "?" : sKids.Count.ToString())}");
        if (sKids is not null && sKids.Count > 0)
            W($"    child[0] = {sKids[0].GetType().FullName}");
    }

    private static string SafeCall(Func<string> f)
    { try { return f(); } catch (Exception ex) { return "threw " + ex.GetType().Name + ": " + ex.Message; } }

    private static object? SafeObj(Func<object?> f)
    { try { return f(); } catch { return null; } }

    // ====================================================================== Q11

    private async Task Q11Async()
    {
        Section("Q11  Can the container express a CURRENT row that is not selected?\n" +
                "     Every visual state group and every state name in the container template,\n" +
                "     in each selection mode, live and from ControlTemplate.LoadContent().");

        foreach (var (label, mode, cb, style) in new (string, ListViewSelectionMode, bool?, string?)[]
        {
            ("SelectionMode=None, no ItemContainerStyle", ListViewSelectionMode.None, null, null),
            ("SelectionMode=Multiple, no ItemContainerStyle", ListViewSelectionMode.Multiple, null, null),
            ("SelectionMode=Multiple, IsMultiSelectCheckBoxEnabled=False, TightItemStyle",
                ListViewSelectionMode.Multiple, false, "TightItemStyle"),
        })
        {
            W("");
            W($"---------------- {label} ----------------");
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
            if (cb.HasValue) lv.IsMultiSelectCheckBoxEnabled = cb.Value;
            await ShowAsync(lv, 450);
            if (lv.ContainerFromIndex(0) is ListViewItem c) DumpStates(c);
            else W("  container 0 not realized");
        }

        // Does keyboard focus alone draw anything, with nothing selected?
        W("");
        W("---------------- current-but-unselected, by pixels ----------------");
        W("    row 1 gets keyboard focus and is NOT selected. row 3 is selected and not focused.");
        {
            var lv = MakeMarkerList(ListViewSelectionMode.Multiple, false, null);
            await ShowAsync(lv, 450);
            var src = (List<Item>)lv.ItemsSource;
            lv.SelectedItems.Add(src[3]);
            var c1 = lv.ContainerFromIndex(1) as ListViewItem;
            bool ok = c1 is not null && c1.Focus(FocusState.Keyboard);
            await Settle(400);
            W($"    row1.Focus(Keyboard) returned {ok}; row1.FocusState={(c1?.FocusState.ToString() ?? "?")}; row1.IsSelected={c1?.IsSelected}");
            await MeasureRow(lv, 1, "row 1  FOCUSED, NOT selected");
            await MeasureRow(lv, 3, "row 3  SELECTED, not focused");
            await MeasureRow(lv, 5, "row 5  neither (control)");

            // Try every state name we found, to see whether any of them is accepted.
            if (c1 is not null)
            {
                W("    VisualStateManager.GoToState results on the row-1 container:");
                foreach (var name in CollectStateNames(c1))
                    W($"      GoToState(\"{name}\") = {VisualStateManager.GoToState(c1, name, false)}");
            }
        }
    }

    private void DumpStates(ListViewItem c)
    {
        W($"  container type = {c.GetType().FullName}");
        var root = VisualTreeHelper.GetChildrenCount(c) > 0
            ? VisualTreeHelper.GetChild(c, 0) as FrameworkElement : null;
        W($"  live template root = {root?.GetType().FullName ?? "(none)"}");
        if (root is not null) DumpGroups(VisualStateManager.GetVisualStateGroups(root), "live tree");
        W($"  Style = {(c.Style is null ? "(null)" : "TargetType=" + c.Style.TargetType?.Name + ", setters=" + c.Style.Setters.Count)}");
        W($"  Template = {(c.Template is null ? "(null)" : c.Template.GetType().FullName)}");
        var mi = c.Template?.GetType().GetMethod("LoadContent",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes);
        if (mi is null) W("  ControlTemplate exposes no LoadContent method; the template can only be read from the live tree.");
        else
        {
            try
            {
                var lc = mi.Invoke(c.Template, null) as FrameworkElement;
                W($"  LoadContent() root = {lc?.GetType().FullName ?? "(null)"}");
                if (lc is not null) DumpGroups(VisualStateManager.GetVisualStateGroups(lc), "LoadContent");
            }
            catch (Exception ex) { W("  LoadContent threw " + ex.GetType().Name + ": " + ex.Message); }
        }
    }

    private void DumpGroups(IList<VisualStateGroup>? groups, string where)
    {
        if (groups is null || groups.Count == 0) { W($"    [{where}] VisualStateGroups: none"); return; }
        W($"    [{where}] {groups.Count} group(s)");
        foreach (var g in groups)
        {
            var names = g.States.Select(s => s.Name).ToList();
            W($"      group '{g.Name}' current='{g.CurrentState?.Name ?? "(null)"}' states({names.Count}): {string.Join(", ", names)}");
        }
    }

    private static List<string> CollectStateNames(ListViewItem c)
    {
        var names = new List<string>();
        var root = VisualTreeHelper.GetChildrenCount(c) > 0
            ? VisualTreeHelper.GetChild(c, 0) as FrameworkElement : null;
        if (root is not null)
        {
            var groups = VisualStateManager.GetVisualStateGroups(root);
            if (groups is not null)
                foreach (var g in groups) foreach (var s in g.States) if (s.Name is not null) names.Add(s.Name);
        }
        if (names.Count == 0) names.AddRange(new[] { "Normal", "PointerOver", "Pressed", "Selected", "Enabled", "Disabled" });
        return names;
    }

    // ====================================================================== Q12

    private async Task Q12Async()
    {
        Section("Q12  Geometry re-run in the surviving mode.\n" +
                "     SelectionMode = Multiple, IsMultiSelectCheckBoxEnabled = False,\n" +
                "     ItemContainerStyle = TableItemStyle (Padding 0, no negative Margin).\n" +
                "     Q8's first recorded alternative route, for reference only: " + _fixName);

        RowPanel.SharedOffset = 0;

        var lv = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            IsMultiSelectCheckBoxEnabled = false,
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
        _fixBeforeShow?.Invoke(lv);
        ScrollViewer.SetHorizontalScrollMode(lv, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(lv, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(lv, ScrollMode.Enabled);
        ScrollViewer.SetVerticalScrollBarVisibility(lv, ScrollBarVisibility.Auto);

        await ShowAsync(lv, 900);
        _fixAfterRealize?.Invoke(lv);
        await Settle(300);

        var sv = FindDescendant<ScrollViewer>(lv);
        W($"  SelectionMode={lv.SelectionMode} IsMultiSelectCheckBoxEnabled={lv.IsMultiSelectCheckBoxEnabled}");
        if (sv is not null)
            W($"  ScrollViewer ExtentWidth={sv.ExtentWidth:0.##} ViewportWidth={sv.ViewportWidth:0.##} " +
              $"HorizontalOffset={sv.HorizontalOffset:0.##} ScrollableWidth={sv.ScrollableWidth:0.##} " +
              $"ExtentHeight={sv.ExtentHeight:0.##} VerticalOffset={sv.VerticalOffset:0.##}");
        W($"  ItemsPanelRoot = {lv.ItemsPanelRoot?.GetType().Name}");
        W($"  realized containers with 10,000 items = {lv.ItemsPanelRoot?.Children.Count ?? -1}");

        var c0 = lv.ContainerFromIndex(0) as ListViewItem;
        var row0 = c0 is null ? null : FindDescendant<RowPanel>(c0);
        if (c0 is null || row0 is null) { W("  container 0 or RowPanel missing; cannot continue"); return; }

        W("");
        W("  --- arrange at table-owned offset 0 ---");
        W($"  container0 ActualWidth={c0.ActualWidth:0.##} ActualHeight={c0.ActualHeight:0.##} " +
          $"Padding={c0.Padding} Margin={c0.Margin}");
        W($"  RowPanel measure available={row0.LastMeasureAvailable.Width:0.##}  returned={row0.LastMeasureReturned.Width:0.##}" +
          $"  arrange finalSize={row0.LastArrangeFinal.Width:0.##}x{row0.LastArrangeFinal.Height:0.##}");
        DumpCells(row0, lv, "offset=0");
        var shot0 = await CaptureAsync(lv);
        double rowY = Origin(c0, lv).Y + c0.ActualHeight / 2;
        W($"  capture {shot0}");
        W($"  pixel x=100 (expect c0 BGR(128,16,16))  = {Pixel(shot0, 100, rowY)}");
        W($"  pixel x=300 (expect c1 BGR(255,160,32)) = {Pixel(shot0, 300, rowY)}");
        W($"  pixel x=700 (expect c3 BGR(0,208,255))  = {Pixel(shot0, 700, rowY)}");

        // ---- offset change, InvalidateArrange ALONE -------------------------
        W("");
        W("  --- offset 0 -> 1200 using InvalidateArrange ALONE (no InvalidateMeasure) ---");
        var panels = new List<RowPanel>();
        if (lv.ItemsPanelRoot is not null) FindAll(lv.ItemsPanelRoot, panels);
        int measuresBefore = panels.Sum(p => p.MeasureCount);
        int arrangesBefore = panels.Sum(p => p.ArrangeCount);
        W($"  live RowPanels={panels.Count}  total MeasureOverride calls so far={measuresBefore}  ArrangeOverride={arrangesBefore}");

        RowPanel.SharedOffset = 1200;
        foreach (var p in panels) p.InvalidateArrange();
        await Settle(450);

        var panels2 = new List<RowPanel>();
        if (lv.ItemsPanelRoot is not null) FindAll(lv.ItemsPanelRoot, panels2);
        int measuresAfter = panels.Sum(p => p.MeasureCount);
        int arrangesAfter = panels.Sum(p => p.ArrangeCount);
        W($"  after InvalidateArrange alone: total MeasureOverride={measuresAfter} (delta {measuresAfter - measuresBefore})" +
          $"  ArrangeOverride={arrangesAfter} (delta {arrangesAfter - arrangesBefore})");
        W($"  specification section 20 requires the delta on MeasureOverride to be 0.");

        var c0b = lv.ContainerFromIndex(0) as ListViewItem;
        var row0b = c0b is null ? null : FindDescendant<RowPanel>(c0b);
        if (row0b is not null && c0b is not null)
        {
            DumpCells(row0b, lv, "offset=1200");
            var shot1 = await CaptureAsync(lv);
            double y2 = Origin(c0b, lv).Y + c0b.ActualHeight / 2;
            W($"  pixel x=100 (expect c6 BGR(192,0,144)) = {Pixel(shot1, 100, y2)}");
            W($"  pixel x=300 (expect c7 BGR(192,192,0)) = {Pixel(shot1, 300, y2)}");
            W($"  pixel x=700 (expect c9 BGR(64,64,64))  = {Pixel(shot1, 700, y2)}");
            var hostShot = await CaptureAsync(Host);
            double hostY = Origin(c0b, Host).Y + c0b.ActualHeight / 2;
            W($"  Host x=750 (inside the ListView) = {Pixel(hostShot, 750, hostY)}");
            W($"  Host x=850 (outside the 800 DIP ListView) = {Pixel(hostShot, 850, hostY)}");
        }
        if (sv is not null)
            W($"  ScrollViewer after the offset change: HOffset={sv.HorizontalOffset:0.##} " +
              $"ExtentWidth={sv.ExtentWidth:0.##} ScrollableWidth={sv.ScrollableWidth:0.##}");

        // ---- containers realized WHILE the offset is non-zero ---------------
        W("");
        W("  --- containers realized while the offset is already 1200 ---");
        var known = new HashSet<RowPanel>(panels2);
        int before = lv.ItemsPanelRoot?.Children.Count ?? -1;
        sv?.ChangeView(null, 100000, null, true);
        await Settle(700);
        var panels3 = new List<RowPanel>();
        if (lv.ItemsPanelRoot is not null) FindAll(lv.ItemsPanelRoot, panels3);
        int fresh = panels3.Count(p => !known.Contains(p));
        W($"  after ChangeView to the end: VerticalOffset={sv?.VerticalOffset:0.##}, realized containers={lv.ItemsPanelRoot?.Children.Count ?? -1} (was {before})");
        W($"  RowPanel objects now live={panels3.Count}, of which NOT seen before the scroll = {fresh}" +
          $" (so {panels3.Count - fresh} were recycled)");
        var anyContainer = panels3.Count > 0 ? panels3[0] : null;
        if (anyContainer is not null)
        {
            var owner = FindAncestor<ListViewItem>(anyContainer);
            W($"  first live RowPanel after the scroll belongs to item index {(owner is null ? -1 : lv.IndexFromContainer(owner))}");
            W($"    its ArrangeOverride rects: {string.Join(" ", anyContainer.LastArrangeRects.Take(4).Select(r => $"[{r.X:0.#}]"))}" +
              $"   (offset 1200 means cell 0 at x=-1200)");
            if (anyContainer.Children.Count > 0)
                W($"    live position of cell 0 inside the ListView: x={Origin(anyContainer.Children[0], lv).X:0.##}");
            if (anyContainer.Children.Count > 6)
                W($"    live position of cell 6 inside the ListView: x={Origin(anyContainer.Children[6], lv).X:0.##}  (expected 0 at offset 1200)");
        }
        var shotEnd = await CaptureAsync(lv);
        W($"  pixel at ListView (100, 200) after the scroll = {Pixel(shotEnd, 100, 200)}  (c6 BGR(192,0,144) means the recycled rows carry the offset)");

        // ---- ScrollIntoView -------------------------------------------------
        W("");
        W("  --- ScrollIntoView and the horizontal offset ---");
        RowPanel.SharedOffset = 1200;
        sv?.ChangeView(null, 0, null, true);
        await Settle(500);
        var items = (List<Item>)lv.ItemsSource;
        if (sv is not null)
            W($"  before: V={sv.VerticalOffset:0.##} H={sv.HorizontalOffset:0.##} ExtentW={sv.ExtentWidth:0.##} ScrollableW={sv.ScrollableWidth:0.##}");
        lv.ScrollIntoView(items[7500]);
        await Settle(700);
        if (sv is not null)
            W($"  after 1st ScrollIntoView(7500): V={sv.VerticalOffset:0.##} H={sv.HorizontalOffset:0.##} " +
              $"ExtentW={sv.ExtentWidth:0.##} ScrollableW={sv.ScrollableWidth:0.##} ViewportH={sv.ViewportHeight:0.##}");
        var t1 = lv.ContainerFromItem(items[7500]) as ListViewItem;
        W($"  container for 7500 realized={t1 is not null}" + (t1 is null ? "" : $", y-in-ListView={Origin(t1, lv).Y:0.##}, height={t1.ActualHeight:0.##}"));
        lv.ScrollIntoView(items[7500]);
        await Settle(600);
        if (sv is not null)
            W($"  after 2nd ScrollIntoView(7500): V={sv.VerticalOffset:0.##} H={sv.HorizontalOffset:0.##}");
        var t2 = lv.ContainerFromItem(items[7500]) as ListViewItem;
        W($"  container for 7500 realized={t2 is not null}" + (t2 is null ? "" : $", y-in-ListView={Origin(t2, lv).Y:0.##}"));
        if (t2 is not null)
        {
            var rp = FindDescendant<RowPanel>(t2);
            if (rp is not null && rp.Children.Count > 6)
                W($"  that row's cell 6 x-in-ListView = {Origin(rp.Children[6], lv).X:0.##} (expected 0 at offset 1200)");
        }
        W($"  realized containers after ScrollIntoView = {lv.ItemsPanelRoot?.Children.Count ?? -1}");
    }

    private void DumpCells(RowPanel row, ListView lv, string label)
    {
        var parts = new List<string>();
        for (int i = 0; i < row.LastArrangeRects.Count; i++)
        {
            var r = row.LastArrangeRects[i];
            var child = row.Children[i] as FrameworkElement;
            string live = child is null ? "-" : $"{Origin(child, lv).X:0.##}";
            parts.Add($"c{i} arranged@{r.X:0.#} live@{live}");
        }
        W($"    cells ({label}): {string.Join("  ", parts)}");
    }

    private string Pixel(Shot s, double dipX, double dipY)
    {
        if (!s.Ok) return "capture-failed";
        int x = (int)Math.Round(dipX * _scale), y = (int)Math.Round(dipY * _scale);
        if (x < 0 || y < 0 || x >= s.W || y >= s.H) return "out of range";
        int i = (y * s.W + x) * 4;
        return $"BGR({s.Px[i]},{s.Px[i + 1]},{s.Px[i + 2]})";
    }
}

// ====================================================================== Q10 types

/// <summary>ListViewItem whose automation peer we supply.</summary>
public partial class PeerItem : ListViewItem
{
    public static int PeerCreated;

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        PeerCreated++;
        return new ItemPeer(this);
    }
}

/// <summary>Container peer that claims the SelectionItem pattern the stock container peer refuses.</summary>
public partial class ItemPeer : ListViewItemAutomationPeer, ISelectionItemProvider
{
    public static int Created;
    public static int PatternCoreCalls;

    private readonly ListViewItem _owner;

    public ItemPeer(ListViewItem owner) : base(owner)
    {
        _owner = owner;
        Created++;
    }

    protected override object GetPatternCore(PatternInterface patternInterface)
    {
        PatternCoreCalls++;
        if (patternInterface == PatternInterface.SelectionItem) return this;
        return base.GetPatternCore(patternInterface);
    }

    protected override string GetClassNameCore() => "TableViewRow";

    public bool IsSelected => _owner.IsSelected;

    public IRawElementProviderSimple? SelectionContainer
    {
        get
        {
            var lv = Ancestor(_owner);
            if (lv is null) return null;
            var peer = FrameworkElementAutomationPeer.FromElement(lv);
            return peer is null ? null : ProviderFromPeer(peer);
        }
    }

    public void AddToSelection() { _owner.IsSelected = true; }
    public void RemoveFromSelection() { _owner.IsSelected = false; }
    public void Select() { _owner.IsSelected = true; }

    private static ListView? Ancestor(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is ListView lv) return lv;
        return null;
    }
}

public partial class ListPeer : ListViewAutomationPeer
{
    public static int ChildrenCoreCalls;

    public ListPeer(ListView owner) : base(owner) { }

    protected override IList<AutomationPeer> GetChildrenCore()
    {
        ChildrenCoreCalls++;
        var lv = (ListView)Owner;
        var list = new List<AutomationPeer>();
        for (int i = 0; i < lv.Items.Count; i++)
        {
            if (lv.ContainerFromIndex(i) is UIElement c)
            {
                var p = FrameworkElementAutomationPeer.FromElement(c) ??
                        FrameworkElementAutomationPeer.CreatePeerForElement(c);
                if (p is not null) list.Add(p);
            }
        }
        return list;
    }
}

/// <summary>ListView that supplies both its own containers and its own peer.</summary>
public partial class PeerList : ListView
{
    public static int PeerCreated;

    protected override DependencyObject GetContainerForItemOverride() => new PeerItem();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is PeerItem;

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        PeerCreated++;
        return new ListPeer(this);
    }
}
