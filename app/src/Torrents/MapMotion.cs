using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Syno.TinyTorrent.Shared;
using Windows.Foundation;
using Windows.UI;
using static Syno.TinyTorrent.Torrents.MapModel;

namespace Syno.TinyTorrent.Torrents;

/// <summary>
/// The peer map on screen: the latest <see cref="Picture"/> of the map with
/// the moving dots over it, drawn on Win2D's game loop thread, so work on the
/// UI thread, such as the peers table filling, never stops the dots. The UI
/// thread draws each picture with <see cref="Draw"/> and hands over the dots'
/// <see cref="Scene"/> with <see cref="Show"/>; only the game loop reads the
/// dots.
/// </summary>
/// <remarks>
/// The map draws in this layer rather than on a canvas under it, because a
/// WinUI 3 swap chain covers what lies under it: through its transparent
/// pixels only the window's backdrop shows.
/// </remarks>
internal sealed class MapMotion : IDisposable
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(33);

    /// <summary>The smallest dot size, which the legend shows before the first traffic.</summary>
    internal const long SmallestDot = 1024;

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _wake;
    private readonly ConcurrentQueue<Picture> _free = new();
    private Scene? _scene;
    private Picture? _pending;
    private volatile bool _idle = true;
    private bool _disposed;
    private int _newSurface;
    // The device and DPI the UI thread draws pictures with, from the latest
    // surface it was told of.
    private CanvasDevice? _device;
    private float _dpi;

    // Game loop thread only, apart from the lost-device flag.
    private readonly MapDots<Lane> _dots = new(SmallestDot);
    private readonly Dictionary<DestinationKey, Lane> _lanes = [];
    private Picture? _shown;
    private Traffic? _applied;
    private CanvasRenderTarget? _mark;
    private volatile bool _markLost;
    private int _drawn;
    private (long? Bytes, double Total) _reported;
#if CAPTURE
    private int _frames;
    private int _pictures;
    private long _cycles;

    /// <summary>
    /// The CPU cycles the game loop thread had used at its latest update,
    /// which counts the previous frame's present.
    /// </summary>
    internal long Cycles => Volatile.Read(ref _cycles);

    /// <summary>The frames the game loop has drawn with dots in flight.</summary>
    internal int Frames => Volatile.Read(ref _frames);

    /// <summary>The frames the game loop has drawn with a new picture of the map.</summary>
    internal int Pictures => Volatile.Read(ref _pictures);

    /// <summary>Whether the game loop is paused, waiting for the next dot.</summary>
    internal bool Paused => Control.Paused;

    /// <summary>The scene and dots, read across threads for a capture report only.</summary>
    internal string State =>
        $"scene={_scene is not null} routes={_scene?.Traffic.Routes.Count}"
        + $" bytes={_dots.Bytes} total={_dots.Total} flying={_dots.Flying.Count} paused={Control.Paused}";
