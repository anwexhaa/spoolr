using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Spoolr.Core.Abstractions;

namespace Spoolr.Infrastructure.Messaging;

/// <summary>
/// Publishes job lifecycle events to an Azure Service Bus topic.
/// </summary>
/// <remarks>
/// Publishing is deliberately best effort. A job that printed has printed, and failing the
/// caller because a notification could not be delivered would turn a downstream outage into
/// a printing outage. Failures are logged and swallowed.
/// </remarks>
public sealed class ServiceBusJobEventPublisher(
    ServiceBusSender sender,
    ILogger<ServiceBusJobEventPublisher> logger) : IJobEventPublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(JobStateChanged jobEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(jobEvent, SerializerOptions))
            {
                ContentType = "application/json",
                Subject = nameof(JobStateChanged),

                // Deterministic, so a retried publish is deduplicated by the broker rather
                // than delivered twice. A job reaches a given status on a given attempt once.
                MessageId = $"{jobEvent.JobId}:{jobEvent.Status}:{jobEvent.AttemptCount}",

                // Lets subscribers filter server-side instead of receiving the whole stream.
                ApplicationProperties =
                {
                    ["status"] = jobEvent.Status.ToString(),
                    ["printerId"] = jobEvent.PrinterId.ToString(),
                },
            };

            await sender.SendMessageAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex,
                "Could not publish {Status} for job {JobId}. The job state itself is unaffected.",
                jobEvent.Status,
                jobEvent.JobId);
        }
    }
}
