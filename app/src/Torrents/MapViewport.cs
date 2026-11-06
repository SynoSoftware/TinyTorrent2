using System.Numerics;
using Syno.TinyTorrent.Shared;
using Windows.Foundation;

namespace Syno.TinyTorrent.Torrents;

internal sealed class MapViewport
{
    internal const float MaxZoom = 128;
    private const float OverviewRatio = 1;

    internal Size Size { get; private set; }
    internal Size FrameSize { get; private set; }
    internal float Scale { get; private set; }
    internal Vector2 Offset { get; private set; }
    internal float Meridian { get; set; }
    internal float Zoom { get; private set; } = 1;
    internal float Ratio { get; private set; } = OverviewRatio;
    internal Vector2 Center { get; private set; }
    internal float ViewScale => Scale * Zoom;
    internal Vector2 AxisScale => new Vector2(Ratio, 1) * ViewScale;
    internal bool IsFitted => Zoom == 1 && Center == Vector2.Zero;
    internal Matrix3x2 Transform =>
        Matrix3x2.CreateScale(AxisScale)
        * Matrix3x2.CreateTranslation(Offset - Center * AxisScale);

    // The payload tells the control whether its zoom and Fit display changed.
    internal event EventHandler<bool>? Changed;

    internal static float FitScale(Size size) =>
        (float)Math.Max(0, Math.Min((size.Width - 24) / (360 * OverviewRatio), (size.Height - 20) / 180));

    internal void Resize(Size size)
    {
        Size = size;
        FrameSize = size;
        Scale = FitScale(size);
        Offset = new Vector2((float)size.Width / 2, (float)size.Height / 2);
        SetView(Zoom, Center, CalculateRatio(Zoom, Center.Y));
    }

    // Geometry stays in geographic coordinates so resizing retains its GPU caches.
    internal Vector2 Coordinates(GeoPoint point) => Coordinates(point, Meridian);

    internal static Vector2 Coordinates(GeoPoint point, float meridian) =>
        new(Wrap((float)(point.Longitude - meridian), 0), (float)-point.Latitude);

    /// <summary>
    /// The point on the fitted map centred on <paramref name="meridian"/>, in
    /// DIPs from the map's centre at <paramref name="scale"/> DIPs per degree.
    /// </summary>
    internal static Vector2 Project(GeoPoint point, float meridian, float scale) =>
        Coordinates(point, meridian) * new Vector2(OverviewRatio, 1) * scale;

    /// <summary>The longitude, moved by whole turns to within 180° of centre.</summary>
    internal static float Wrap(float longitude, float centre) =>
        longitude + 360 * MathF.Round((centre - longitude) / 360);

    internal Vector2 Screen(GeoPoint point) => View.Screen(point);

    internal MapView View => new(Size, Meridian, Center, AxisScale, Transform)
        { FrameSize = FrameSize == Size ? null : FrameSize };

    internal static MapViewport From(MapView view)
    {
        var frame = view.FrameSize ?? view.Size;
        var scale = FitScale(frame);
        return new()
        {
            Size = view.Size,
            FrameSize = frame,
            Scale = scale,
            Offset = Vector2.Transform(view.Center, view.Transform),
            Meridian = view.Meridian,
            Zoom = view.AxisScale.Y / scale,
            Ratio = view.AxisScale.X / view.AxisScale.Y,
            Center = view.Center,
        };
    }

    internal Vector2 ToMap(Vector2 point) => (point - Offset) / AxisScale + Center;

    private Vector2 Constrain(float zoom, Vector2 center)
    {
        var limit = Scale > 0 ? Math.Max(0, 90 - Offset.Y / (Scale * zoom)) : 0;
        // Longitude scrolls without end; latitude stops at the map's edges.
        return new Vector2(Wrap(center.X, 0), Math.Clamp(center.Y, -limit, limit));
    }

    internal void SetView(float zoom, Vector2 center, float ratio)
    {
        center = Constrain(zoom, center);
        if (zoom == Zoom && center == Center && ratio == Ratio)
            return;
        var previous = (Zoom, IsFitted);
        Zoom = zoom;
        Center = center;
        Ratio = ratio;
        Changed?.Invoke(this, previous != (Zoom, IsFitted));
    }

    internal void ZoomAt(float factor, Vector2 anchor)
    {
        if (Scale <= 0)
            return;
        var zoom = Math.Clamp(Zoom * factor, 1, MaxZoom);
        if (zoom != Zoom)
            Hold(zoom, ToMap(anchor), anchor);
    }

    // Zooming and dragging keep the map point at the screen anchor, except
    // where vertical bounds constrain it; the ratio follows the new latitude.
    internal void Hold(float zoom, Vector2 point, Vector2 anchor)
    {
        var center = Constrain(zoom, point - (anchor - Offset) / (Scale * zoom));
        var ratio = CalculateRatio(zoom, center.Y);
        center.X = point.X - (anchor.X - Offset.X) / (Scale * zoom * ratio);
        SetView(zoom, center, ratio);
    }

    internal float CalculateRatio(float zoom, float centerY)
    {
        // A finite polar view is centred below 90°, keeping its scale positive.
        var center = Constrain(zoom, new Vector2(0, centerY));
        return (float)Math.Cos(center.Y * Math.PI / 180);
    }

    internal void Pan(Vector2 delta)
    {
        if (Scale > 0)
            Hold(Zoom, Center - delta / AxisScale, Offset);
    }

    internal void Fit() => SetView(1, Vector2.Zero, OverviewRatio);

    internal void Reveal(GeoPoint destination)
    {
        var point = Screen(destination);
        var inset = Vector2.Min(new Vector2(12), Offset);
        var visible = Vector2.Clamp(point, inset, 2 * Offset - inset);
        // Holding the destination keeps it visible when latitude changes the ratio.
        if (visible != point)
            Hold(Zoom, ToMap(point), visible);
    }
}

/// <summary>
/// A copy of the view, which the moving dots read on their own thread while
/// the person pans and zooms.
/// </summary>
internal sealed record MapView(Size Size, float Meridian, Vector2 Center, Vector2 AxisScale, Matrix3x2 Transform)
{
    internal Size? FrameSize { get; init; }
    internal Rect Bounds
    {
        get
        {
            var left = Math.Max(0, Vector2.Transform(new Vector2(Center.X - 180, 0), Transform).X);
            var right = Math.Min(Size.Width, Vector2.Transform(new Vector2(Center.X + 180, 0), Transform).X);
            return new Rect(left, 0, Math.Max(0, right - left), Size.Height);
        }
    }

    internal Vector2 Coordinates(GeoPoint point) => MapViewport.Coordinates(point, Meridian);

    internal Vector2 ToMap(Vector2 point) => (point - new Vector2(Transform.M31, Transform.M32)) / AxisScale;

    internal bool Shows(Rect bounds, float margin)
    {
        for (var turn = -1; turn <= 1; turn++)
        {
            var low = Vector2.Transform(new Vector2((float)bounds.Left + 360 * turn, (float)bounds.Top), Transform);
            var high = Vector2.Transform(new Vector2((float)bounds.Right + 360 * turn, (float)bounds.Bottom), Transform);
            if (high.X >= -margin && low.X <= Size.Width + margin
                && high.Y >= -margin && low.Y <= Size.Height + margin)
                return true;
        }
        return false;
    }

    // A location shows once, in the turn of longitude around the view centre.
    internal Vector2 Screen(GeoPoint point)
    {
        var mapped = Coordinates(point);
        mapped.X = MapViewport.Wrap(mapped.X, Center.X);
        return Vector2.Transform(mapped, Transform);
    }
}
