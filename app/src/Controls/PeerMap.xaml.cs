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
    private readonly Dictionary<string, CanvasGeometry> _land = [];
    private readonly Dictionary<string, Group> _countries = [];
    private readonly List<Destination> _destinations = [];
    private readonly Dictionary<(string Code, GeoPoint? Point, bool Estimated), float> _displayed =
    [];
    private readonly DispatcherQueueTimer _animation;
    private CanvasControl? _canvas;
    private Dictionary<string, Geography.Country> _world = [];
    private Observation[] _observations = [];
    private Location? _origin;
    private GeoPoint? _arcOrigin;
    private CanvasRadialGradientBrush? _originMask;
    private double _originStrength;
    private static readonly CanvasStrokeStyle Plain = new();
    private readonly Dictionary<
        string,
        (CanvasCachedGeometry Fill, CanvasCachedGeometry Border, float Zoom)
    > _realized = [];
    private long _rebuildUntil;
    private bool _rebuildPending;
    private Strings? _text;
    private string _language = string.Empty;
    private (string Code, GeoPoint? Point, bool Estimated)? _selected;
    private Models.Peer? _selectedPeer;
    private Destination? _pointed;
    private bool _dirty = true;
    private bool _cities;
    private Size _size;
    private float _scale;
    private Vector2 _offset;
    private long _started;

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
            _selected = null;
            _pointed = null;
        }
        _cities = cities;
        _observations = observations;
        if (
            _selectedPeer is not null
            && !observations.Any(observation => observation.Peer == _selectedPeer)
        )
            _selectedPeer = null;
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

    internal void Select(Models.Peer? peer)
    {
        if (ReferenceEquals(_selectedPeer, peer) && _selected is null && _pointed is null)
            return;
        _selectedPeer = peer;
        _selected = null;
        _pointed = null;
        Describe();
        _canvas?.Invalidate();
    }

    private (string Code, GeoPoint? Point, bool Estimated)? Selection
    {
        get
        {
            if (_selectedPeer is null)
                return _selected;
            return _cities && _selectedPeer.Location is { Point: { } point } location
                ? (Geography.Code(location.Code), point, false)
                : null;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            _world = await Geography.Countries;
            if (!IsLoaded || _canvas is not null)
                return;
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
            when (error
                    is InvalidDataException
                        or System.Text.Json.JsonException
                        or InvalidOperationException
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
        _offset + new Vector2((float)point.Longitude, (float)-point.Latitude) * _scale;

    private GeoPoint? Locate(Location? location) =>
        location?.Point
        ?? (
            location is null
                ? null
                : _world.GetValueOrDefault(Geography.Code(location.Code))?.Anchor
        );

    internal static float FitScale(Size size) =>
        (float)Math.Max(0, Math.Min((size.Width - 24) / 360, (size.Height - 20) / 180));

    private void Arrange(CanvasControl canvas)
    {
        if (_size != canvas.Size || _land.Count == 0)
        {
            EndDrag(true);
            ReleaseGeometry();
            _size = canvas.Size;
            _scale = FitScale(_size);
            _offset = new Vector2((float)_size.Width / 2, (float)_size.Height / 2);
            SetView(_zoom, _center);
            foreach (var country in _world.Values)
            {
                using var path = new CanvasPathBuilder(canvas);
                path.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Alternate);
                foreach (var ring in country.Rings.Where(ring => ring.Length >= 3))
                {
                    path.BeginFigure(Project(ring[0]));
                    foreach (var point in ring.Skip(1))
                        path.AddLine(Project(point));
                    path.EndFigure(CanvasFigureLoop.Closed);
                }
                _land[country.Code] = CanvasGeometry.CreatePath(path);
            }
            _dirty = true;
        }
        if (!_dirty)
            return;
        _dirty = false;
        var pointed = _pointed?.Key;
        var previous = _destinations.ToDictionary(destination => destination.Key);
        _destinations.Clear();
        _countries.Clear();
        Dictionary<(string Code, GeoPoint? Point), Group> locations = [];
        foreach (var observation in _observations)
        {
            if (
                observation.State != PeerState.Connected
                || observation.Location is not { } location
                || Locate(location) is not { } point
            )
                continue;
            var code = Geography.Code(location.Code);
            var country = _world.GetValueOrDefault(code);
            if (!_countries.TryGetValue(code, out var group))
            {
                group = new Group(
                    code,
                    location.CountryName(_text!.Language),
                    country?.Anchor ?? point,
                    estimated: !_cities
                );
                _countries.Add(code, group);
            }
            group.Add(observation);
            if (!_cities)
                continue;
            if (!locations.TryGetValue((code, location.Point), out var destination))
            {
                destination = new Group(code, observation.Peer.LocationText, point);
                locations.Add((code, location.Point), destination);
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
                || group.Active == 0
                || !_world.TryGetValue(group.Code, out var country)
                || country.Areas.Length == 0
            )
            {
                Add(group, group.Point, 1, null);
                continue;
            }
            var candidates = country.Areas;
            List<Geography.Area> areas = [];
            foreach (var area in candidates)
            {
                var nearby = areas.FindIndex(placed =>
                    Vector2.Distance(Project(placed.Point), Project(area.Point)) < 25
                );
                if (nearby < 0)
                    areas.Add(area);
                else
                    areas[nearby] = areas[nearby] with
                    {
                        Population = areas[nearby].Population + area.Population,
                    };
            }
            Geography.Area[] chosen =
                group.Active == 1
                    ? [candidates[0]]
                    : areas.OrderByDescending(area => area.Population).Take(group.Active).ToArray();
            var total = chosen.Sum(area => area.Population);
            foreach (var area in chosen)
                Add(group, area.Point, area.Population / total, area.Name);
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

        void Add(Group group, GeoPoint point, double share, string? area)
        {
            var strength = Strength((group.Down + group.Up) * share);
            var destination = new Destination(group, point, share, area) { Strength = strength };
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
        var previous = Project(origin);
        path.BeginFigure(previous);
        for (var index = 1; index <= count; index++)
        {
            var travelled = angle * index / count;
            var direction =
                from * (float)Math.Cos(travelled) + tangent * (float)Math.Sin(travelled);
            var point = Project(
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
            // Split at the date line instead of drawing a chord across the map.
            if (Math.Abs(point.X - previous.X) > 180 * _scale)
            {
                var width = 360 * _scale;
                var unwrapped = point.X + (point.X > previous.X ? -width : width);
                var edge = _offset.X + (previous.X > _offset.X ? width / 2 : -width / 2);
                var amount =
                    unwrapped == previous.X ? 0 : (edge - previous.X) / (unwrapped - previous.X);
                var crossing = new Vector2(edge, previous.Y + (point.Y - previous.Y) * amount);
                path.AddLine(crossing);
                path.EndFigure(CanvasFigureLoop.Open);
                path.BeginFigure(new Vector2(2 * _offset.X - crossing.X, crossing.Y));
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
        Arrange(canvas);
        var draw = args.DrawingSession;
        var ink = Colour(Ink.Foreground);
        var land = Colour(Land.Background);
        var edge = Colour(Edge.Background);
        var down = Colour(Download.Background);
        var up = Colour(Upload.Background);
        var contrast = _accessibility.HighContrast;
        var selection = Selection;
        var progress = _settings.AnimationsEnabled && !contrast ? AnimationProgress : 1;
        _rebuildUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 6 / 1000;
        _rebuildPending = false;
        draw.Transform = ViewTransform;
        foreach (var (code, geometry) in _land)
        {
            var fill = land;
            if (_countries.TryGetValue(code, out var group) && group.Estimated)
            {
                var strength =
                    group.Start + (Strength(group.Down + group.Up) - group.Start) * (float)progress;
                _displayed[(code, null, true)] = strength;
                fill = Blend(land, down, group.Active > 0 ? .1 + .35 * strength : .08);
            }
            if (Realized(canvas, code, geometry) is { } shape)
                draw.DrawCachedGeometry(shape.Fill, fill);
            else
                draw.FillGeometry(geometry, fill);
        }
        foreach (var (code, geometry) in _land)
        {
            if (_realized.TryGetValue(code, out var shape))
                draw.DrawCachedGeometry(shape.Border, contrast ? ink : edge);
            else
                draw.DrawGeometry(geometry, contrast ? ink : edge, .5f / _zoom);
        }
        draw.Transform = Matrix3x2.Identity;
        foreach (var destination in _destinations)
            _displayed[destination.Key] =
                destination.Start + (destination.Strength - destination.Start) * (float)progress;
        var arcs = _destinations.Where(destination => destination.Arc is not null).ToArray();
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
            var view = ViewTransform;
            using (draw.CreateLayer(_originMask))
            {
                if (!contrast)
                {
                    using var glow = new CanvasCommandList(canvas);
                    using (var halo = glow.CreateDrawingSession())
                    {
                        halo.Transform = view;
                        foreach (var destination in arcs)
                        {
                            var strength = _displayed[destination.Key];
                            var colour = destination.Group.Down >= destination.Group.Up ? down : up;
                            var paint = PaintOf(canvas, destination, home);
                            var brush = paint.Halo(colour);
                            brush.Center = Project(destination.Point);
                            brush.RadiusX = brush.RadiusY = 30 / _zoom;
                            brush.Opacity = .5f * .35f * strength * strength;
                            if (paint.Glow(Step(2 + 3 * strength, 2)) is { } stroke)
                                halo.DrawCachedGeometry(stroke, brush);
                            else
                                halo.DrawGeometry(
                                    destination.Arc,
                                    brush,
                                    (2 + 3 * strength) / _zoom
                                );
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
                    using var blur = new GaussianBlurEffect { Source = crop, BlurAmount = 1.5f };
                    draw.DrawImage(blur);
                }
                draw.Transform = view;
                foreach (var destination in arcs)
                {
                    var strength = _displayed[destination.Key];
                    var paint = PaintOf(canvas, destination, home);
                    var line = paint.Line(Step(1 + strength, 4));
                    var width = (1 + strength) / _zoom;
                    if (contrast)
                    {
                        if (line is not null)
                            draw.DrawCachedGeometry(line, ink);
                        else
                            draw.DrawGeometry(destination.Arc, ink, width);
                        continue;
                    }
                    var brush = paint.Core(
                        Blend(destination.Group.Down >= destination.Group.Up ? down : up, ink, .2)
                    );
                    brush.Opacity = .18f + .82f * strength;
                    if (line is not null)
                        draw.DrawCachedGeometry(line, brush);
                    else
                        draw.DrawGeometry(destination.Arc, brush, width);
                }
                draw.Transform = Matrix3x2.Identity;
            }
            // Selection stays readable even inside the fading hub.
            draw.Transform = ViewTransform;
            foreach (var destination in arcs.Where(IsSelected))
                draw.DrawGeometry(destination.Arc, ink, 2 / _zoom);
            draw.Transform = Matrix3x2.Identity;
            draw.FillCircle(origin, 3, ink);
        }
        foreach (var destination in _destinations)
        {
            var group = destination.Group;
            var point = Screen(destination.Point);
            var city = !group.Estimated;
            var density = city ? .4 + .6 * Math.Min(1, Math.Log2(1 + group.Count) / 4) : .5;
            if (group.Active > 0)
            {
                var radius = city ? 2.4f : 1.7f + 1.1f * _displayed[destination.Key];
                var opacity = contrast || !city ? 1 : density;
                var colour =
                    contrast ? ink
                    : group.Down >= group.Up ? down
                    : up;
                draw.FillCircle(point, radius, Alpha(Blend(ink, colour, .15), opacity));
                draw.DrawCircle(point, radius, Alpha(colour, opacity), .7f);
            }
            else if (
                city
                || !_land.TryGetValue(group.Code, out var geometry)
                || geometry.ComputeArea() < 25
            )
                draw.DrawCircle(point, 2.4f, Alpha(ink, contrast ? 1 : density), 1);
            if (IsSelected(destination) || ReferenceEquals(destination, _pointed))
                draw.DrawCircle(point, 5, ink, 1.2f);
        }
        if (_rebuildPending)
            canvas.Invalidate();

        bool IsSelected(Destination destination) => selection == destination.Key;
    }

    // Direct2D rasterizes a plain geometry on the CPU at every draw, at a cost
    // that grows with its size on screen, so zoomed arcs took most of a second.
    // A realization is tessellated once for a zoom and then drawn by the GPU,
    // but making one is slow, and a zoom step makes one for every country and
    // arc. Each frame therefore rebuilds only until _rebuildUntil. Within 5% of
    // its zoom a realization looks the same; one further off still draws, a
    // little too wide or narrow, until its turn. A shape with none yet draws
    // plainly.
    private bool IsCurrent(float zoom) => Math.Abs(_zoom / zoom - 1) <= .05f;

    private bool MayRebuild()
    {
        if (Stopwatch.GetTimestamp() < _rebuildUntil)
            return true;
        _rebuildPending = true;
        return false;
    }

    private (CanvasCachedGeometry Fill, CanvasCachedGeometry Border, float Zoom)? Realized(
        CanvasControl canvas,
        string code,
        CanvasGeometry geometry
    )
    {
        var found = _realized.TryGetValue(code, out var shape);
        if ((found && IsCurrent(shape.Zoom)) || !MayRebuild())
            return found ? shape : null;
        if (found)
        {
            shape.Fill.Dispose();
            shape.Border.Dispose();
        }
        var tolerance = CanvasGeometry.ComputeFlatteningTolerance(canvas.Dpi, _zoom);
        shape = (
            CanvasCachedGeometry.CreateFill(geometry, tolerance),
            CanvasCachedGeometry.CreateStroke(geometry, .5f / _zoom, Plain, tolerance),
            _zoom
        );
        _realized[code] = shape;
        return shape;
    }

    // Widths move in steps, so animated activity rebuilds an arc's
    // realization only when it reaches the next step.
    private static float Step(float width, float steps) => MathF.Round(width * steps) / steps;

    private void OnPointer(object sender, PointerRoutedEventArgs args)
    {
        if (_canvas is null || MoveDrag(args))
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
            var fitted = ToFit(point);
            nearest ??= _destinations.FirstOrDefault(destination =>
                destination.Arc?.StrokeContainsPoint(fitted, 8 / _zoom) == true
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
                text.Format("units", "rate", text.Bytes(group.Up))
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

    private sealed class Group(string code, string name, GeoPoint point, bool estimated = false)
    {
        internal string Code { get; } = code;
        internal string Name { get; } = name;
        internal GeoPoint Point { get; } = point;
        internal bool Estimated { get; private set; } = estimated;
        internal float Start { get; set; }
        internal int Count { get; private set; }
        internal int Active { get; private set; }
        internal double Down { get; private set; }
        internal double Up { get; private set; }

        internal void Add(Observation observation)
        {
            Count++;
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
            Project(origin),
            Project(destination.Point)
        );

    // Gradient brushes and realizations are expensive to create, and each one
    // adds native memory pressure that can force a full garbage collection.
    // Each arc therefore keeps them, and a frame only sets their size and
    // opacity. They follow the map's zoom under its rebuild limit. The arc
    // stays owned by its destination; start and end are in fitted-map space.
    private sealed class Paint(
        PeerMap map,
        CanvasControl canvas,
        CanvasGeometry arc,
        Vector2 start,
        Vector2 end
    ) : IDisposable
    {
        private readonly float _length = Math.Max(1, Vector2.Distance(start, end));
        private CanvasRadialGradientBrush? _halo;
        private CanvasRadialGradientBrush? _core;
        private Color _haloColour;
        private Color _coreColour;
        private float _coreZoom;
        private CanvasCachedGeometry? _glow;
        private CanvasCachedGeometry? _line;
        private (float Width, float Zoom) _glowMade;
        private (float Width, float Zoom) _lineMade;

        /// <summary>The halo's stroke for a width in DIPs on screen, or null before the first one is made.</summary>
        internal CanvasCachedGeometry? Glow(float width) => Stroke(ref _glow, ref _glowMade, width);

        /// <summary>The core's stroke for a width in DIPs on screen, or null before the first one is made.</summary>
        internal CanvasCachedGeometry? Line(float width) => Stroke(ref _line, ref _lineMade, width);

        private CanvasCachedGeometry? Stroke(
            ref CanvasCachedGeometry? stroke,
            ref (float Width, float Zoom) made,
            float width
        )
        {
            if (
                (stroke is not null && made.Width == width && map.IsCurrent(made.Zoom))
                || !map.MayRebuild()
            )
                return stroke;
            stroke?.Dispose();
            stroke = CanvasCachedGeometry.CreateStroke(
                arc,
                width / map._zoom,
                Plain,
                CanvasGeometry.ComputeFlatteningTolerance(canvas.Dpi, map._zoom)
            );
            made = (width, map._zoom);
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
        // own. The fade is measured on screen, so a zoom change rebuilds it.
        internal CanvasRadialGradientBrush Core(Color colour)
        {
            if (
                _core is null
                || _coreColour != colour
                || (!map.IsCurrent(_coreZoom) && map.MayRebuild())
            )
            {
                var zoom = map._zoom;
                _core?.Dispose();
                var landed = 30 / (_length * zoom);
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
                            Route(1 - position) * Landing(position * _length * zoom)
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
                _coreZoom = zoom;
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
        internal PeerState State { get; } = Peer.State;
    }
}
