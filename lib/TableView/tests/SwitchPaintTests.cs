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

/// <summary>A peer row whose first cell is one solid color: its role in the change.</summary>
public sealed class PaintRow
{
    internal PaintRow(int id) => Id = id;

    public int Id { get; }
    public SolidColorBrush Brush { get; internal set; } = new(Microsoft.UI.Colors.Gray);
}

/// <summary>
/// Records how a 200-row table paints a peer switch, frame by frame as the compositor presents
/// the window, so the list's composition animations are included. It asserts no result; it
/// reports blank frames, the frames before the rows settle, and any whole-list Reset.
/// </summary>
[TestClass]
public class SwitchPaintTests
{
    private static readonly Windows.UI.Color LeavingColor = new() { A = 255, R = 220, G = 40, B = 30 };
    private static readonly Windows.UI.Color StayingColor = new() { A = 255, R = 30, G = 60, B = 220 };
    private static readonly Windows.UI.Color ArrivingColor = new() { A = 255, R = 30, G = 170, B = 40 };

    public TestContext TestContext { get; set; } = null!;

    private sealed record Scenario(string Name, Func<int, bool> Before, Func<int, bool> After, int Selected = -1);

    private static readonly Scenario[] Scenarios =
    {
        // A poll: every fourth peer leaves, the selected one among them, and the rest slide up.
        new("churn-selected-leaves", id => id < 200, id => id < 200 && id % 4 != 3, Selected: 3),
        // Another torrent's peers: no row stays.
        new("switch", id => id < 200, id => id >= 1000 && id < 1200),
        new("switch-selected-leaves", id => id < 200, id => id >= 1000 && id < 1200, Selected: 3),
    };

    [TestMethod]
    [TestCategory("Measurement")]
    public Task PeerSwitchPaint() =>
        TestHost.RunAsync(async () =>
        {
            Window window = (Window)typeof(TestHost)
                .GetField("_window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .GetValue(null)!;
            double scale = TestHost.RootPanel.XamlRoot.RasterizationScale;
            window.AppWindow.ResizeClient(new Windows.Graphics.SizeInt32((int)(820 * scale), (int)(620 * scale)));
            await Task.Delay(500);
            using Recorder recorder = new(WinRT.Interop.WindowNative.GetWindowHandle(window));
            await Task.Delay(500);
            foreach (Scenario scenario in Scenarios)
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
        PaintRow[] rows = Enumerable.Range(0, 1200).Select(id => new PaintRow(id)).ToArray();
        List<PaintRow> before = rows.Where(row => scenario.Before(row.Id)).ToList();
        List<PaintRow> after = rows.Where(row => scenario.After(row.Id)).ToList();
        foreach (PaintRow row in rows)
        {
            bool was = scenario.Before(row.Id), stays = scenario.After(row.Id);
            row.Brush = new SolidColorBrush(was && stays ? StayingColor : was ? LeavingColor : ArrivingColor);
        }

        Table table = new() { Width = 800, Height = 560 };
        table.Columns.Add(new Column { Id = "role", DisplayName = "Role", Width = 120,
            CellTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>"
                + "<Border Background='{Binding Brush}' Width='100' Height='30'/></DataTemplate>") });
        foreach (string id in new[] { "b", "c", "d", "e", "f" })
        {
            table.Columns.Add(TestData.Column(id, 120));
        }
        table.Schema<PaintRow>().Key(row => row.Id);
        table.ItemsSource = before;
        Grid host = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.White), RequestedTheme = ElementTheme.Light };
        host.Children.Add(table);
        TaskCompletionSource loaded = new();
        table.Loaded += (_, _) => loaded.TrySetResult();
        TestHost.RootPanel.Children.Add(host);
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
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

        Dictionary<string, int> notices = new();
        IObservableVector<object> view = (IObservableVector<object>)surface.ItemsSource;
        void OnVector(IObservableVector<object> sender, IVectorChangedEventArgs e)
        {
            string kind = e.CollectionChange.ToString();
            notices[kind] = notices.GetValueOrDefault(kind) + 1;
        }
        view.VectorChanged += OnVector;

        recorder.Start();
        await Task.Delay(150);
        double start = Now();
        table.ItemsSource = after;
        await Task.Delay(1000);
        (Shot? pre, List<Shot> shots) = recorder.Stop(start);
        view.VectorChanged -= OnVector;
        TestHost.RootPanel.Children.Remove(host);
        await Task.Delay(200);

        return $"{scenario.Name}: {Describe(pre, shots, rowPixels)}; notices "
            + (notices.Count == 0 ? "none" : string.Join(", ", notices.Select(n => $"{n.Key} {n.Value}")));
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
        int blank = shots.Count(shot => shot.Background > baseline + rowPixels);
        int settled = shots.FindIndex(shot => shot.SameAs(last));
        List<Shot> changing = shots.Take(settled + 1).Where(shot => !shot.SameAs(pre)).ToList();
        string path = string.Join(" | ", shots.Take(Math.Min(shots.Count, settled + 2))
            .Select(shot => $"+{shot.At:0}ms s{shot.Staying} l{shot.Leaving} a{shot.Arriving} bg{shot.Background} o{shot.Other}"));
        string colours = pre.Staying + pre.Leaving + pre.Arriving > 0 ? ""
            : $"no role colour in the column, which shows {pre.Commonest}; ";
        return colours + $"before s{pre.Staying} l{pre.Leaving} a{pre.Arriving} bg{pre.Background}; "
            + $"frames {shots.Count}, changing frames until settled {changing.Count}, blank frames {blank}, "
            + $"most background {(shots.Max(shot => shot.Background) - baseline) / (double)rowPixels:0.0} rows over baseline; "
            + $"row {rowPixels}px; path {path}";
    }

    private sealed class Shot
    {
        public double At;
        public int Staying, Leaving, Arriving, Background, Other;
        public string Commonest = "";

        public bool SameAs(Shot other) =>
            Math.Abs(Staying - other.Staying) + Math.Abs(Leaving - other.Leaving)
            + Math.Abs(Arriving - other.Arriving) + Math.Abs(Background - other.Background) <= 4;

        public static Shot Classify(byte[] column, double at)
        {
            Shot shot = new() { At = at };
            for (int i = 0; i + 3 < column.Length; i += 4)
            {
                int b = column[i], g = column[i + 1], r = column[i + 2];
                if (Near(StayingColor))
                {
                    shot.Staying++;
                }
                else if (Near(LeavingColor))
                {
                    shot.Leaving++;
                }
                else if (Near(ArrivingColor))
                {
                    shot.Arriving++;
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
