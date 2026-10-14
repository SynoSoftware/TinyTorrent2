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
using VirtualKey = Windows.System.VirtualKey;

namespace Syno.TinyTorrent.Controls;

public sealed partial class PeerMap : UserControl
{
    private readonly UISettings _settings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private readonly Dictionary<string, CanvasGeometry> _land = [];
    private readonly Dictionary<string, Group> _countries = [];
    private readonly List<Destination> _destinations = [];
    private readonly Dictionary<(string Code, GeoPoint? Point, bool Estimated), float> _displayed = [];
    private readonly DispatcherQueueTimer _animation;
    private CanvasControl? _canvas;
    private Dictionary<string, Geography.Country> _world = [];
    private Observation[] _observations = [];
    private Location? _origin;
    private Strings? _text;
    private string _language = string.Empty;
    private (string Code, GeoPoint? Point)? _selected;
    private Models.Peer? _selectedPeer;
    private string? _hovered;
    private Destination? _pointed;
    private bool _dirty = true;
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
            if (AnimationProgress >= 1)
                _animation.Stop();
        };
        Loaded += OnLoaded;
        Unloaded += (_, _) => Stop();
        ActualThemeChanged += (_, _) => _canvas?.Invalidate();
        foreach (var swatch in new[] { Land, Edge, Download, Upload })
            swatch.RegisterPropertyChangedCallback(Border.BackgroundProperty, (_, _) => _canvas?.Invalidate());
        Ink.RegisterPropertyChangedCallback(TextBlock.ForegroundProperty, (_, _) => _canvas?.Invalidate());
        KeyDown += OnKey;
    }

    internal void Show(IReadOnlyList<Models.Peer>? peers, Location? origin, Strings text)
    {
        var observations = (peers ?? []).Select(peer =>
            new Observation(peer, peer.DownloadRate, peer.UploadRate, peer.Location)).ToArray();
        if (_observations.SequenceEqual(observations) && _origin == origin && _language == text.Language)
            return;
        _observations = observations;
        if (_selectedPeer is not null && !observations.Any(observation => observation.Peer == _selectedPeer))
            _selectedPeer = null;
        _origin = origin;
        _text = text;
        _language = text.Language;
        AutomationProperties.SetName(this, text.Get("peer_map", "name"));
        AutomationProperties.SetHelpText(this, text.Get("peer_map", "keyboard"));
        _dirty = true;
        _canvas?.Invalidate();
    }

    internal void Select(Models.Peer? peer)
    {
        _selectedPeer = peer;
        _selected = null;
        _pointed = null;
        _hovered = null;
        Describe();
        _canvas?.Invalidate();
    }

    private (string Code, GeoPoint? Point)? Selection
    {
        get
        {
            if (_selectedPeer is null)
                return _selected;
            return _selectedPeer.Location is { } location ? (Geography.Code(location.Code), location.Point) : null;
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
                _pointed = null;
                _hovered = null;
                Describe();
                canvas.Invalidate();
            };
            canvas.PointerPressed += (_, args) =>
            {
                Focus(FocusState.Pointer);
                OnPointer(canvas, args);
                _selectedPeer = null;
                if (_pointed is not null)
                    _selected = (_pointed.Group.Code, _pointed.Point);
                else if (_hovered is { } code)
                    _selected = (code, null);
                else
                    _selected = null;
                Describe();
                canvas.Invalidate();
            };
            Drawing.Children.Add(canvas);
        }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
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
            destination.Arc?.Dispose();
        _destinations.Clear();
        _pointed = null;
    }

    private Vector2 Project(GeoPoint point) => _offset
        + new Vector2((float)point.Longitude, (float)-point.Latitude) * _scale;

    private GeoPoint? Locate(Location? location) => location?.Point
        ?? (location is null ? null : _world.GetValueOrDefault(Geography.Code(location.Code))?.Anchor);

    internal static float FitScale(Size size) =>
        (float)Math.Max(0, Math.Min((size.Width - 24) / 360, (size.Height - 20) / 180));

    private void Arrange(CanvasControl canvas)
    {
        if (_size != canvas.Size || _land.Count == 0)
        {
            ReleaseGeometry();
            _size = canvas.Size;
            _scale = FitScale(_size);
            _offset = new Vector2((float)_size.Width / 2, (float)_size.Height / 2);
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
        foreach (var destination in _destinations)
            destination.Arc?.Dispose();
        _destinations.Clear();
        _countries.Clear();
        Dictionary<(string Code, GeoPoint? Point), Group> locations = [];
        foreach (var observation in _observations)
        {
            if (observation.State != PeerState.Connected || observation.Location is not { } location
                || Locate(location) is not { } point)
                continue;
            var code = Geography.Code(location.Code);
            var country = _world.GetValueOrDefault(code);
            if (!_countries.TryGetValue(code, out var group))
            {
                var name = location.Country.Name(_text!.Language) ?? location.Code;
                group = new Group(code, name, country?.Anchor ?? point);
                _countries.Add(code, group);
            }
            group.Add(observation);
            if (!locations.TryGetValue((code, location.Point), out var destination))
            {
                destination = new Group(code, observation.Peer.LocationText, point);
                locations.Add((code, location.Point), destination);
            }
            destination.Add(observation);
        }
        foreach (var group in _countries.Values)
            group.Start = _displayed.GetValueOrDefault((group.Code, null, true), Strength(group.Down + group.Up));
        var origin = Locate(_origin);
        foreach (var group in locations.Values)
        {
            if (!group.Estimated || group.Active == 0
                || !_world.TryGetValue(group.Code, out var country) || country.Areas.Length == 0)
            {
                Add(group, group.Point, 1, null);
                continue;
            }
            var candidates = country.Areas;
            List<Geography.Area> areas = [];
            foreach (var area in candidates)
            {
                var nearby = areas.FindIndex(previous => Vector2.Distance(Project(previous.Point), Project(area.Point)) < 25);
                if (nearby < 0)
                    areas.Add(area);
                else
                    areas[nearby] = areas[nearby] with { Population = areas[nearby].Population + area.Population };
            }
            Geography.Area[] chosen = group.Active == 1 ? [candidates[0]]
                : areas.OrderByDescending(area => area.Population).Take(group.Active).ToArray();
            var total = chosen.Sum(area => area.Population);
            foreach (var area in chosen)
                Add(group, area.Point, area.Population / total, area.Name);
        }
        var keys = _destinations.Select(destination => destination.Key)
            .Concat(_countries.Keys.Select(code => (code, (GeoPoint?)null, true))).ToHashSet();
        foreach (var key in _displayed.Keys.Where(key => !keys.Contains(key)).ToArray())
            _displayed.Remove(key);
        _pointed = _destinations.FirstOrDefault(destination => destination.Key == pointed);
        _started = Stopwatch.GetTimestamp();
        if (_settings.AnimationsEnabled && !_accessibility.HighContrast)
            _animation.Start();
        Describe();

        void Add(Group group, GeoPoint point, double share, string? area)
        {
            var strength = Strength((group.Down + group.Up) * share);
            var arc = origin is { } source && group.Active > 0 ? Arc(canvas, source, point) : null;
            var destination = new Destination(group, point, share, area) { Strength = strength, Arc = arc };
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
            var direction = from * (float)Math.Cos(travelled) + tangent * (float)Math.Sin(travelled);
            var point = Project(new GeoPoint(Math.Atan2(direction.Y, direction.X) * 180 / Math.PI,
                Math.Atan2(direction.Z, Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y)) * 180 / Math.PI));
            // Split at the date line instead of drawing a chord across the map.
            if (Math.Abs(point.X - previous.X) > 180 * _scale)
            {
                var width = 360 * _scale;
                var unwrapped = point.X + (point.X > previous.X ? -width : width);
                var edge = _offset.X + (previous.X > _offset.X ? width / 2 : -width / 2);
                var amount = unwrapped == previous.X ? 0 : (edge - previous.X) / (unwrapped - previous.X);
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
        return new((float)(Math.Cos(latitude) * Math.Cos(longitude)),
            (float)(Math.Cos(latitude) * Math.Sin(longitude)), (float)Math.Sin(latitude));
    }

    private double AnimationProgress => Math.Min(1, Stopwatch.GetElapsedTime(_started).TotalSeconds / 1.6);
    private static float Strength(double rate)
    {
        const double ceiling = 5 * 1024 * 1024;
        return (float)Math.Clamp(Math.Log(1 + rate / 1024) / Math.Log(1 + ceiling / 1024), 0, 1);
    }
    private static Color Colour(Brush brush) => ((SolidColorBrush)brush).Color;
    private static Color Alpha(Color colour, double opacity) => Color.FromArgb((byte)Math.Clamp(colour.A * opacity, 0, 255), colour.R, colour.G, colour.B);
    private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
        (byte)(from.A + (to.A - from.A) * amount),
        (byte)(from.R + (to.R - from.R) * amount), (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

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
        foreach (var (code, geometry) in _land)
        {
            var fill = land;
            if (_countries.TryGetValue(code, out var group) && group.Estimated)
            {
                var strength = group.Start + (Strength(group.Down + group.Up) - group.Start) * (float)progress;
                _displayed[(code, null, true)] = strength;
                fill = Blend(land, down, group.Active > 0 ? .1 + .35 * strength : .08);
            }
            if (code == selection?.Code)
                fill = Blend(fill, down, .35);
            draw.FillGeometry(geometry, fill);
            draw.DrawGeometry(geometry, contrast || code == _hovered ? ink : edge,
                code == selection?.Code || code == _hovered ? 1.3f : .5f);
        }
        foreach (var destination in _destinations)
            _displayed[destination.Key] = destination.Start + (destination.Strength - destination.Start) * (float)progress;
        var arcs = _destinations.Where(destination => destination.Arc is not null).ToArray();
        if (arcs.Length > 0 && Locate(_origin) is { } home)
        {
            var origin = Project(home);
            var target = Math.Clamp(.8 * Math.Sqrt(5.0 / arcs.Length), .1, .8);
            var amount = Math.Clamp((arcs.Length - 8) / 12.0, 0, 1);
            var opacity = contrast ? 1 : 1 - (1 - target) * amount * amount * (3 - 2 * amount);
            using var mask = new CanvasRadialGradientBrush(canvas,
                [new CanvasGradientStop { Position = 0, Color = Alpha(ink, opacity) },
                 new CanvasGradientStop { Position = .25f, Color = Alpha(ink, opacity + (1 - opacity) * .15625) },
                 new CanvasGradientStop { Position = .5f, Color = Alpha(ink, (1 + opacity) / 2) },
                 new CanvasGradientStop { Position = .75f, Color = Alpha(ink, opacity + (1 - opacity) * .84375) },
                 new CanvasGradientStop { Position = 1, Color = ink }])
                { Center = origin, RadiusX = 40, RadiusY = 40 };
            using (draw.CreateLayer(mask))
            {
                if (!contrast)
                {
                    using var glow = new CanvasCommandList(canvas);
                    using (var halo = glow.CreateDrawingSession())
                        foreach (var destination in arcs)
                        {
                            var strength = _displayed[destination.Key];
                            halo.DrawGeometry(destination.Arc, Alpha(destination.Group.Down >= destination.Group.Up ? down : up,
                                .5 * strength * strength), 2 + 3 * strength);
                        }
                    using var blur = new GaussianBlurEffect { Source = glow, BlurAmount = 1.5f };
                    draw.DrawImage(blur);
                }
                foreach (var destination in arcs)
                {
                    var strength = _displayed[destination.Key];
                    var colour = Blend(destination.Group.Down >= destination.Group.Up ? down : up, ink, .2);
                    if (contrast)
                    {
                        draw.DrawGeometry(destination.Arc, ink, 1 + strength);
                        continue;
                    }
                    using var brush = new CanvasLinearGradientBrush(canvas,
                        [new CanvasGradientStop { Position = 0, Color = Alpha(colour, .55) },
                         new CanvasGradientStop { Position = .35f, Color = colour },
                         new CanvasGradientStop { Position = .8f, Color = Alpha(colour, .9) },
                         new CanvasGradientStop { Position = 1, Color = Alpha(colour, .65) }])
                        { StartPoint = origin, EndPoint = Project(destination.Point), Opacity = .18f + .82f * strength };
                    draw.DrawGeometry(destination.Arc, brush, 1 + strength);
                }
            }
            // Selection stays readable even inside the fading hub.
            foreach (var destination in arcs.Where(IsSelected))
                draw.DrawGeometry(destination.Arc, ink, 2);
            draw.FillCircle(origin, 3, ink);
        }
        foreach (var destination in _destinations)
        {
            var group = destination.Group;
            var point = Project(destination.Point);
            var city = !group.Estimated;
            var density = city ? .4 + .6 * Math.Min(1, Math.Log2(1 + group.Count) / 4) : .5;
            if (group.Active > 0)
            {
                var radius = city ? 2.4f : 1.7f + 1.1f * _displayed[destination.Key];
                var opacity = contrast || !city ? 1 : density;
                var colour = contrast ? ink : group.Down >= group.Up ? down : up;
                draw.FillCircle(point, radius, Alpha(Blend(ink, colour, .15), opacity));
                draw.DrawCircle(point, radius, Alpha(colour, opacity), .7f);
            }
            else if (city || !_land.TryGetValue(group.Code, out var geometry) || geometry.ComputeArea() < 25)
                draw.DrawCircle(point, 2.4f, Alpha(ink, contrast ? 1 : density), 1);
            if (IsSelected(destination) || ReferenceEquals(destination, _pointed))
                draw.DrawCircle(point, 5, ink, 1.2f);
        }

        bool IsSelected(Destination destination) => selection is { } location
            && location.Code == destination.Group.Code
            && (location.Point is null || location.Point == destination.Point);
    }

    private void OnPointer(object sender, PointerRoutedEventArgs args)
    {
        if (_canvas is null)
            return;
        var position = args.GetCurrentPoint(_canvas).Position;
        var point = new Vector2((float)position.X, (float)position.Y);
        Destination? nearest = null;
        var distance = 12f;
        foreach (var destination in _destinations)
        {
            var current = Vector2.Distance(point, Project(destination.Point));
            if (current < distance)
            {
                nearest = destination;
                distance = current;
            }
        }
        if (nearest is null)
            nearest = _destinations.FirstOrDefault(destination => destination.Arc?.StrokeContainsPoint(point, 8) == true);
        var country = nearest is null
            ? _land.FirstOrDefault(country => _countries.ContainsKey(country.Key) && country.Value.FillContainsPoint(point)).Key
            : null;
        if (ReferenceEquals(_pointed, nearest) && _hovered == country)
            return;
        _pointed = nearest;
        _hovered = country;
        Describe();
        _canvas.Invalidate();
    }

    private void OnKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Left or VirtualKey.Up or VirtualKey.Right or VirtualKey.Down)
        {
            if (_destinations.Count == 0)
                return;
            var step = args.Key is VirtualKey.Left or VirtualKey.Up ? -1 : 1;
            var index = _pointed is null ? -1 : _destinations.IndexOf(_pointed);
            if (index < 0)
                index = step > 0 ? 0 : _destinations.Count - 1;
            else
                index = (index + step + _destinations.Count) % _destinations.Count;
            _pointed = _destinations[index];
            _selected = (_pointed.Group.Code, _pointed.Point);
            _hovered = null;
        }
        else if (args.Key == VirtualKey.Escape)
        {
            _selected = null;
            _hovered = null;
            _pointed = null;
        }
        else
            return;
        _selectedPeer = null;
        Describe();
        _canvas?.Invalidate();
        args.Handled = true;
    }

    private void Describe()
    {
        if (_text is not { } text)
            return;
        var destination = _pointed;
        if (destination is null && _hovered is null && Selection is { Point: not null } selection)
            destination = _destinations.FirstOrDefault(candidate =>
                candidate.Group.Code == selection.Code && candidate.Point == selection.Point);
        var code = _hovered ?? Selection?.Code;
        var group = destination?.Group ?? (code is not null ? _countries.GetValueOrDefault(code) : null);
        string value;
        if (group is null)
        {
            value = text.Get("peer_map", Locate(_origin) is null ? "origin_unknown" : "approximate");
        }
        else
        {
            value = text.Format("peer_map", "detail", group.Name, group.Count, group.Active,
                text.Format("units", "rate", text.Bytes(group.Down)), text.Format("units", "rate", text.Bytes(group.Up)));
            if (destination?.Area is { } area)
            {
                value += " · " + text.Format("peer_map", "estimated", area,
                    text.Format("units", "rate", text.Bytes(group.Down * destination.Share)),
                    text.Format("units", "rate", text.Bytes(group.Up * destination.Share)));
            }
            else
            {
                var precision = destination is null || group.Estimated
                    ? "country_only" : "location_approximate";
                value += " · " + text.Get("peer_map", precision);
            }
        }
        ToolTipService.SetToolTip(this, value);
        AutomationProperties.SetHelpText(this, value + " " + text.Get("peer_map", "keyboard"));
    }

    private sealed class Group(string code, string name, GeoPoint point)
    {
        internal string Code { get; } = code;
        internal string Name { get; } = name;
        internal GeoPoint Point { get; } = point;
        internal bool Estimated { get; private set; }
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
        internal (string Code, GeoPoint? Point, bool Estimated) Key => (Group.Code, Point, Group.Estimated);
        internal float Start { get; set; }
        internal float Strength { get; init; }
        internal CanvasGeometry? Arc { get; init; }
    }

    // Peer instances update in place; retain the last observation to avoid
    // running an animation when an idle snapshot has not changed.
    private sealed record Observation(Models.Peer Peer, double Down, double Up, Location? Location)
    {
        internal PeerState State { get; } = Peer.State;
    }
}
