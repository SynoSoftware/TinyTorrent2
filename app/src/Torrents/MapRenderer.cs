using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Syno.TinyTorrent.Shared;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;
using static Syno.TinyTorrent.Torrents.MapModel;

namespace Syno.TinyTorrent.Torrents;

internal sealed class MapRenderer : IDisposable
{
    private readonly CanvasControl _canvas;
    private readonly MapModel _model;
    private readonly MapViewport _viewport;
    private Dictionary<string, Geography.Country> _world;
    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly Dictionary<string, Outline> _land = [];
    private readonly Dictionary<string, float> _countryStarts = [];
    private readonly List<Route> _routes = [];
    private readonly Dictionary<DestinationKey, float> _displayed = [];
    private readonly DispatcherQueueTimer _animation;
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

    private float _detail;
    private long _started;
    private bool _prepared;

    internal MapRenderer(CanvasControl canvas, MapModel model, MapViewport viewport, Dictionary<string, Geography.Country> world)
    {
        _canvas = canvas;
        _model = model;
        _viewport = viewport;
        _world = world;
        _animation = canvas.DispatcherQueue.CreateTimer();
        _animation.Interval = TimeSpan.FromMilliseconds(33);
        _animation.Tick += (_, _) =>
        {
            canvas.Invalidate();
            if (AnimationProgress >= 1 || !_settings.AnimationsEnabled || _accessibility.HighContrast)
                _animation.Stop();
        };
    }

    internal readonly record struct Palette
    {
        internal Color Ink { get; init; }
        internal Color Land { get; init; }
        internal Color Edge { get; init; }
        internal Color Download { get; init; }
        internal Color Upload { get; init; }
    }

    internal readonly record struct Highlight(
        DestinationKey? Chosen,
        IReadOnlyDictionary<PeerPlace, int> Places,
        DestinationKey? Pointed
    )
    {
        internal bool IsMarked(Destination destination) =>
            destination.Key == Chosen || Places.GetValueOrDefault(destination.Group.Place) > destination.Rank;
    }

    public void Dispose()
    {
        _animation.Stop();
        ReleaseGeometry();
        _displayed.Clear();
        _world = [];
    }

    internal void ReleaseGeometry()
    {
        _prepared = false;
        foreach (var shape in _land.Values)
            shape.Dispose();
        _land.Clear();
        foreach (var destination in _routes)
            destination.Release();
        _routes.Clear();
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
    }

    internal void Prepare()
    {
        var canvas = _canvas;
        var origin = _model.OriginPoint;
        if (_model.Meridian != _viewport.Meridian)
        {
            ReleaseGeometry();
            _viewport.Meridian = _model.Meridian;
        }
        _arrangement++;
        var previous = _routes.ToDictionary(route => route.Destination.Key);
        _routes.Clear();
        _countryStarts.Clear();
        foreach (var group in _model.Countries.Values)
            _countryStarts[group.Code] = _displayed.GetValueOrDefault(
                new DestinationKey(group.Code, null, true), Strength(group.Down + group.Up));
        foreach (var destination in _model.Destinations)
        {
            if (!previous.Remove(destination.Key, out var route))
                route = new Route(destination);
            route.Destination = destination;
            if (origin != _arcOrigin || destination.Group.Active == 0)
                route.Release();
            if (route.Arc is null && origin is { } source && destination.Group.Active > 0)
                route.Arc = Arc(canvas, source, destination.Point);
            route.Strength = Strength((destination.Group.Down + destination.Group.Up) * destination.Share);
            route.Start = _displayed.GetValueOrDefault(destination.Key, route.Strength);
            _routes.Add(route);
        }
        foreach (var route in previous.Values)
            route.Release();
        _arcOrigin = origin;
        var keys = _model.Destinations.Select(destination => destination.Key)
            .Concat(_model.Countries.Keys.Select(code => new DestinationKey(code, null, true))).ToHashSet();
        foreach (var key in _displayed.Keys.Where(key => !keys.Contains(key)).ToArray())
            _displayed.Remove(key);
        _started = Stopwatch.GetTimestamp();
        var animates = _routes.Any(route =>
            route.Start != route.Strength
            && (route.Arc is not null || route.Destination.Group.Estimated && route.Destination.Group.Active > 0))
            || _model.Countries.Values.Any(group =>
                group.Estimated && group.Active > 0
                && _countryStarts[group.Code] != Strength(group.Down + group.Up));
        if (animates && _settings.AnimationsEnabled && !_accessibility.HighContrast)
            _animation.Start();
        else
            _animation.Stop();
        _prepared = true;
    }

