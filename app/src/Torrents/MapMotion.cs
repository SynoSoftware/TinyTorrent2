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
/// the moving dots over it. A serialized worker prepares and rasterizes the
/// map; Win2D's game loop presents completed pictures and advances dots without
/// waiting for that worker or the UI.
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
    /// <summary>The radius of a destination's dot, in DIPs.</summary>
    internal const float MarkerRadius = 1.5f;
    private const float HaloRadius = 4;

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _wake;
    private readonly ConcurrentQueue<Picture> _free = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly Task _rendering;
    private readonly Dictionary<string, Geography.Country> _world;
    private Input? _input;
    private Input? _appliedInput;
    private CancellationTokenSource? _cancellation;
    private Request? _request;
    private Surface? _surface;
    private MapView? _appliedView;
    private MapRenderer? _renderer;
    private bool _again;
    private MapRenderer.Highlight _highlight;
    private Dictionary<PeerPlace, int> _marks = [];
    private HashSet<DestinationKey>? _selection;
    // The destinations the drawn arcs mark as chosen or selected.
    private HashSet<DestinationKey> _marked = [];
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Scene? _scene;
    private Completed? _pending;
    private MapView? _view;
    // Replaced whole, and only under _gate. Only the game loop calls its
    // pointer and changes the viewport of the instance it read.
    private Drag? _drag;
    private volatile bool _idle = true;
    private long _revision;
    private volatile bool _disposed;
    private int _newSurface;
    // Render worker only.
    private CanvasDevice? _device;
    private float _dpi;
    private Sprites? _sprites;
    private Completed? _completed;

    // The game loop owns simulation and publishes detached display state.
    private readonly MapDots<DestinationKey> _dots = new(SmallestDot);
    private Completed? _shown;
    private Traffic? _applied;
    private (Traffic? Traffic, MapView? View, bool Visible) _visibility;
    private Displayed? _displayed;
    private int _drawn;
    private (long? Bytes, double Total) _reported;
#if CAPTURE
    private int _frames;
    private int _pictures;
    private long _cycles;
    private MapView? _drawnView;
    private Completed? _drawnPicture;
    private int _captureRedraw;
    private readonly ConcurrentQueue<Action> _captures = new();

    internal MapView? DrawnView => Volatile.Read(ref _drawnView);
    internal int DrawnRoutes => Volatile.Read(ref _drawnPicture)?.Scene?.Traffic.Routes.Count ?? 0;
    internal int DrawnVisibleRoutes { get; private set; }
    internal int KnownLocations => Volatile.Read(ref _drawnPicture)?.Input?.Model.Destinations.Count ?? 0;
    internal int DrawnLocations { get; private set; }
    internal (int Count, DestinationKey? Route, bool Upload, long Start, double Progress) DrawnDots { get; private set; }

    internal bool ShowsConnections(object subject, int peers)
    {
        var picture = Volatile.Read(ref _drawnPicture);
        return picture?.Input?.Model is { } model && ReferenceEquals(model.Subject, subject)
            && model.Countries.Values.Sum(group => group.Count) == peers
            && picture?.Scene?.Traffic.Routes.Count > 0;
    }

    /// <summary>The subject and peer count of the picture drawn last, read on the game loop thread.</summary>
    internal (object Subject, int Peers)? DrawnSubject => Volatile.Read(ref _drawnPicture)?.Input?.Model is { } model
        ? (model.Subject, model.Countries.Values.Sum(group => group.Count)) : null;

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

    internal MapMotion(DispatcherQueue dispatcher, Dictionary<string, Geography.Country> world)
    {
        _dispatcher = dispatcher;
        _world = world;
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
            _dispatcher.TryEnqueue(NewSurface);
        };
        Control.Update += OnUpdate;
        Control.Draw += OnDraw;
        Control.GameLoopStopped += OnStopped;
        _rendering = Task.Factory.StartNew(Render, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>The control that shows the map, to place where the map goes.</summary>
    internal CanvasAnimatedControl Control { get; }

    /// <summary>
    /// Raised on the UI thread when the rendering surface becomes available
    /// or changes device or DPI.
    /// </summary>
    internal event Action<CanvasDevice, float>? SurfaceChanged;

    /// <summary>
    /// Raised on the UI thread with the bytes one dot stands for, null before
    /// the first traffic, and the total rate of the routes that carry dots.
    /// </summary>
    internal event Action<long?, double>? Measured;

    /// <summary>Raised on the UI thread when the displayed places, their highlight or the surface change.</summary>
    internal event Action? DisplayChanged;

    internal Task Ready => _ready.Task;
    internal Task Released => _rendering;
    internal Displayed? Display => !_disposed && Volatile.Read(ref _displayed) is { } displayed
        && ReferenceEquals(displayed.Surface, Volatile.Read(ref _surface)) ? displayed : null;

    internal sealed record Displayed(MapRenderer.Inspection Inspection, MapView View, object Surface);

    internal sealed record Input
    {
        internal required Presentation Model { get; init; }
        internal required MapRenderer.Palette Palette { get; init; }
        internal required MapModel.Selection Selection { get; init; }
        internal IReadOnlySet<PeerPlace>? Chosen { get; init; }
        internal DestinationKey? Pointed { get; init; }
        internal required Color Background { get; init; }
        internal MapAnimation Animation { get; init; }
        internal MapDotTotal DotTotal { get; init; }
        internal int ArcLimit { get; init; }
        internal bool Animates { get; init; }
        internal bool HighContrast { get; init; }
    }

    private sealed record Surface(CanvasDevice Device, float Dpi);
    private sealed record Request(Input Input, MapView View, Surface Surface, CancellationToken Cancellation);

    /// <summary>A drag the game loop follows: the map point it keeps under the pointer, on its own copy of the view.</summary>
    private sealed record Drag(Vector2 Grip, Func<Vector2?> Pointer, MapViewport Viewport);

#if CAPTURE
    internal event Action<double>? FrameMeasured;
    internal event Action<string, double>? WorkMeasured;
    internal Action<CancellationToken>? DrawingStarted { get; set; }
    internal Action<MapView>? PicturePublished { get; set; }
    internal MapRenderer? Renderer => _renderer;

    internal Task CaptureBarrier()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _captures.Enqueue(() => ready.TrySetResult());
        Signal();
        return ready.Task;
    }

    internal void CaptureRedraw()
    {
        Interlocked.Exchange(ref _captureRedraw, 1);
        Changed();
    }

    internal Task CaptureDraw(MapView view, Color background, Func<CanvasDrawingSession, bool> draw)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _captures.Enqueue(() =>
        {
            try
            {
                var surface = _surface!;
                _device = surface.Device;
                _dpi = surface.Dpi;
                Draw(view, background, (map, _) => draw(map));
                ready.TrySetResult();
            }
            catch (Exception error)
            {
                ready.TrySetException(error);
            }
        });
        Signal();
        Wake();
        return ready.Task;
    }
