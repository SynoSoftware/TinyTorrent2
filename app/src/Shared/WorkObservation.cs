namespace Syno.TinyTorrent.Shared;

internal sealed record WorkObservation(
    WorkDescription Description,
    WorkProgress Progress,
    WorkExecution Execution
);

internal sealed record WorkResult(
    WorkObservation Observation,
    WorkOutcome Outcome,
    Exception? Error,
    string? SummaryKey,
    object[] Arguments
);

internal abstract record WorkDescription
{
    private WorkDescription() { }

    internal sealed record Imdb(ImdbPhase Phase) : WorkDescription;

    internal sealed record City(CityPhase Phase) : WorkDescription;

    internal sealed record Metadata(MetadataScope Scope) : WorkDescription;

    internal sealed record Subtitles(SubtitleWork Work) : WorkDescription;

    internal sealed record FileProperties : WorkDescription;

    internal sealed record Update(UpdatePhase Phase) : WorkDescription;

    internal sealed record Sources : WorkDescription;

    internal sealed record Maintenance(MaintenanceWork Work) : WorkDescription;
}

internal abstract record WorkProgress
{
    private WorkProgress() { }

    internal sealed record Unknown : WorkProgress;

    internal sealed record Amount : WorkProgress
    {
        internal double Completed { get; }
        internal WorkUnit Unit { get; }

        internal Amount(double completed, WorkUnit unit)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(completed);
            if (!double.IsFinite(completed))
                throw new ArgumentOutOfRangeException(nameof(completed));
            Completed = completed;
            Unit = unit;
        }
    }

    internal sealed record Fraction : WorkProgress
    {
        internal double Completed { get; }
        internal double Total { get; }
        internal WorkUnit Unit { get; }
        internal bool Approximate { get; }

        internal Fraction(double completed, double total, WorkUnit unit, bool approximate = false)
        {
            if (!double.IsFinite(total) || total <= 0)
                throw new ArgumentOutOfRangeException(nameof(total));
            if (!double.IsFinite(completed) || completed < 0 || completed > total)
                throw new ArgumentOutOfRangeException(nameof(completed));
            Completed = completed;
            Total = total;
            Unit = unit;
            Approximate = approximate;
        }
    }
}

internal abstract record WorkExecution
{
    private WorkExecution() { }

    internal bool CanCancel =>
        this is Running { Cancellable: true } or Waiting { Cancellable: true };

    internal sealed record Running(bool Cancellable = true) : WorkExecution;

    internal sealed record Waiting(bool Cancellable = true) : WorkExecution
    {
        internal DateTimeOffset? RetryAt { get; init; }
    }

    internal sealed record Cancelling : WorkExecution;

    internal sealed record Finishing : WorkExecution;
}
