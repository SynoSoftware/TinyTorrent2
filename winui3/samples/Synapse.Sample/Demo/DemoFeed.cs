using System.Globalization;
using Microsoft.UI.Dispatching;

namespace Synapse_Sample;

/// <summary>
/// The sample's synthetic source: a render farm's job list. It generates rows once and then
/// advances the active ones on a one second tick, the way a polling host receives a snapshot.
/// Nothing else in the repository can drive the table at a size and a rate a static list never
/// reaches, which is the whole reason it exists — the numbers specification sections 5.3, 9 and 20
/// quote are taken against this.
/// </summary>
/// <remarks>
/// Deliberately not a torrent list. The measurements are about the control, so the row type it
/// drives has to be something other than the one host the control was first written against.
/// <para>
/// The generated population spans every <see cref="DemoState"/>, carries faults on a few rows and
/// pending rows at the end, and spreads progress, both rates, worker counts, yield, estimate and
/// both dates across their whole range — including the two absent values, an unknown estimate and
/// an unfinished job. A feed that produced a thousand identical rows would leave most of what the
/// cell templates can draw untested and make a rich page look empty.
/// </para>
/// </remarks>
public sealed class DemoFeed
{
    /// <summary>Fixed, so two runs of a measurement generate the same rows.</summary>
    private readonly Random _random = new(20260906);

    private readonly List<DemoRow> _rows;
    private readonly DispatcherQueueTimer _ticker;

    public DemoFeed(DispatcherQueue dispatcher, int count)
    {
        _rows = Build(count);

        _ticker = dispatcher.CreateTimer();
        _ticker.Interval = TimeSpan.FromSeconds(1);
        _ticker.IsRepeating = true;

        // A throw inside a DispatcherQueueTimer callback tears the process down as a stowed
        // exception with no managed stack, so the tick reports its own failure instead.
        _ticker.Tick += (_, _) =>
        {
            try
            {
                Tick();
                Ticked?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _ticker.Stop();
                TickFailed?.Invoke(this, ex);
            }
        };
    }

    /// <summary>One completed snapshot. A host publishes on this, exactly as it would on a poll.</summary>
    public event EventHandler? Ticked;

    /// <summary>Raised when a tick threw. The ticker is already stopped when this arrives.</summary>
    public event EventHandler<Exception>? TickFailed;

    /// <summary>Every row, in generation order. A host copies this; the feed never reorders it.</summary>
    public IReadOnlyList<DemoRow> Rows => _rows;

    public bool IsRunning => _ticker.IsRunning;

    public void Start() => _ticker.Start();

    public void Stop() => _ticker.Stop();

    /// <summary>One simulated snapshot: every active row moves, so every sorted order moves with it.</summary>
    /// <remarks>
    /// The waiting and paused rows deliberately stay where they are. Membership is fixed, because
    /// specification 5.3's measurements need a snapshot that differs only in order, so no new job
    /// ever arrives — and a lifecycle that only moves forward over a fixed population ends with
    /// every row in the last state. A feed whose rows all look alike is the exact failure this one
    /// exists to avoid.
    /// </remarks>
    public void Tick()
    {
        foreach (DemoRow row in _rows)
        {
            switch (row.State)
            {
                case DemoState.Rendering:
                    Render(row);
                    break;

                case DemoState.Verifying:
                    Verify(row);
                    break;

                case DemoState.Publishing:
                    Publish(row);
                    break;

                case DemoState.Stalled:
                    // A stalled job holds its frame count and waits for a worker. When one frees
                    // up it starts again, which is the one state transition that runs backwards.
                    row.WorkersBusy = _random.Next(0, 3);
                    if (row.WorkersBusy > 0)
                    {
                        row.State = DemoState.Rendering;
                    }

                    break;
            }
        }
    }

    private void Render(DemoRow row)
    {
        int workers = Math.Clamp(row.WorkersBusy + _random.Next(-2, 3), 0, row.WorkersTotal);
        row.WorkersBusy = workers;

        if (workers == 0)
        {
            row.State = DemoState.Stalled;
            row.InRate = 0;
            row.OutRate = 0;
            row.Remaining = null;
            return;
        }

        double inRate = Math.Clamp(
            row.InRate + ((_random.NextDouble() - 0.45) * 400_000), 40_000, 24_000_000);
        row.InRate = inRate;
        row.OutRate = inRate * (0.4 + (_random.NextDouble() * 0.5));

        long framesPerTick = Math.Max(1, (long)(workers * 0.7));
        long done = Math.Min(row.Frames, row.FramesDone + framesPerTick);
        row.FramesDone = done;
        row.Yield = row.InRate <= 0 ? 0 : row.OutRate / row.InRate;

        long left = row.Frames - done;
        row.Remaining = left <= 0 ? null : TimeSpan.FromSeconds((double)left / framesPerTick);

        if (left <= 0)
        {
            row.State = DemoState.Verifying;
            row.Remaining = null;
        }
    }

    /// <summary>A verify walks the written frames back, then hands the job to the publisher.</summary>
    private void Verify(DemoRow row)
    {
        row.InRate = 2_000_000 + (_random.NextDouble() * 8_000_000);
        row.OutRate = 0;

        if (_random.NextDouble() < 0.25)
        {
            // Every frame is written and checked, so this is the moment the job has a finish time.
            // It is the only place one is stamped while the feed runs, which is what makes the
            // Finished column something a person can watch change rather than a generated constant.
            row.State = DemoState.QueuedToPublish;
            row.InRate = 0;
            row.Finished = DateTimeOffset.Now;
        }
    }

