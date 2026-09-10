using Spoolr.Core.Jobs;

namespace Spoolr.Core.Abstractions;

/// <summary>
/// Filter for listing print jobs. A null field means the field is not filtered on.
/// </summary>
public sealed record JobQuery
{
    public Guid? PrinterId { get; init; }

    public JobStatus? Status { get; init; }

    public string? SubmittedBy { get; init; }

    public int Skip { get; init; }

    public int Take { get; init; } = 50;
}

/// <summary>
/// Persistence for print jobs, including the claim path the dispatcher runs on.
/// </summary>
public interface IPrintJobStore
{
    Task AddAsync(PrintJob job, CancellationToken cancellationToken = default);

    Task<PrintJob?> FindAsync(Guid jobId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PrintJob>> ListAsync(JobQuery query, CancellationToken cancellationToken = default);

    Task<int> CountAsync(JobQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims the next job due for a printer and moves it to Dispatched.
    /// </summary>
    /// <remarks>
    /// Candidates are ordered by priority, then by submission time. The claim must be safe
    /// against other dispatcher replicas running the same query at the same instant: exactly
    /// one caller may win a given job.
    /// </remarks>
    /// <returns>The claimed job, or <see langword="null"/> if nothing is due.</returns>
    Task<PrintJob?> ClaimNextForPrinterAsync(
        Guid printerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns jobs stuck in Dispatched past <paramref name="dispatchTimeout"/> to the queue.
    /// </summary>
    /// <remarks>
    /// A printer can take a job and then lose power before acknowledging it. Without this
    /// sweep those jobs sit in Dispatched forever, because nothing else moves them.
    /// </remarks>
    /// <returns>How many jobs were recovered.</returns>
    Task<int> ReclaimStalledAsync(
        TimeSpan dispatchTimeout,
        TimeSpan requeueDelay,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>Counts jobs waiting to be claimed, for the queue-depth gauge.</summary>
    Task<int> CountClaimableAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
