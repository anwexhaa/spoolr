namespace Spoolr.Core.Jobs;

/// <summary>
/// One delivery attempt for a print job, kept so a support engineer can reconstruct
/// what happened to a job without replaying application logs.
/// </summary>
public sealed class JobAttempt
{
    /// <summary>Required by EF Core materialisation. Not for application use.</summary>
    private JobAttempt()
    {
    }

    public Guid Id { get; private init; }

    public Guid JobId { get; private init; }

    /// <summary>One-based attempt number within the parent job.</summary>
    public int AttemptNumber { get; private init; }

    public DateTimeOffset StartedAt { get; private init; }

    /// <summary>When the printer confirmed it had taken the job, if it ever did.</summary>
    public DateTimeOffset? AcknowledgedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public bool Succeeded { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Wall-clock time this attempt was in flight, once it has finished.</summary>
    public TimeSpan? Duration => FinishedAt is null ? null : FinishedAt - StartedAt;

    internal static JobAttempt Started(Guid jobId, int attemptNumber, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            JobId = jobId,
            AttemptNumber = attemptNumber,
            StartedAt = now,
        };

    internal void MarkAcknowledged(DateTimeOffset now) => AcknowledgedAt = now;

    internal void MarkSucceeded(DateTimeOffset now)
    {
        FinishedAt = now;
        Succeeded = true;
        Error = null;
    }

    internal void MarkFailed(string error, DateTimeOffset now)
    {
        // An attempt that already finished keeps its original outcome.
        if (FinishedAt is not null)
        {
            return;
        }

        FinishedAt = now;
        Succeeded = false;
        Error = error;
    }
}