#endif

    internal MapMotion(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _wake = dispatcher.CreateTimer();
        _wake.IsRepeating = false;
        _wake.Tick += (_, _) => Wake();
        Control = new CanvasAnimatedControl
        {
            ClearColor = Microsoft.UI.Colors.Transparent,
            IsHitTestVisible = false,
            // A new picture shows at the next display refresh, so panning and
            // zooming keep the display's rate.
            IsFixedTimeStep = false,
            Opacity = 0,
        };
        // Raised for each new device or DPI, which makes a new surface.
        Control.CreateResources += (_, _) =>
        {
            _markLost = true;
            _dispatcher.TryEnqueue(NewSurface);
        };
        Control.Update += OnUpdate;
        Control.Draw += OnDraw;
    }

    /// <summary>The control that shows the map, to place where the map goes.</summary>
    internal CanvasAnimatedControl Control { get; }

    /// <summary>
    /// Raised on the UI thread with the device and DPI that pictures are drawn
    /// with from now on: the first, and each new one after a lost device or a
    /// DPI change. What was made for the previous ones must be made again.
    /// </summary>
    internal event Action<CanvasDevice, float>? SurfaceChanged;

    /// <summary>
    /// Raised on the UI thread with the bytes one dot stands for, null before
    /// the first traffic, and the total rate of the routes that carry dots.
    /// </summary>
    internal event Action<long?, double>? Measured;

    /// <summary>
    /// Draws a new picture of the map in <paramref name="view"/> on the UI
    /// thread and shows it from the next frame, with the dots placed in that
    /// view. <paramref name="draw"/> returns whether it drew; a picture it did
    /// not draw is not shown. Nothing draws before the first
    /// <see cref="SurfaceChanged"/>.
    /// </summary>
    internal void Draw(MapView view, Func<CanvasDrawingSession, bool> draw)
    {
        if (_device is not { } device)
            return;
        Picture? picture = null;
        try
        {
            picture = Lease(device, view.Size);
            bool drawn;
            using (var session = picture.Bitmap.CreateDrawingSession())
                drawn = draw(session);
            if (drawn)
                Present(picture, view);
            else
                _free.Enqueue(picture);
        }
        catch (Exception error) when (device.IsDeviceLost(error.HResult))
        {
            // The game loop's next frame finds the device lost too and makes a
            // new one, which SurfaceChanged reports.
            if (picture is not null)
                _free.Enqueue(picture);
            Wake();
        }
    }

    // One the game loop has finished with, or a new one.
    private Picture Lease(CanvasDevice device, Size size)
    {
        while (_free.TryDequeue(out var picture))
        {
            var bitmap = picture.Bitmap;
            if (ReferenceEquals(picture.Device, device) && bitmap.Dpi == _dpi && picture.Size == size)
                return picture;
            bitmap.Dispose();
        }
        return new Picture(device, size, new CanvasRenderTarget(device, (float)size.Width, (float)size.Height, _dpi));
    }

    private void Present(Picture picture, MapView view)
    {
        picture.View = view;
        // A picture the game loop never took is free again at once.
        if (Interlocked.Exchange(ref _pending, picture) is { } skipped)
            _free.Enqueue(skipped);
        Wake();
    }

    /// <summary>
    /// Hands the game loop the dots' scene, or null for no dots. Call on the UI
    /// thread whenever the traffic or the colours change.
    /// </summary>
    internal void Show(Scene? scene)
    {
        var previous = _scene;
        Volatile.Write(ref _scene, scene);
        // A new colour moves no dot, so a sleeping loop sleeps on.
        if (scene is not null && !ReferenceEquals(scene.Traffic, previous?.Traffic))
            Wake();
    }

    public void Dispose()
    {
        _disposed = true;
        _wake.Stop();
        Volatile.Write(ref _scene, null);
        Control.RemoveFromVisualTree();
    }

    // Until the game loop presents its first frame, a new surface can show
    // black, so the layer stays hidden until then.
    private void NewSurface()
    {
        if (_disposed)
            return;
        Control.Opacity = 0;
        Interlocked.Exchange(ref _newSurface, 1);
        Wake();
        _device = Control.Device;
        _dpi = Control.Dpi;
        SurfaceChanged?.Invoke(_device, _dpi);
    }

    private void Wake()
    {
        _wake.Stop();
        if (!_disposed)
            Control.Paused = false;
    }

    // While no dot flies and the picture is on screen the loop pauses until
    // the next dot leaves, so slow traffic wakes the app once per dot. A newer
    // traffic or picture woke it already.
    private void Sleep(Traffic? traffic, TimeSpan? wait, long since)
    {
        if (
            _disposed
            || !_idle
            || Volatile.Read(ref _pending) is not null
            || !ReferenceEquals(traffic, _scene?.Traffic)
        )
            return;
        Control.Paused = true;
        _wake.Stop();
        if (wait is not { } delay)
            return;
        var remaining = delay - Stopwatch.GetElapsedTime(since);
        _wake.Interval = remaining > TimeSpan.FromMilliseconds(1) ? remaining : TimeSpan.FromMilliseconds(1);
        _wake.Start();
    }

    private void OnUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
#if CAPTURE
        Volatile.Write(ref _cycles, (long)CaptureCycles.Current());
