using System.Security.Claims;
using Spoolr.Api.Authentication;
using Spoolr.Api.Contracts;
using Spoolr.Core.Abstractions;
using Spoolr.Core.Jobs;
using Spoolr.Core.Resilience;
using Spoolr.Infrastructure.Observability;

namespace Spoolr.Api.Endpoints;

/// <summary>
/// Endpoints printers call for themselves.
/// </summary>
/// <remarks>
/// Every handler takes the printer identity from the authenticated device rather than from
/// the route, so one printer cannot poll for, acknowledge, or complete another printer's
/// work by guessing an id.
/// </remarks>
public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var device = app.MapGroup("/api/v1/device")
            .RequireAuthorization(SpoolrPolicies.Device)
            .WithTags("Device");

        device.MapPost("/heartbeat", HeartbeatAsync)
            .WithName("Heartbeat")
            .WithSummary("Records a device check-in and the status it reports.")
            .ValidatingBody<HeartbeatRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        device.MapPost("/jobs/next", ClaimNextAsync)
            .WithName("ClaimNextJob")
            .WithSummary("Claims the next job due for this printer.")
            .WithDescription("Returns 204 when nothing is waiting.")
            .Produces<JobResponse>()
            .Produces(StatusCodes.Status204NoContent);

        device.MapPost("/jobs/{jobId:guid}/acknowledge", AcknowledgeAsync)
            .WithName("AcknowledgeJob")
            .WithSummary("Confirms the printer has started rendering a claimed job.")
            .Produces<JobResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        device.MapPost("/jobs/{jobId:guid}/result", ReportResultAsync)
            .WithName("ReportJobResult")
            .WithSummary("Reports that a job finished or failed.")
            .ValidatingBody<JobResultRequest>()
            .Produces<JobResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> HeartbeatAsync(
        HeartbeatRequest request,
        ClaimsPrincipal device,
        IPrinterStore printers,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var printer = await printers.FindAsync(PrinterIdOf(device), cancellationToken);

        if (printer is null)
        {
            return TypedResults.Problem(
                "This printer is no longer registered.",
                statusCode: StatusCodes.Status404NotFound);
        }

        try
        {
            printer.Heartbeat(request.Status, clock.GetUtcNow());
        }
        catch (ArgumentOutOfRangeException)
        {
            return TypedResults.Problem(
                "A device may only report Online, Degraded, or Offline.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await printers.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<IResult> ClaimNextAsync(
        ClaimsPrincipal device,
        IPrintJobStore jobs,
        IJobEventPublisher events,
        SpoolrMetrics metrics,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var job = await jobs.ClaimNextForPrinterAsync(PrinterIdOf(device), now, cancellationToken);

        if (job is null)
        {
            return TypedResults.NoContent();
        }

        metrics.JobDispatched(job.SubmittedAt, now);
        await events.PublishAsync(JobStateChanged.From(job, now), cancellationToken);

        return TypedResults.Ok(JobResponse.From(job));
    }

    private static async Task<IResult> AcknowledgeAsync(
        Guid jobId,
        ClaimsPrincipal device,
        IPrintJobStore jobs,
        IJobEventPublisher events,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(jobId, cancellationToken);

        if (job is null || job.PrinterId != PrinterIdOf(device))
        {
            // A job belonging to another printer is reported as missing rather than
            // forbidden, so this endpoint cannot be used to discover which ids exist.
            return TypedResults.Problem($"No job with id {jobId}.", statusCode: StatusCodes.Status404NotFound);
        }

        var now = clock.GetUtcNow();

        try
        {
            job.Acknowledge(now);
        }
        catch (JobStateException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }

        await jobs.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(JobStateChanged.From(job, now), cancellationToken);

        return TypedResults.Ok(JobResponse.From(job));
    }

    private static async Task<IResult> ReportResultAsync(
        Guid jobId,
        JobResultRequest request,
        ClaimsPrincipal device,
        IPrintJobStore jobs,
        IJobEventPublisher events,
        BackoffPolicy backoff,
        SpoolrMetrics metrics,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(jobId, cancellationToken);

        if (job is null || job.PrinterId != PrinterIdOf(device))
        {
            return TypedResults.Problem($"No job with id {jobId}.", statusCode: StatusCodes.Status404NotFound);
        }

        var now = clock.GetUtcNow();

        try
        {
            if (request.Succeeded)
            {
                job.Complete(now);
                metrics.JobCompleted(job.AttemptCount);
            }
            else
            {
                var reason = string.IsNullOrWhiteSpace(request.Error)
                    ? "The printer reported a failure without a reason."
                    : request.Error;

                var rescheduled = job.Fail(reason, backoff.DelayForAttempt(job.AttemptCount), now);

                if (rescheduled)
                {
                    metrics.JobRetried(job.AttemptCount);
                }
                else
                {
                    metrics.JobFailed(job.AttemptCount);
                }
            }
        }
        catch (JobStateException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }

        await jobs.SaveChangesAsync(cancellationToken);
        await events.PublishAsync(JobStateChanged.From(job, now), cancellationToken);

        return TypedResults.Ok(JobResponse.From(job));
    }

    private static Guid PrinterIdOf(ClaimsPrincipal device) =>
        Guid.Parse(device.FindFirstValue(SpoolrClaims.PrinterId)!);
}
