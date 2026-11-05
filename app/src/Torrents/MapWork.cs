using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Syno.TinyTorrent.Shared;
using Windows.UI;
using static Syno.TinyTorrent.Torrents.MapModel;

namespace Syno.TinyTorrent.Torrents;

/// <summary>One worker owns the map's mutable arrangement and native geometry for its loaded lifetime.</summary>
internal sealed class MapWork(Dictionary<string, Geography.Country> world) : IDisposable
{
#if CAPTURE
    internal Action? Started { get; set; }
#endif
    internal sealed record Input
    {
        internal required Snapshot Model { get; init; }
        internal required MapViewport Viewport { get; init; }
        internal required CanvasDevice Device { get; init; }
        internal required float Dpi { get; init; }
        internal required MapRenderer.Palette Palette { get; init; }
        internal required Color Underlay { get; init; }
        internal required (Peer Peer, Location? Location)[] Selection { get; init; }
        internal PeerPlace? Place { get; init; }
        internal DestinationKey? Chosen { get; init; }
        internal DestinationKey? Pointed { get; init; }
        internal bool? PointedCities { get; init; }
        internal MapAnimation Animation { get; init; }
        internal MapDotTotal DotTotal { get; init; }
        internal int ArcLimit { get; init; }
        internal bool Animates { get; init; }
        internal bool HighContrast { get; init; }
        internal bool Arrange { get; init; }
        internal bool Draw { get; init; }
        internal CancellationToken Cancellation { get; init; }
    }

    internal sealed class Output : IDisposable
    {
        internal required Presentation Model { get; init; }
        internal required MapView View { get; init; }
        internal required IReadOnlyList<Peer> Selection { get; init; }
        internal required Dictionary<PeerPlace, int> Marks { get; init; }
        internal Destination? Chosen { get; init; }
        internal Destination? Pointed { get; init; }
        internal CanvasCommandList? Picture { get; init; }
        internal MapRenderer.Inspection? Inspection { get; set; }
        internal MapMotion.Scene? Scene { get; init; }
        internal TimeSpan? Delay { get; init; }
#if CAPTURE
        internal double PreparationMs { get; init; }
        internal double DrawingMs { get; init; }
        internal IReadOnlyList<(string Phase, double Ms)> Work { get; init; } = [];
#endif

        public void Dispose()
        {
            Picture?.Dispose();
            Inspection?.Dispose();
        }
    }

    private readonly Arrangement _model = new();
    private MapRenderer? _renderer;
    private CanvasDevice? _device;
    private float _dpi;
    private Dictionary<PeerPlace, int> _marks = [];
    private MapRenderer.Highlight _highlight;
    private Snapshot? _snapshot;
    private Presentation? _presentation;
    private float _scale;
    private (MapAnimation Animation, MapDotTotal Dots, int Arcs, bool Animates, bool Contrast) _options;
    private bool _again;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Released => _released.Task;

    internal Output Prepare(Input input)
    {
#if CAPTURE
        Started?.Invoke();
#endif
        input.Cancellation.ThrowIfCancellationRequested();
#if CAPTURE
        var started = Stopwatch.GetTimestamp();
        List<(string, double)> measurements = [];
#endif
        var view = input.Viewport;
        _again = false;
        var first = _renderer is null;
        var factsChanged = _snapshot is null || !input.Model.Matches(_snapshot);
        var arrange = first || factsChanged || view.Scale != _scale;
        if (input.Arrange || first || factsChanged)
            _model.Update(input.Model);
        if (arrange)
        {
            _model.Arrange(world, view.Scale);
            _presentation = _model.Present();
        }
        _snapshot = input.Model;
        _scale = view.Scale;
        if (_renderer is null)
            _renderer = new(input.Device, input.Dpi, () => _again = true, _model, view, world);
        else
            _renderer.UseView(view);
        var deviceChanged = !ReferenceEquals(_device, input.Device) || _dpi != input.Dpi;
        if (deviceChanged && !first)
            _renderer.UseDevice(input.Device, input.Dpi);
        _device = input.Device;
        _dpi = input.Dpi;
        _renderer.Animation = input.Animation;
        _renderer.DotTotal = input.DotTotal;
        _renderer.ArcLimit = input.ArcLimit;
        _renderer.Animates = input.Animates;
        _renderer.HighContrast = input.HighContrast;
        var options = (input.Animation, input.DotTotal, input.ArcLimit, input.Animates, input.HighContrast);
        var prepare = first || deviceChanged || arrange || options != _options;
        _options = options;
#if CAPTURE
        measurements.Add(("arrange", Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        _renderer.WorkMeasured = (phase, ms) => measurements.Add((phase, ms));
        var preparing = Stopwatch.GetTimestamp();
#endif
        var changed = prepare ? _renderer.Prepare() : _renderer.Advance();
        var selected = input.Selection;
        if (selected.Length > 0)
        {
            var present = input.Model.Observations.Select(observation => observation.Peer).ToHashSet();
            selected = selected.Where(item => present.Contains(item.Peer)).ToArray();
        }
        var marks = _model.Marks(selected.Select(item => item.Location).ToArray());
        if (marks.Count != _marks.Count || marks.Any(mark => _marks.GetValueOrDefault(mark.Key) != mark.Value))
            _marks = marks;
        var model = _presentation!;
        var chosen = model.Destinations.FirstOrDefault(destination => destination.Key == input.Chosen && destination.Group.Place == input.Place)
            ?? model.Destinations.FirstOrDefault(destination => destination.Group.Place == input.Place);
        var pointed = input.PointedCities == input.Model.Cities
            ? model.Destinations.FirstOrDefault(destination => destination.Key == input.Pointed) : null;
        var highlight = new MapRenderer.Highlight(chosen?.Key, _marks, pointed?.Key);
#if CAPTURE
        var drawingAt = Stopwatch.GetTimestamp();
        measurements.Add(("prepare", Stopwatch.GetElapsedTime(preparing, drawingAt).TotalMilliseconds));
#endif
        CanvasCommandList? picture = null;
        MapRenderer.Inspection? inspection = null;
        try
        {
            if (input.Draw || changed || highlight != _highlight)
            {
                do
                {
                    input.Cancellation.ThrowIfCancellationRequested();
                    _again = false;
                    picture?.Dispose();
                    picture = new(input.Device);
                    using (var drawing = picture.CreateDrawingSession())
                    {
                        drawing.Clear(input.Underlay);
                        _renderer.Draw(drawing, input.Palette, highlight);
                    }
                    while (_renderer.HasStaleBorders)
                    {
                        input.Cancellation.ThrowIfCancellationRequested();
                        _renderer.StrokeBorders();
                    }
                } while (_again);
            }
            inspection = _renderer.Inspect(model);
            _highlight = highlight;
            return new()
            {
                Model = model,
                View = view.View,
                Selection = selected.Select(item => item.Peer).ToArray(),
                Marks = _marks,
                Chosen = chosen,
                Pointed = pointed,
                Picture = picture,
                Inspection = inspection,
                Scene = _renderer.Scene,
                Delay = _renderer.Delay,
#if CAPTURE
                PreparationMs = Stopwatch.GetElapsedTime(started, drawingAt).TotalMilliseconds,
                DrawingMs = Stopwatch.GetElapsedTime(drawingAt).TotalMilliseconds,
                Work = measurements,
#endif
            };
        }
        catch
        {
            picture?.Dispose();
            inspection?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _renderer?.Dispose();
        _model.Clear();
        _snapshot = null;
        _presentation = null;
        world = [];
        _released.TrySetResult();
    }
}
