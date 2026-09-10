using Microsoft.Extensions.Logging;
using Spoolr.Core.Abstractions;

namespace Spoolr.Infrastructure.Messaging;

/// <summary>
/// Writes job lifecycle events to the log instead of a broker.
/// </summary>
/// <remarks>
/// Used when no Service Bus namespace is configured, so the service runs end to end on a
/// laptop and in tests without an Azure subscription. The events are still visible, just
/// only to whoever is reading the log.
/// </remarks>
public sealed class LoggingJobEventPublisher(ILogger<LoggingJobEventPublisher> logger) : IJobEventPublisher
{
    public Task PublishAsync(JobStateChanged jobEvent, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Job {JobId} on printer {PrinterId} is now {Status} after {AttemptCount} attempt(s).",
            jobEvent.JobId,
            jobEvent.PrinterId,
            jobEvent.Status,
            jobEvent.AttemptCount);

        return Task.CompletedTask;
    }
}
