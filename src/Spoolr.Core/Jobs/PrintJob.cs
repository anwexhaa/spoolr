using System.Collections.Frozen;

namespace Spoolr.Core.Jobs;

/// <summary>
/// A document queued for printing on a specific printer.
/// </summary>
/// <remarks>
/// The job owns its own lifecycle. Every state change goes through a method on this
/// type so an invalid transition fails loudly at the domain boundary instead of
/// silently corrupting a row. Callers supply the current time rather than reading the
/// clock here, which keeps retry scheduling deterministic under test.
/// </remarks>
public sealed class PrintJob
{
    /// <summary>
    /// The only transitions the lifecycle permits. Anything absent here throws.
    /// </summary>
    private static readonly FrozenDictionary<JobStatus, FrozenSet<JobStatus>> AllowedTransitions =
        new Dictionary<JobStatus, FrozenSet<JobStatus>>
        {
            [JobStatus.Queued] = new[] { JobStatus.Dispatched, JobStatus.Cancelled }.ToFrozenSet(),
            [JobStatus.Dispatched] = new[]
            {
                JobStatus.Printing,
                JobStatus.Queued,
                JobStatus.Failed,
                JobStatus.Cancelled,
            }.ToFrozenSet(),
            [JobStatus.Printing] = new[]
            {
                JobStatus.Completed,
                JobStatus.Queued,
                JobStatus.Failed,
                JobStatus.Cancelled,
            }.ToFrozenSet(),
            [JobStatus.Completed] = FrozenSet<JobStatus>.Empty,
            [JobStatus.Failed] = FrozenSet<JobStatus>.Empty,
            [JobStatus.Cancelled] = FrozenSet<JobStatus>.Empty,
        }.ToFrozenDictionary();

    private readonly List<JobAttempt> _attempts = [];

    /// <summary>Required by EF Core materialisation. Not for application use.</summary>
    private PrintJob()
    {
        DocumentName = string.Empty;
        SubmittedBy = string.Empty;
    }

    public Guid Id { get; private init; }

    public Guid PrinterId { get; private init; }

    public string DocumentName { get; private init; }

    public int PageCount { get; private init; }

    public JobPriority Priority { get; private init; }

    public string SubmittedBy { get; private init; }

    public JobStatus Status { get; private set; }

    /// <summary>How many times a dispatcher has handed this job to a printer.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>Total delivery attempts allowed before the job is failed for good.</summary>
    public int MaxAttempts { get; private init; }

    /// <summary>
    /// Earliest time a dispatcher may claim this job. Set to the submission time on
    /// arrival and pushed forward by the backoff delay after each failed attempt.
    /// </summary>
    public DateTimeOffset AvailableAt { get; private set; }

    public DateTimeOffset SubmittedAt { get; private init; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// The key the submitter sent to make the request safe to retry, if any. At most one
    /// job exists per submitter and key, so a retried request finds this job instead of
    /// creating a second one.
    /// </summary>
    public string? IdempotencyKey { get; private init; }

    /// <summary>
    /// Fingerprint of the request that created the job. A retry carrying the same key but a
    /// different body is a client bug, and this is what detects it.
    /// </summary>
    public string? RequestHash { get; private init; }

    /// <summary>
    /// Incremented on every mutation and mapped as an EF Core concurrency token, so two
    /// dispatcher replicas racing for the same job cannot both win the claim.
    /// </summary>
    public int Version { get; private set; }

    public IReadOnlyList<JobAttempt> Attempts => _attempts;

    /// <summary>True once the job has reached a state it can never leave.</summary>
    public bool IsTerminal => AllowedTransitions[Status].Count == 0;

    /// <summary>True while the job still has delivery attempts left to spend.</summary>
    public bool HasAttemptsRemaining => AttemptCount < MaxAttempts;

    /// <summary>
    /// Accepts a new job onto the queue.
    /// </summary>
    /// <param name="idempotencyKey">
    /// Optional client-chosen key that makes the submission safe to retry. When given,
    /// <paramref name="requestHash"/> is required, so a reused key can be told apart from a
    /// genuine retry.
    /// </param>
    public static PrintJob Submit(
        Guid printerId,
        string documentName,
        int pageCount,
        JobPriority priority,
        string submittedBy,
        DateTimeOffset now,
        int maxAttempts = 3,
        string? idempotencyKey = null,
        string? requestHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(submittedBy);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);

        if (idempotencyKey is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        }

        return new PrintJob
        {
            // Version 7 GUIDs are time-ordered, so inserts land at the end of the
            // index instead of scattering page splits across it.
            Id = Guid.CreateVersion7(now),
            PrinterId = printerId,
            DocumentName = documentName.Trim(),
            PageCount = pageCount,
            Priority = priority,
            SubmittedBy = submittedBy.Trim(),
            Status = JobStatus.Queued,
            AttemptCount = 0,
            MaxAttempts = maxAttempts,
            AvailableAt = now,
            SubmittedAt = now,
            IdempotencyKey = idempotencyKey,
            RequestHash = idempotencyKey is null ? null : requestHash,
        };
    }

