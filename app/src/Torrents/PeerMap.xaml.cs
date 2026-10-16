using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Torrents;

public sealed partial class PeerMap : UserControl
{
    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly Dictionary<string, Outline> _land = [];
    private readonly Dictionary<string, Group> _countries = [];
    private readonly List<Destination> _destinations = [];
    private readonly Dictionary<(string Code, GeoPoint? Point, bool Estimated), float> _displayed =
    [];
    private readonly DispatcherQueueTimer _animation;
    private CanvasControl? _canvas;
    private Dictionary<string, Geography.Country> _world = [];
    private Observation[] _observations = [];
    private Location? _origin;
    private float _meridian;
    private GeoPoint? _arcOrigin;
    private CanvasRadialGradientBrush? _originMask;
    private CanvasCachedGeometry? _ring;
    private CanvasCachedGeometry? _wideRing;
    private CanvasRenderTarget? _dot;
    private CanvasRenderTarget? _activeDot;
    private CanvasCommandList? _glowLines;
    private GaussianBlurEffect? _glowBlur;
    private GlowInputs? _glowInputs;
    private int _arrangement;
    private double _originStrength;
    // Miter joins spike past the line at sharp outline corners.
    private static readonly CanvasStrokeStyle Rounded = new() { LineJoin = CanvasLineJoin.Round };
    private static readonly CanvasStrokeStyle FixedStroke = new()
    {
        LineJoin = CanvasLineJoin.Round,
        TransformBehavior = CanvasStrokeTransformBehavior.Fixed,
    };
    private readonly Dictionary<string, Fill> _fills = [];
    private Extent _window;
    private float _windowDetail;
    private long _rebuildUntil;
    private bool _rebuildPending;
    private Strings? _text;
    private string _language = string.Empty;
    private (string Code, GeoPoint? Point, bool Estimated)? _selected;
    private IReadOnlyList<Peer> _selectedPeers = [];
    private Dictionary<PeerPlace, int> _marks = [];
    private Destination? _pointed;
    private bool _dirty = true;
    private bool _cities;
    private float _detail;
    private Size _size;
    private float _scale;
    private Vector2 _offset;
    private long _started;
#if CAPTURE
    internal Action<double>? FrameMeasured { get; set; }
#endif

    public PeerMap()
    {
        InitializeComponent();
        _animation = DispatcherQueue.CreateTimer();
        _animation.Interval = TimeSpan.FromMilliseconds(33);
        _animation.Tick += (_, _) =>
        {
            _canvas?.Invalidate();
            if (
                AnimationProgress >= 1
                || !_settings.AnimationsEnabled
                || _accessibility.HighContrast
            )
                _animation.Stop();
        };
        Loaded += OnLoaded;
        Unloaded += (_, _) => Stop();
        ActualThemeChanged += (_, _) => _canvas?.Invalidate();
        foreach (var swatch in new[] { Land, Edge, Download, Upload })
            swatch.RegisterPropertyChangedCallback(
                Border.BackgroundProperty,
                (_, _) => _canvas?.Invalidate()
            );
        Ink.RegisterPropertyChangedCallback(
            TextBlock.ForegroundProperty,
            (_, _) => _canvas?.Invalidate()
        );
        KeyDown += OnKey;
        ContextRequested += (_, _) => EndDrag(true);
        FitGlyph.FontFamily = Syno.Lucide.Font;
        FitGlyph.Glyph = Syno.Lucide.Maximize;
    }

    internal void Show(
        IReadOnlyList<Peer>? peers,
        Location? origin,
        Strings text,
        bool cities = true
    )
    {
        peers ??= [];
        // Most refreshes change nothing, so the peers are compared with the
        // last snapshot before a new one is allocated.
        if (
            IsShowing(peers)
            && _origin == origin
            && _language == text.Language
            && _cities == cities
        )
            return;
        var observations = peers
            .Select(peer => new Observation(
                peer,
                peer.DownloadRate,
                peer.UploadRate,
                peer.Location
            ))
            .ToArray();
        if (_cities != cities)
        {
            Choose(null);
            _pointed = null;
        }
        _cities = cities;
        _observations = observations;
        if (_selectedPeers.Count > 0)
        {
            var present = observations.Select(observation => observation.Peer).ToHashSet();
            SelectPeers(_selectedPeers.Where(present.Contains).ToArray());
        }
        _origin = origin;
        _text = text;
        if (_language != text.Language)
        {
            _language = text.Language;
            AutomationProperties.SetName(this, text.Get("peer_map", "name"));
            RefreshNavigation();
        }
        _dirty = true;
        _canvas?.Invalidate();
    }

    /// <summary>
    /// Raised when the person chooses a location on the map or clears the
    /// choice; the peers table shows only that place's peers.
    /// </summary>
    internal event EventHandler<PeerPlace?>? PlaceChanged;

    /// <summary>
    /// Highlights the locations of the peers selected in the table. The location
    /// chosen on the map stays chosen.
    /// </summary>
    internal void Select(IReadOnlyList<Peer> peers)
    {
        if (_selectedPeers.SequenceEqual(peers) && _pointed is null)
            return;
        SelectPeers(peers);
        _pointed = null;
        Describe();
        _canvas?.Invalidate();
    }

