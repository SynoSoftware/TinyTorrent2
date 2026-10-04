using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Synapse_Sample;

/// <summary>
/// What a render job is doing. Nine members and no qualifying flag: the three waiting kinds differ
/// only in what they are waiting for, and a boolean beside a smaller enum would leave that
/// difference unnamed. <see cref="Cancelling"/> is the display-only one — the page refuses
/// interaction on it, so the sample drives a non-interactive item as well as the rest.
/// </summary>
public enum DemoState
{
    Rendering,
    Publishing,
    Verifying,
    Stalled,
    QueuedToRender,
    QueuedToVerify,
    QueuedToPublish,
    Paused,
    Cancelling,
}

/// <summary>
/// One generated job. Name, frame count, worker pool and submission time are fixed when the row is
/// made; everything the feed advances on its tick is a settable property that announces itself, so
/// a realized cell redraws without the row being re-projected. <see cref="Index"/> is the sort key
/// no tick moves, which is what lets a reversal be timed twice and compared.
/// </summary>
/// <remarks>
/// Every display string is built in its getter and kept until the value behind it changes, so a
/// tick costs one field write and one <see cref="PropertyChanged"/> per changed value, and the
/// string is produced only if a realized cell reads it. At 20,000 rows, formatting eagerly would
/// allocate several strings a second for the rows nobody can see.
/// </remarks>
public sealed class DemoRow : INotifyPropertyChanged
{
    private DemoState _state;
    private string? _stateText;
    private long _framesDone;
    private string? _doneText;
    private double _progress;
    private string? _progressText;
    private double _inRate;
    private string? _inRateText;
    private double _outRate;
    private string? _outRateText;
    private int _workersBusy;
    private string? _workersText;
    private double _yield;
    private string? _yieldText;
    private TimeSpan? _remaining;
    private string? _remainingText;
    private DateTimeOffset? _finished;
    private string? _finishedText;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Stable across every publish, so the schema can reconcile selection by it.</summary>
    public required string Id { get; init; }

    /// <summary>Generation order. The one sort key a tick never changes.</summary>
    public required int Index { get; init; }

    /// <summary>Deliberately of very uneven length: some trim in the column and some do not.</summary>
    public required string Name { get; init; }

    public required long Frames { get; init; }

    public required string FramesText { get; init; }

    public required string SubmittedText { get; init; }

    /// <summary>Empty on a healthy job. A handful of rows carry one, and the name cell shows it.</summary>
    public required string Fault { get; init; }

    /// <summary>Size of the pool this job may draw on. <see cref="WorkersBusy"/> moves inside it.</summary>
    public required int WorkersTotal { get; init; }

    public bool HasFault => Fault.Length > 0;

    /// <summary>A job the farm has not confirmed. It renders and nothing else.</summary>
    public bool IsPending => _state == DemoState.Cancelling;

    /// <summary>Section 5's non-interactive item, made visible: a pending row is drawn back.</summary>
    public double RowOpacity => IsPending ? 0.55 : 1;

