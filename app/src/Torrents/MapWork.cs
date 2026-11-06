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
        internal CancellationToken Cancellation { get; init; }
    }

    internal sealed record Output
    {
        internal required Presentation Model { get; init; }
#if CAPTURE
        internal double PreparationMs { get; init; }
#endif
    }

    private readonly Arrangement _model = new();
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
        var factsChanged = _snapshot is null || input.Model.Subject != _snapshot.Subject || !input.Model.Matches(_snapshot);
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
        var model = _presentation!;
        return new()
        {
            Model = model,
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