    /// <summary>
    /// Claims the job for delivery to its printer and spends one attempt.
    /// </summary>
    public void Dispatch(DateTimeOffset now)
    {
        Transition(JobStatus.Dispatched);
        AttemptCount++;
        _attempts.Add(JobAttempt.Started(Id, AttemptCount, now));
    }

    /// <summary>
    /// Records that the printer picked the job up and started rendering it.
    /// </summary>
    public void Acknowledge(DateTimeOffset now)
    {
        Transition(JobStatus.Printing);
        CurrentAttempt()?.MarkAcknowledged(now);
    }

    /// <summary>
    /// Marks the job finished successfully. Terminal.
    /// </summary>
    public void Complete(DateTimeOffset now)
    {
        Transition(JobStatus.Completed);
        CompletedAt = now;
        LastError = null;
        CurrentAttempt()?.MarkSucceeded(now);
    }

    /// <summary>
    /// Records a failed attempt. Returns the job to the queue behind <paramref name="retryDelay"/>
    /// while attempts remain, otherwise fails it permanently.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the job was rescheduled, <see langword="false"/> if it is now terminal.
    /// </returns>
    public bool Fail(string reason, TimeSpan retryDelay, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero, nameof(retryDelay));

        CurrentAttempt()?.MarkFailed(reason, now);

        if (HasAttemptsRemaining)
        {
            Transition(JobStatus.Queued);
            AvailableAt = now + retryDelay;
            LastError = reason;
            return true;
        }

        Transition(JobStatus.Failed);
        CompletedAt = now;
        LastError = reason;
        return false;
    }

    /// <summary>
    /// Returns an in-flight job to the queue without spending an attempt, for cases where
    /// the printer never answered and the delivery itself is in doubt.
    /// </summary>
    public void ReturnToQueue(string reason, TimeSpan delay, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero, nameof(delay));

        Transition(JobStatus.Queued);
        AvailableAt = now + delay;
        LastError = reason;

        // The attempt is refunded: nothing confirms the printer ever saw this job.
        if (AttemptCount > 0)
        {
            AttemptCount--;
        }
    }

    /// <summary>
    /// Cancels the job at the request of the submitter. Terminal.
    /// </summary>
    public void Cancel(string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Transition(JobStatus.Cancelled);
        CompletedAt = now;
        LastError = reason;
        CurrentAttempt()?.MarkFailed(reason, now);
    }

    /// <summary>
    /// True when a dispatcher running at <paramref name="now"/> may claim this job.
    /// </summary>
    public bool IsClaimableAt(DateTimeOffset now) =>
        Status == JobStatus.Queued && AvailableAt <= now;

    private void Transition(JobStatus next)
    {
        if (!AllowedTransitions[Status].Contains(next))
        {
            throw new JobStateException(Id, Status, next);
        }

        Status = next;
        Version++;
    }

    private JobAttempt? CurrentAttempt() =>
        _attempts.Count == 0 ? null : _attempts[^1];
}
