using System.Diagnostics;
using System.Numerics;
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

        /// <summary>
        /// The DIPs per degree of longitude and latitude of the view's last rest at the
        /// current size, which decide the destinations that show as one.
        /// </summary>
        internal required Vector2 Shown { get; init; }
        internal CancellationToken Cancellation { get; init; }
    }

    internal sealed record Output
    {
        internal required Presentation Model { get; init; }
#if CAPTURE
        internal double PreparationMs { get; init; }
#endif
    }

    private readonly Arrangement _model = new(world);
    private Presentation? _presentation;
    private float _scale;
    private Vector2 _shown;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Released => _released.Task;

    internal Output Prepare(Input input)
    {
#if CAPTURE
        Started?.Invoke();
        var started = Stopwatch.GetTimestamp();
#endif
        var language = _model.Text?.Language;
        _model.Update(input.Model, input.Cancellation);
        input.Cancellation.ThrowIfCancellationRequested();
        var presentation = _presentation;
        var placed = presentation is not null && !_model.Changed && input.Scale == _scale;
        if (!placed || input.Shown != _shown)
        {
            // Only the merge reads the shown scale, so a rest merges the placements again
            // without placing every group.
            if (placed)
            {
                _model.Merge(input.Shown);
            }
            else
            {
                _model.Arrange(input.Scale, input.Shown);
            }
            var arranged = _model.Present();
            // A new population of equal peer facts, such as a return to the
            // subject's peers, keeps the presentation, so the map does not
            // inspect and publish the same picture again.
            if (presentation is null || language != input.Model.Text.Language || !arranged.Matches(presentation))
                presentation = _presentation = arranged;
            _scale = input.Scale;
            _shown = input.Shown;
        }
        input.Cancellation.ThrowIfCancellationRequested();
        return new()
        {
            Model = presentation,
#if CAPTURE
            PreparationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
#endif
        };
    }

    public void Dispose()
    {
        _model.Clear();
        _presentation = null;
        _released.TrySetResult();
    }
}