    private void Publish(DemoRow row)
    {
        int workers = Math.Clamp(row.WorkersBusy + _random.Next(-1, 2), 0, row.WorkersTotal);
        row.WorkersBusy = workers;
        row.InRate = 0;
        row.OutRate = workers == 0
            ? 0
            : Math.Clamp(row.OutRate + ((_random.NextDouble() - 0.5) * 200_000), 0, 6_000_000);

        if (row.Frames > 0)
        {
            row.Yield += row.OutRate / row.Frames / 1_000;
        }
    }

    // ------------------------------------------------------------- generation

    private List<DemoRow> Build(int count)
    {
        string[] shots =
        {
            "atrium", "canyon", "harbour", "lantern", "meadow", "obsidian", "prairie", "quartz",
            "ridgeline", "solstice", "tundra", "vantage", "willow", "zephyr", "basalt", "cinder",
        };
        string[] takes = { "layout", "lighting", "fx", "comp", "grade", "matte" };
        string[] passes = { "beauty", "depth", "normals", "motion", "shadow", "ao" };

        // Long enough to trim in a 300 DIP column, so the cell's trimming is exercised beside the
        // short names that fit.
        string[] longShots =
        {
            "sequence-084-establishing-wide-with-volumetric-atmosphere-and-crowd-duplication",
            "sequence-112-hero-closeup-subsurface-skin-and-eye-refraction-turntable",
        };

        string[] faults =
        {
            "Worker 14 lost its licence lease and dropped the frame range.",
            "No space left on the output volume.",
            "Asset 'obsidian/rock_04.usd' failed to resolve on three workers.",
        };

        DateTimeOffset start = new(2024, 1, 1, 8, 0, 0, TimeSpan.Zero);
        List<DemoRow> rows = new(count);

        for (int i = 0; i < count; i++)
        {
            DemoState state = StateOf(i);
            long frames = 240 + (_random.Next(0, 96) * 30);
            double share = state switch
            {
                DemoState.Publishing or DemoState.QueuedToPublish => 1.0,
                DemoState.Verifying or DemoState.QueuedToVerify => 1.0,
                DemoState.Paused => _random.NextDouble(),
                DemoState.QueuedToRender => 0,
                _ => _random.NextDouble() * 0.98,
            };

            int workersTotal = _random.Next(4, 320);
            int workersBusy = state is DemoState.Rendering or DemoState.Publishing
                ? _random.Next(1, Math.Min(60, workersTotal))
                : 0;

            double inRate = state == DemoState.Rendering
                ? 60_000 + (_random.NextDouble() * 6_000_000)
                : 0;
            double outRate = state == DemoState.Publishing
                ? _random.NextDouble() * 2_000_000
                : inRate * 0.5;

            DateTimeOffset submitted = start.AddMinutes(_random.Next(1, 400_000));
            long done = (long)(frames * share);

            rows.Add(new DemoRow
            {
                Id = i.ToString("D7", CultureInfo.InvariantCulture),
                Index = i,
                Name = i % 97 == 3
                    ? longShots[(i / 97) % longShots.Length]
                    : $"{shots[i % shots.Length]}-{100 + (i % 900)}-" +
                      $"{takes[i % takes.Length]}-{passes[i % passes.Length]}",
                Frames = frames,
                FramesText = frames.ToString("N0", CultureInfo.CurrentCulture) + " frames",
                SubmittedText = submitted.ToString("yyyy-MM-dd HH:mm"),
                Fault = i % 173 == 5 ? faults[(i / 173) % faults.Length] : string.Empty,
                WorkersTotal = workersTotal,
                State = state,
                FramesDone = done,
                WorkersBusy = workersBusy,
                InRate = inRate,
                OutRate = outRate,
                Yield = state is DemoState.Publishing or DemoState.QueuedToPublish
                    ? Math.Round(_random.NextDouble() * 6, 2)
                    : Math.Round(_random.NextDouble() * 0.6, 2),
                Remaining = state == DemoState.Rendering && inRate > 1000
                    ? TimeSpan.FromSeconds(_random.Next(20, 400_000))
                    : null,
                Finished = share >= 1
                    ? submitted + TimeSpan.FromHours(_random.Next(1, 300))
                    : null,
            });
        }

        // Two jobs the farm has been asked to cancel and has not confirmed. They are display only.
        for (int pending = 0; pending < 2 && rows.Count > 0; pending++)
        {
            rows.Add(new DemoRow
            {
                Id = (count + pending).ToString("D7", CultureInfo.InvariantCulture),
                Index = count + pending,
                Name = pending == 0 ? "willow-471-comp-beauty" : "zephyr-118-grade-matte",
                Frames = 0,
                FramesText = "0 frames",
                SubmittedText = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"),
                Fault = string.Empty,
                WorkersTotal = 0,
                State = DemoState.Cancelling,
            });
        }

        return rows;
    }

    /// <summary>
    /// Which state a generated row starts in. Twenty is the cycle, so a screenful of about
    /// twenty-five rows shows every one of the eight running states without scrolling.
    /// </summary>
    private static DemoState StateOf(int index) => (index % 20) switch
    {
        < 7 => DemoState.Rendering,
        < 12 => DemoState.Publishing,
        < 14 => DemoState.Paused,
        14 => DemoState.Stalled,
        15 => DemoState.QueuedToRender,
        16 => DemoState.QueuedToPublish,
        17 => DemoState.Verifying,
        18 => DemoState.QueuedToVerify,
        _ => DemoState.Rendering,
    };
}
