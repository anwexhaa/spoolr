using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Spoolr.Api.Authentication;
using Spoolr.Api.Contracts;
using Spoolr.Core.Abstractions;
using Spoolr.Core.Jobs;
using Spoolr.Core.Printers;
using Spoolr.Infrastructure.Configuration;
using Spoolr.Infrastructure.Observability;

namespace Spoolr.Api.Endpoints;

/// <summary>
/// Endpoints people use: submitting work and asking what happened to it.
/// </summary>
public static class JobEndpoints
{
    /// <summary>Request header that makes a submission safe to retry.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Response header set when a submission was answered from an earlier request.</summary>
    public const string ReplayedHeader = "Idempotent-Replayed";

    private const int MaxIdempotencyKeyLength = 255;

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/api/v1/jobs")
            .RequireAuthorization(SpoolrPolicies.Operator)
            .WithTags("Jobs");

        jobs.MapPost("/", SubmitAsync)
            .WithName("SubmitJob")
            .WithSummary("Queues a document for printing.")
            .ValidatingBody<SubmitJobRequest>()
            .Produces<JobResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        jobs.MapGet("/{jobId:guid}", GetAsync)
            .WithName("GetJob")
            .WithSummary("Returns a job and its delivery attempts.")
            .Produces<JobResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        jobs.MapGet("/", ListAsync)
            .WithName("ListJobs")
            .WithSummary("Lists jobs, most recently submitted first.")
            .Produces<PagedResponse<JobResponse>>();