#endif
        // An update follows the previous frame's present, so a frame drawn on
        // the current surface is on screen, and the picture it drew can be
        // drawn into again.
        if (Interlocked.Exchange(ref _newSurface, 0) == 1)
            _drawn = 0;
        else if (_drawn == 1)
        {
            _drawn = 2;
            _dispatcher.TryEnqueue(() =>
            {
                if (!_disposed)
                    Control.Opacity = 1;
            });
        }
        if (Interlocked.Exchange(ref _pending, null) is { } picture)
        {
            if (_shown is { } previous)
                _free.Enqueue(previous);
            _shown = picture;
#if CAPTURE
            Interlocked.Increment(ref _pictures);
#endif
        }
        var now = Stopwatch.GetTimestamp();
        var scene = Volatile.Read(ref _scene);
        if (scene is null)
        {
            _dots.Clear();
            _applied = null;
        }
        else
        {
            if (!ReferenceEquals(scene.Traffic, _applied))
                Apply(scene.Traffic, now);
            _dots.Advance(now);
        }
        Report();
        _idle = _dots.Flying.Count == 0;
        if (!_idle || _drawn < 2)
            return;
        var wait = scene is null ? null : _dots.Wait();
        if (wait is null || wait > 2 * Frame)
            _dispatcher.TryEnqueue(() => Sleep(scene?.Traffic, wait, now));
    }

    private void Apply(Traffic traffic, long now)
    {
        var present = new HashSet<DestinationKey>();
        foreach (var route in traffic.Routes)
        {
            if (!_lanes.TryGetValue(route.Key, out var lane))
                _lanes[route.Key] = lane = new Lane();
            lane.Path = route.Path;
            lane.Peer = route.Peer;
            present.Add(route.Key);
        }
        foreach (var key in _lanes.Keys.Where(key => !present.Contains(key)).ToArray())
            _lanes.Remove(key);
        _dots.Update(
            traffic.Routes.Select(route =>
                new MapDots<Lane>.Traffic(_lanes[route.Key], route.Down, route.Up, route.Length)),
            traffic.Area,
            traffic.Ceiling,
            traffic.Speed,
            traffic.Total,
            now);
        _applied = traffic;
    }

    private void Report()
    {
        var measured = (_dots.Bytes, _dots.Total);
        if (measured == _reported)
            return;
        _reported = measured;
        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed)
                Measured?.Invoke(measured.Bytes, measured.Total);
        });
    }

    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        if (_drawn == 0)
            _drawn = 1;
        // A picture from a lost device waits for the one the new device draws.
        if (_shown is not { } picture || !ReferenceEquals(picture.Device, sender.Device))
            return;
        var draw = args.DrawingSession;
        draw.DrawImage(picture.Bitmap);
        if (Volatile.Read(ref _scene) is not { } scene || _dots.Flying.Count == 0)
            return;
#if CAPTURE
        Interlocked.Increment(ref _frames);
