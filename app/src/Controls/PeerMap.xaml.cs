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
using Syno.TinyTorrent.Models;
using Syno.TinyTorrent.Services;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Syno.TinyTorrent.Controls;

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
    private double _originStrength;
    private static readonly CanvasStrokeStyle Plain = new();
    private readonly Dictionary<
        string,
        (CanvasCachedGeometry Fill, CanvasCachedGeometry Border, float Scale)
    > _realized = [];
    private long _rebuildUntil;
    private bool _rebuildPending;
    private Strings? _text;
    private string _language = string.Empty;
    private (string Code, GeoPoint? Point, bool Estimated)? _selected;
    private IReadOnlyList<Models.Peer> _selectedPeers = [];
    private Dictionary<PeerPlace, int> _marks = [];
    private Destination? _pointed;
    private bool _dirty = true;
    private bool _cities;
    private float _detail;

    // world.bin leaves out points below this level, so a finer level traces
    // the same outlines.
    private const float FinestDetail = 1f / 128;
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
        IReadOnlyList<Models.Peer>? peers,
        Location? origin,
        Strings text,
        bool cities = true
    )
    {
        var observations = (peers ?? [])
            .Select(peer => new Observation(
                peer,
                peer.DownloadRate,
                peer.UploadRate,
                peer.Location
            ))
            .ToArray();
        if (
            _observations.SequenceEqual(observations)
            && _origin == origin
            && _language == text.Language
            && _cities == cities
        )
            return;
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
    internal void Select(IReadOnlyList<Models.Peer> peers)
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
        foreach (var (fill, border, _) in _realized.Values)
        {
            fill.Dispose();
            border.Dispose();
        }
        _realized.Clear();
        _arcOrigin = null;
        _pointed = null;
    }

    private Vector2 Project(GeoPoint point) =>
        _offset + Coordinates(point) * _scale;

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
        (float)Math.Max(0, Math.Min((size.Width - 24) / 360, (size.Height - 20) / 180));

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
            SetView(_zoom, _center);
            _dirty = true;
        }
        // Outline points closer than half a DIP to their simplified line are
        // invisible. The level moves in powers of two, so zooming traces the
        // land again only once per doubling, and stops at FinestDetail.
        // Realized traces each country again when it next realizes it.
        if (ViewScale > 0)
            _detail = Math.Max(
                MathF.Pow(2, MathF.Round(MathF.Log2(.5f / ViewScale))),
                FinestDetail
            );
        if (_land.Count == 0)
        {
            foreach (var country in _world.Values.Where(country => country.Rings.Length > 0))
                _land[country.Code] = Trace(canvas, country);
        }
        if (!_dirty)
            return;
        _dirty = false;
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
                group = new Group(
                    PeerPlace.Of(location, false),
                    location.CountryName(_text!.Language),
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
                destination = new Group(place, observation.Peer.LocationText, point);
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
    // a ring left with fewer than three is too small to see.
    private Outline Trace(CanvasControl canvas, Geography.Country country)
    {
        var cuts = false;
        foreach (var vertices in country.Rings)
            cuts |= HasCut(vertices);
        using var fill = new CanvasPathBuilder(canvas);
        using var border = cuts ? new CanvasPathBuilder(canvas) : null;
        fill.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Alternate);
        var west = float.MaxValue;
        var east = float.MinValue;
        List<Vector2> ring = [];
        foreach (var vertices in country.Rings)
        {
            ring.Clear();
            var low = float.MaxValue;
            var high = float.MinValue;
            foreach (var vertex in vertices)
            {
                if (vertex.Level < _detail)
                    continue;
                ring.Add(vertex.Position);
                low = Math.Min(low, vertex.Position.X);
                high = Math.Max(high, vertex.Position.X);
            }
            if (ring.Count < 3)
                continue;
            var shift = 360 * MathF.Round(-((low + high) / 2 - _meridian) / 360) - _meridian;
            west = Math.Min(west, low + shift);
            east = Math.Max(east, high + shift);
            AddRing(fill, ring, shift);
            if (border is null)
                continue;
            // Natural Earth cuts countries at 180°; the cut is not a border.
            var cut = -1;
            for (var index = 0; index < ring.Count && cut < 0; index++)
            {
                if (IsCut(ring[index], ring[(index + 1) % ring.Count]))
                    cut = index;
            }
            if (cut < 0)
            {
                AddRing(border, ring, shift);
                continue;
            }
            var drawing = false;
            for (var step = 1; step <= ring.Count; step++)
            {
                var from = (cut + step) % ring.Count;
                var to = (from + 1) % ring.Count;
                if (IsCut(ring[from], ring[to]))
                {
                    if (drawing)
                        border.EndFigure(CanvasFigureLoop.Open);
                    drawing = false;
                    continue;
                }
                if (!drawing)
                {
                    border.BeginFigure(Map(ring[from], shift));
                    drawing = true;
                }
                border.AddLine(Map(ring[to], shift));
            }
            if (drawing)
                border.EndFigure(CanvasFigureLoop.Open);
        }
        var shape = CanvasGeometry.CreatePath(fill);
        return new Outline(
            shape,
            border is null ? shape : CanvasGeometry.CreatePath(border),
            west,
            east,
            _detail
        );

        static void AddRing(CanvasPathBuilder path, List<Vector2> ring, float shift)
        {
            path.BeginFigure(Map(ring[0], shift));
            for (var index = 1; index < ring.Count; index++)
                path.AddLine(Map(ring[index], shift));
            path.EndFigure(CanvasFigureLoop.Closed);
        }

        static Vector2 Map(Vector2 point, float shift) => new(point.X + shift, -point.Y);

        static bool IsCut(Vector2 from, Vector2 to) =>
            MathF.Abs(from.X) == 180 && to.X == from.X;

        // Points on a cut have an infinite level, so every detail keeps them.
        static bool HasCut(Geography.Vertex[] vertices)
        {
            for (var index = 0; index < vertices.Length; index++)
            {
                var next = vertices[(index + 1) % vertices.Length];
                if (IsCut(vertices[index].Position, next.Position))
                    return true;
            }
            return false;
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
        // Prioritise the selected strokes before map drawing uses its rebuild budget.
        if (Locate(_origin) is { } source)
        {
            foreach (var selected in _destinations.Where(IsMarked))
            {
                if (selected.Arc is not null)
                    PaintOf(canvas, selected, source).Line(2);
            }
        }
        foreach (var destination in _destinations)
            _displayed[destination.Key] =
                destination.Start + (destination.Strength - destination.Start) * (float)progress;
        // The view shows one turn of longitude around its centre. A shape near
        // a map edge also draws a turn away, so it continues on the other side.
        var view = ViewTransform;
        var west = Math.Max(ToMap(Vector2.Zero).X, _center.X - 180);
        var east = Math.Min(ToMap(new Vector2((float)_size.Width, 0)).X, _center.X + 180);
        var bounds = new Rect(
            _offset.X + (west - _center.X) * ViewScale,
            0,
            (east - west) * ViewScale,
            _size.Height
        );
        using (draw.CreateLayer(1, bounds))
        {
            // A ring moved to within a turn of the centre can reach past 180°,
            // so each country tests every turn. It realizes, and so spends
            // rebuild budget, only where it shows.
            for (var turn = -1; turn <= 1; turn++)
            {
                draw.Transform = Turned(turn);
                foreach (var (code, outline) in _land)
                {
                    if (!Shows(outline.West, outline.East, turn))
                        continue;
                    var fill = land;
                    if (_countries.TryGetValue(code, out var group) && group.Estimated)
                    {
                        var strength =
                            group.Start
                            + (Strength(group.Down + group.Up) - group.Start) * (float)progress;
                        _displayed[(code, null, true)] = strength;
                        fill = Blend(land, down, group.Active > 0 ? .1 + .35 * strength : .08);
                    }
                    if (Realized(canvas, code, outline) is { } cached)
                        draw.DrawCachedGeometry(cached.Fill, fill);
                    else
                        draw.FillGeometry(outline.Fill, fill);
                }
            }
            for (var turn = -1; turn <= 1; turn++)
            {
                draw.Transform = Turned(turn);
                foreach (var (code, outline) in _land)
                {
                    if (!Shows(outline.West, outline.East, turn))
                        continue;
                    if (_realized.TryGetValue(code, out var shape))
                        draw.DrawCachedGeometry(shape.Border, contrast ? ink : edge);
                    else
                        draw.DrawGeometry(outline.Border, contrast ? ink : edge, .5f / ViewScale);
                }
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
                    if (!contrast)
                    {
                        using var glow = new CanvasCommandList(canvas);
                        using (var halo = glow.CreateDrawingSession())
                        {
                            foreach (var turn in turns)
                            {
                                halo.Transform = Turned(turn);
                                foreach (var destination in arcs)
                                {
                                    if (turns.Length > 1 && !Reaches(destination, turn))
                                        continue;
                                    var strength = _displayed[destination.Key];
                                    var colour =
                                        destination.Group.Down >= destination.Group.Up ? down : up;
                                    var paint = PaintOf(canvas, destination, home);
                                    var brush = paint.Halo(colour);
                                    brush.Center = Coordinates(destination.Point);
                                    brush.RadiusX = brush.RadiusY = 30 / ViewScale;
                                    brush.Opacity = .5f * .35f * strength * strength;
                                    if (paint.Glow(Step(2 + 3 * strength, 2)) is { } stroke)
                                        halo.DrawCachedGeometry(stroke, brush);
                                    else
                                        halo.DrawGeometry(
                                            destination.Arc,
                                            brush,
                                            (2 + 3 * strength) / ViewScale
                                        );
                                }
                            }
                        }
                        using var crop = new CropEffect
                        {
                            Source = glow,
                            SourceRectangle = new Rect(
                                -6,
                                -6,
                                canvas.Size.Width + 12,
                                canvas.Size.Height + 12
                            ),
                        };
                        using var blur = new GaussianBlurEffect
                        {
                            Source = crop,
                            BlurAmount = 1.5f,
                        };
                        draw.DrawImage(blur);
                    }
                    foreach (var turn in turns)
                    {
                        draw.Transform = Turned(turn);
                        foreach (var destination in arcs)
                        {
                            if (turns.Length > 1 && !Reaches(destination, turn))
                                continue;
                            var strength = _displayed[destination.Key];
                            var paint = PaintOf(canvas, destination, home);
                            var marked = IsMarked(destination);
                            var line = paint.Line(marked ? 2 : Step(1 + strength, 4));
                            var width = (marked ? 2 : 1 + strength) / ViewScale;
                            if (contrast)
                            {
                                if (line is not null)
                                    draw.DrawCachedGeometry(line, ink);
                                else
                                    draw.DrawGeometry(destination.Arc, ink, width);
                                continue;
                            }
                            var brush = paint.Core(
                                marked ? ink : destination.Group.Down >= destination.Group.Up ? down : up
                            );
                            brush.Opacity = .18f + .82f * strength;
                            if (line is not null)
                                draw.DrawCachedGeometry(line, brush);
                            else
                                draw.DrawGeometry(destination.Arc, brush, width);
                        }
                    }
                    draw.Transform = Matrix3x2.Identity;
                }
                draw.FillCircle(origin, 3, ink);
            }
        }
        foreach (var destination in _destinations)
        {
            var group = destination.Group;
            var point = Screen(destination.Point);
            var kinds =
                (group.Seeds > 0 ? 1 : 0)
                + (group.Leechers > 0 ? 1 : 0)
                + (group.Unknown > 0 ? 1 : 0);
            var offset = -2.5f * (kinds - 1);
            Mark(group.Seeds, down);
            Mark(group.Leechers, up);
            Mark(group.Unknown, ink);
            if (IsMarked(destination) || ReferenceEquals(destination, _pointed))
                draw.DrawCircle(point, kinds > 1 ? 8 : 5, ink, 1.2f);

            void Mark(int count, Color colour)
            {
                if (count == 0)
                    return;
                var position = point + new Vector2(offset, 0);
                draw.FillCircle(
                    position,
                    group.Active > 0 ? 2.4f : 2,
                    contrast ? ink : colour
                );
                offset += 5;
            }
        }
        if (_rebuildPending)
            canvas.Invalidate();
#if CAPTURE
        FrameMeasured?.Invoke(Stopwatch.GetElapsedTime(measured).TotalMilliseconds);
#endif

        bool Shows(float from, float to, int turn) =>
            from + 360 * turn < east && to + 360 * turn > west;

        Matrix3x2 Turned(int turn) => Matrix3x2.CreateTranslation(360 * turn, 0) * view;

        // A route runs from the origin, at 0, to its destination without
        // passing either end's longitude, so a copy a turn away shows only near
        // the map's edge. The margin covers its glow.
        bool Reaches(Destination destination, int turn)
        {
            var end = Coordinates(destination.Point).X;
            var margin = 30 / ViewScale;
            return Shows(Math.Min(0, end) - margin, Math.Max(0, end) + margin, turn);
        }
    }

    // Direct2D rasterizes a plain geometry on the CPU at every draw, at a cost
    // that grows with its size on screen, so zoomed arcs took most of a second.
    // A realization is tessellated once for a scale and then drawn by the GPU,
    // but making one is slow, and a scale change makes one for every country and
    // arc. Each frame therefore rebuilds only until _rebuildUntil. Within 5% of
    // its scale a realization looks the same; one further off, or made at
    // another outline detail, still draws, a little too wide, narrow or coarse,
    // until its turn. A shape with none yet draws plainly. A country's outline
    // is traced again at the current detail only when it realizes, so a zoom
    // spends the same budget on tracing, and only on countries that show.
    private bool IsCurrent(float scale) => Math.Abs(ViewScale / scale - 1) <= .05f;

    private bool MayRebuild()
    {
        if (Stopwatch.GetTimestamp() < _rebuildUntil)
            return true;
        _rebuildPending = true;
        return false;
    }

    private (CanvasCachedGeometry Fill, CanvasCachedGeometry Border, float Scale)? Realized(
        CanvasControl canvas,
        string code,
        Outline outline
    )
    {
        var found = _realized.TryGetValue(code, out var shape);
        if ((found && IsCurrent(shape.Scale) && outline.Detail == _detail) || !MayRebuild())
            return found ? shape : null;
        if (found)
        {
            shape.Fill.Dispose();
            shape.Border.Dispose();
        }
        if (outline.Detail != _detail)
        {
            outline.Dispose();
            outline = Trace(canvas, _world[code]);
            // Replacing a value keeps Draw's enumeration of _land valid.
            _land[code] = outline;
        }
        var tolerance = CanvasGeometry.ComputeFlatteningTolerance(canvas.Dpi, ViewScale);
        shape = (
            CanvasCachedGeometry.CreateFill(outline.Fill, tolerance),
            CanvasCachedGeometry.CreateStroke(outline.Border, .5f / ViewScale, Plain, tolerance),
            ViewScale
        );
        _realized[code] = shape;
        return shape;
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
            nearest ??= _destinations.FirstOrDefault(destination =>
                destination.Arc?.StrokeContainsPoint(mapped, 8 / ViewScale) == true
            );
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
    /// A country in map coordinates, traced at one outline detail. The border
    /// leaves out the 180° cuts in the country data, and is the fill itself for
    /// a country without one; West and East bound both shapes.
    /// </summary>
    private sealed record Outline(
        CanvasGeometry Fill,
        CanvasGeometry Border,
        float West,
        float East,
        float Detail
    ) : IDisposable
    {
        public void Dispose()
        {
            Fill.Dispose();
            if (!ReferenceEquals(Border, Fill))
                Border.Dispose();
        }
    }

    private sealed class Group(PeerPlace place, string name, GeoPoint point, bool estimated = false)
    {
        internal PeerPlace Place { get; } = place;
        internal string Code => Place.Code;
        internal string Name { get; } = name;
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
        internal CanvasGeometry? Arc { get; set; }

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
        private readonly float _length = Math.Max(1 / map._scale, Vector2.Distance(start, end));
        private CanvasRadialGradientBrush? _halo;
        private CanvasRadialGradientBrush? _core;
        private Color _haloColour;
        private Color _coreColour;
        private float _coreScale;
        private CanvasCachedGeometry? _glow;
        private CanvasCachedGeometry? _line;
        private (float Width, float Scale) _glowMade;
        private (float Width, float Scale) _lineMade;

        /// <summary>The halo's stroke for a width in DIPs on screen, or null before the first one is made.</summary>
        internal CanvasCachedGeometry? Glow(float width) => Stroke(ref _glow, ref _glowMade, width);

        /// <summary>The core's stroke for a width in DIPs on screen, or null before the first one is made.</summary>
        internal CanvasCachedGeometry? Line(float width) => Stroke(ref _line, ref _lineMade, width);

        private CanvasCachedGeometry? Stroke(
            ref CanvasCachedGeometry? stroke,
            ref (float Width, float Scale) made,
            float width
        )
        {
            if (
                (stroke is not null && made.Width == width && map.IsCurrent(made.Scale))
                || !map.MayRebuild()
            )
                return stroke;
            stroke?.Dispose();
            stroke = CanvasCachedGeometry.CreateStroke(
                arc,
                width / map.ViewScale,
                Plain,
                CanvasGeometry.ComputeFlatteningTolerance(canvas.Dpi, map.ViewScale)
            );
            made = (width, map.ViewScale);
            return stroke;
        }

        internal CanvasRadialGradientBrush Halo(Color colour)
        {
            if (_halo is null || _haloColour != colour)
            {
                _halo?.Dispose();
                _halo = CreateLanding(canvas, colour);
                _haloColour = colour;
            }
            return _halo;
        }

        // One radial brush around the destination carries both the brightness
        // along the route and the landing fade, so an arc needs no layer of its
        // own. The fade is measured on screen, so a scale change rebuilds it.
        internal CanvasRadialGradientBrush Core(Color colour)
        {
            if (
                _core is null
                || _coreColour != colour
                || (!map.IsCurrent(_coreScale) && map.MayRebuild())
            )
            {
                var scale = map.ViewScale;
                _core?.Dispose();
                var landed = 30 / (_length * scale);
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
                            Route(1 - position) * Landing(position * _length * scale)
                        ),
                    })
                    .ToArray();
                _core = new CanvasRadialGradientBrush(canvas, stops)
                {
                    Center = end,
                    RadiusX = _length,
                    RadiusY = _length,
                };
                _coreColour = colour;
                _coreScale = scale;
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
            _glow?.Dispose();
            _line?.Dispose();
        }
    }

    // Peer instances update in place; retain the last observation to avoid
    // running an animation when an idle snapshot has not changed.
    private sealed record Observation(Models.Peer Peer, double Down, double Up, Location? Location)
    {
        internal PeerRole Role { get; } = Peer.Role;
    }
}