        jobs.MapPost("/{jobId:guid}/cancel", CancelAsync)
            .WithName("CancelJob")
            .WithSummary("Withdraws a job that has not finished.")
            .Produces<JobResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    /// <remarks>
    /// A client that times out waiting for this call cannot tell whether the job was queued.
    /// Retrying without a key risks printing the document twice; retrying with the same
    /// <c>Idempotency-Key</c> is always safe. The first request creates the job, and every
    /// retry gets that same job back, marked with <c>Idempotent-Replayed: true</c>.
    /// </remarks>
    private static async Task<IResult> SubmitAsync(
        SubmitJobRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        ClaimsPrincipal user,
        HttpResponse response,
        IPrintJobStore jobs,
        IPrinterStore printers,
        IJobEventPublisher events,
        IOptions<RetryOptions> retryOptions,
        SpoolrMetrics metrics,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var submitter = SubmitterOf(user);
        var fingerprint = request.Fingerprint();

        if (idempotencyKey is not null)
        {
            if (!IsValidIdempotencyKey(idempotencyKey))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    [IdempotencyKeyHeader] =
                    [
                        $"Must be 1 to {MaxIdempotencyKeyLength} printable ASCII characters with no spaces.",
                    ],
                });
            }

            // Checked before anything else: a retry must get the original answer even if the
            // printer has been retired since, or the state it was validated against has moved.
            var prior = await jobs.FindByIdempotencyKeyAsync(submitter, idempotencyKey, cancellationToken);

            if (prior is not null)
            {
                return Replay(prior, fingerprint, response);
            }
        }

        var printer = await printers.FindAsync(request.PrinterId, cancellationToken);

        if (printer is null)
        {
            return TypedResults.Problem(
                $"No printer is registered with id {request.PrinterId}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (printer.ReportedStatus == PrinterStatus.Retired)
        {
            return TypedResults.Problem(
                $"Printer {printer.Name} has been retired and accepts no new work.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // Rejected here rather than at the device, so the submitter finds out immediately
        // instead of after the job has queued, dispatched, and failed three times.
        if (request.PageCount > printer.MaxPagesPerJob)
        {
            return TypedResults.Problem(
                $"Printer {printer.Name} accepts at most {printer.MaxPagesPerJob} pages per job.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var now = clock.GetUtcNow();

        var job = PrintJob.Submit(
            request.PrinterId,
            request.DocumentName,
            request.PageCount,
            request.Priority,
            submitter,
            now,
            retryOptions.Value.MaxAttempts,
            idempotencyKey,
            fingerprint);

        if (idempotencyKey is null)
        {
            await jobs.AddAsync(job, cancellationToken);
            await jobs.SaveChangesAsync(cancellationToken);
        }
        else
        {
            // A concurrent request with the same key may have committed between the lookup
            // above and this insert. The store lets exactly one of them create the job.
            var owner = await jobs.AddOnceAsync(job, cancellationToken);

            if (owner.Id != job.Id)
            {
                return Replay(owner, fingerprint, response);
            }
        }

        metrics.JobSubmitted(job.Priority);
        await events.PublishAsync(JobStateChanged.From(job, now), cancellationToken);

        return TypedResults.Created($"/api/v1/jobs/{job.Id}", JobResponse.From(job));
    }

    private static async Task<IResult> GetAsync(
        Guid jobId,
        IPrintJobStore jobs,
        CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(jobId, cancellationToken);

        return job is null
            ? TypedResults.Problem($"No job with id {jobId}.", statusCode: StatusCodes.Status404NotFound)
            : TypedResults.Ok(JobResponse.From(job));
    }

    private static async Task<IResult> ListAsync(
        IPrintJobStore jobs,
        CancellationToken cancellationToken,
        Guid? printerId = null,
        JobStatus? status = null,
        int skip = 0,
        int take = 50)
    {
        var query = new JobQuery
        {
            PrinterId = printerId,
            Status = status,
            Skip = Math.Max(skip, 0),

            // Clamped rather than rejected: a caller asking for ten thousand rows gets a
            // page, not an error, and the database is never asked for the whole table.
            Take = Math.Clamp(take, 1, 200),
        };

        var page = await jobs.ListAsync(query, cancellationToken);
        var total = await jobs.CountAsync(query, cancellationToken);

        return TypedResults.Ok(new PagedResponse<JobResponse>(
            [.. page.Select(JobResponse.From)],
            total,
            query.Skip,
            query.Take));
    }

    private static async Task<IResult> CancelAsync(
        Guid jobId,
        CancelJobRequest? request,
        IPrintJobStore jobs,
        IJobEventPublisher events,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(jobId, cancellationToken);

        if (job is null)
        {
            return TypedResults.Problem($"No job with id {jobId}.", statusCode: StatusCodes.Status404NotFound);
        }

        if (job.IsTerminal)
        {
            return TypedResults.Problem(
                $"Job {jobId} already finished as {job.Status} and cannot be cancelled.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var now = clock.GetUtcNow();

        job.Cancel(
            string.IsNullOrWhiteSpace(request?.Reason) ? "Cancelled by the submitter." : request.Reason,
            now);

        await jobs.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(JobStateChanged.From(job, now), cancellationToken);

        return TypedResults.Ok(JobResponse.From(job));
    }

    /// <summary>
    /// Answers a retried submission with the job its key already created. Nothing is
    /// queued, counted, or published a second time.
    /// </summary>
    private static IResult Replay(PrintJob prior, string fingerprint, HttpResponse response)
    {
        if (!string.Equals(prior.RequestHash, fingerprint, StringComparison.Ordinal))
        {
            // Same key, different request. Returning the earlier job would tell the client
            // its new request succeeded when it was never run, so refuse instead.
            return TypedResults.Problem(
                "This Idempotency-Key was already used for a different request. Use a new key for a new job.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        response.Headers[ReplayedHeader] = "true";

        return TypedResults.Created($"/api/v1/jobs/{prior.Id}", JobResponse.From(prior));
    }

    private static bool IsValidIdempotencyKey(string key) =>
        key.Length is > 0 and <= MaxIdempotencyKeyLength
        && key.All(c => c is > ' ' and <= '~');

    private static string SubmitterOf(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Name)
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "unknown";
}
