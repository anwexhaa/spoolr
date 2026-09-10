namespace Spoolr.Core.Jobs;

/// <summary>
/// Thrown when a caller attempts a state transition the job lifecycle does not allow.
/// </summary>
public sealed class JobStateException : InvalidOperationException
{
    public JobStateException(Guid jobId, JobStatus from, JobStatus to)
        : base($"Print job {jobId} cannot move from {from} to {to}.")
    {
        JobId = jobId;
        From = from;
        To = to;
    }

    public Guid JobId { get; }

    public JobStatus From { get; }

    public JobStatus To { get; }
}
