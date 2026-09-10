using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Spoolr.Core.Abstractions;
using Spoolr.Core.Jobs;
using Spoolr.Infrastructure.Persistence;

namespace Spoolr.UnitTests.Persistence;

/// <summary>
/// Exercises the store against a real relational engine rather than an in-memory fake, so
/// the mappings, indexes, and concurrency token are the ones that actually ship.
/// </summary>
public sealed class EfPrintJobStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid PrinterId = Guid.CreateVersion7();
    private static readonly Guid OtherPrinterId = Guid.CreateVersion7();

    private SqliteConnection _connection = null!;

    public async Task InitializeAsync()
    {
        // Held open for the life of the test: an in-memory SQLite database exists only as
        // long as a connection to it does.
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task ClaimNext_ReturnsNothingWhenTheQueueIsEmpty()
    {
        await using var db = NewContext();

        Assert.Null(await NewStore(db).ClaimNextForPrinterAsync(PrinterId, Now));
    }

    [Fact]
    public async Task ClaimNext_SkipsJobsThatAreNotDueYet()
    {
        var job = NewJob();
        job.Dispatch(Now);
        job.Fail("paper jam", TimeSpan.FromMinutes(10), Now);
        await Seed(job);

        await using var db = NewContext();
        var store = NewStore(db);

        Assert.Null(await store.ClaimNextForPrinterAsync(PrinterId, Now.AddMinutes(9)));
        Assert.NotNull(await store.ClaimNextForPrinterAsync(PrinterId, Now.AddMinutes(10)));
    }

    [Fact]
    public async Task ClaimNext_PrefersHigherPriorityOverEarlierSubmission()
    {
        // Submitted oldest first, so anything ordering purely by time picks the wrong one.
        await Seed(
            NewJob(JobPriority.Low, "low.pdf", Now),
            NewJob(JobPriority.Normal, "normal.pdf", Now.AddSeconds(1)),
            NewJob(JobPriority.High, "high.pdf", Now.AddSeconds(2)));

        await using var db = NewContext();

        var claimed = await NewStore(db).ClaimNextForPrinterAsync(PrinterId, Now.AddMinutes(1));

        Assert.NotNull(claimed);
        Assert.Equal("high.pdf", claimed.DocumentName);
    }

    [Fact]
    public async Task ClaimNext_BreaksPriorityTiesByEarliestSubmission()
    {
        await Seed(
            NewJob(JobPriority.Normal, "second.pdf", Now.AddSeconds(10)),
            NewJob(JobPriority.Normal, "first.pdf", Now));

        await using var db = NewContext();

        var claimed = await NewStore(db).ClaimNextForPrinterAsync(PrinterId, Now.AddMinutes(1));

        Assert.NotNull(claimed);
        Assert.Equal("first.pdf", claimed.DocumentName);
    }

    [Fact]
    public async Task ClaimNext_IgnoresWorkQueuedForADifferentPrinter()
    {
        await Seed(NewJob(printerId: OtherPrinterId));

        await using var db = NewContext();

        Assert.Null(await NewStore(db).ClaimNextForPrinterAsync(PrinterId, Now));
    }

    [Fact]
    public async Task ClaimNext_MovesTheJobToDispatchedAndSpendsAnAttempt()
    {
        await Seed(NewJob());

        await using (var db = NewContext())
        {
            await NewStore(db).ClaimNextForPrinterAsync(PrinterId, Now);
        }

        await using var verify = NewContext();
        var stored = await verify.Jobs.SingleAsync();

        Assert.Equal(JobStatus.Dispatched, stored.Status);
        Assert.Equal(1, stored.AttemptCount);
    }

    [Fact]
    public async Task ConcurrencyToken_RejectsAStaleWriteFromASecondReplica()
    {
        await Seed(NewJob());

        // Both replicas read the same row before either writes.
        await using var replicaA = NewContext();
        await using var replicaB = NewContext();

        var seenByA = await replicaA.Jobs.SingleAsync();
        var seenByB = await replicaB.Jobs.SingleAsync();

        // A cancels rather than dispatches, so the only thing B can collide with is the
        // version column. This isolates the token from the unique index below.
        seenByA.Cancel("withdrawn by submitter", Now);
        await replicaA.SaveChangesAsync();

        seenByB.Dispatch(Now);

        // B is writing against a version that no longer exists. Without the token it would
        // silently overwrite A and print a job the submitter had already withdrawn.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => replicaB.SaveChangesAsync());
    }

    [Fact]
    public async Task AttemptNumber_IsUniquePerJobAsASecondDefence()
    {
        await Seed(NewJob());

        await using var replicaA = NewContext();
        await using var replicaB = NewContext();

        var seenByA = await replicaA.Jobs.SingleAsync();
        var seenByB = await replicaB.Jobs.SingleAsync();

        seenByA.Dispatch(Now);
        await replicaA.SaveChangesAsync();

        seenByB.Dispatch(Now);

        // Both replicas believe they are making attempt 1. Whichever statement the batch
        // reaches first, the write is refused: here the unique index rejects it before the
        // version check is even evaluated, which is why the store catches the base type.
        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => replicaB.SaveChangesAsync());

        Assert.Contains("UNIQUE", ex.InnerException?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClaimNext_HandsASingleJobToExactlyOneCaller()
    {
        await Seed(NewJob());

        await using var first = NewContext();
        await using var second = NewContext();

        var claimedByFirst = await NewStore(first).ClaimNextForPrinterAsync(PrinterId, Now);
        var claimedBySecond = await NewStore(second).ClaimNextForPrinterAsync(PrinterId, Now);

        Assert.NotNull(claimedByFirst);
        Assert.Null(claimedBySecond);
    }

    [Fact]
    public async Task ReclaimStalled_ReturnsUnacknowledgedDispatchesToTheQueue()
    {
        var job = NewJob();
        job.Dispatch(Now);
        await Seed(job);

        await using var db = NewContext();
        var store = NewStore(db);

        var recovered = await store.ReclaimStalledAsync(
            dispatchTimeout: TimeSpan.FromSeconds(60),
            requeueDelay: TimeSpan.FromSeconds(5),
            now: Now.AddSeconds(90));

        Assert.Equal(1, recovered);

        await using var verify = NewContext();
        var stored = await verify.Jobs.SingleAsync();

        Assert.Equal(JobStatus.Queued, stored.Status);

        // The attempt is refunded, so a printer that died does not burn the job's retries.
        Assert.Equal(0, stored.AttemptCount);
        Assert.Equal(Now.AddSeconds(95), stored.AvailableAt);
    }

    [Fact]
    public async Task ReclaimStalled_LeavesADispatchInsideTheTimeoutAlone()
    {
        var job = NewJob();
        job.Dispatch(Now);
        await Seed(job);

        await using var db = NewContext();

        var recovered = await NewStore(db).ReclaimStalledAsync(
            dispatchTimeout: TimeSpan.FromSeconds(60),
            requeueDelay: TimeSpan.FromSeconds(5),
            now: Now.AddSeconds(30));

        Assert.Equal(0, recovered);
    }

    [Fact]
    public async Task ReclaimStalled_LeavesAcknowledgedJobsAlone()
    {
        var job = NewJob();
        job.Dispatch(Now);
        job.Acknowledge(Now.AddSeconds(1));
        await Seed(job);

        await using var db = NewContext();

        var recovered = await NewStore(db).ReclaimStalledAsync(
            dispatchTimeout: TimeSpan.FromSeconds(60),
            requeueDelay: TimeSpan.FromSeconds(5),
            now: Now.AddHours(1));

        Assert.Equal(0, recovered);
    }

    [Fact]
    public async Task CountClaimable_CountsOnlyJobsThatAreDue()
    {
        var due = NewJob(documentName: "due.pdf");

        var held = NewJob(documentName: "held.pdf");
        held.Dispatch(Now);
        held.Fail("jam", TimeSpan.FromMinutes(30), Now);

        var running = NewJob(documentName: "running.pdf");
        running.Dispatch(Now);

        await Seed(due, held, running);

        await using var db = NewContext();

        Assert.Equal(1, await NewStore(db).CountClaimableAsync(Now));
    }

    [Fact]
    public async Task List_FiltersByStatusAndPrinter()
    {
        var queued = NewJob(documentName: "queued.pdf");

        var dispatched = NewJob(documentName: "dispatched.pdf");
        dispatched.Dispatch(Now);

        await Seed(queued, dispatched, NewJob(printerId: OtherPrinterId));

        await using var db = NewContext();
        var store = NewStore(db);

        var forPrinter = await store.ListAsync(new JobQuery { PrinterId = PrinterId });
        Assert.Equal(2, forPrinter.Count);

        var onlyQueued = await store.ListAsync(new JobQuery { PrinterId = PrinterId, Status = JobStatus.Queued });
        Assert.Equal("queued.pdf", Assert.Single(onlyQueued).DocumentName);
    }

    private static PrintJob NewJob(
        JobPriority priority = JobPriority.Normal,
        string documentName = "report.pdf",
        DateTimeOffset? submittedAt = null,
        Guid? printerId = null) =>
        PrintJob.Submit(
            printerId ?? PrinterId,
            documentName,
            pageCount: 3,
            priority,
            submittedBy: "anwesha@contoso.com",
            submittedAt ?? Now);

    private async Task Seed(params PrintJob[] jobs)
    {
        await using var db = NewContext();
        db.Jobs.AddRange(jobs);
        await db.SaveChangesAsync();
    }

    private SpoolrDbContext NewContext() =>
        new(new DbContextOptionsBuilder<SpoolrDbContext>().UseSqlite(_connection).Options);

    private static EfPrintJobStore NewStore(SpoolrDbContext db) =>
        new(db, NullLogger<EfPrintJobStore>.Instance);
}