    private void UpdateDetail()
    {
        // Half-DIP simplification changes in powers of two; Draw retraces only
        // visible countries, within its rebuild allowance.
        if (_viewport.ViewScale > 0)
            _detail = MathF.Pow(2, MathF.Round(MathF.Log2(.5f / _viewport.ViewScale)));
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
            var shift = 360 * MathF.Round(-((low + high) / 2 - _viewport.Meridian) / 360) - _viewport.Meridian;
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
        var previous = _viewport.Coordinates(origin);
        path.BeginFigure(previous);
        for (var index = 1; index <= count; index++)
        {
            var travelled = angle * index / count;
            var direction =
                from * (float)Math.Cos(travelled) + tangent * (float)Math.Sin(travelled);
            var point = _viewport.Coordinates(
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

    internal void Draw(CanvasDrawingSession draw, Palette palette, Highlight highlight)
    {
        if (!_prepared || _viewport.ViewScale <= 0)
            return;
        var canvas = _canvas;
        UpdateDetail();
        var ink = palette.Ink;
        var land = palette.Land;
        var edge = palette.Edge;
        var down = palette.Download;
        var up = palette.Upload;
        var contrast = _accessibility.HighContrast;
        var progress = _settings.AnimationsEnabled && !contrast ? AnimationProgress : 1;
        _rebuildUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 6 / 1000;
        _rebuildPending = false;
        // The view shows one turn of longitude around its centre. A shape near
        // a map edge also draws a turn away, so it continues on the other side.
        var view = _viewport.Transform;
        var scale = _viewport.AxisScale;
        var west = Math.Max(_viewport.ToMap(Vector2.Zero).X, _viewport.Center.X - 180);
        var east = Math.Min(_viewport.ToMap(new Vector2((float)_viewport.Size.Width, 0)).X, _viewport.Center.X + 180);
        // The margin covers a border's width.
        var visible = new Extent(
            new Vector2(west, _viewport.ToMap(Vector2.Zero).Y),
            new Vector2(east, _viewport.ToMap(new Vector2(0, (float)_viewport.Size.Height)).Y)
        ).Grown(new Vector2(2) / scale);
        MoveWindow(visible);
        // Prioritise the selected routes before map drawing uses its rebuild budget.
        if (_model.OriginPoint is { } source)
        {
            foreach (var selected in _routes.Where(route => highlight.IsMarked(route.Destination)))
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
        // Initial outlines share the same allowance as later retracing, so
        // showing or recreating a map does not trace the whole world at once.
        foreach (var country in _world.Values)
        {
            if (country.Rings.Length == 0 || _land.ContainsKey(country.Code))
                continue;
            if (!MayRebuild())
                break;
            _land[country.Code] = Trace(country);
        }
        foreach (var route in _routes)
            _displayed[route.Destination.Key] =
                route.Start + (route.Strength - route.Start) * (float)progress;
        var bounds = new Rect(
            _viewport.Offset.X + (west - _viewport.Center.X) * scale.X,
            0,
            (east - west) * scale.X,
            _viewport.Size.Height
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
                    if (_model.Countries.TryGetValue(code, out var group) && group.Estimated)
                    {
                        var strength =
                            _countryStarts[code]
                            + (Strength(group.Down + group.Up) - _countryStarts[code]) * (float)progress;
                        _displayed[new DestinationKey(code, null, true)] = strength;
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
                            var widened = _viewport.AxisScale / stroke.Scale;
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
            var arcs = _routes.Where(route => route.Arc is not null).ToArray();
            // Routes lie within 180° of the origin, which is the map's centre.
            Span<int> turns = stackalloc int[3];
            var count = 0;
            for (var turn = -1; turn <= 1; turn++)
            {
                if (Shows(-180, 180, turn))
                    turns[count++] = turn;
            }
            turns = turns[..count];
            if (arcs.Length > 0 && _model.OriginPoint is { } home)
            {
                var origin = _viewport.Screen(home);
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
                    var inputs = new GlowInputs(view, _viewport.Size, down, up, _arrangement);
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
                                foreach (var route in arcs)
                                {
                                    if (!Reaches(route, turn))
                                        continue;
                                    var strength = _displayed[route.Destination.Key];
                                    var colour =
                                        route.Destination.Group.Down >= route.Destination.Group.Up ? down : up;
                                    var paint = PaintOf(canvas, route, home);
                                    var glowStroke = paint.Glow(Step(2 + 3 * strength, 2));
                                    var brush = paint.Halo(colour);
                                    brush.Center = _viewport.Coordinates(route.Destination.Point);
                                    brush.RadiusX = 30 / scale.X;
                                    brush.RadiusY = 30 / scale.Y;
                                    brush.Opacity = .5f * .35f * strength * strength;
                                    ApplyStroke(halo, glowStroke, transform, ref applied);
                                    if (glowStroke is { } stroke)
                                        halo.DrawCachedGeometry(stroke.Geometry, brush);
                                    else
                                        halo.DrawGeometry(
                                            route.Arc,
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
                        foreach (var route in arcs)
                        {
                            if (!Reaches(route, turn))
                                continue;
                            var strength = _displayed[route.Destination.Key];
                            var paint = PaintOf(canvas, route, home);
                            var marked = highlight.IsMarked(route.Destination);
                            var line = paint.Line(marked ? 2 : Step(1 + strength, 4));
                            var width = marked ? 2 : 1 + strength;
                            if (contrast)
                            {
                                ApplyStroke(draw, line, transform, ref applied);
                                if (line is { } stroke)
                                    draw.DrawCachedGeometry(stroke.Geometry, ink);
                                else
                                    draw.DrawGeometry(route.Arc, ink, width, FixedStroke);
                                continue;
                            }
                            var brush = paint.Core(
                                marked ? ink : route.Destination.Group.Down >= route.Destination.Group.Up ? down : up
                            );
                            brush.Opacity = .18f + .82f * strength;
                            ApplyStroke(draw, line, transform, ref applied);
                            if (line is { } core)
                                draw.DrawCachedGeometry(core.Geometry, brush);
                            else
                                draw.DrawGeometry(route.Arc, brush, width, FixedStroke);
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
            foreach (var route in _routes)
            {
                var group = route.Destination.Group;
                var point = _viewport.Screen(route.Destination.Point);
                // Marks and their selection ring stay within 16 DIPs of the point.
                if (
                    point.X < -16
                    || point.Y < -16
                    || point.X > _viewport.Size.Width + 16
                    || point.Y > _viewport.Size.Height + 16
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
                if (highlight.IsMarked(route.Destination) || route.Destination.Key == highlight.Pointed)
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

        bool Shows(float from, float to, int turn) =>
            from + 360 * turn < east && to + 360 * turn > west;

        Matrix3x2 Turned(int turn) => Matrix3x2.CreateTranslation(360 * turn, 0) * view;

        // A route lies within its path's bounds; the margin covers its glow.
        bool Reaches(Route route, int turn) =>
            route.Reach.Grown(new Vector2(30) / scale).Moved(360 * turn).Overlaps(visible);
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
                CanvasGeometry.ComputeFlatteningTolerance(canvas.Dpi, _viewport.ViewScale)
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
        var change = _viewport.AxisScale / scale - Vector2.One;
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
        var scale = _viewport.AxisScale;
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

    internal Destination? HitTest(Vector2 point)
    {
        Destination? nearest = null;
        if (point.X >= 0 && point.Y >= 0 && point.X <= _viewport.Size.Width && point.Y <= _viewport.Size.Height)
        {
            var distance = 12f;
            foreach (var route in _routes)
            {
                var current = Vector2.Distance(point, _viewport.Screen(route.Destination.Point));
                if (current < distance)
                {
                    nearest = route.Destination;
                    distance = current;
                }
            }
            // Routes are traced once, within 180° of the origin.
            var mapped = _viewport.ToMap(point);
            mapped.X = MapViewport.Wrap(mapped.X, 0);
            if (nearest is null)
            {
                var scale = _viewport.AxisScale;
                var transform = Matrix3x2.CreateScale(scale);
                foreach (var route in _routes)
                {
                    if (route.Arc is not { } arc)
                        continue;
                    using var geometry = arc.Transform(transform);
                    if (!geometry.StrokeContainsPoint(mapped * scale, 8))
                        continue;
                    nearest = route.Destination;
                    break;
                }
            }
        }
        return nearest;
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

    private sealed class Route(Destination destination)
    {
        internal Destination Destination { get; set; } = destination;
        internal float Start { get; set; }
        internal float Strength { get; set; }
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
            Arc = null;
            Paint = null;
        }
    }


    private Paint PaintOf(CanvasControl canvas, Route route, GeoPoint origin) =>
        route.Paint ??= new Paint(this, canvas, route, origin);

    // Gradient brushes and realizations are expensive to create, and each one
    // adds native memory pressure that can force a full garbage collection.
    // Each arc therefore keeps them, and a frame only sets their size and
    // opacity. They follow the map's scale under its rebuild limit. The arc
    // stays owned by its route; its endpoints are geographic coordinates.
    private sealed class Paint(
        MapRenderer map,
        CanvasControl canvas,
        Route route,
        GeoPoint origin
    ) : IDisposable
    {
        private readonly CanvasGeometry _arc = route.Arc!;
        private readonly Vector2 _start = map._viewport.Coordinates(origin);
        private readonly Vector2 _end = map._viewport.Coordinates(route.Destination.Point);
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
            stroke = map.CreateStroke(canvas, _arc, width);
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
                var scale = map._viewport.AxisScale;
                var length = Math.Max(1, ((_end - _start) * scale).Length());
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
                    Center = _end,
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

}
