using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Spoolr.Core.Abstractions;
using Spoolr.Core.Jobs;

namespace Spoolr.Infrastructure.Persistence;

/// <summary>
/// Entity Framework Core implementation of <see cref="IPrintJobStore"/>.
/// </summary>
public sealed class EfPrintJobStore(SpoolrDbContext db, ILogger<EfPrintJobStore> logger) : IPrintJobStore
{
    /// <summary>
    /// How many times a poll will re-target after losing a claim race before giving up.
    /// Losing repeatedly means the queue is busy, not that anything is broken, so the
    /// caller simply polls again.
    /// </summary>
    private const int MaxClaimAttempts = 5;

    /// <summary>Upper bound on jobs recovered per sweep, to keep one pass bounded.</summary>
    private const int ReclaimBatchSize = 100;

    public async Task AddAsync(PrintJob job, CancellationToken cancellationToken = default) =>
        await db.Jobs.AddAsync(job, cancellationToken);

    public async Task<PrintJob?> FindAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        await db.Jobs
            .Include(j => j.Attempts)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

    public async Task<PrintJob?> FindByIdempotencyKeyAsync(
        string submittedBy,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        await db.Jobs
            .Include(j => j.Attempts)
            .FirstOrDefaultAsync(
                j => j.SubmittedBy == submittedBy && j.IdempotencyKey == idempotencyKey,
                cancellationToken);

    public async Task<PrintJob> AddOnceAsync(PrintJob job, CancellationToken cancellationToken = default)
    {
        if (job.IdempotencyKey is not { } key)
        {
            throw new ArgumentException("Only a job that carries an idempotency key can be added once.", nameof(job));
        }

        await db.Jobs.AddAsync(job, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return job;
        }
        catch (DbUpdateException ex)
        {
            Detach(job);

            // The unique index on (submitter, key) is what refuses a second insert, so a
            // failure here is usually a concurrent request with the same key that committed
            // first. If no such job exists, the failure was something else and is not ours
            // to swallow.
            var winner = await FindByIdempotencyKeyAsync(job.SubmittedBy, key, cancellationToken);

            if (winner is null)
            {
                throw;
            }

            logger.LogDebug(
                ex,
                "Lost an idempotency-key race for key {IdempotencyKey}; returning job {JobId}.",
                key,
                winner.Id);

            return winner;
        }
    }

    public async Task<IReadOnlyList<PrintJob>> ListAsync(
        JobQuery query,
        CancellationToken cancellationToken = default) =>
        await Filter(query)
            .OrderByDescending(j => j.SubmittedAt)
            .Skip(query.Skip)
            .Take(query.Take)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<int> CountAsync(JobQuery query, CancellationToken cancellationToken = default) =>
        await Filter(query).CountAsync(cancellationToken);

    public async Task<int> CountClaimableAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        await db.Jobs
            .Where(j => j.Status == JobStatus.Queued && j.AvailableAt <= now)
            .CountAsync(cancellationToken);

    public async Task<PrintJob?> ClaimNextForPrinterAsync(
        Guid printerId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        for (var round = 0; round < MaxClaimAttempts; round++)
        {
            var candidate = await db.Jobs
                .Where(j => j.PrinterId == printerId
                    && j.Status == JobStatus.Queued
                    && j.AvailableAt <= now)
                .OrderByDescending(j => j.Priority)
                .ThenBy(j => j.SubmittedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (candidate is null)
            {
                return null;
            }

            candidate.Dispatch(now);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return candidate;
            }
            catch (DbUpdateException ex)
            {
                // Another replica claimed this job between our read and our write. Two
                // defences can report that, and either is a lost race:
                //
                //   DbUpdateConcurrencyException  the version column moved under us
                //   a unique violation            the winner already wrote attempt N
                //
                // Which one surfaces depends on the order of statements in the batch, so
                // catching only the first would let the second escape as a request failure.
                // Nothing else is written in this unit of work, so any failure here means
                // the claim did not land.
                logger.LogDebug(
                    ex,
                    "Lost claim race for job {JobId} on printer {PrinterId}, retargeting.",
                    candidate.Id,
                    printerId);

                Detach(candidate);
            }
        }

        logger.LogInformation(
            "Gave up claiming for printer {PrinterId} after {Rounds} contended rounds.",
            printerId,
            MaxClaimAttempts);

        return null;
    }

    public async Task<int> ReclaimStalledAsync(
        TimeSpan dispatchTimeout,
        TimeSpan requeueDelay,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var cutoff = now - dispatchTimeout;

        var stalled = await db.Jobs
            .Include(j => j.Attempts)
            .Where(j => j.Status == JobStatus.Dispatched
                && j.Attempts.Any(a => a.FinishedAt == null && a.StartedAt <= cutoff))
            .Take(ReclaimBatchSize)
            .ToListAsync(cancellationToken);

        if (stalled.Count == 0)
        {
            return 0;
        }

        foreach (var job in stalled)
        {
            job.ReturnToQueue("Printer did not acknowledge before the dispatch timeout.", requeueDelay, now);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The printer acknowledged mid-sweep. It is not stalled after all, and the
            // next sweep will see the correct state.
            logger.LogDebug("Stalled-job sweep raced a live acknowledgement; skipping this batch.");
            foreach (var job in stalled)
            {
                Detach(job);
            }

            return 0;
        }

        logger.LogWarning(
            "Returned {Count} job(s) to the queue after the dispatch timeout elapsed.",
            stalled.Count);

        return stalled.Count;
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        await db.SaveChangesAsync(cancellationToken);

    private IQueryable<PrintJob> Filter(JobQuery query)
    {
        var jobs = db.Jobs.AsQueryable();

        if (query.PrinterId is { } printerId)
        {
            jobs = jobs.Where(j => j.PrinterId == printerId);
        }

        if (query.Status is { } status)
        {
            jobs = jobs.Where(j => j.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.SubmittedBy))
        {
            jobs = jobs.Where(j => j.SubmittedBy == query.SubmittedBy);
        }

        return jobs;
    }

    /// <summary>
    /// Detaches a job and the attempts tracked alongside it, leaving any unrelated tracked
    /// work in this context untouched.
    /// </summary>
    private void Detach(PrintJob job)
    {
        foreach (var attempt in db.ChangeTracker.Entries<JobAttempt>()
                     .Where(e => e.Entity.JobId == job.Id)
                     .ToList())
        {
            attempt.State = EntityState.Detached;
        }

        db.Entry(job).State = EntityState.Detached;
    }
}