    public DemoState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value))
            {
                return;
            }

            _stateText = null;
            Raise(nameof(StateText));
            Raise(nameof(StateGlyph));
            Raise(nameof(StateAccentKey));
            Raise(nameof(IsPending));
            Raise(nameof(RowOpacity));
        }
    }

    public string StateText => _stateText ??= _state switch
    {
        DemoState.Rendering => "Rendering",
        DemoState.Publishing => "Publishing",
        DemoState.Verifying => "Verifying",
        DemoState.Stalled => "Stalled",
        DemoState.QueuedToRender => "Queued to render",
        DemoState.QueuedToVerify => "Queued to verify",
        DemoState.QueuedToPublish => "Queued to publish",
        DemoState.Paused => "Paused",
        _ => "Cancelling",
    };

    /// <summary>Segoe Fluent Icons, from the set the reference host already draws.</summary>
    public string StateGlyph => _state switch
    {
        DemoState.Rendering => "",
        DemoState.Publishing => "",
        DemoState.Verifying => "",
        DemoState.Stalled => "",
        DemoState.Paused => "",
        DemoState.Cancelling => "",
        _ => "",
    };

    /// <summary>
    /// A theme resource key, resolved by the cell template. The row never holds a brush: one
    /// resolved on first read is the wrong colour for the rest of the session as soon as the user
    /// switches theme.
    /// </summary>
    public string StateAccentKey => _state switch
    {
        DemoState.Rendering or DemoState.Verifying => "AccentTextFillColorPrimaryBrush",
        DemoState.Publishing => "SystemFillColorSuccessBrush",
        DemoState.Stalled => "SystemFillColorCautionBrush",
        _ => "TextFillColorSecondaryBrush",
    };

    /// <summary>Frames written so far. The progress bar and its label both come from this.</summary>
    public long FramesDone
    {
        get => _framesDone;
        set
        {
            if (!Set(ref _framesDone, value))
            {
                return;
            }

            _doneText = null;
            Raise(nameof(DoneText));
            Progress = Frames == 0 ? 0 : (double)value / Frames * 100;
        }
    }

    public string DoneText => _doneText ??=
        $"{_framesDone.ToString("N0", CultureInfo.CurrentCulture)} of {FramesText}";

    /// <summary>Completed percentage, 0 to 100, which is what the bar in the cell shows.</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            _progressText = null;
            if (Set(ref _progress, value))
            {
                Raise(nameof(ProgressText));
            }
        }
    }

    public string ProgressText => _progressText ??=
        _progress.ToString("0.0", CultureInfo.CurrentCulture) + "%";

    /// <summary>Bytes a second pulled from the asset store. Every tick moves it on an active row.</summary>
    public double InRate
    {
        get => _inRate;
        set
        {
            _inRateText = null;
            if (Set(ref _inRate, value))
            {
                Raise(nameof(InRateText));
            }
        }
    }

    public string InRateText => _inRateText ??= Rate(_inRate);

    /// <summary>Bytes a second written back. Both directions are drawn, in one cell.</summary>
    public double OutRate
    {
        get => _outRate;
        set
        {
            _outRateText = null;
            if (Set(ref _outRate, value))
            {
                Raise(nameof(OutRateText));
            }
        }
    }

    public string OutRateText => _outRateText ??= Rate(_outRate);

    /// <summary>Whichever direction this row is judged by, which is what its state decides.</summary>
    public double ActiveRate => _state == DemoState.Publishing ? _outRate : _inRate;

    public int WorkersBusy
    {
        get => _workersBusy;
        set
        {
            _workersText = null;
            if (Set(ref _workersBusy, value))
            {
                Raise(nameof(WorkersText));
            }
        }
    }

    public string WorkersText => _workersText ??= $"{_workersBusy} / {WorkersTotal}";

    /// <summary>Output bytes per input byte. A job that has produced nothing yet reads 0.00.</summary>
    public double Yield
    {
        get => _yield;
        set
        {
            _yieldText = null;
            if (Set(ref _yield, value))
            {
                Raise(nameof(YieldText));
            }
        }
    }

    public string YieldText => _yieldText ??= _yield.ToString("0.00", CultureInfo.CurrentCulture);

    /// <summary>Null is "no estimate", which the cell has to draw as well as a duration.</summary>
    public TimeSpan? Remaining
    {
        get => _remaining;
        set
        {
            _remainingText = null;
            if (Set(ref _remaining, value))
            {
                Raise(nameof(RemainingText));
            }
        }
    }

    public string RemainingText => _remainingText ??= Duration(_remaining);

    /// <summary>Null is "not finished", which is most rows.</summary>
    public DateTimeOffset? Finished
    {
        get => _finished;
        set
        {
            _finishedText = null;
            if (Set(ref _finished, value))
            {
                Raise(nameof(FinishedText));
            }
        }
    }

    public string FinishedText => _finishedText ??=
        _finished is DateTimeOffset when ? when.ToString("yyyy-MM-dd HH:mm") : "—";

    /// <summary>How the whole row sorts by completion: a job with no finish time sorts last.</summary>
    public DateTimeOffset FinishedOrder => _finished ?? DateTimeOffset.MaxValue;

    /// <summary>
    /// Re-announce the accent key so the cell resolves it again. The key does not change with the
    /// theme; the brush it names does, and nothing else would ask for it.
    /// </summary>
    public void InvalidateStateAccent() => Raise(nameof(StateAccentKey));

    /// <summary>An em dash below a kilobyte, so a stopped row reads as stopped rather than as 0.</summary>
    private static string Rate(double bytesPerSecond) => bytesPerSecond switch
    {
        < 1_000 => "—",
        < 1_000_000 => (bytesPerSecond / 1_000).ToString("0", CultureInfo.CurrentCulture) + " KB/s",
        _ => (bytesPerSecond / 1_000_000).ToString("0.0", CultureInfo.CurrentCulture) + " MB/s",
    };

    private static string Duration(TimeSpan? span) => span switch
    {
        null => "—",
        { TotalDays: >= 1 } d => $"{(int)d.TotalDays}d {d.Hours}h",
        { TotalHours: >= 1 } h => $"{(int)h.TotalHours}h {h.Minutes}m",
        { TotalMinutes: >= 1 } m => $"{(int)m.TotalMinutes}m {m.Seconds}s",
        { } s => $"{(int)s.TotalSeconds}s",
    };

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