    private void Choose(Destination? destination)
    {
        _selected = destination?.Key;
        PlaceChanged?.Invoke(this, destination?.Group.Place);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            var world = await Geography.Load();
            if (!IsLoaded || _canvas is not null)
                return;
            _world = world;
            var canvas = new CanvasControl();
            _canvas = canvas;
            canvas.CreateResources += (_, _) =>
            {
                ReleaseGeometry();
                _dirty = true;
            };
            canvas.Draw += Draw;
            canvas.PointerMoved += OnPointer;
            canvas.PointerExited += (_, _) =>
            {
                if (_drag is { IsActive: true })
                    return;
                EndDrag(false);
                if (_pointed is null)
                    return;
                _pointed = null;
                Describe();
                canvas.Invalidate();
            };
            canvas.PointerPressed += OnPressed;
            canvas.PointerReleased += OnReleased;
            canvas.PointerCaptureLost += (_, _) => EndDrag(true);
            canvas.PointerCanceled += (_, _) => EndDrag(true);
            canvas.PointerWheelChanged += OnWheel;
            canvas.DoubleTapped += OnDoubleTapped;
            Drawing.Children.Add(canvas);
        }
        catch (Exception error)
            when (
                error is InvalidDataException or EndOfStreamException or InvalidOperationException
            )
        {
            var message = _text?.Error(error) ?? error.Message;
            Failure.Text = message;
            Failure.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(this, message);
            AutomationProperties.SetHelpText(this, message);
        }
    }

    private void Stop()
    {
        EndDrag(true);
        ContextFlyout?.Hide();
        _animation.Stop();
        ReleaseGeometry();
        _canvas?.RemoveFromVisualTree();
        _canvas = null;
        _size = default;
        _displayed.Clear();
        // The map reads its data again when it shows, so a hidden map holds none.
        _world = [];
    }

    private void ReleaseGeometry()
    {
        foreach (var shape in _land.Values)
            shape.Dispose();
        _land.Clear();
        foreach (var destination in _destinations)
            destination.Release();
        _destinations.Clear();
        _originMask?.Dispose();
        _originMask = null;
        _ring?.Dispose();
        _ring = null;
        _wideRing?.Dispose();
        _wideRing = null;
        _dot?.Dispose();
        _dot = null;
        _activeDot?.Dispose();
        _activeDot = null;
        ReleaseGlow();
        foreach (var fill in _fills.Values)
            fill.Geometry?.Dispose();
        _fills.Clear();
        _windowDetail = 0;
        _arcOrigin = null;
        _pointed = null;
    }

    private Vector2 Project(GeoPoint point) =>
        _offset + Coordinates(point) * new Vector2(OverviewRatio, 1) * _scale;

    // Geometry stays in geographic coordinates so resizing retains its GPU caches.
    // Longitude is measured from _meridian, the centre of the map.
    private Vector2 Coordinates(GeoPoint point) =>
        new(Wrap((float)(point.Longitude - _meridian), 0), (float)-point.Latitude);

    /// <summary>The longitude, moved by whole turns to within 180° of centre.</summary>
    private static float Wrap(float longitude, float centre) =>
        longitude + 360 * MathF.Round((centre - longitude) / 360);

    private GeoPoint? Locate(Location? location) =>
        location?.Point
        ?? (
            location is null
                ? null
                : _world.GetValueOrDefault(location.Code)?.Anchor
        );

    internal static float FitScale(Size size) =>
        (float)Math.Max(0, Math.Min((size.Width - 24) / (360 * OverviewRatio), (size.Height - 20) / 180));

    private void Arrange(CanvasControl canvas)
    {
        // Centring the origin keeps its routes away from the map's edges.
        var meridian = (float)(Locate(_origin)?.Longitude ?? 0);
        if (meridian != _meridian)
        {
            ReleaseGeometry();
            _meridian = meridian;
        }
        if (_size != canvas.Size || _land.Count == 0)
        {
            EndDrag(true);
            _size = canvas.Size;
            _scale = FitScale(_size);
            _offset = new Vector2((float)_size.Width / 2, (float)_size.Height / 2);
            // A new size can move the centre's latitude limit, so the ratio
            // follows the constrained centre.
            SetView(_zoom, _center, CalculateRatio(_zoom, _center.Y));
            _dirty = true;
        }
        // Outline points closer than half a DIP to their simplified line are
        // invisible. The level moves in powers of two, so zooming traces the
        // land again only once per doubling. Draw traces each country again
        // when it next shows.
        if (ViewScale > 0)
            _detail = MathF.Pow(2, MathF.Round(MathF.Log2(.5f / ViewScale)));
        if (_land.Count == 0)
        {
            foreach (var country in _world.Values.Where(country => country.Rings.Length > 0))
                _land[country.Code] = Trace(country);
        }
        if (!_dirty)
            return;
        _dirty = false;
        _arrangement++;
        var pointed = _pointed?.Key;
        var previous = _destinations.ToDictionary(destination => destination.Key);
        _destinations.Clear();
        _countries.Clear();
        Dictionary<PeerPlace, Group> locations = [];
        foreach (var observation in _observations)
        {
            if (
                observation.Location is not { } location
                || Locate(location) is not { } point
            )
                continue;
            var code = location.Code;
            var country = _world.GetValueOrDefault(code);
            if (!_countries.TryGetValue(code, out var group))
            {
                var language = _text!.Language;
                group = new Group(
                    PeerPlace.Of(location, false),
                    () => location.CountryName(language),
                    country?.Anchor ?? point,
                    estimated: !_cities
                );
                _countries.Add(code, group);
            }
            group.Add(observation);
            if (!_cities)
                continue;
            var place = PeerPlace.Of(location, true);
            if (!locations.TryGetValue(place, out var destination))
            {
                var peer = observation.Peer;
                destination = new Group(place, () => peer.LocationText, point);
                locations.Add(place, destination);
            }
            destination.Add(observation);
        }
        foreach (var group in _countries.Values)
            group.Start = _displayed.GetValueOrDefault(
                (group.Code, null, true),
                Strength(group.Down + group.Up)
            );
        var origin = Locate(_origin);
        IEnumerable<Group> destinations = _cities ? locations.Values : _countries.Values;
        foreach (var group in destinations)
        {
            if (
                !group.Estimated
                || !_world.TryGetValue(group.Code, out var country)
                || country.Areas.Length == 0
            )
            {
                Add(group, group.Point, 1, null);
                continue;
            }
            group.Regions = Regions(country.Areas);
            var rank = 0;
            // Idle peers are placed like one connection, at the most likely region.
            foreach (var (region, share) in Distribute(group.Regions, Math.Max(group.Active, 1)))
                Add(group, region.Point, share, region.Name, rank++);
        }
        foreach (var destination in previous.Values)
            destination.Release();
        _arcOrigin = origin;
        var keys = _destinations
            .Select(destination => destination.Key)
            .Concat(_countries.Keys.Select(code => (code, (GeoPoint?)null, true)))
            .ToHashSet();
        foreach (var key in _displayed.Keys.Where(key => !keys.Contains(key)).ToArray())
            _displayed.Remove(key);
        _pointed = _destinations.FirstOrDefault(destination => destination.Key == pointed);
        RefreshMarks();
        _started = Stopwatch.GetTimestamp();
        var animates =
            _destinations.Any(destination =>
                destination.Start != destination.Strength
                && (
                    destination.Arc is not null
                    || destination.Group.Estimated && destination.Group.Active > 0
                )
            )
            || _countries.Values.Any(group =>
                group.Estimated
                && group.Active > 0
                && group.Start != Strength(group.Down + group.Up)
            );
        if (animates && _settings.AnimationsEnabled && !_accessibility.HighContrast)
            _animation.Start();
        else
            _animation.Stop();
        Describe();

        void Add(Group group, GeoPoint point, double share, string? area, int rank = 0)
        {
            var strength = Strength((group.Down + group.Up) * share);
            var destination = new Destination(group, point, share, area)
            {
                Strength = strength,
                Rank = rank,
            };
            if (previous.Remove(destination.Key, out var existing))
            {
                if (origin == _arcOrigin && group.Active > 0)
                {
                    destination.Arc = existing.Arc;
                    destination.Paint = existing.Paint;
                }
                else
                    existing.Release();
            }
            if (destination.Arc is null && origin is { } source && group.Active > 0)
                destination.Arc = Arc(canvas, source, point);
            destination.Start = _displayed.GetValueOrDefault(destination.Key, strength);
            _destinations.Add(destination);
        }
    }

    // A ring never jumps across 180°, so it moves whole to within a turn of
    // the centre. Draw shows any part past a map edge through a copy of the
    // country on the other side. Only points at or above _detail are traced;
    // a ring left with fewer than three is too small to see. The border holds
    // only the sections this country draws, so each line is stroked once.
    private Outline Trace(Geography.Country country)
    {
        List<Vector2[]> rings = [];
        List<Piece> pieces = [];
        List<Vector2[]> lines = [];
        var gathered = 0;
        var extent = Extent.Empty;
        List<Vector2> ring = [];
        List<Vector2> line = [];
        foreach (var parts in country.Rings)
        {
            ring.Clear();
            foreach (var part in parts)
                Collect(part, ring, false);
            var low = float.MaxValue;
            var high = float.MinValue;
            foreach (var point in ring)
            {
                low = Math.Min(low, point.X);
                high = Math.Max(high, point.X);
            }
            var shift = 360 * MathF.Round(-((low + high) / 2 - _meridian) / 360) - _meridian;
            var placed = Place(ring, 0, ring.Count, shift);
            // A ring too small to draw still counts, so the country shows, and
            // is traced finer, once zooming makes it large enough.
            extent = extent.With(Extent.Of(placed));
            if (placed.Length < 3)
                continue;
            rings.Add(placed);
            foreach (var part in parts)
            {
                if (!part.Draws)
                    continue;
                line.Clear();
                Collect(part, line, true);
                // A piece gathers parts, each a line of its own, so short
                // parts share one draw; a longer part splits into lines that
                // share their end point, so it stays whole.
                for (var start = 0; start < line.Count - 1; start += Piece.Length - 1)
                {
                    var count = Math.Min(Piece.Length, line.Count - start);
                    if (gathered + count > Piece.Length)
                        Gather();
                    lines.Add(Place(line, start, count, shift));
                    gathered += count;
                }
            }
        }
        Gather();
        return new Outline([.. rings], [.. pieces], extent, _detail);

        void Gather()
        {
            if (lines.Count == 0)
                return;
            pieces.Add(new Piece([.. lines]));
            lines.Clear();
            gathered = 0;
        }

        // A part starts where the previous one ends, so a ring takes each
        // part's start from the part before it.
        void Collect(Geography.Part part, List<Vector2> points, bool start)
        {
            var section = part.Section;
            for (var step = start ? 0 : 1; step < section.Length; step++)
            {
                var vertex = section[part.Reversed ? section.Length - 1 - step : step];
                if (vertex.Level >= _detail)
                    points.Add(vertex.Position);
            }
        }

        // Map coordinates: longitude moved by the ring's shift, latitude inverted.
        static Vector2[] Place(List<Vector2> points, int start, int count, float shift)
        {
            var placed = new Vector2[count];
            for (var index = 0; index < count; index++)
            {
                var point = points[start + index];
                placed[index] = new Vector2(point.X + shift, -point.Y);
            }
            return placed;
        }
    }

    private static void AddFigure(
        CanvasPathBuilder path,
        IReadOnlyList<Vector2> points,
        CanvasFigureLoop loop
    )
    {
        path.BeginFigure(points[0]);
        for (var index = 1; index < points.Count; index++)
            path.AddLine(points[index]);
        path.EndFigure(loop);
    }

    // Sutherland-Hodgman: the part of a ring inside a window. Each ring is cut
    // on its own, so the Alternate fill keeps every hole inside the window.
    private static IReadOnlyList<Vector2> Crop(Vector2[] ring, Extent window)
    {
        var extent = Extent.Of(ring);
        if (window.Contains(extent))
            return ring;
        if (!window.Overlaps(extent))
            return [];
        var low = window.Low;
        var high = window.High;
        IReadOnlyList<Vector2> points = ring;
        points = Cut(points, point => point.X >= low.X, (a, b) => Across(a, b, (low.X - a.X) / (b.X - a.X)));
        points = Cut(points, point => point.X <= high.X, (a, b) => Across(a, b, (high.X - a.X) / (b.X - a.X)));
        points = Cut(points, point => point.Y >= low.Y, (a, b) => Across(a, b, (low.Y - a.Y) / (b.Y - a.Y)));
        points = Cut(points, point => point.Y <= high.Y, (a, b) => Across(a, b, (high.Y - a.Y) / (b.Y - a.Y)));
        return points;

        static Vector2 Across(Vector2 from, Vector2 to, float amount) => from + (to - from) * amount;

        static List<Vector2> Cut(
            IReadOnlyList<Vector2> points,
            Func<Vector2, bool> inside,
            Func<Vector2, Vector2, Vector2> edge
        )
        {
            List<Vector2> kept = [];
            if (points.Count == 0)
                return kept;
            var previous = points[^1];
            foreach (var point in points)
            {
                if (inside(point) != inside(previous))
                    kept.Add(edge(previous, point));
                if (inside(point))
                    kept.Add(point);
                previous = point;
            }
            return kept;
        }
    }

    private CanvasGeometry Arc(CanvasControl canvas, GeoPoint origin, GeoPoint end)
    {
        using var path = new CanvasPathBuilder(canvas);
        var from = Sphere(origin);
        var to = Sphere(end);
        var angle = Math.Acos(Math.Clamp(Vector3.Dot(from, to), -1, 1));
        var tangent = to - from * (float)Math.Cos(angle);
        if (tangent.LengthSquared() < .00000001f)
            tangent = Vector3.Cross(from, Math.Abs(from.Z) < .9f ? Vector3.UnitZ : Vector3.UnitY);
        tangent = Vector3.Normalize(tangent);
        var count = Math.Max(8, (int)Math.Ceiling(angle * 48));
        var previous = Coordinates(origin);
        path.BeginFigure(previous);
        for (var index = 1; index <= count; index++)
        {
            var travelled = angle * index / count;
            var direction =
                from * (float)Math.Cos(travelled) + tangent * (float)Math.Sin(travelled);
            var point = Coordinates(
                new GeoPoint(
                    Math.Atan2(direction.Y, direction.X) * 180 / Math.PI,
                    Math.Atan2(
                        direction.Z,
                        Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y)
                    )
                        * 180
                        / Math.PI
                )
            );
            // Split at the map's edge instead of drawing a chord across the map.
            if (Math.Abs(point.X - previous.X) > 180)
            {
                const float width = 360;
                var unwrapped = point.X + (point.X > previous.X ? -width : width);
                var edge = previous.X > 0 ? width / 2 : -width / 2;
                var amount =
                    unwrapped == previous.X ? 0 : (edge - previous.X) / (unwrapped - previous.X);
                var crossing = new Vector2(edge, previous.Y + (point.Y - previous.Y) * amount);
                path.AddLine(crossing);
                path.EndFigure(CanvasFigureLoop.Open);
                path.BeginFigure(new Vector2(-crossing.X, crossing.Y));
                path.AddLine(point);
            }
            else
                path.AddLine(point);
            previous = point;
        }
        path.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(path);
    }

    private static Vector3 Sphere(GeoPoint point)
    {
        var longitude = point.Longitude * Math.PI / 180;
        var latitude = point.Latitude * Math.PI / 180;
        return new(
            (float)(Math.Cos(latitude) * Math.Cos(longitude)),
            (float)(Math.Cos(latitude) * Math.Sin(longitude)),
            (float)Math.Sin(latitude)
        );
    }

    private double AnimationProgress =>
        Math.Min(1, Stopwatch.GetElapsedTime(_started).TotalSeconds / 1.6);

    private static float Strength(double rate)
    {
        const double ceiling = 5 * 1024 * 1024;
        return (float)Math.Clamp(Math.Log(1 + rate / 1024) / Math.Log(1 + ceiling / 1024), 0, 1);
    }

    private static Color Colour(Brush brush) => ((SolidColorBrush)brush).Color;

    private static Color Alpha(Color colour, double opacity) =>
        Color.FromArgb((byte)Math.Clamp(colour.A * opacity, 0, 255), colour.R, colour.G, colour.B);

    private static Color Blend(Color from, Color to, double amount) =>
        Color.FromArgb(
            (byte)(from.A + (to.A - from.A) * amount),
            (byte)(from.R + (to.R - from.R) * amount),
            (byte)(from.G + (to.G - from.G) * amount),
            (byte)(from.B + (to.B - from.B) * amount)
        );

    private static CanvasRadialGradientBrush CreateLanding(CanvasControl canvas, Color colour) =>
        new(
            canvas,
            [
                new CanvasGradientStop { Position = 0, Color = Alpha(colour, .3) },
                new CanvasGradientStop { Position = 1, Color = colour },
            ]
        )
        {
            RadiusX = 30,
            RadiusY = 30,
        };

    private void Draw(CanvasControl canvas, CanvasDrawEventArgs args)
    {
        if (_text is null || canvas.Size.Width < 1 || canvas.Size.Height < 1)
            return;
#if CAPTURE
        var measured = Stopwatch.GetTimestamp();
#endif
        Arrange(canvas);
        if (ViewScale <= 0)
            return;
        var draw = args.DrawingSession;
        var ink = Colour(Ink.Foreground);
        var land = Colour(Land.Background);
        var edge = Colour(Edge.Background);
        var down = Colour(Download.Background);
        var up = Colour(Upload.Background);
        var contrast = _accessibility.HighContrast;
        var progress = _settings.AnimationsEnabled && !contrast ? AnimationProgress : 1;
        _rebuildUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 6 / 1000;
        _rebuildPending = false;
        // The view shows one turn of longitude around its centre. A shape near
        // a map edge also draws a turn away, so it continues on the other side.
        var view = ViewTransform;
        var scale = AxisScale;
        var west = Math.Max(ToMap(Vector2.Zero).X, _center.X - 180);
        var east = Math.Min(ToMap(new Vector2((float)_size.Width, 0)).X, _center.X + 180);
        // The margin covers a border's width.
        var visible = new Extent(
            new Vector2(west, ToMap(Vector2.Zero).Y),
            new Vector2(east, ToMap(new Vector2(0, (float)_size.Height)).Y)
        ).Grown(new Vector2(2) / scale);
        MoveWindow(visible);
        // Prioritise the selected routes before map drawing uses its rebuild budget.
        if (Locate(_origin) is { } source)
        {
            foreach (var selected in _destinations.Where(IsMarked))
            {
                if (
                    selected.Arc is null
                    || !(Reaches(selected, -1) || Reaches(selected, 0) || Reaches(selected, 1))
                )
                    continue;
                var paint = PaintOf(canvas, selected, source);
                paint.Line(2);
                paint.Core(ink);
            }
        }
        foreach (var destination in _destinations)
            _displayed[destination.Key] =
                destination.Start + (destination.Strength - destination.Start) * (float)progress;
        var bounds = new Rect(
            _offset.X + (west - _center.X) * scale.X,
            0,
            (east - west) * scale.X,
            _size.Height
        );
        using (draw.CreateLayer(1, bounds))
        {
            // A ring moved to within a turn of the centre can reach past 180°,
            // so each country tests every turn. It traces and realizes, and so
            // spends rebuild budget, only where it shows.
            for (var turn = -1; turn <= 1; turn++)
            {
                draw.Transform = Turned(turn);
                foreach (var (code, traced) in _land)
                {
                    if (!traced.Extent.Moved(360 * turn).Overlaps(visible))
                        continue;
                    var outline =
                        traced.Detail == _detail || !MayRebuild()
                            ? traced
                            : Retrace(canvas, code, traced, visible.Moved(-360 * turn));
                    var fill = land;
                    if (_countries.TryGetValue(code, out var group) && group.Estimated)
                    {
                        var strength =
                            group.Start
                            + (Strength(group.Down + group.Up) - group.Start) * (float)progress;
                        _displayed[(code, null, true)] = strength;
                        fill = Blend(land, down, group.Active > 0 ? .1 + .35 * strength : .08);
                    }
                    if (Filled(canvas, code, outline, turn, visible) is { } cached)
                        draw.DrawCachedGeometry(cached, fill);
                }
            }
            var borderWidth = BorderWidth(canvas);
            var border = contrast ? ink : Alpha(edge, .5 / borderWidth);
            List<Piece> stale = [];
            for (var turn = -1; turn <= 1; turn++)
            {
                var transform = Turned(turn);
                Vector2? applied = null;
                foreach (var outline in _land.Values)
                {
                    if (!outline.Extent.Moved(360 * turn).Overlaps(visible))
                        continue;
                    foreach (var piece in outline.Pieces)
                    {
                        if (!piece.Extent.Moved(360 * turn).Overlaps(visible))
                            continue;
                        if (Stroked(canvas, piece) is { } stroke)
                        {
                            if (!IsCurrent(stroke.Scale, BorderTolerance))
                                stale.Add(piece);
                            ApplyStroke(draw, stroke, transform, ref applied);
                            // A stroke from another scale draws that much wider
                            // or narrower; its ink changes inversely, so the
                            // border keeps its weight until it is rebuilt.
                            var widened = AxisScale / stroke.Scale;
                            draw.DrawCachedGeometry(
                                stroke.Geometry,
                                contrast ? ink : Alpha(border, 2 / (widened.X + widened.Y))
                            );
                        }
                        else
                        {
                            ApplyStroke(draw, null, transform, ref applied);
                            draw.DrawGeometry(piece.Line(canvas), border, borderWidth, FixedStroke);
                        }
                    }
                }
            }
            // A stale stroke still draws in place, so pieces with none take the
            // budget first and stale ones are replaced from what is left. This
            // frame already drew the old strokes, so the next one shows the
            // replacements.
            foreach (var piece in stale)
            {
                if (piece.Stroke is { } made && IsCurrent(made.Scale, BorderTolerance))
                    continue;
                if (!MayRebuild())
                    break;
                Restroke(canvas, piece);
                _rebuildPending = true;
            }
            draw.Transform = Matrix3x2.Identity;
            var arcs = _destinations.Where(destination => destination.Arc is not null).ToArray();
            // Routes lie within 180° of the origin, which is the map's centre.
            Span<int> turns = stackalloc int[3];
            var count = 0;
            for (var turn = -1; turn <= 1; turn++)
            {
                if (Shows(-180, 180, turn))
                    turns[count++] = turn;
            }
            turns = turns[..count];
            if (arcs.Length > 0 && Locate(_origin) is { } home)
            {
                var origin = Screen(home);
                var target = Math.Clamp(.8 * Math.Sqrt(5.0 / arcs.Length), .1, .8);
                var amount = Math.Clamp((arcs.Length - 8) / 12.0, 0, 1);
                var opacity = contrast ? 1 : 1 - (1 - target) * amount * amount * (3 - 2 * amount);
                if (_originMask is null || _originStrength != opacity)
                {
                    // Masks use only alpha; theme text colours can be translucent.
                    var opaque = Microsoft.UI.Colors.White;
                    _originMask?.Dispose();
                    _originMask = new CanvasRadialGradientBrush(
                        canvas,
                        [
                            new CanvasGradientStop { Position = 0, Color = Alpha(opaque, opacity) },
                            new CanvasGradientStop
                            {
                                Position = .25f,
                                Color = Alpha(opaque, opacity + (1 - opacity) * .15625),
                            },
                            new CanvasGradientStop
                            {
                                Position = .5f,
                                Color = Alpha(opaque, (1 + opacity) / 2),
                            },
                            new CanvasGradientStop
                            {
                                Position = .75f,
                                Color = Alpha(opaque, opacity + (1 - opacity) * .84375),
                            },
                            new CanvasGradientStop { Position = 1, Color = opaque },
                        ]
                    )
                    {
                        RadiusX = 40,
                        RadiusY = 40,
                    };
                    _originStrength = opacity;
                }
                _originMask.Center = origin;
                _originMask.Opacity = contrast ? 1 : .8f;
                using (draw.CreateLayer(_originMask))
                {
                    // The blurred glow is kept while its inputs stay the same.
                    // Animation changes it at every frame, and a frame that
                    // leaves a rebuild pending forgets it, because the next
                    // frame draws better strokes.
                    var inputs = new GlowInputs(view, _size, down, up, _arrangement);
                    if (!contrast && (progress < 1 || _glowInputs != inputs || _glowBlur is null))
                    {
                        ReleaseGlow();
                        _glowLines = new CanvasCommandList(canvas);
                        using (var halo = _glowLines.CreateDrawingSession())
                        {
                            foreach (var turn in turns)
                            {
                                var transform = Turned(turn);
                                Vector2? applied = null;
                                foreach (var destination in arcs)
                                {
                                    if (!Reaches(destination, turn))
                                        continue;
                                    var strength = _displayed[destination.Key];
                                    var colour =
                                        destination.Group.Down >= destination.Group.Up ? down : up;
                                    var paint = PaintOf(canvas, destination, home);
                                    var glowStroke = paint.Glow(Step(2 + 3 * strength, 2));
                                    var brush = paint.Halo(colour);
                                    brush.Center = Coordinates(destination.Point);
                                    brush.RadiusX = 30 / scale.X;
                                    brush.RadiusY = 30 / scale.Y;
                                    brush.Opacity = .5f * .35f * strength * strength;
                                    ApplyStroke(halo, glowStroke, transform, ref applied);
                                    if (glowStroke is { } stroke)
                                        halo.DrawCachedGeometry(stroke.Geometry, brush);
                                    else
                                        halo.DrawGeometry(
                                            destination.Arc,
                                            brush,
                                            2 + 3 * strength,
                                            FixedStroke
                                        );
                                }
                            }
                        }
                        _glowBlur = new GaussianBlurEffect
                        {
                            Source = new CropEffect
                            {
                                Source = _glowLines,
                                SourceRectangle = new Rect(
                                    -6,
                                    -6,
                                    canvas.Size.Width + 12,
                                    canvas.Size.Height + 12
                                ),
                            },
                            BlurAmount = 1.5f,
                            CacheOutput = true,
                        };
                        _glowInputs = progress < 1 ? null : inputs;
                    }
                    if (!contrast && _glowBlur is not null)
                        draw.DrawImage(_glowBlur);
                    foreach (var turn in turns)
                    {
                        var transform = Turned(turn);
                        Vector2? applied = null;
                        foreach (var destination in arcs)
                        {
                            if (!Reaches(destination, turn))
                                continue;
                            var strength = _displayed[destination.Key];
                            var paint = PaintOf(canvas, destination, home);
                            var marked = IsMarked(destination);
                            var line = paint.Line(marked ? 2 : Step(1 + strength, 4));
                            var width = marked ? 2 : 1 + strength;
                            if (contrast)
                            {
                                ApplyStroke(draw, line, transform, ref applied);
                                if (line is { } stroke)
                                    draw.DrawCachedGeometry(stroke.Geometry, ink);
                                else
                                    draw.DrawGeometry(destination.Arc, ink, width, FixedStroke);
                                continue;
                            }
                            var brush = paint.Core(
                                marked ? ink : destination.Group.Down >= destination.Group.Up ? down : up
                            );
                            brush.Opacity = .18f + .82f * strength;
                            ApplyStroke(draw, line, transform, ref applied);
                            if (line is { } core)
                                draw.DrawCachedGeometry(core.Geometry, brush);
                            else
                                draw.DrawGeometry(destination.Arc, brush, width, FixedStroke);
                        }
                    }
                    draw.Transform = Matrix3x2.Identity;
                }
                draw.FillCircle(origin, 3, ink);
            }
        }
        // Every mark is a tinted copy of one white dot, so one sprite batch
        // submits them all; selection rings then draw above every mark.
        List<(Vector2 Point, bool Wide)> rings = [];
        using (var batch = draw.CreateSpriteBatch())
        {
            foreach (var destination in _destinations)
            {
                var group = destination.Group;
                var point = Screen(destination.Point);
                // Marks and their selection ring stay within 16 DIPs of the point.
                if (
                    point.X < -16
                    || point.Y < -16
                    || point.X > _size.Width + 16
                    || point.Y > _size.Height + 16
                )
                    continue;
                var kinds =
                    (group.Seeds > 0 ? 1 : 0)
                    + (group.Leechers > 0 ? 1 : 0)
                    + (group.Unknown > 0 ? 1 : 0);
                var offset = -2.5f * (kinds - 1);
                Mark(group.Seeds, down);
                Mark(group.Leechers, up);
                Mark(group.Unknown, ink);
                if (IsMarked(destination) || ReferenceEquals(destination, _pointed))
                    rings.Add((point, kinds > 1));

                void Mark(int count, Color colour)
                {
                    if (count == 0)
                        return;
                    var radius = group.Active > 0 ? 2.4f : 2;
                    var dot = group.Active > 0
                        ? Dot(canvas, ref _activeDot, radius)
                        : Dot(canvas, ref _dot, radius);
                    var tint = contrast ? ink : colour;
                    batch.Draw(
                        dot,
                        point + new Vector2(offset - radius - 1, -radius - 1),
                        new Vector4(tint.R, tint.G, tint.B, tint.A) / 255
                    );
                    offset += 5;
                }
            }
        }
        foreach (var (point, wide) in rings)
            draw.DrawCachedGeometry(
                wide ? Ring(canvas, ref _wideRing, 8) : Ring(canvas, ref _ring, 5),
                point,
                ink
            );
        if (_rebuildPending)
        {
            _glowInputs = null;
            canvas.Invalidate();
        }
#if CAPTURE
        FrameMeasured?.Invoke(Stopwatch.GetElapsedTime(measured).TotalMilliseconds);
#endif

        bool Shows(float from, float to, int turn) =>
            from + 360 * turn < east && to + 360 * turn > west;

        Matrix3x2 Turned(int turn) => Matrix3x2.CreateTranslation(360 * turn, 0) * view;

        // A route lies within its path's bounds; the margin covers its glow.
        bool Reaches(Destination destination, int turn) =>
            destination.Reach.Grown(new Vector2(30) / scale).Moved(360 * turn).Overlaps(visible);
    }

    // Fills are clipped to a window three views wide, so a cut edge is always
    // a view off screen. The window moves once the view comes within half a
    // view of its edge, or the detail changes, so a drag rebuilds fills once
    // per half view and the window never outgrows the view. A view that wraps
    // a turn keeps the window a turn away, so its fills stay current.
    private void MoveWindow(Extent visible)
    {
        var span = visible.High - visible.Low;
        var needed = visible.Grown(span / 2);
        if (_windowDetail == _detail)
        {
            for (var turn = -1; turn <= 1; turn++)
            {
                if (_window.Moved(360 * turn).Contains(needed))
                {
                    _window = _window.Moved(360 * turn);
                    return;
                }
            }
        }
        _window = visible.Grown(span);
        _windowDetail = _detail;
    }

    // The old outline draws until this replaces it in one budgeted step, which
    // also makes the paths its visible pieces draw, so a detail change never
    // makes them all in one unbudgeted frame. View is in the outline's own
    // coordinates.
    private Outline Retrace(CanvasControl canvas, string code, Outline outline, Extent view)
    {
        outline.Dispose();
        var traced = Trace(_world[code]);
        foreach (var piece in traced.Pieces)
        {
            if (piece.Extent.Overlaps(view))
                piece.Line(canvas);
        }
        // Replacing a value keeps Draw's enumeration of _land valid.
        _land[code] = traced;
        return traced;
    }

    // A fill keeps drawing its last realization, at another detail or window,
    // until the budget replaces it; a country with none yet shows no fill,
    // because a plain fill of a large country takes far longer than a frame.
    // A clipped fill whose window no longer holds the country's visible part
    // would show its cut edge, as after a fast drag, so it is replaced even
    // past the budget.
    private CanvasCachedGeometry? Filled(
        CanvasControl canvas,
        string code,
        Outline outline,
        int turn,
        Extent visible
    )
    {
        // The window in the outline's own coordinates, for the turn it shows in.
        var window = _window.Moved(-360 * turn);
        var turns = 0;
        for (var other = -1; other <= 1; other++)
        {
            if (outline.Extent.Moved(360 * other).Overlaps(_window))
                turns++;
        }
        // A country the window holds whole, or meets in two turns, stays whole.
        Extent? clip = turns > 1 || window.Contains(outline.Extent) ? null : window;
        var found = _fills.TryGetValue(code, out var fill);
        var covers =
            !found
            || fill.Window is not { } made
            || made.Contains(visible.Moved(-360 * turn).Within(outline.Extent));
        if (
            (found && fill.Detail == outline.Detail && (fill.Window is null || fill.Window == clip))
            || (covers && !MayRebuild())
        )
            return found ? fill.Geometry : null;
        using var path = new CanvasPathBuilder(canvas);
        path.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Alternate);
        var figures = 0;
        foreach (var ring in outline.Rings)
        {
            var points = clip is { } bounds ? Crop(ring, bounds) : ring;
            if (points.Count < 3)
                continue;
            AddFigure(path, points, CanvasFigureLoop.Closed);
            figures++;
        }
        fill.Geometry?.Dispose();
        CanvasCachedGeometry? geometry = null;
        if (figures > 0)
        {
            using var shape = CanvasGeometry.CreatePath(path);
            // Country fills contain only lines; changing the view cannot expose
            // curve-flattening error, so only a detail or window change replaces one.
            geometry = CanvasCachedGeometry.CreateFill(
                shape,
                CanvasGeometry.ComputeFlatteningTolerance(canvas.Dpi, ViewScale)
            );
        }
        _fills[code] = new Fill(geometry, outline.Detail, clip);
        return geometry;
    }

    // A piece with no stroke draws plainly, which Direct2D tessellates on the
    // CPU at every frame, so it gets one first. A piece keeps drawing its last
    // stroke, made at another scale, until Draw replaces it.
    private Stroke? Stroked(CanvasControl canvas, Piece piece)
    {
        if (piece.Stroke is null && MayRebuild())
            Restroke(canvas, piece);
        return piece.Stroke;
    }

    private void Restroke(CanvasControl canvas, Piece piece)
    {
        piece.Stroke?.Geometry.Dispose();
        piece.Stroke = CreateStroke(canvas, piece.Line(canvas), BorderWidth(canvas));
    }

    // Direct2D rasterizes a plain geometry on the CPU at every draw, at a cost
    // that grows with its size on screen, so zoomed arcs took most of a second.
    // A realization is tessellated once for a scale and then drawn by the GPU,
    // but making one is slow, and a scale change makes one for every visible
    // border piece and arc. Each frame therefore rebuilds only until
    // _rebuildUntil. Within 5% of its scale a realization looks the same; one
    // further off, or made at another outline detail, still draws, a little too
    // wide, narrow or coarse, until its turn. A country's outline is traced
    // again at the current detail only when it shows, so a zoom spends the same
    // budget on tracing, and only on countries that show.
    // A border carries half a DIP of ink. Below one physical pixel its
    // antialiasing breaks into uneven steps, so it widens to a pixel and Draw
    // fades its colour to keep the same ink.
    private static float BorderWidth(CanvasControl canvas) => Math.Max(.5f, 96 / canvas.Dpi);

    // A border's ink follows its width error, so it keeps its weight over a
    // larger change than a route, and a pan toward a pole rebuilds it half as
    // often.
    private const float BorderTolerance = .1f;

    private bool IsCurrent(Vector2 scale, float tolerance = .05f)
    {
        var change = AxisScale / scale - Vector2.One;
        return Math.Abs(change.X) <= tolerance && Math.Abs(change.Y) <= tolerance;
    }

    private void ReleaseGlow()
    {
        (_glowBlur?.Source as CropEffect)?.Dispose();
        _glowBlur?.Dispose();
        _glowBlur = null;
        _glowLines?.Dispose();
        _glowLines = null;
        _glowInputs = null;
    }

    /// <summary>What the route glow depends on besides the routes' animation.</summary>
    private readonly record struct GlowInputs(
        Matrix3x2 View,
        Size Size,
        Color Down,
        Color Up,
        int Arrangement
    );

    private bool MayRebuild()
    {
        if (Stopwatch.GetTimestamp() < _rebuildUntil)
            return true;
        _rebuildPending = true;
        return false;
    }

    private readonly record struct Stroke(CanvasCachedGeometry Geometry, Vector2 Scale);

    private Stroke CreateStroke(CanvasControl canvas, CanvasGeometry source, float width)
    {
        // Widen after applying both axis scales, so the cached pen stays round.
        var scale = AxisScale;
        using var geometry = source.Transform(Matrix3x2.CreateScale(scale));
        return new Stroke(
            CanvasCachedGeometry.CreateStroke(
                geometry,
                width,
                Rounded,
                CanvasGeometry.ComputeFlatteningTolerance(canvas.Dpi, 1)
            ),
            scale
        );
    }

    private static void ApplyStroke(
        CanvasDrawingSession draw,
        Stroke? stroke,
        Matrix3x2 transform,
        ref Vector2? applied
    )
    {
        var scale = stroke?.Scale ?? Vector2.One;
        if (applied == scale)
            return;
        draw.Transform = Matrix3x2.CreateScale(Vector2.One / scale) * transform;
        applied = scale;
    }

    // A mark is this white dot, with a DIP of margin, multiplied by its theme
    // colour in a sprite batch.
    private static CanvasRenderTarget Dot(CanvasControl canvas, ref CanvasRenderTarget? dot, float radius)
    {
        if (dot is null)
        {
            var size = 2 * (radius + 1);
            dot = new CanvasRenderTarget(canvas, size, size, canvas.Dpi);
            using var session = dot.CreateDrawingSession();
            session.Clear(Microsoft.UI.Colors.Transparent);
            session.FillCircle(radius + 1, radius + 1, radius, Microsoft.UI.Colors.White);
        }
        return dot;
    }

    // Direct2D outlines a plain circle on the CPU at every draw. A selection
    // ring has the same shape at every place, so one realization per radius
    // draws them all.

    private static CanvasCachedGeometry Ring(
        CanvasControl canvas,
        ref CanvasCachedGeometry? ring,
        float radius
    )
    {
        if (ring is null)
        {
            using var circle = CanvasGeometry.CreateCircle(canvas, Vector2.Zero, radius);
            ring = CanvasCachedGeometry.CreateStroke(
                circle,
                1.2f,
                Rounded,
                CanvasGeometry.ComputeFlatteningTolerance(canvas.Dpi, 1)
            );
        }
        return ring;
    }

    // Widths move in steps, so animated activity rebuilds an arc's
    // realization only when it reaches the next step.
    private static float Step(float width, float steps) => MathF.Round(width * steps) / steps;

    private void OnPointer(object sender, PointerRoutedEventArgs args)
    {
        if (_canvas is null || MoveDrag(args) || ViewScale <= 0)
            return;
        var position = args.GetCurrentPoint(_canvas).Position;
        var point = new Vector2((float)position.X, (float)position.Y);
        Destination? nearest = null;
        if (point.X >= 0 && point.Y >= 0 && point.X <= _size.Width && point.Y <= _size.Height)
        {
            var distance = 12f;
            foreach (var destination in _destinations)
            {
                var current = Vector2.Distance(point, Screen(destination.Point));
                if (current < distance)
                {
                    nearest = destination;
                    distance = current;
                }
            }
            // Routes are traced once, within 180° of the origin.
            var mapped = ToMap(point);
            mapped.X = Wrap(mapped.X, 0);
            if (nearest is null)
            {
                var scale = AxisScale;
                var transform = Matrix3x2.CreateScale(scale);
                foreach (var destination in _destinations)
                {
                    if (destination.Arc is not { } arc)
                        continue;
                    using var geometry = arc.Transform(transform);
                    if (!geometry.StrokeContainsPoint(mapped * scale, 8))
                        continue;
                    nearest = destination;
                    break;
                }
            }
        }
        if (ReferenceEquals(_pointed, nearest))
            return;
        _pointed = nearest;
        Describe();
        _canvas.Invalidate();
    }

    private void Describe()
    {
        if (_text is not { } text)
            return;
        var destination = Inspected;
        var group = destination?.Group;
        string value;
        if (group is null)
        {
            value = text.Get(
                "peer_map",
                Locate(_origin) is null ? "origin_unknown" : "approximate"
            );
        }
        else
        {
            value = text.Format(
                "peer_map",
                "detail",
                group.Name,
                group.Count,
                group.Active,
                text.Format("units", "rate", text.Bytes(group.Down)),
                text.Format("units", "rate", text.Bytes(group.Up)),
                group.Seeds,
                group.Leechers,
                group.Unknown
            );
            if (destination?.Area is { } area)
            {
                value +=
                    " · "
                    + text.Format(
                        "peer_map",
                        "estimated",
                        area,
                        text.Format("units", "rate", text.Bytes(group.Down * destination.Share)),
                        text.Format("units", "rate", text.Bytes(group.Up * destination.Share))
                    );
            }
            else
            {
                var precision = group.Estimated ? "country_only" : "location_approximate";
                value += " · " + text.Get("peer_map", precision);
            }
        }
        ToolTipService.SetToolTip(
            this,
            group is null || _drag is { IsActive: true } ? null : value
        );
        AutomationProperties.SetHelpText(this, value + " " + text.Get("peer_map", "keyboard"));
    }

    /// <summary>
    /// A country's points in map coordinates, traced at one outline detail.
    /// Extent bounds its rings and pieces.
    /// </summary>
    private sealed record Outline(Vector2[][] Rings, Piece[] Pieces, Extent Extent, float Detail)
        : IDisposable
    {
        public void Dispose()
        {
            foreach (var piece in Pieces)
                piece.Dispose();
        }
    }

    /// <summary>Lines of one country's border, short enough together to stroke within a frame's rebuild budget.</summary>
    private sealed class Piece(Vector2[][] lines) : IDisposable
    {
        // A stroke takes time in proportion to its points to build.
        internal const int Length = 512;

        private CanvasGeometry? _line;

        internal Extent Extent { get; } =
            lines.Aggregate(Extent.Empty, (extent, line) => extent.With(Extent.Of(line)));

        /// <summary>The last stroke built, possibly at another scale; null before the first.</summary>
        internal Stroke? Stroke { get; set; }

        internal CanvasGeometry Line(ICanvasResourceCreator creator)
        {
            if (_line is null)
            {
                using var path = new CanvasPathBuilder(creator);
                foreach (var line in lines)
                    AddFigure(path, line, CanvasFigureLoop.Open);
                _line = CanvasGeometry.CreatePath(path);
            }
            return _line;
        }

        public void Dispose()
        {
            _line?.Dispose();
            Stroke?.Geometry.Dispose();
        }
    }

    /// <summary>
    /// A country's realized fill, null when nothing of it lies in its window,
    /// with the detail it was traced at and the window it was clipped to, null
    /// when it is whole.
    /// </summary>
    private readonly record struct Fill(CanvasCachedGeometry? Geometry, float Detail, Extent? Window);

    /// <summary>An axis-aligned box in map coordinates.</summary>
    private readonly record struct Extent(Vector2 Low, Vector2 High)
    {
        internal static Extent Empty => new(new Vector2(float.MaxValue), new Vector2(float.MinValue));

        internal static Extent Of(ReadOnlySpan<Vector2> points)
        {
            var extent = Empty;
            foreach (var point in points)
                extent = new(Vector2.Min(extent.Low, point), Vector2.Max(extent.High, point));
            return extent;
        }

        internal static Extent Of(Rect bounds) =>
            new(
                new Vector2((float)bounds.Left, (float)bounds.Top),
                new Vector2((float)bounds.Right, (float)bounds.Bottom)
            );

        internal Extent With(Extent other) =>
            new(Vector2.Min(Low, other.Low), Vector2.Max(High, other.High));

        internal Extent Moved(float longitude) =>
            new(Low + new Vector2(longitude, 0), High + new Vector2(longitude, 0));

        internal Extent Grown(Vector2 margin) => new(Low - margin, High + margin);

        /// <summary>The part of this box inside another; callers pass boxes that overlap.</summary>
        internal Extent Within(Extent other) =>
            new(Vector2.Max(Low, other.Low), Vector2.Min(High, other.High));

        internal bool Overlaps(Extent other) =>
            High.X >= other.Low.X
            && Low.X <= other.High.X
            && High.Y >= other.Low.Y
            && Low.Y <= other.High.Y;

        internal bool Contains(Extent other) =>
            other.Low.X >= Low.X
            && other.High.X <= High.X
            && other.Low.Y >= Low.Y
            && other.High.Y <= High.Y;
    }

    // Only the inspected place shows its name, so a group formats it when first read.
    private sealed class Group(
        PeerPlace place,
        Func<string> name,
        GeoPoint point,
        bool estimated = false
    )
    {
        internal PeerPlace Place { get; } = place;
        internal string Code => Place.Code;
        internal string Name => field ??= name();
        internal GeoPoint Point { get; } = point;
        internal bool Estimated { get; private set; } = estimated;
        internal float Start { get; set; }
        internal int Count { get; private set; }
        internal int Seeds { get; private set; }
        internal int Leechers { get; private set; }
        internal int Unknown => Count - Seeds - Leechers;
        internal int Active { get; private set; }
        internal double Down { get; private set; }
        internal double Up { get; private set; }

        /// <summary>
        /// The regions its estimated distribution places peers in, most populous
        /// first; empty when the group has no estimated distribution.
        /// </summary>
        internal Geography.Area[] Regions { get; set; } = [];

        internal void Add(Observation observation)
        {
            Count++;
            if (observation.Role == PeerRole.Seed)
                Seeds++;
            else if (observation.Role == PeerRole.Leecher)
                Leechers++;
            Estimated |= observation.Location?.Point is null;
            if (observation.Down + observation.Up > 0)
                Active++;
            Down += observation.Down;
            Up += observation.Up;
        }
    }

    private sealed record Destination(Group Group, GeoPoint Point, double Share, string? Area)
    {
        internal (string Code, GeoPoint? Point, bool Estimated) Key =>
            (Group.Code, Point, Group.Estimated);

        /// <summary>
        /// The position in its country's estimated distribution, most populous
        /// first; 0 for a destination at supplied coordinates.
        /// </summary>
        internal int Rank { get; init; }
        internal float Start { get; set; }
        internal float Strength { get; init; }
        internal CanvasGeometry? Arc
        {
            get => _arc;
            set
            {
                _arc = value;
                Reach = value is null ? default : Extent.Of(value.ComputeBounds());
            }
        }

        private CanvasGeometry? _arc;

        /// <summary>The bounds of <see cref="Arc"/> in map coordinates.</summary>
        internal Extent Reach { get; private set; }

        /// <summary>The brushes for <see cref="Arc"/>; it shares the arc's lifetime.</summary>
        internal Paint? Paint { get; set; }

        internal void Release()
        {
            Arc?.Dispose();
            Paint?.Dispose();
        }
    }

    private Paint PaintOf(CanvasControl canvas, Destination destination, GeoPoint origin) =>
        destination.Paint ??= new Paint(
            this,
            canvas,
            destination.Arc!,
            Coordinates(origin),
            Coordinates(destination.Point)
        );

    // Gradient brushes and realizations are expensive to create, and each one
    // adds native memory pressure that can force a full garbage collection.
    // Each arc therefore keeps them, and a frame only sets their size and
    // opacity. They follow the map's scale under its rebuild limit. The arc
    // stays owned by its destination; start and end are geographic coordinates.
    private sealed class Paint(
        PeerMap map,
        CanvasControl canvas,
        CanvasGeometry arc,
        Vector2 start,
        Vector2 end
    ) : IDisposable
    {
        private CanvasRadialGradientBrush? _halo;
        private CanvasRadialGradientBrush? _core;
        private Color _haloColour;
        private Color _coreColour;
        private Vector2 _coreScale;
        private Matrix3x2 _haloTransform;
        private Matrix3x2 _coreTransform;
        private Stroke? _glow;
        private Stroke? _line;
        private float _glowWidth;
        private float _lineWidth;

        /// <summary>The halo's stroke for a width in DIPs on screen, or null before the first one is made.</summary>
        internal Stroke? Glow(float width) => Realize(ref _glow, ref _glowWidth, width);

        /// <summary>The core's stroke for a width in DIPs on screen, or null before the first one is made.</summary>
        internal Stroke? Line(float width) => Realize(ref _line, ref _lineWidth, width);

        private Stroke? Realize(
            ref Stroke? stroke,
            ref float made,
            float width
        )
        {
            if (
                (stroke is { } cached && made == width && map.IsCurrent(cached.Scale))
                || !map.MayRebuild()
            )
                return stroke;
            stroke?.Geometry.Dispose();
            stroke = map.CreateStroke(canvas, arc, width);
            made = width;
            return stroke;
        }

        internal CanvasRadialGradientBrush Halo(Color colour)
        {
            if (_halo is null || _haloColour != colour)
            {
                _halo?.Dispose();
                _halo = CreateLanding(canvas, colour);
                _haloColour = colour;
                _haloTransform = Matrix3x2.Identity;
            }
            var transform = Matrix3x2.CreateScale(_glow?.Scale ?? Vector2.One);
            if (_haloTransform != transform)
            {
                _halo.Transform = transform;
                _haloTransform = transform;
            }
            return _halo;
        }

        // One radial brush around the destination carries both the brightness
        // along the route and the landing fade, so an arc needs no layer of its
        // own. The fade is measured on screen, so a scale change rebuilds it.
        // A new colour, as when a selection marks many routes, waits for the
        // rebuild budget like a new scale.
        internal CanvasRadialGradientBrush Core(Color colour)
        {
            if (
                _core is null
                || ((_coreColour != colour || !map.IsCurrent(_coreScale)) && map.MayRebuild())
            )
            {
                var scale = map.AxisScale;
                var length = Math.Max(1, ((end - start) * scale).Length());
                _core?.Dispose();
                var landed = 30 / length;
                var stops = new[]
                {
                    0,
                    landed / 4,
                    landed / 2,
                    landed * 3 / 4,
                    landed,
                    .2f,
                    .65f,
                    1,
                }
                    .Where(position => position <= 1)
                    .Order()
                    .Select(position => new CanvasGradientStop
                    {
                        Position = position,
                        Color = Alpha(
                            colour,
                            Route(1 - position) * Landing(position * length)
                        ),
                    })
                    .ToArray();
                _core = new CanvasRadialGradientBrush(canvas, stops)
                {
                    Center = end,
                    RadiusX = length / scale.X,
                    RadiusY = length / scale.Y,
                };
                _coreColour = colour;
                _coreScale = scale;
                _coreTransform = Matrix3x2.Identity;
            }
            var transform = Matrix3x2.CreateScale(_line?.Scale ?? Vector2.One);
            if (_coreTransform != transform)
            {
                _core.Transform = transform;
                _coreTransform = transform;
            }
            return _core;
        }

        /// <summary>Brightness at a fraction of the way from origin to destination.</summary>
        private static double Route(double along) =>
            along < .35 ? .55 + .45 * along / .35
            : along < .8 ? 1 - .1 * (along - .35) / .45
            : .9 - .25 * (along - .8) / .2;

        /// <summary>Strength at a screen distance in DIPs from the destination.</summary>
        private static double Landing(double distance) => .3 + .7 * Math.Min(1, distance / 30);

        public void Dispose()
        {
            _halo?.Dispose();
            _core?.Dispose();
            _glow?.Geometry.Dispose();
            _line?.Geometry.Dispose();
        }
    }

    // Peer instances update in place; retain the last observation to avoid
    // running an animation when an idle snapshot has not changed.
    private bool IsShowing(IReadOnlyList<Peer> peers)
    {
        if (peers.Count != _observations.Length)
            return false;
        for (var index = 0; index < peers.Count; index++)
        {
            if (!_observations[index].Matches(peers[index]))
                return false;
        }
        return true;
    }

    private sealed record Observation(Peer Peer, double Down, double Up, Location? Location)
    {
        internal PeerRole Role { get; } = Peer.Role;

        /// <summary>Whether the peer still has the values this observation recorded.</summary>
        internal bool Matches(Peer peer) =>
            ReferenceEquals(Peer, peer)
            && Down == peer.DownloadRate
            && Up == peer.UploadRate
            && Equals(Location, peer.Location)
            && Role == peer.Role;
    }
}
