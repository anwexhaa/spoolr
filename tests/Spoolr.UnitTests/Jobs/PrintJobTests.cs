using Spoolr.Core.Jobs;

namespace Spoolr.UnitTests.Jobs;

public sealed class PrintJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid PrinterId = Guid.CreateVersion7();

    private static PrintJob NewJob(int maxAttempts = 3, JobPriority priority = JobPriority.Normal) =>
        PrintJob.Submit(
            PrinterId,
            "quarterly-report.pdf",
            pageCount: 12,
            priority,
            submittedBy: "anwesha@contoso.com",
            Now,
            maxAttempts);

    [Fact]
    public void Submit_StartsQueuedAndImmediatelyClaimable()
    {
        var job = NewJob();

        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(0, job.AttemptCount);
        Assert.Equal(Now, job.AvailableAt);
        Assert.True(job.IsClaimableAt(Now));
        Assert.False(job.IsTerminal);
        Assert.Null(job.CompletedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Submit_RejectsBlankDocumentName(string documentName) =>
        Assert.Throws<ArgumentException>(() => PrintJob.Submit(
            PrinterId, documentName, 1, JobPriority.Normal, "user@contoso.com", Now));

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void Submit_RejectsNonPositivePageCount(int pageCount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PrintJob.Submit(
            PrinterId, "doc.pdf", pageCount, JobPriority.Normal, "user@contoso.com", Now));

    [Fact]
    public void Submit_TrimsFreeTextFields()
    {
        var job = PrintJob.Submit(
            PrinterId, "  doc.pdf  ", 1, JobPriority.Low, "  user@contoso.com  ", Now);

        Assert.Equal("doc.pdf", job.DocumentName);
        Assert.Equal("user@contoso.com", job.SubmittedBy);
    }

    [Fact]
    public void Dispatch_SpendsAnAttemptAndOpensAnAttemptRecord()
    {
        var job = NewJob();

        job.Dispatch(Now);

        Assert.Equal(JobStatus.Dispatched, job.Status);
        Assert.Equal(1, job.AttemptCount);
        Assert.Single(job.Attempts);
        Assert.Equal(1, job.Attempts[0].AttemptNumber);
        Assert.Null(job.Attempts[0].FinishedAt);
    }

    [Fact]
    public void FullLifecycle_ReachesCompletedAndClearsLastError()
    {
        var job = NewJob();

        job.Dispatch(Now);
        job.Acknowledge(Now.AddSeconds(1));
        job.Complete(Now.AddSeconds(30));

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.True(job.IsTerminal);
        Assert.Equal(Now.AddSeconds(30), job.CompletedAt);
        Assert.Null(job.LastError);

        var attempt = Assert.Single(job.Attempts);
        Assert.True(attempt.Succeeded);
        Assert.Equal(Now.AddSeconds(1), attempt.AcknowledgedAt);
        Assert.Equal(TimeSpan.FromSeconds(30), attempt.Duration);
    }

    [Fact]
    public void Fail_WithAttemptsRemaining_RequeuesBehindTheBackoffDelay()
    {
        var job = NewJob(maxAttempts: 3);
        job.Dispatch(Now);

        var rescheduled = job.Fail("paper jam", TimeSpan.FromSeconds(45), Now);

        Assert.True(rescheduled);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal("paper jam", job.LastError);
        Assert.Equal(Now.AddSeconds(45), job.AvailableAt);
        Assert.False(job.IsTerminal);

        // Still held back until the backoff window elapses.
        Assert.False(job.IsClaimableAt(Now.AddSeconds(44)));
        Assert.True(job.IsClaimableAt(Now.AddSeconds(45)));
    }

    [Fact]
    public void Fail_OnFinalAttempt_IsTerminalAndKeepsTheError()
    {
        var job = NewJob(maxAttempts: 2);

        job.Dispatch(Now);
        Assert.True(job.Fail("offline", TimeSpan.FromSeconds(1), Now));

        job.Dispatch(Now.AddSeconds(1));
        var rescheduled = job.Fail("offline", TimeSpan.FromSeconds(1), Now.AddSeconds(2));

        Assert.False(rescheduled);
        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.True(job.IsTerminal);
        Assert.False(job.HasAttemptsRemaining);
        Assert.Equal("offline", job.LastError);
        Assert.Equal(2, job.Attempts.Count);
        Assert.All(job.Attempts, a => Assert.False(a.Succeeded));
    }

    [Fact]
    public void ReturnToQueue_RefundsTheAttemptBecauseDeliveryIsUnconfirmed()
    {
        var job = NewJob();
        job.Dispatch(Now);

        job.ReturnToQueue("printer never acknowledged", TimeSpan.FromSeconds(10), Now);

        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal(0, job.AttemptCount);
        Assert.True(job.HasAttemptsRemaining);
        Assert.Equal(Now.AddSeconds(10), job.AvailableAt);
    }

    [Fact]
    public void Cancel_FromQueued_IsTerminal()
    {
        var job = NewJob();

        job.Cancel("withdrawn by submitter", Now);

        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.True(job.IsTerminal);
        Assert.Equal(Now, job.CompletedAt);
    }

    [Fact]
    public void Cancel_MidPrint_IsAllowed()
    {
        var job = NewJob();
        job.Dispatch(Now);
        job.Acknowledge(Now);

        job.Cancel("withdrawn by submitter", Now.AddSeconds(5));

        Assert.Equal(JobStatus.Cancelled, job.Status);
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Cancelled)]
    public void TerminalJob_RefusesFurtherTransitions(JobStatus terminal)
    {
        var job = NewJob();
        job.Dispatch(Now);
        job.Acknowledge(Now);

        if (terminal == JobStatus.Completed)
        {
            job.Complete(Now);
        }
        else
        {
            job.Cancel("withdrawn", Now);
        }

        Assert.Throws<JobStateException>(() => job.Dispatch(Now));
        Assert.Throws<JobStateException>(() => job.Complete(Now));
        Assert.Throws<JobStateException>(() => job.Cancel("again", Now));
    }

    [Fact]
    public void Acknowledge_BeforeDispatch_Throws()
    {
        var job = NewJob();

        var ex = Assert.Throws<JobStateException>(() => job.Acknowledge(Now));

        Assert.Equal(JobStatus.Queued, ex.From);
        Assert.Equal(JobStatus.Printing, ex.To);
        Assert.Equal(job.Id, ex.JobId);
    }

    [Fact]
    public void Dispatch_Twice_WithoutResolution_Throws()
    {
        var job = NewJob();
        job.Dispatch(Now);

        Assert.Throws<JobStateException>(() => job.Dispatch(Now));
        Assert.Equal(1, job.AttemptCount);
    }

    [Fact]
    public void Version_IncrementsOnEveryTransitionForConcurrencyChecks()
    {
        var job = NewJob();
        var initial = job.Version;

        job.Dispatch(Now);
        job.Acknowledge(Now);
        job.Complete(Now);

        Assert.Equal(initial + 3, job.Version);
    }

    [Fact]
    public void Id_IsTimeOrderedAcrossSubmissions()
    {
        var first = PrintJob.Submit(PrinterId, "a.pdf", 1, JobPriority.Normal, "u@c.com", Now);
        var second = PrintJob.Submit(PrinterId, "b.pdf", 1, JobPriority.Normal, "u@c.com", Now.AddSeconds(1));

        // Version 7 puts a 48-bit big-endian millisecond timestamp in the leading bytes,
        // which is the part an index actually orders on.
        var firstStamp = first.Id.ToByteArray(bigEndian: true)[..6];
        var secondStamp = second.Id.ToByteArray(bigEndian: true)[..6];

        Assert.True(
            firstStamp.AsSpan().SequenceCompareTo(secondStamp) < 0,
            "Version 7 GUIDs should sort by submission time so inserts stay sequential.");
    }
}
