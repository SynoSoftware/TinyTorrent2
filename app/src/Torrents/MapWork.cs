using System.Diagnostics;
using Syno.TinyTorrent.Shared;
using static Syno.TinyTorrent.Torrents.MapModel;

namespace Syno.TinyTorrent.Torrents;

/// <summary>Groups captured peer facts on one worker without sharing its mutable groups.</summary>
internal sealed class MapWork(Dictionary<string, Geography.Country> world) : IDisposable
{
#if CAPTURE
    internal Action? Started { get; set; }
#endif
    internal sealed record Input
    {
        internal required Snapshot Model { get; init; }
        internal required float Scale { get; init; }
        internal required (Peer Peer, Location? Location)[] Selection { get; init; }
        internal PeerPlace? Place { get; init; }
        internal DestinationKey? Chosen { get; init; }
        internal DestinationKey? Pointed { get; init; }
        internal bool? PointedCities { get; init; }
        internal CancellationToken Cancellation { get; init; }
    }

    internal sealed record Output
    {
        internal required Presentation Model { get; init; }
        internal required IReadOnlyList<Peer> Selection { get; init; }
        internal required Dictionary<PeerPlace, int> Marks { get; init; }
        internal Destination? Chosen { get; init; }
        internal Destination? Pointed { get; init; }
#if CAPTURE
        internal double PreparationMs { get; init; }
#endif
    }

    private readonly Arrangement _model = new();
    private Dictionary<PeerPlace, int> _marks = [];
    private Snapshot? _snapshot;
    private Presentation? _presentation;
    private float _scale;
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
#endif
        var factsChanged = _snapshot is null || !input.Model.Matches(_snapshot);
        var arrange = factsChanged || input.Scale != _scale;
        if (factsChanged)
            _model.Update(input.Model);
        if (arrange)
        {
            _model.Arrange(world, input.Scale);
            _presentation = _model.Present();
        }
        _snapshot = input.Model;
        _scale = input.Scale;
        input.Cancellation.ThrowIfCancellationRequested();
        var selected = input.Selection;
        if (selected.Length > 0)
        {
            var present = input.Model.Observations.Select(observation => observation.Peer).ToHashSet();
            selected = selected.Where(item => present.Contains(item.Peer)).ToArray();
        }
        var model = _presentation!;
        var marks = model.Marks(selected.Select(item => item.Location).ToArray());
        if (marks.Count != _marks.Count || marks.Any(mark => _marks.GetValueOrDefault(mark.Key) != mark.Value))
            _marks = marks;
        var chosen = model.Destinations.FirstOrDefault(destination => destination.Key == input.Chosen && destination.Group.Place == input.Place)
            ?? model.Destinations.FirstOrDefault(destination => destination.Group.Place == input.Place);
        var pointed = input.PointedCities == input.Model.Cities
            ? model.Destinations.FirstOrDefault(destination => destination.Key == input.Pointed) : null;
        return new()
        {
            Model = model,
            Selection = selected.Select(item => item.Peer).ToArray(),
            Marks = _marks,
            Chosen = chosen,
            Pointed = pointed,
#if CAPTURE
            PreparationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
#endif
        };
    }

    public void Dispose()
    {
        _model.Clear();
        _snapshot = null;
        _presentation = null;
        world = [];
        _released.TrySetResult();
    }
}
