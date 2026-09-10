using Spoolr.Core.Jobs;

namespace Spoolr.Core.Abstractions;

/// <summary>
/// Emitted whenever a print job changes state, for consumers outside this service such as
/// billing, reporting, or a tenant webhook.
/// </summary>
public sealed record JobStateChanged
{
    public required Guid JobId { get; init; }

    public required Guid PrinterId { get; init; }

    public required JobStatus Status { get; init; }

    public required int AttemptCount { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? Error { get; init; }

    public static JobStateChanged From(PrintJob job, DateTimeOffset occurredAt) =>
        new()
        {
            JobId = job.Id,
            PrinterId = job.PrinterId,
            Status = job.Status,
            AttemptCount = job.AttemptCount,
            OccurredAt = occurredAt,
            Error = job.LastError,
        };
}

/// <summary>
/// Publishes job lifecycle events to downstream consumers.
/// </summary>
/// <remarks>
/// Publishing is best effort and must never fail the operation that produced the event.
/// A job that printed successfully has printed, whether or not the notification landed.
/// </remarks>
public interface IJobEventPublisher
{
    Task PublishAsync(JobStateChanged jobEvent, CancellationToken cancellationToken = default);
}