#endif

    /// <summary>
    /// Replaces a cached picture on the render worker; transparent pixels
    /// replace the old content instead of accumulating over it.
    /// </summary>
    private void Draw(MapView view, Color background, Func<CanvasDrawingSession, CanvasDrawingSession, bool> draw, Request? request = null)
    {
        if (_disposed || _device is not { } device)
            return;
        Picture? picture = null;
        try
        {
            picture = Lease(device, view);
            bool drawn;
            using (var session = picture.Bitmap.CreateDrawingSession())
            using (var arcs = picture.Arcs.CreateDrawingSession())
            {
                // Targets retain their previous pixels until explicitly cleared.
                session.Clear(Microsoft.UI.Colors.Transparent);
                arcs.Clear(Microsoft.UI.Colors.Transparent);
                drawn = draw(session, arcs);
            }
            request?.Cancellation.ThrowIfCancellationRequested();
            if (drawn)
            {
                var inspection = request is null ? null : _renderer!.Inspect(request.Input.Model, request.View);
                var completed = new Completed
                {
                    Picture = picture,
                    View = request?.View ?? view,
                    RasterView = view,
                    Background = background,
                    Input = request?.Input,
                    Scene = request is null ? null : _renderer!.Scene,
                    Inspection = inspection,
                    Markers = request is null ? null : PrepareMarkers(request.Input, request.View, inspection!),
                    Surface = request?.Surface ?? Volatile.Read(ref _surface),
                };
                picture = null;
                Publish(completed, request?.Cancellation ?? _shutdown.Token);
            }
            else
                Return(picture);
        }
        catch (OperationCanceledException) when (request?.Cancellation.IsCancellationRequested == true)
        {
            if (picture is not null)
                Return(picture);
            throw;
        }
        catch (Exception error) when (device.IsDeviceLost(error.HResult))
        {
            if (picture is not null)
                Return(picture);
            device.RaiseDeviceLost();
        }
        catch
        {
            if (picture is not null)
                Return(picture);
            throw;
        }
    }

    /// <summary>
    /// Draws a new subject's first picture once and publishes it three times:
    /// its land, then its arcs with the places they connect, then every place.
    /// </summary>
    /// <remarks>
    /// The worker keeps its lease's reference until the last publication or a
    /// cancellation, and each publication takes a reference of its own. The
    /// game loop draws the arcs target only from the publication that completes it.
    /// </remarks>
    private void DrawPhases(MapView raster, MapRenderer.Highlight highlight, Request request)
    {
        if (_disposed || _device is not { } device || _renderer is not { } renderer)
            return;
        var input = request.Input;
        var cancellation = request.Cancellation;
        if (!renderer.CanDraw)
        {
            renderer.PrepareArcs(cancellation);
            _appliedInput = input;
            return;
        }
        Picture? picture = null;
        try
        {
            picture = Lease(device, raster);
            using (var land = picture.Bitmap.CreateDrawingSession())
            {
                land.Clear(Microsoft.UI.Colors.Transparent);
                renderer.DrawLand(land, input.Palette, highlight, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            var completed = new Completed
            {
                Picture = picture,
                View = request.View,
                RasterView = raster,
                Background = input.Background,
                Input = input,
                Inspection = renderer.Inspect(input.Model, new List<Destination>(), request.View),
                Surface = request.Surface,
                ArcsDrawn = false,
            };
            picture.Retain();
            Publish(completed, cancellation);

            renderer.PrepareArcs(cancellation);
            using (var arcs = picture.Arcs.CreateDrawingSession())
            {
                arcs.Clear(Microsoft.UI.Colors.Transparent);
                renderer.DrawArcs(arcs, input.Palette, highlight, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            var connected = renderer.Inspect(input.Model,
                input.Model.Destinations.Where(destination => destination.Totals.Connections > 0).ToList(), request.View);
            completed = completed with
            {
                Scene = renderer.Scene,
                Inspection = connected,
                Markers = PrepareMarkers(input, request.View, connected),
                ArcsDrawn = true,
            };
            picture.Retain();
            Publish(completed, cancellation);

            var every = renderer.Inspect(input.Model, request.View);
            completed = completed with { Inspection = every, Markers = PrepareMarkers(input, request.View, every) };
            picture.Retain();
            Publish(completed, cancellation);
            _appliedInput = input;
        }
        catch (Exception error) when (device.IsDeviceLost(error.HResult))
        {
            device.RaiseDeviceLost();
        }
        finally
        {
            if (picture is not null)
                Return(picture);
        }
    }

    // One the game loop has finished with, or a new one.
    private Picture Lease(CanvasDevice device, MapView view)
    {
        while (true)
        {
            Picture? picture;
            lock (_gate)
                _free.TryDequeue(out picture);
            if (picture is not null)
            {
                if (ReferenceEquals(picture.Device, device) && picture.Dpi == _dpi && picture.Size == view.Size)
                {
                    picture.Retain();
                    return picture;
                }
                picture.Dispose();
                continue;
            }
            return new(SpritesFor(device), view);
        }
    }

    private Sprites SpritesFor(CanvasDevice device)
    {
        if (_sprites is null || !ReferenceEquals(_sprites.Device, device) || _sprites.Dpi != _dpi)
        {
            _sprites?.Release();
            // A failed creation leaves no released set for a later call to
            // release again.
            _sprites = null;
            _sprites = new Sprites(device, _dpi);
        }
        return _sprites;
    }

    private void Return(Picture picture)
    {
        if (picture.Release())
            lock (_gate)
                _free.Enqueue(picture);
    }

    private void Publish(Completed completed, CancellationToken cancellation)
    {
        try
        {
            lock (_gate)
            {
                cancellation.ThrowIfCancellationRequested();
                if (_disposed)
                {
                    Return(completed.Picture);
                    return;
                }
                completed.Picture.Retain();
                if (_completed is { } previous)
                    Return(previous.Picture);
                _completed = completed;
                if (Interlocked.Exchange(ref _pending, completed) is { } skipped)
                    Return(skipped.Picture);
            }
        }
        catch
        {
            Return(completed.Picture);
            throw;
        }
#if CAPTURE
        PicturePublished?.Invoke(completed.View);
#endif
        _dispatcher.TryEnqueue(Wake);
    }

    internal void Navigate(MapView view)
    {
        if (_disposed)
        {
            return;
        }
        lock (_gate)
        {
            if (_drag is { } drag)
            {
                Volatile.Write(ref _drag, drag with { Viewport = MapViewport.From(view) });
                return;
            }
        }
        if (view == Volatile.Read(ref _view))
        {
            return;
        }
        Volatile.Write(ref _view, view);
        Changed();
    }

    /// <summary>
    /// Lets the game loop move the view for a drag, keeping the map point
    /// <paramref name="grip"/> under the position that <paramref name="pointer"/>
    /// returns once per frame. Called on the UI thread.
    /// </summary>
    /// <remarks>
    /// <paramref name="pointer"/> runs on the game loop's thread and returns
    /// the position in DIPs of the map control, or null once the button is up;
    /// the view then stays where it is. Until <see cref="Release"/>,
    /// <see cref="Navigate"/> keeps the view's centre and takes only its size,
    /// zoom and meridian, at the next frame.
    /// </remarks>
    /// <returns>False when the map is disposed or has no view yet; the caller then moves the view itself.</returns>
    internal bool Follow(Vector2 grip, Func<Vector2?> pointer)
    {
        lock (_gate)
        {
            if (_disposed || _view is not { } view)
            {
                return false;
            }
            Volatile.Write(ref _drag, new Drag(grip, pointer, MapViewport.From(view)));
        }
        Wake();
        return true;
    }

    /// <summary>
    /// Ends the drag that <see cref="Follow"/> started. Called on the UI
    /// thread; after it, <see cref="Navigate"/> sets the view again.
    /// </summary>
    /// <returns>The view the game loop reached, or null when no drag was followed.</returns>
    internal MapView? Release()
    {
        lock (_gate)
        {
            if (_drag is null)
            {
                return null;
            }
            Volatile.Write(ref _drag, null);
            return _view;
        }
    }

    /// <summary>
    /// Publishes detached facts and appearance without running native drawing
    /// on the caller's UI thread.
    /// </summary>
    internal void Show(Input input)
    {
        if (_disposed || input == Volatile.Read(ref _input) && Volatile.Read(ref _request) is not null)
            return;
        Volatile.Write(ref _input, input);
        Changed();
    }

    private void Changed()
    {
        Submit();
        Wake();
    }

    // Also called on the game loop's thread, where Wake must not run, because
    // it uses the UI thread's timer and the control.
    private void Submit()
    {
        lock (_gate)
        {
            if (_input is { } input && _view is { } view && _surface is { } surface)
            {
                var previous = _request;
                if (_cancellation is null || previous is null
                    || previous.Input.Model.Subject != input.Model.Subject
                    || previous.Input.Model.Cities != input.Model.Cities
                    || previous.Input.Model.Origin != input.Model.Origin
                    || previous.View.Meridian != view.Meridian || previous.Surface != surface)
                {
                    CancelRequest();
                    _cancellation = new();
                }
                Volatile.Write(ref _request, new(input, view, surface, _cancellation.Token));
            }
            // Under the gate, so two threads cannot each replace a completed
            // task and leave a caller waiting on the one that is lost.
            if (_ready.Task.IsCompleted)
            {
                Volatile.Write(ref _ready, new(TaskCreationOptions.RunContinuationsAsynchronously));
            }
        }
        Signal();
    }

    private void Signal()
    {
        lock (_gate)
            if (_changed.CurrentCount == 0)
                _changed.Release();
    }

    private void Render()
    {
        Thread.CurrentThread.Name = "Peer map rendering";
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        try
        {
            while (true)
            {
                _changed.Wait(_shutdown.Token);
                while (!_disposed)
                {
#if CAPTURE
                    while (_captures.TryDequeue(out var capture))
                        capture();
#endif
                    if (Volatile.Read(ref _request) is not { } request)
                        break;
                    TimeSpan? delay = null;
                    try
                    {
                        RenderMap(request);
                        delay = _again || _renderer?.HasStaleStrokes == true ? TimeSpan.Zero : _renderer?.Delay;
                    }
                    catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
                    {
                        _again = true;
                    }
                    catch (Exception error) when (request.Surface.Device.IsDeviceLost(error.HResult))
                    {
                        request.Surface.Device.RaiseDeviceLost();
                    }
                    if (_changed.Wait(0))
                        continue;
                    if (delay is null)
                        break;
                    _changed.Wait(delay.Value, _shutdown.Token);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            _ready.TrySetException(error);
            _dispatcher.TryEnqueue(() => throw new InvalidOperationException("Map rendering failed.", error));
        }
        finally
        {
            _renderer?.Dispose();
            _renderer = null;
            if (_completed is { } completed)
                Return(completed.Picture);
            _completed = null;
            _stopped.Task.GetAwaiter().GetResult();
            if (Interlocked.Exchange(ref _pending, null) is { } pending)
                Return(pending.Picture);
            while (_free.TryDequeue(out var picture))
                picture.Dispose();
            _sprites?.Release();
            _sprites = null;
            _changed.Dispose();
            _shutdown.Dispose();
        }
    }

    internal void CancelDrawing()
    {
        // Under the gate, so the game loop cannot submit the old input between the two.
        lock (_gate)
        {
            CancelRequest();
            Volatile.Write(ref _input, null);
        }
    }

    private void CancelRequest()
    {
        lock (_gate)
        {
            if (_cancellation is { } cancellation)
                _ = Cancel(cancellation);
            _cancellation = null;
            Volatile.Write(ref _request, null);
        }
    }

    private static async Task Cancel(CancellationTokenSource cancellation)
    {
        await cancellation.CancelAsync();
        cancellation.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_gate)
        {
            Volatile.Write(ref _drag, null);
        }
        CancelDrawing();
        _ = _shutdown.CancelAsync();
        _wake.Stop();
        Volatile.Write(ref _input, null);
        _ready.TrySetCanceled();
        Control.Update -= OnUpdate;
        Control.Draw -= OnDraw;
        Control.RemoveFromVisualTree();
        if (_surface is null)
            _stopped.TrySetResult();
    }

    private void OnStopped(ICanvasAnimatedControl sender, object args)
    {
        // Win2D stops the game loop before raising Unloaded, so releasing the
        // shown reference lets the worker retire its native resources safely.
        if (_shown is { } shown)
            Return(shown.Picture);
        _shown = null;
        _scene = null;
        _stopped.TrySetResult();
    }

    // Until the game loop presents its first frame, a new surface can show
    // black, so the layer stays hidden until then.
    private void NewSurface()
    {
        if (_disposed)
            return;
        Control.Opacity = 0;
        Interlocked.Exchange(ref _newSurface, 1);
        _surface = new(Control.Device, Control.Dpi);
        Changed();
        SurfaceChanged?.Invoke(Control.Device, Control.Dpi);
    }

    internal void Wake()
    {
        Interlocked.Increment(ref _revision);
        _wake.Stop();
        if (!_disposed)
            Control.Paused = false;
    }

    // A newer picture, traffic or surface invalidates the queued decision,
    // including one made while flying dots were outside the previous view.
    private void Sleep(long revision, TimeSpan? wait, long since)
    {
        if (
            _disposed
            || !_idle
            || Volatile.Read(ref _pending) is not null
            || revision != Volatile.Read(ref _revision)
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

    private void RenderMap(Request request)
    {
        var input = request.Input;
        var view = request.View;
        var cancellation = request.Cancellation;
        cancellation.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_device, request.Surface.Device) || _dpi != request.Surface.Dpi)
        {
            _renderer?.Dispose();
            _renderer = null;
            _appliedInput = null;
            _appliedView = null;
            _device = request.Surface.Device;
            _dpi = request.Surface.Dpi;
        }
        if (input.Model.Meridian != view.Meridian
            || _device is not { } device || MapViewport.FitScale(view.Size) <= 0)
            return;
#if CAPTURE
        var started = Stopwatch.GetTimestamp();
        List<(string Phase, double Ms)> measurements = [];
#endif
        var previous = _appliedInput;
        var previousView = _appliedView;
        var first = _renderer is null;
        var margin = Margin(view);
        var raster = view with
        {
            Size = new(view.Size.Width + 2 * margin.X, view.Size.Height + 2 * margin.Y),
            Transform = view.Transform * Matrix3x2.CreateTranslation(margin),
            FrameSize = view.Size,
        };
        var renderer = _renderer ??= new MapRenderer(device, _dpi, () => _again = true,
            input.Model, MapViewport.From(raster), _world);
        var viewChanged = view != previousView;
        if (viewChanged)
        {
            renderer.UseView(MapViewport.From(raster));
            _appliedView = view;
        }
        var modelChanged = !ReferenceEquals(input.Model, previous?.Model);
        if (modelChanged || !ReferenceEquals(input.Selection, previous?.Selection))
        {
            var marks = input.Model.Marks(input.Selection);
            if (marks.Count != _marks.Count || marks.Any(mark => _marks.GetValueOrDefault(mark.Key) != mark.Value))
                _marks = marks;
            var marking = new MapRenderer.Highlight(null, _marks, null);
            var selected = !input.Selection.HasPeers ? null : input.Model.Destinations
                .Where(marking.IsSelected).Select(destination => destination.Key).ToHashSet();
            if (selected is null || _selection is null || !_selection.SetEquals(selected))
                _selection = selected;
        }
        var highlight = new MapRenderer.Highlight(input.Chosen, _marks, input.Pointed)
        {
            Selection = input.HighContrast ? null : _selection,
        };
        // A regroup can move a chosen or selected place to another destination while the
        // highlight stays equal, and the drawn arcs show which destinations it marks.
        var marked = _marked;
        if (modelChanged || highlight != _highlight)
        {
            var keys = input.Model.Destinations.Where(highlight.IsMarked).Select(destination => destination.Key).ToHashSet();
            if (!keys.SetEquals(_marked))
                marked = keys;
        }
        var prepare = first || modelChanged || view.Size != previousView?.Size
            || input.Animation != previous?.Animation || input.DotTotal != previous?.DotTotal
            || input.ArcLimit != previous?.ArcLimit || input.Animates != previous?.Animates
            || input.HighContrast != previous?.HighContrast;
        renderer.UseModel(input.Model);
        renderer.Animation = input.Animation;
        renderer.DotTotal = input.DotTotal;
        renderer.ArcLimit = input.ArcLimit;
        renderer.Animates = input.Animates;
        renderer.HighContrast = input.HighContrast;
#if CAPTURE
        renderer.WorkMeasured = (phase, ms) => measurements.Add((phase, ms));
#endif
        if (prepare)
            _appliedInput = null;
        // A new subject's land shows before its arcs are built. Its input
        // applies after the last phase, so a pass cancelled between phases
        // prepares and draws again.
        var phased = prepare && _completed?.Input?.Model.Subject != input.Model.Subject;
        var changed = !prepare ? renderer.Advance() : phased ? renderer.PrepareRoutes() : renderer.Prepare(cancellation);
        if (!phased)
            _appliedInput = input;
#if CAPTURE
        DrawingStarted?.Invoke(cancellation);
#endif
        cancellation.ThrowIfCancellationRequested();
        // The game loop moves the latest picture with the view, so a pan that picture still
        // covers draws no new one until the view rests.
        var postponed = viewChanged && _completed is { } latest && Covers(latest.View, view)
            && renderer.Postpone(latest.View.AxisScale);
        var draw = phased || _again || (viewChanged && !postponed) || changed || input.Palette != previous?.Palette
            || highlight != _highlight || !ReferenceEquals(marked, _marked) || input.Background != previous?.Background
            || input.HighContrast != previous?.HighContrast;
#if CAPTURE
        var captureRedraw = Interlocked.Exchange(ref _captureRedraw, 0) != 0;
        draw |= captureRedraw;
        // What made the pass draw, one letter per cause, so a capture shows which input keeps
        // the worker drawing: P phase, A again, V view (c when the latest picture covered it),
        // C activity, L palette, H highlight, M marked destinations, B background, K contrast,
        // R capture redraw.
        if (draw)
        {
            var covered = viewChanged && !postponed && _completed is { } shown && Covers(shown.View, view);
            measurements.Add(("draw " + string.Concat(phased ? "P" : "", _again ? "A" : "",
                viewChanged && !postponed ? covered ? "Vc" : "V" : "", changed ? "C" : "",
                input.Palette != previous?.Palette ? "L" : "", highlight != _highlight ? "H" : "",
                !ReferenceEquals(marked, _marked) ? "M" : "",
                input.Background != previous?.Background ? "B" : "", input.HighContrast != previous?.HighContrast ? "K" : "",
                captureRedraw ? "R" : ""), 0));
        }
#endif
        if (draw)
        {
            _again = false;
            _highlight = highlight;
            _marked = marked;
            if (phased)
                DrawPhases(raster, highlight, request);
            else
                Draw(raster, input.Background, (geography, arcs) => renderer.Draw(geography, arcs, input.Palette, highlight, cancellation), request);
        }
        else if (renderer.HasStaleStrokes)
            renderer.RebuildStrokes(cancellation);
        if (!draw && _completed is { } completed
            && (!ReferenceEquals(renderer.Scene, completed.Scene) || !ReferenceEquals(input.Model, completed.Input?.Model)))
        {
            var inspection = renderer.Inspect(input.Model, view);
            var markers = PrepareMarkers(input, view, inspection);
            completed.Picture.Retain();
            Publish(completed with
            {
                Input = input,
                Scene = renderer.Scene,
                Inspection = inspection,
                Markers = markers,
            }, cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        _highlight = highlight;
        var ready = Volatile.Read(ref _ready);
        if (!_again && !renderer.HasStaleStrokes && !renderer.WaitsForRest && !ready.Task.IsCompleted)
            _dispatcher.TryEnqueue(() =>
            {
                if (!_disposed && ReferenceEquals(input, _input) && view == _view)
                    ready.TrySetResult();
            });
#if CAPTURE
        if (draw || prepare)
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _dispatcher.TryEnqueue(() =>
            {
                if (_disposed)
                    return;
                foreach (var (phase, ms) in measurements)
                    WorkMeasured?.Invoke(phase, ms);
                FrameMeasured?.Invoke(elapsed);
            });
        }
#endif
    }

    /// <summary>How much of the map a picture holds beyond each edge of its view, in DIPs.</summary>
    private static Vector2 Margin(MapView view) =>
        new Vector2((float)view.Size.Width, (float)view.Size.Height) / 8;

    /// <summary>
    /// Whether a picture drawn for <paramref name="drawn"/> shows all of <paramref name="view"/>
    /// at the same zoom, with half its margin to spare for the time a new picture takes.
    /// </summary>
    private static bool Covers(MapView drawn, MapView view)
    {
        if (drawn.Size != view.Size || drawn.Meridian != view.Meridian || drawn.AxisScale.Y != view.AxisScale.Y)
        {
            return false;
        }

        var size = new Vector2((float)view.Size.Width, (float)view.Size.Height);
        // The view's centre wraps at 180°, so its corners are placed in the turn
        // nearest the picture.
        var turn = new Vector2(MapViewport.Wrap(view.Center.X, drawn.Center.X) - view.Center.X, 0);
        var low = Vector2.Transform(view.ToMap(Vector2.Zero) + turn, drawn.Transform);
        var high = Vector2.Transform(view.ToMap(size) + turn, drawn.Transform);
        var spare = Margin(view) / 2;
        return low.X >= -spare.X
            && low.Y >= -spare.Y
            && high.X <= size.X + spare.X
            && high.Y <= size.Y + spare.Y;
    }

    private void OnUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        var revision = Volatile.Read(ref _revision);
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
        var device = sender.Device;
        var dpi = sender.Dpi;
        if (Interlocked.Exchange(ref _pending, null) is { } picture)
        {
            var input = Volatile.Read(ref _input);
            if (ReferenceEquals(picture.Device, device) && picture.Picture.Dpi == dpi && Matches(picture, input))
            {
                if (_shown?.Input?.Model.Subject != picture.Input?.Model.Subject)
                {
                    _dots.Clear();
                    _applied = null;
                }
                if (_shown is { } previous)
                    Return(previous.Picture);
                Volatile.Write(ref _shown, picture);
                _scene = picture.Scene;
#if CAPTURE
                Interlocked.Increment(ref _pictures);
#endif
            }
            else
                Return(picture.Picture);
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
        // Before the view is read, so this frame draws where the pointer is.
        Track();
        var view = CurrentView;
        if (!ReferenceEquals(_visibility.Traffic, scene?.Traffic) || _visibility.View != view)
            _visibility = (scene?.Traffic, view, scene is not null && view is { } current && HasRouteInView(scene.Traffic, current));
        var visible = _visibility.Visible;
        // A followed drag keeps the loop running: the UI thread that would
        // pause and wake it may be blocked.
        _idle = Volatile.Read(ref _drag) is null && (_dots.Flying.Count == 0 || !visible);
        if (!_idle || _drawn < 2)
            return;
        var wait = scene is null ? null : _dots.Wait(visible);
        if (wait is null || wait > 2 * Frame)
            _dispatcher.TryEnqueue(() => Sleep(revision, wait, now));
    }

    private static bool HasRouteInView(Traffic traffic, MapView view)
    {
        for (var index = 0; index < traffic.Routes.Count; index++)
        {
            var route = traffic.Routes[index];
            if (route.Length >= MapDots<DestinationKey>.ShortRoute && view.Shows(route.Bounds, 13))
            {
                return true;
            }
        }
        return false;
    }

    // Game loop thread.
    private void Track()
    {
        if (Volatile.Read(ref _drag) is not { } drag || drag.Viewport is not { Scale: > 0 } viewport
            || drag.Pointer() is not { } position)
        {
            return;
        }
        var (center, ratio) = (viewport.Center, viewport.Ratio);
        viewport.Hold(viewport.Zoom, drag.Grip, position);
        if (viewport.Center == center && viewport.Ratio == ratio)
        {
            return;
        }
        var view = viewport.View;
        lock (_gate)
        {
            // A Release or Navigate after this frame read the drag wins.
            if (!ReferenceEquals(_drag, drag) || view == _view)
            {
                return;
            }
            Volatile.Write(ref _view, view);
        }
        Submit();
    }

    private void Apply(Traffic traffic, long now)
    {
        _dots.Update(traffic.Dots, now);
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
#if CAPTURE
        DrawnDots = default;
        DrawnLocations = 0;
#endif
        // A picture from a lost device waits for the one the new device draws.
        if (_shown is not { } picture || !ReferenceEquals(picture.Device, sender.Device))
            return;
        if (_drawn == 0)
            _drawn = 1;
        var draw = args.DrawingSession;
        var view = CurrentView ?? picture.View;
        draw.Clear(picture.Background);
        var source = picture.RasterView;
        Matrix3x2.Invert(source.Transform, out var inverse);
        var left = Math.Max(0, Vector2.Transform(new Vector2(source.Center.X - 180, 0), source.Transform).X);
        var right = Math.Min(source.Size.Width, Vector2.Transform(new Vector2(source.Center.X + 180, 0), source.Transform).X);
        var rectangle = new Rect(left, 0, Math.Max(0, right - left), source.Size.Height);
        using (draw.CreateLayer(1, view.Bounds))
        {
            DrawLayer(picture.Bitmap);
            DrawMarks(draw, picture, view);
            // The worker may still be drawing the arcs of a land-only picture.
            if (picture.ArcsDrawn)
                DrawLayer(picture.Arcs);
        }
        DrawDots(draw, picture, view);
#if CAPTURE
        Volatile.Write(ref _drawnView, view);
        Volatile.Write(ref _drawnPicture, picture);
        DrawnVisibleRoutes = picture.Scene?.Traffic.Routes.Count(route => view.Shows(route.Bounds, 13)) ?? 0;
#endif
        var previous = Volatile.Read(ref _displayed);
        if (picture.Inspection is { } inspection && picture.Surface is { } surface)
        {
            var displayed = previous is { } current && ReferenceEquals(current.Inspection, inspection) && current.View == view
                && ReferenceEquals(current.Surface, surface) ? current : new Displayed(inspection, view, surface);
            Volatile.Write(ref _displayed, displayed);
            // Every new picture brings a new inspection, also while the view only
            // moves, so the UI hears only when the places or their highlight change.
            if (previous is null || !ReferenceEquals(previous.Surface, surface)
                || !ReferenceEquals(previous.Inspection.Destinations, inspection.Destinations)
                || previous.Inspection.Highlight != inspection.Highlight)
                _dispatcher.TryEnqueue(() =>
                {
                    if (!_disposed)
                        DisplayChanged?.Invoke();
                });
        }
        else
            Volatile.Write(ref _displayed, null);

        void DrawLayer(CanvasRenderTarget bitmap)
        {
            for (var turn = -1; turn <= 1; turn++)
            {
                draw.Transform = inverse * Matrix3x2.CreateTranslation(360 * turn, 0) * view.Transform;
                draw.DrawImage(bitmap, new Vector2((float)left, 0), rectangle);
            }
            draw.Transform = Matrix3x2.Identity;
        }
    }

    private void DrawDots(CanvasDrawingSession draw, Completed picture, MapView view)
    {
        if (Volatile.Read(ref _scene) is not { } scene || _dots.Flying.Count == 0)
            return;
#if CAPTURE
        Interlocked.Increment(ref _frames);
#endif
        var now = Stopwatch.GetTimestamp();
        var mark = picture.Picture.Mark;
        // Fades go into the tint and the angle into the transform, so every
        // dot stays in one batch.
        using var batch = draw.CreateSpriteBatch();
#if CAPTURE
        var visibleDots = 0;
        MapDots<DestinationKey>.Dot? firstDot = null;
#endif
        foreach (var dot in _dots.Flying)
        {
            if (Place(scene, view, dot, now) is not { } placed)
                continue;
#if CAPTURE
            visibleDots++;
            firstDot ??= dot;
#endif
            var tint = dot.Upload ? scene.Upload : scene.Download;
            tint = scene.Highlight.Colour(tint, dot.Route);
            batch.Draw(
                mark,
                Matrix3x2.CreateTranslation(-Head)
                    * Matrix3x2.CreateRotation(placed.Angle)
                    * Matrix3x2.CreateTranslation(placed.Point),
                new Vector4(tint.R, tint.G, tint.B, tint.A) / 255 * placed.Strength
            );
        }
#if CAPTURE
        if (firstDot is { } first)
            DrawnDots = (visibleDots, first.Route, first.Upload, first.Start, first.Progress(now));
#endif
    }

    private MapView? CurrentView => _shown is { } shown && Volatile.Read(ref _view) is { } view
        && view.Meridian == shown.View.Meridian ? view : _shown?.View;

    private bool Matches(Completed completed, Input? input) => Volatile.Read(ref _surface) is { } surface
        && ReferenceEquals(completed.Surface, surface)
        && (completed.Input is not { } previous || input is not null && previous.Model.Subject == input.Model.Subject
            && previous.Model.Cities == input.Model.Cities && previous.Model.Origin == input.Model.Origin
            && completed.View.Meridian == input.Model.Meridian);

    private void DrawMarks(CanvasDrawingSession draw, Completed picture, MapView view)
    {
        if (picture.Markers is not { } markers)
            return;
        if (markers.Origin is { } origin)
            draw.FillCircle(view.Screen(origin), 3, markers.Ink);
        var dot = markers.HighContrast ? picture.Picture.Dot : picture.Picture.Halo;
        var radius = markers.HighContrast ? MarkerRadius : HaloRadius;
        using (var batch = draw.CreateSpriteBatch())
        {
            foreach (var marker in markers.Locations)
            {
                var point = view.Screen(marker.Point);
                if (point.X < -16 || point.Y < -16 || point.X > view.Size.Width + 16 || point.Y > view.Size.Height + 16)
                    continue;
                var transform = Matrix3x2.CreateScale(marker.Scale) * Matrix3x2.CreateTranslation(point);
                batch.Draw(dot,
                    Matrix3x2.CreateTranslation(new Vector2(-radius - 1)) * transform, marker.Tint);
#if CAPTURE
                DrawnLocations++;
#endif
            }
        }
    }

    private Markers PrepareMarkers(Input input, MapView view, MapRenderer.Inspection inspection)
    {
        var previous = _completed;
        if (previous?.Markers is { } retained && previous.Input is { } earlier
            && ReferenceEquals(inspection.Destinations, previous.Inspection?.Destinations)
            && view.Meridian == previous.View.Meridian && input.Palette == earlier.Palette
            && input.HighContrast == earlier.HighContrast && inspection.Highlight == previous.Inspection?.Highlight
            && inspection.Origin == previous.Inspection?.Origin)
            return retained;

        var ink = input.Palette.Ink;
        var highlight = inspection.Highlight;
        Vector2? origin = inspection.Origin is { } location ? view.Coordinates(location) : null;
        var locations = inspection.Destinations.Select(destination =>
        {
            var colour = input.HighContrast ? ink : highlight.Colour(ink, destination.Key);
            return new Marker(view.Coordinates(destination.Point),
                new Vector4(colour.R, colour.G, colour.B, colour.A) / 255,
                highlight.IsMarked(destination) || destination.Key == highlight.Pointed ? 1.5f : 1);
        });
        if (previous?.Markers is { } cached && cached.Origin == origin && cached.Ink == ink
            && cached.HighContrast == input.HighContrast && locations.SequenceEqual(cached.Locations))
            return cached;
        return new Markers
        {
            Origin = origin,
            Ink = ink,
            HighContrast = input.HighContrast,
            Locations = locations.ToArray(),
        };
    }

    private static void DrawDot(CanvasRenderTarget dot)
    {
        const float radius = MarkerRadius;
        using var session = dot.CreateDrawingSession();
        session.Clear(Microsoft.UI.Colors.Transparent);
        session.FillCircle(radius + 1, radius + 1, radius, Microsoft.UI.Colors.White);
    }

    private static void DrawHalo(CanvasRenderTarget halo, CanvasDevice device)
    {
        var centre = new Vector2(HaloRadius + 1);
        var white = Microsoft.UI.Colors.White;
        using var session = halo.CreateDrawingSession();
        session.Clear(Microsoft.UI.Colors.Transparent);
        using var brush = new CanvasRadialGradientBrush(device,
            [
                new CanvasGradientStop { Position = 0, Color = MapRenderer.Alpha(white, .3) },
                new CanvasGradientStop { Position = .4f, Color = MapRenderer.Alpha(white, .12) },
                new CanvasGradientStop { Position = 1, Color = MapRenderer.Alpha(white, 0) },
            ])
        {
            Center = centre,
            RadiusX = HaloRadius,
            RadiusY = HaloRadius,
        };
        session.FillCircle(centre, HaloRadius, brush);
        session.FillCircle(centre, MarkerRadius, white);
    }

    /// <summary>
    /// Where a dot shows, the angle it moves at and its strength, or null when
    /// it is off screen or faded out.
    /// </summary>
    private static (Vector2 Point, float Angle, float Strength)? Place(
        Scene scene,
        MapView view,
        MapDots<DestinationKey>.Dot dot,
        long now
    )
    {
        var progress = dot.Progress(now);
        if (progress >= 1)
            return null;
        var lane = scene.Traffic.Membership[dot.Route];
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
    private static void DrawMark(CanvasRenderTarget mark, CanvasDevice device)
    {
        var white = Microsoft.UI.Colors.White;
        using var session = mark.CreateDrawingSession();
        session.Clear(Microsoft.UI.Colors.Transparent);
        using var tail = new CanvasLinearGradientBrush(device, MapRenderer.Alpha(white, .6), MapRenderer.Alpha(white, 0))
        {
            StartPoint = Head,
            EndPoint = Head with { X = Head.X - 8 },
        };
        using var shape = CanvasGeometry.CreatePolygon(
            device,
            [
                Head with { Y = Head.Y - 1.2f },
                Head with { Y = Head.Y + 1.2f },
                Head with { X = Head.X - 8 },
            ]
        );
        session.FillGeometry(shape, tail);
        using var head = new CanvasRadialGradientBrush(
            device,
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
    }

    /// <summary>
    /// Rate-only completions share frozen pixels. A buffer returns to the
    /// worker after its cached, pending and shown references have retired.
    /// </summary>
    private sealed class Picture : IDisposable
    {
        private readonly Sprites _sprites;
        private int _references = 1;
        internal CanvasDevice Device => _sprites.Device;
        internal float Dpi => _sprites.Dpi;

        internal Picture(Sprites sprites, MapView view)
        {
            _sprites = sprites;
            sprites.Retain();
            Size = view.Size;
            try
            {
                Bitmap = new(sprites.Device, (float)view.Size.Width, (float)view.Size.Height, sprites.Dpi);
                Arcs = new(sprites.Device, (float)view.Size.Width, (float)view.Size.Height, sprites.Dpi);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>The size in DIPs it was made for, which its pixels round.</summary>
        internal Size Size { get; }

        internal CanvasRenderTarget Bitmap { get; }
        internal CanvasRenderTarget Arcs { get; }
        internal CanvasRenderTarget Mark => _sprites.Mark;
        internal CanvasRenderTarget Dot => _sprites.Dot;
        internal CanvasRenderTarget Halo => _sprites.Halo;
        internal void Retain() => Interlocked.Increment(ref _references);
        internal bool Release() => Interlocked.Decrement(ref _references) == 0;

        public void Dispose()
        {
            Bitmap?.Dispose();
            Arcs?.Dispose();
            _sprites.Release();
        }
    }

    /// <summary>
    /// The dot, mark and halo sprites for one device and DPI. Every picture of
    /// that device and DPI draws the same pixels, so they share one set. The
    /// render worker owns the current set and each picture holds a reference,
    /// because the game loop draws with the sprites of the picture it shows
    /// after the worker has moved on to another device or DPI. The last
    /// reference disposes the set, and every picture is disposed on the worker.
    /// </summary>
    private sealed class Sprites
    {
        private int _references = 1;

        internal Sprites(CanvasDevice device, float dpi)
        {
            Device = device;
            Dpi = dpi;
            try
            {
                Mark = new(device, 13, 8, dpi);
                DrawMark(Mark, device);
                Dot = new(device, 2 * (MarkerRadius + 1), 2 * (MarkerRadius + 1), dpi);
                DrawDot(Dot);
                Halo = new(device, 2 * (HaloRadius + 1), 2 * (HaloRadius + 1), dpi);
                DrawHalo(Halo, device);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal CanvasDevice Device { get; }
        internal float Dpi { get; }
        internal CanvasRenderTarget Mark { get; }
        internal CanvasRenderTarget Dot { get; }
        internal CanvasRenderTarget Halo { get; }

        internal void Retain() => Interlocked.Increment(ref _references);

        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
            {
                Dispose();
            }
        }

        private void Dispose()
        {
            Mark?.Dispose();
            Dot?.Dispose();
            Halo?.Dispose();
        }
    }

    private sealed record Completed
    {
        internal required Picture Picture { get; init; }
        internal CanvasRenderTarget Bitmap => Picture.Bitmap;
        internal CanvasRenderTarget Arcs => Picture.Arcs;
        internal CanvasDevice Device => Picture.Device;
        internal required MapView View { get; init; }
        internal required MapView RasterView { get; init; }
        internal Color Background { get; init; }
        internal Input? Input { get; init; }
        internal Scene? Scene { get; init; }
        internal MapRenderer.Inspection? Inspection { get; init; }
        internal Markers? Markers { get; init; }
        internal Surface? Surface { get; init; }

        /// <summary>Whether <see cref="Arcs"/> holds this picture's arcs, which a subject's land-only first publication lacks.</summary>
        internal bool ArcsDrawn { get; init; } = true;
    }

    private sealed record Markers
    {
        internal Vector2? Origin { get; init; }
        internal Color Ink { get; init; }
        internal bool HighContrast { get; init; }
        internal required Marker[] Locations { get; init; }
    }

    private readonly record struct Marker(Vector2 Point, Vector4 Tint, float Scale);

    /// <summary>What the dots draw: the traffic, where the user is and the transfer colours.</summary>
    /// <param name="OriginOpacity">The origin mask's strength at its centre.</param>
    internal sealed record Scene(
        Traffic Traffic,
        GeoPoint Origin,
        double OriginOpacity,
        Color Download,
        Color Upload
    )
    {
        internal MapRenderer.Highlight Highlight { get; init; }
    }

    /// <summary>
    /// The routes that carry dots and what sizes them. A new instance means
    /// new rates; the same instance means only the colours changed.
    /// </summary>
    /// <param name="Area">The map's area in DIP².</param>
    /// <param name="Ceiling">The connection capacity in bytes per second.</param>
    /// <param name="Speed">How fast dots travel, as a multiple of the full speed, which Fast uses.</param>
    /// <param name="Total">How many dots fly at most, as a multiple of the normal maximum.</param>
    internal sealed record Traffic(IReadOnlyList<Route> Routes, double Area, double Ceiling, double Speed, double Total)
    {
        internal IReadOnlyDictionary<DestinationKey, Route> Membership { get; } = Routes.ToDictionary(route => route.Key);
        internal MapDots<DestinationKey>.Input Dots { get; } = new(
            Routes.Select(route => new MapDots<DestinationKey>.Traffic(route.Key, route.Down, route.Up, route.Length)),
            Area, Ceiling, Speed, Total);
    }

    /// <summary>A route's arc, its peer's end, its rates in bytes per second and its length in DIPs on the fitted overview.</summary>
    internal readonly record struct Route(
        DestinationKey Key,
        MapRenderer.GreatCircle Path,
        GeoPoint Peer,
        double Down,
        double Up,
        double Length
    )
    {
        internal Rect Bounds { get; init; }
    }
}
