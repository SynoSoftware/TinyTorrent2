using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Library;

/// <summary>
/// The targets that one automatic pass over the entries on screen accepted,
/// in the order it serves them. A pass accepts the pending targets the
/// person has not cancelled, leaves a target whose row scrolled away to the
/// pass that finds it on screen again, and on the person's Cancel remembers
/// the targets it did not finish. It is not synchronized: its owner calls
/// Next, Advance and Cancel under the gate that guards the displayed
/// entries, because the person's Cancel arrives on another thread.
/// </summary>
internal sealed class Pass<T>
    where T : class
{
    private readonly List<T> _targets;
    private readonly HashSet<T> _cancelled;
    private readonly Func<T, long> _entry;

    /// <param name="cancelled">The owner's targets that later passes skip.</param>
    /// <param name="entry">The Library entry whose row shows the target.</param>
    internal Pass(IEnumerable<T> pending, HashSet<T> cancelled, Func<T, long> entry)
    {
        _cancelled = cancelled;
        _entry = entry;
        _targets = pending.Where(target => !cancelled.Contains(target)).ToList();
    }

    /// <summary>How many targets the pass finished.</summary>
    internal int Done { get; private set; }

    /// <summary>How many targets the pass accepted and has not left to a later pass.</summary>
    internal int Count => _targets.Count;

    internal bool IsFinished => Done == Count;

    internal WorkProgress Progress => WorkProgress.Counted(Done, Count, WorkUnit.Files);

    /// <summary>
    /// The next target on screen, or null when none is left. The targets
    /// before it that scrolled away leave the pass.
    /// </summary>
    internal T? Next(IReadOnlySet<long> displayed)
    {
        var next = _targets.FindIndex(Done, target => displayed.Contains(_entry(target)));
        var end = next < 0 ? _targets.Count : next;
        _targets.RemoveRange(Done, end - Done);
        return next < 0 ? null : _targets[Done];
    }

    /// <summary>Counts the target that Next returned as finished.</summary>
    internal void Advance() => Done++;

    /// <summary>The person cancelled the pass: later passes skip the targets it did not finish.</summary>
    internal void Cancel()
    {
        foreach (var target in _targets.Skip(Done))
        {
            _cancelled.Add(target);
        }
    }
}
