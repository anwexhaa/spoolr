using System.Security.Claims;
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
            .ProducesProblem(StatusCodes.Status409Conflict);

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

    private static async Task<IResult> SubmitAsync(
        SubmitJobRequest request,
        ClaimsPrincipal user,
        IPrintJobStore jobs,
        IPrinterStore printers,
        IJobEventPublisher events,
        IOptions<RetryOptions> retryOptions,
        SpoolrMetrics metrics,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
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
            SubmitterOf(user),
            now,
            retryOptions.Value.MaxAttempts);

        await jobs.AddAsync(job, cancellationToken);
        await jobs.SaveChangesAsync(cancellationToken);

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

    private static string SubmitterOf(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.Name)
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "unknown";
}
