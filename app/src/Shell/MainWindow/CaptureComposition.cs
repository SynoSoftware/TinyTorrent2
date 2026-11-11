using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Shared;
using Syno.TinyTorrent.Torrents;
using Windows.Foundation;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private async Task CaptureMapComposition(List<object> outcomes)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        static T Read<T>(object owner, string name) => (T)owner.GetType().GetField(name, flags)!.GetValue(owner)!;
        static async Task Until(Func<bool> ready)
        {
            var clock = Stopwatch.StartNew();
            while (!ready())
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(10))
                    throw new TimeoutException("The map composition fixture did not present.");
                await Task.Delay(16);
            }
        }
        var surface = new Grid { Width = 256, Height = 128 };
        Grid.SetRow(surface, 1);
        var motion = new MapMotion(DispatcherQueue, []);
        CanvasDevice? device = null;
        var dpi = 96f;
        motion.SurfaceChanged += (current, density) => { device = current; dpi = density; };
        surface.Children.Add(motion.Control);
        Root.Children.Add(surface);
        try
        {
            await Until(() => device is not null);
            var view = new MapView(new Size(256, 128), 0, Vector2.Zero, Vector2.One, Matrix3x2.Identity);
            using var picture = new CanvasCommandList(device!);
            using (var draw = picture.CreateDrawingSession())
            {
                draw.Clear(Windows.UI.Color.FromArgb(25, 32, 32, 32));
                draw.FillRectangle(100, 20, 10, 10, Microsoft.UI.Colors.Lime);
            }
            using var expected = new CanvasRenderTarget(device!, 256, 128, dpi);
            using (var draw = expected.CreateDrawingSession())
            {
                draw.Clear(Microsoft.UI.Colors.Transparent);
                draw.DrawImage(picture);
            }
            var wanted = expected.GetPixelBytes();
            var reused = new HashSet<object>();
            for (var frame = 0; frame < 8; frame++)
            {
                var taken = motion.Pictures;
                await motion.CaptureDraw(view, Microsoft.UI.Colors.Transparent, draw =>
                {
                    if (frame < 2)
                    {
                        draw.Clear(Microsoft.UI.Colors.Transparent);
                        draw.FillRectangle(20, 20, 10, 10, Microsoft.UI.Colors.Red);
                    }
                    else
                        draw.DrawImage(picture);
                    return true;
                });
                await Until(() => motion.Pictures > taken && motion.Paused);
                var shown = Read<object>(motion, "_shown");
                var bitmap = (CanvasRenderTarget)shown.GetType().GetProperty("Bitmap", flags)!.GetValue(shown)!;
                reused.Add(bitmap);
                if (frame < 2)
                    continue;
                var actual = bitmap.GetPixelBytes();
                if (!actual.SequenceEqual(wanted))
                {
                    outcomes.Add(new { journey = "map picture replacement", frame, buffers = reused.Count,
                        differences = actual.Zip(wanted).Count(pair => pair.First != pair.Second) });
                    throw new InvalidOperationException("A reused map picture retained old marks or accumulated background opacity.");
                }
            }
            outcomes.Add(new { journey = "map picture replacement", frames = 8, buffers = reused.Count,
                oldMarks = 0, backgroundAccumulation = false });
            var point = new GeoPoint(100, -40);
            var destination = new MapModel.Destination(
                [new MapModel.Placement(new MapModel.Group(new PeerPlace("XX", point, false), () => "Fixture", point), point, 1, null)],
                Model.Text);
            var inspection = new MapRenderer.Inspection(view,
                [new(destination, new MapRenderer.Extent(new Vector2(50, 40), new Vector2(100, 40)),
                    [new Vector2(50, 40), new Vector2(100, 40)])]);
            var navigated = view with { AxisScale = new Vector2(2, 1.5f),
                Transform = Matrix3x2.CreateScale(2, 1.5f) * Matrix3x2.CreateTranslation(-30, 0) };
            if (inspection.HitTest(new Vector2(170, 60), navigated) != destination
                || inspection.HitTest(new Vector2(100, 62), navigated) != destination
                || inspection.HitTest(new Vector2(100, 70), navigated) is not null)
                throw new InvalidOperationException("Map inspection did not follow navigation or retain its screen-space target width.");
            outcomes.Add(new { journey = "map inspection", marker = true, arc = true, screenSpaceWidth = true });
        }
        finally
        {
            motion.Dispose();
            Root.Children.Remove(surface);
            await motion.Released.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var model = new MapModel(Model.Inspector);
        var world = await Geography.Load();
        string[] codes = ["US", "BR", "GB", "FR", "DE", "RU", "IN", "CN", "JP", "AU", "ZA", "SG"];
        var peers = Enumerable.Range(0, 200).Select(index =>
        {
            var code = codes[index % codes.Length];
            var country = world[code];
            var point = country.Areas.Length == 0 ? country.Anchor : country.Areas[index / codes.Length % country.Areas.Length].Point;
            return new Peer(Model.Text, "map-composition", $"198.18.{index / 256}.{index % 256}:51413",
                new Location(code, new Place(code, null), null, point), ConnectedPeer(index));
        }).ToArray();
        var origin = new Location("GB", new Place("GB", null), null, new GeoPoint(0, 51.5));
        model.Update(CapturePopulation(model, peers), origin, Model.Text);
        var map = new PeerMap(model) { Width = 900, Height = 500 };
        Grid.SetRow(map, 1);
        Root.Children.Add(map);
        try
        {
            await Until(() => CaptureRenderer(map) is not null);
            await map.Ready.WaitAsync(TimeSpan.FromSeconds(15));
            var viewport = Read<MapViewport>(map, "_viewport");
            var layer = Read<MapMotion>(map, "_motion");
            await Until(() => layer.DrawnView is not null);
            await Until(() => !Read<bool>(CaptureRenderer(map)!, "_interpolating"));
            await layer.CaptureBarrier().WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => CaptureMapIdle(map)
                && ReferenceEquals(Read<object>(layer, "_shown"), Read<object>(layer, "_completed")));
            var previousTraffic = Read<MapMotion.Scene>(layer, "_scene").Traffic;
            var previousPicture = Read<object>(layer, "_shown");
            var previousBitmap = previousPicture.GetType().GetProperty("Bitmap", flags)!.GetValue(previousPicture);
            CaptureRates(peers[1], peers[1].DownloadRate + .0001);
            model.Update(CapturePopulation(model, peers), origin, Model.Text);
            await Until(() => !ReferenceEquals(Read<MapMotion.Scene>(layer, "_scene").Traffic, previousTraffic));
            var updatedPicture = Read<object>(layer, "_shown");
            if (!ReferenceEquals(previousBitmap, updatedPicture.GetType().GetProperty("Bitmap", flags)!.GetValue(updatedPicture)))
                throw new InvalidOperationException("A sub-colour-level rate update rasterized an unchanged map.");
            outcomes.Add(new { journey = "unchanged map traffic handoff", reusedBitmap = true, updatedTraffic = true });
            var drawing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var releaseDrawing = new ManualResetEventSlim())
            {
                var blocked = 0;
                var cancelled = false;
                var published = new System.Collections.Concurrent.ConcurrentQueue<MapView>();
                layer.PicturePublished = view => published.Enqueue(view);
                layer.DrawingStarted = cancellation =>
                {
                    if (DispatcherQueue.HasThreadAccess)
                        throw new InvalidOperationException("Native map rendering ran on the UI thread.");
                    if (Interlocked.Exchange(ref blocked, 1) != 0)
                        return;
                    drawing.TrySetResult();
                    if (!releaseDrawing.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("The native drawing fixture did not release its request.");
                    cancelled = cancellation.IsCancellationRequested;
                };
                try
                {
                    var original = viewport.View;
                    var traffic = CaptureRenderer(map)!.Scene?.Traffic;
                    await Until(() => layer.Frames > 0);
                    var beforeFrames = layer.Frames;
                    viewport.SetView(2, new Vector2(-30, -10), 1);
                    var obsolete = viewport.View;
                    await drawing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Until(() => layer.DrawnView == obsolete && layer.Frames > beforeFrames);
                    viewport.SetView(original.AxisScale.Y / viewport.Scale, original.Center,
                        original.AxisScale.X / original.AxisScale.Y);
                    await Until(() => layer.DrawnView == viewport.View);
                    releaseDrawing.Set();
                    await Until(() => published.Contains(viewport.View) && layer.DrawnView == viewport.View);
                    if (cancelled || !ReferenceEquals(traffic, CaptureRenderer(map)!.Scene?.Traffic))
                        throw new InvalidOperationException("Navigation cancelled compatible drawing or restarted unchanged traffic.");
                    outcomes.Add(new { journey = "presentation during blocked rendering", navigation = true,
                        dotsAdvanced = true, uiThread = false, unchangedTraffic = true });
                }
                finally
                {
                    releaseDrawing.Set();
                    layer.DrawingStarted = null;
                    layer.PicturePublished = null;
                }
            }
            var preparingRoutes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var renderer = CaptureRenderer(map)!;
            using (var releaseRoutes = new ManualResetEventSlim())
            {
                var blocked = 0;
                var cancelled = false;
                var obsoleteRoutes = 0;
                CancellationToken obsolete = default;
                renderer.RoutePrepared = cancellation =>
                {
                    if (DispatcherQueue.HasThreadAccess)
                        throw new InvalidOperationException("Native route preparation ran on the UI thread.");
                    if (Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
                    {
                        obsolete = cancellation;
                        preparingRoutes.TrySetResult();
                        if (!releaseRoutes.Wait(TimeSpan.FromSeconds(5)))
                            throw new TimeoutException("The native route fixture did not release preparation.");
                        cancelled = cancellation.IsCancellationRequested;
                    }
                    if (cancellation == obsolete)
                        obsoleteRoutes++;
                };
                try
                {
                    var moved = new Location("GB", new Place("GB", null), null, new GeoPoint(0, -20));
                    model.Update(CapturePopulation(model, peers), moved, Model.Text);
                    await preparingRoutes.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    model.Update(CapturePopulation(model, peers), origin, Model.Text);
                    await Until(() => obsolete.IsCancellationRequested);
                    releaseRoutes.Set();
                    await map.Ready.WaitAsync(TimeSpan.FromSeconds(15));
                    if (!cancelled || obsoleteRoutes != 1 || renderer.Scene?.Origin != origin.Point)
                        throw new InvalidOperationException("Obsolete route preparation continued or the restored origin was not rendered.");
                    outcomes.Add(new { journey = "native route preparation cancellation", obsoleteRoutes, restoredOrigin = true });
                }
                finally
                {
                    releaseRoutes.Set();
                    renderer.RoutePrepared = null;
                }
            }
            var worker = Read<MapWork>(map, "_work");
            var changingDetail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var releaseDetail = new ManualResetEventSlim())
            {
                var blocked = 0;
                worker.Started = () =>
                {
                    if (Interlocked.Exchange(ref blocked, 1) != 0)
                        return;
                    changingDetail.TrySetResult();
                    if (!releaseDetail.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("The map detail fixture did not release grouping.");
                };
                try
                {
                    model.Update(CapturePopulation(model, peers), origin, Model.Text, cities: false);
                    await changingDetail.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var pending = Read<(MapWork.Input Input, CancellationTokenSource Cancellation)>(map, "_preparing");
                    model.Update(CapturePopulation(model, peers), origin, Model.Text, cities: false);
                    if (pending.Cancellation.IsCancellationRequested)
                        throw new InvalidOperationException("Compatible telemetry cancelled the replacement detail's grouping.");
                    releaseDetail.Set();
                    await map.Ready.WaitAsync(TimeSpan.FromSeconds(15));
                    if (Read<MapModel.Presentation>(map, "_presentation").Cities)
                        throw new InvalidOperationException("The replacement map detail did not complete.");
                    outcomes.Add(new { journey = "telemetry during replacement grouping", completed = true, cancelled = false });
                }
                finally
                {
                    releaseDetail.Set();
                    worker.Started = null;
                }
            }
            model.Update(CapturePopulation(model, peers), origin, Model.Text, cities: true);
            await map.Ready.WaitAsync(TimeSpan.FromSeconds(15));
            var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var release = new ManualResetEventSlim())
            {
                worker.Started = () =>
                {
                    preparing.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("The navigation fixture did not release preparation.");
                };
                try
                {
                    model.Update(CapturePopulation(model, peers), origin, Model.Text);
                    await preparing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var destination = model.Destinations.First(destination =>
                        destination.Members.Any(member => member.Point == peers[2].Location!.Point));
                    map.Select([peers[2]]);
                    var marked = false;
                    var selectionClock = Stopwatch.StartNew();
                    while (!marked && selectionClock.Elapsed < TimeSpan.FromSeconds(2))
                    {
                        await layer.Control.RunOnGameLoopThreadAsync(() =>
                        {
                            marked = layer.Display?.Inspection.Highlight.IsMarked(destination) == true;
                        });
                        if (!marked)
                            await Task.Delay(16);
                    }
                    if (!marked)
                        throw new InvalidOperationException("Table peer selection waited for aggregation before highlighting its map destination.");
                    outcomes.Add(new { journey = "table peer selection during blocked preparation", highlighted = true });
                    var beforeNavigation = layer.Pictures;
                    viewport.SetView(16, new Vector2(-60, -20), 1);
                    var deadline = Stopwatch.StartNew();
                    while (layer.Pictures == beforeNavigation && deadline.Elapsed < TimeSpan.FromSeconds(2))
                        await Task.Delay(16);
                    if (layer.Pictures == beforeNavigation)
                        throw new InvalidOperationException("Navigation waited for peer preparation instead of drawing the current view.");
                    outcomes.Add(new { journey = "navigation during blocked peer preparation", drewNewPicture = true });
                }
                finally
                {
                    release.Set();
                    worker.Started = null;
                }
            }
            if (ReviewMode == CaptureMode.MapHandoff)
            {
                viewport.Fit();
                await map.Ready.WaitAsync(TimeSpan.FromSeconds(15));
                var fitted = viewport.View;
                var drewFittedPicture = false;
                var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void RecordDrawing(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
                {
                    var shown = Read<object>(layer, "_shown");
                    drewFittedPicture = (MapView)shown.GetType().GetProperty("View", flags)!.GetValue(shown)! == fitted;
                }
                // Update follows presentation; DrawnView alone can describe
                // an older raster's navigation preview.
                void RecordPresentation(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
                {
                    if (drewFittedPicture)
                        presented.TrySetResult();
                }
                layer.Control.Draw += RecordDrawing;
                layer.Control.Update += RecordPresentation;
                layer.Wake();
                try
                {
                    await presented.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    layer.Control.Draw -= RecordDrawing;
                    layer.Control.Update -= RecordPresentation;
                }
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
                using var recording = new MapRecording(Read<CanvasDevice>(map, "_device"), handle);
                await recording.Save(Path.Combine(_captureDirectory!, "map-handoff.png"));
                return;
            }
            viewport.Fit();
            await map.Ready.WaitAsync(TimeSpan.FromSeconds(15));
            var mapDevice = Read<CanvasDevice>(map, "_device");
            var directory = Path.Combine(_captureDirectory!, "navigation");
            Directory.CreateDirectory(directory);
            var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
            using var writer = new MapRecording(mapDevice, window);
            var started = Stopwatch.StartNew();
            var lastPicture = Stopwatch.GetTimestamp();
            var publications = new System.Collections.Concurrent.ConcurrentQueue<long>();
            layer.PicturePublished = _ => publications.Enqueue(Stopwatch.GetTimestamp());
            List<double> renderTimes = [];
            List<object> frames = [];
            var phases = new Dictionary<string, List<double>>();
            map.WorkMeasured = (phase, ms) =>
            {
                if (!phases.TryGetValue(phase, out var samples))
                    phases[phase] = samples = [];
                samples.Add(ms);
            };
            map.FrameMeasured = ms =>
            {
                renderTimes.Add(ms);
                frames.Add(new { ms, phases = phases.ToDictionary(pair => pair.Key, pair => pair.Value.Sum()) });
                phases.Clear();
            };
            List<double> delays = [];
            List<double> times = [];
            var requestedAt = new Dictionary<MapView, long>();
            var drawn = new HashSet<MapView>();
            var images = 0;
            var pictures = layer.Pictures;
            List<MapView> views = [];
            for (var frame = 0; frame < 96; frame++)
            {
                if (layer.DrawnView is { } shownView && drawn.Add(shownView)
                    && requestedAt.TryGetValue(shownView, out var requestedAtTime))
                    delays.Add(Stopwatch.GetElapsedTime(requestedAtTime).TotalMilliseconds);
                var zoom = frame < 16 ? MathF.Pow(1.25f, frame) : frame < 32 ? MathF.Pow(1.25f, 31 - frame)
                    : frame < 72 ? 8 : Math.Max(1, 8 - (frame - 72) / 3f);
                var center = new Vector2(frame < 32 ? -60 : -60 + (frame - 32) * 3,
                    frame < 32 ? -20 : -35 * MathF.Sin((frame - 32) / 64f * MathF.PI));
                viewport.SetView(zoom, center, viewport.CalculateRatio(zoom, center.Y));
                requestedAt[viewport.View] = Stopwatch.GetTimestamp();
                if (frame % 3 == 0)
                    views.Add(viewport.View);
                await Task.Delay(16);
            }
            var navigationMs = started.Elapsed.TotalMilliseconds;
            layer.PicturePublished = null;
            List<double> pictureGaps = [];
            foreach (var publishedAt in publications)
            {
                pictureGaps.Add(Stopwatch.GetElapsedTime(lastPicture, publishedAt).TotalMilliseconds);
                lastPicture = publishedAt;
            }
            map.FrameMeasured = null;
            map.WorkMeasured = null;
            var navigationPictures = layer.Pictures - pictures;
            var visual = Stopwatch.StartNew();
            foreach (var view in views)
            {
                viewport.SetView(view.AxisScale.Y / viewport.Scale, view.Center, view.AxisScale.X / view.AxisScale.Y);
                await Until(() => layer.DrawnView == viewport.View);
                await writer.Save(Path.Combine(directory, $"{images:D3}.png"));
                times.Add(visual.Elapsed.TotalSeconds);
                images++;
            }
            viewport.Fit();
            await map.Ready.WaitAsync(TimeSpan.FromSeconds(15));
            await Until(() => layer.DrawnView == viewport.View);
            await writer.Save(Path.Combine(_captureDirectory!, "map-acrylic.png"));
            var transparent = false;
            await layer.Control.RunOnGameLoopThreadAsync(() =>
            {
                var shown = Read<object>(layer, "_shown");
                var mapBitmap = (CanvasRenderTarget)shown.GetType().GetProperty("Bitmap", flags)!.GetValue(shown)!;
                transparent = mapBitmap.GetPixelBytes()[3] == 0;
            });
            if (!transparent)
                throw new InvalidOperationException("The map picture's empty margin covered the acrylic backdrop.");
            var ordered = delays.Order().ToArray();
            if (ordered.Length == 0)
                throw new InvalidOperationException("The map did not present requested views during continuous navigation.");
            outcomes.Add(new { journey = "map live navigation", requests = 96, drawn = drawn.Count, images, times,
                elapsedMs = navigationMs, pictures = navigationPictures, renderTimes, frames,
                pictureGaps,
                medianMs = ordered[ordered.Length / 2], p95Ms = ordered[(int)(ordered.Length * .95)], maxMs = ordered[^1],
                backgroundAlpha = Read<Windows.UI.Color?>(map, "_underlay")?.A, transparentMargin = true, directory });
        }
        finally
        {
            var layer = Read<MapMotion?>(map, "_motion");
            Root.Children.Remove(map);
            if (layer is not null)
                await layer.Released.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class MapRecording : IDisposable
    {
        private readonly CanvasDevice _device;
        private readonly nint _window;
        private readonly nint _screen;
        private readonly nint _memory;
        private readonly nint _bitmap;
        private readonly nint _previous;
        private readonly nint _bits;
        private readonly int _width;
        private readonly int _height;

        internal MapRecording(CanvasDevice device, nint window)
        {
            _device = device;
            _window = window;
            GetWindowRect(window, out var bounds);
            _width = bounds.Right - bounds.Left;
            _height = bounds.Bottom - bounds.Top;
            _screen = GetDC(window);
            _memory = CreateCompatibleDC(_screen);
            var header = new BitmapHeader { Size = 40, Width = _width, Height = -_height, Planes = 1, Bits = 32 };
            _bitmap = CreateDIBSection(_screen, ref header, 0, out _bits, 0, 0);
            if (_bitmap == 0 || _bits == 0)
            {
                DeleteDC(_memory);
                ReleaseDC(window, _screen);
                throw new InvalidOperationException("The map recording surface could not be created.");
            }
            _previous = SelectObject(_memory, _bitmap);
        }

        internal async Task Save(string path)
        {
            if (!PrintWindow(_window, _memory, 2))
                throw new InvalidOperationException("The full-content map recording failed.");
            var pixels = new byte[_width * _height * 4];
            System.Runtime.InteropServices.Marshal.Copy(_bits, pixels, 0, pixels.Length);
            for (var index = 3; index < pixels.Length; index += 4)
                pixels[index] = 255;
            using var bitmap = CanvasBitmap.CreateFromBytes(_device, pixels, _width, _height,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
            await bitmap.SaveAsync(path, CanvasBitmapFileFormat.Png);
        }

        public void Dispose()
        {
            SelectObject(_memory, _previous);
            DeleteObject(_bitmap);
            DeleteDC(_memory);
            ReleaseDC(_window, _screen);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Bounds { internal int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct BitmapHeader
        {
            internal uint Size;
            internal int Width, Height;
            internal ushort Planes, Bits;
            internal uint Compression, ImageSize;
            internal int XResolution, YResolution;
            internal uint Colours, Important;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Bounds bounds);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetDC(nint window);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapHeader header, uint usage, out nint bits, nint section, uint offset);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    }
}