#endif
        var now = Stopwatch.GetTimestamp();
        var mark = Mark(sender);
        // Fades go into the tint and the angle into the transform, so every
        // dot stays in one batch.
        using var batch = draw.CreateSpriteBatch();
        foreach (var dot in _dots.Flying)
        {
            if (Place(scene, picture.View, dot, now) is not { } placed)
                continue;
            var tint = dot.Upload ? scene.Upload : scene.Download;
            batch.Draw(
                mark,
                Matrix3x2.CreateTranslation(-Head)
                    * Matrix3x2.CreateRotation(placed.Angle)
                    * Matrix3x2.CreateTranslation(placed.Point),
                new Vector4(tint.R, tint.G, tint.B, tint.A) / 255 * placed.Strength
            );
        }
    }

    /// <summary>
    /// Where a dot shows, the angle it moves at and its strength, or null when
    /// it is off screen or faded out.
    /// </summary>
    private static (Vector2 Point, float Angle, float Strength)? Place(
        Scene scene,
        MapView view,
        MapDots<Lane>.Dot dot,
        long now
    )
    {
        var progress = dot.Progress(now);
        if (progress >= 1)
            return null;
        var lane = dot.Route;
        // Upload dots move from the user's location toward the peer.
        var along = dot.Upload ? progress : 1 - progress;
        var here = lane.Path.At(along);
        var point = view.Screen(here);
        // The mark reaches its tail's length behind its head.
        if (
            point.X < -Head.X
            || point.Y < -Head.X
            || point.X > view.Size.Width + Head.X
            || point.Y > view.Size.Height + Head.X
        )
            return null;
        var home = Apart(view, here, scene.Origin);
        var peer = Apart(view, here, lane.Peer);
        var strength = Under(home) * Under(peer) * MapRenderer.Landing(peer)
            * MapRenderer.OriginFade(home / MapRenderer.OriginRadius, scene.OriginOpacity);
        if (strength <= 0)
            return null;
        var ahead = Math.Clamp(along + (dot.Upload ? .01 : -.01), 0, 1);
        var direction = view.Coordinates(lane.Path.At(ahead)) - view.Coordinates(here);
        direction.X = MapViewport.Wrap(direction.X, 0);
        direction *= view.AxisScale;
        return (point, MathF.Atan2(direction.Y, direction.X), (float)strength);
    }

    /// <summary>The distance in DIPs on screen between two places, the short way round.</summary>
    private static float Apart(MapView view, GeoPoint from, GeoPoint to)
    {
        var step = view.Coordinates(to) - view.Coordinates(from);
        step.X = MapViewport.Wrap(step.X, 0);
        return (step * view.AxisScale).Length();
    }

    /// <summary>
    /// A dot's strength at a screen distance in DIPs from a marker at one of
    /// its route's ends, so it seems to pass under the marker.
    /// </summary>
    private static double Under(double distance) => Math.Clamp((distance - 3.5) / 6.5, 0, 1);

    /// <summary>The centre of the dot's head in its mark.</summary>
    private static readonly Vector2 Head = new(9, 4);

    // A dot is this white mark pointing along +X: a 4.5-DIP round head with a
    // soft glow out to 7 DIPs across, and a tail that tapers and fades out 8
    // DIPs behind it. Dots of one direction on a route sit at least 15 DIPs
    // apart, so a longer tail would join them into a line. The mark is made
    // again on a new device or DPI.
    private CanvasRenderTarget Mark(ICanvasAnimatedControl canvas)
    {
        if (_mark is not null && !_markLost && _mark.Dpi == canvas.Dpi)
            return _mark;
        _markLost = false;
        _mark?.Dispose();
        var white = Microsoft.UI.Colors.White;
        _mark = new CanvasRenderTarget(canvas, 13, 8, canvas.Dpi);
        using var session = _mark.CreateDrawingSession();
        session.Clear(Microsoft.UI.Colors.Transparent);
        using var tail = new CanvasLinearGradientBrush(canvas, MapRenderer.Alpha(white, .6), MapRenderer.Alpha(white, 0))
        {
            StartPoint = Head,
            EndPoint = Head with { X = Head.X - 8 },
        };
        using var shape = CanvasGeometry.CreatePolygon(
            canvas,
            [
                Head with { Y = Head.Y - 1.2f },
                Head with { Y = Head.Y + 1.2f },
                Head with { X = Head.X - 8 },
            ]
        );
        session.FillGeometry(shape, tail);
        using var head = new CanvasRadialGradientBrush(
            canvas,
            [
                new CanvasGradientStop { Position = 0, Color = white },
                new CanvasGradientStop { Position = 1.75f / 3.5f, Color = white },
                new CanvasGradientStop { Position = 2.25f / 3.5f, Color = MapRenderer.Alpha(white, .45) },
                new CanvasGradientStop { Position = 1, Color = MapRenderer.Alpha(white, 0) },
            ]
        )
        {
            Center = Head,
            RadiusX = 3.5f,
            RadiusY = 3.5f,
        };
        session.FillCircle(Head, 3.5f, head);
        return _mark;
    }

    /// <summary>
    /// A picture of the map. The UI thread draws it, then only the game loop
    /// reads it until it puts it back in the free pictures.
    /// </summary>
    private sealed class Picture(CanvasDevice device, Size size, CanvasRenderTarget bitmap)
    {
        internal CanvasDevice Device => device;

        /// <summary>The size in DIPs it was made for, which its pixels round.</summary>
        internal Size Size => size;

        internal CanvasRenderTarget Bitmap => bitmap;

        /// <summary>The view the picture shows, which places the dots on it.</summary>
        internal MapView View { get; set; }
    }

    /// <summary>What the dots draw: the traffic, where the user is and the transfer colours.</summary>
    /// <param name="OriginOpacity">The origin mask's strength at its centre.</param>
    internal sealed record Scene(
        Traffic Traffic,
        GeoPoint Origin,
        double OriginOpacity,
        Color Download,
        Color Upload
    );

    /// <summary>
    /// The routes that carry dots and what sizes them. A new instance means
    /// new rates; the same instance means only the colours changed.
    /// </summary>
    /// <param name="Area">The map's area in DIP².</param>
    /// <param name="Ceiling">The connection capacity in bytes per second.</param>
    /// <param name="Speed">How fast dots travel, as a multiple of the normal speed.</param>
    /// <param name="Total">How many dots fly at most, as a multiple of the normal maximum.</param>
    internal sealed record Traffic(IReadOnlyList<Route> Routes, double Area, double Ceiling, double Speed, double Total);

    /// <summary>A route's arc, its peer's end, its rates in bytes per second and its length in DIPs on the fitted overview.</summary>
    internal readonly record struct Route(
        DestinationKey Key,
        MapRenderer.GreatCircle Path,
        GeoPoint Peer,
        double Down,
        double Up,
        double Length
    );

    /// <summary>A destination's route as the dots follow it; it keeps its carried bytes while it transfers.</summary>
    private sealed class Lane
    {
        internal MapRenderer.GreatCircle Path { get; set; }
        internal GeoPoint Peer { get; set; }
    }
}
