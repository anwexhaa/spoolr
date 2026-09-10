using System.Diagnostics.Metrics;
using Spoolr.Core.Jobs;

namespace Spoolr.Infrastructure.Observability;

/// <summary>
/// The service's own metrics, on top of the request and dependency metrics the host emits.
/// </summary>
/// <remarks>
/// These answer the questions an on-call engineer actually asks. Request rate and latency
/// say the API is up; they say nothing about whether anything is printing. Queue depth
/// climbing while completions sit at zero is the signal that matters, and no HTTP metric
/// shows it.
/// </remarks>
public sealed class SpoolrMetrics : IDisposable
{
    /// <summary>Meter name, registered with OpenTelemetry at startup.</summary>
    public const string MeterName = "Spoolr";

    private readonly Meter _meter;
    private readonly Counter<long> _submitted;
    private readonly Counter<long> _completed;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _retried;
    private readonly Counter<long> _recovered;
    private readonly Histogram<double> _timeToDispatch;

    private long _queueDepth;

    public SpoolrMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);

        _submitted = _meter.CreateCounter<long>(
            "spoolr.jobs.submitted",
            unit: "{job}",
            description: "Print jobs accepted onto the queue.");

        _completed = _meter.CreateCounter<long>(
            "spoolr.jobs.completed",
            unit: "{job}",
            description: "Print jobs a printer reported as finished.");

        _failed = _meter.CreateCounter<long>(
            "spoolr.jobs.failed",
            unit: "{job}",
            description: "Print jobs that exhausted every delivery attempt.");

        _retried = _meter.CreateCounter<long>(
            "spoolr.jobs.retried",
            unit: "{job}",
            description: "Failed attempts that were rescheduled rather than given up on.");

        _recovered = _meter.CreateCounter<long>(
            "spoolr.jobs.recovered",
            unit: "{job}",
            description: "Jobs returned to the queue after a printer never acknowledged them.");

        _timeToDispatch = _meter.CreateHistogram<double>(
            "spoolr.jobs.time_to_dispatch",
            unit: "s",
            description: "Seconds a job waited between submission and being claimed.");

        // Sampled from a cached value rather than queried on scrape, so a monitoring system
        // polling this cannot put load on the database.
        _meter.CreateObservableGauge(
            "spoolr.queue.depth",
            () => Interlocked.Read(ref _queueDepth),
            unit: "{job}",
            description: "Jobs waiting that are due to be claimed now.");
    }

    public void JobSubmitted(JobPriority priority) =>
        _submitted.Add(1, new KeyValuePair<string, object?>("priority", priority.ToString()));

    public void JobCompleted(int attemptCount) =>
        _completed.Add(1, new KeyValuePair<string, object?>("attempts", attemptCount));

    public void JobFailed(int attemptCount) =>
        _failed.Add(1, new KeyValuePair<string, object?>("attempts", attemptCount));

    public void JobRetried(int attemptNumber) =>
        _retried.Add(1, new KeyValuePair<string, object?>("attempt", attemptNumber));

    public void JobsRecovered(int count) => _recovered.Add(count);

    public void JobDispatched(DateTimeOffset submittedAt, DateTimeOffset dispatchedAt) =>
        _timeToDispatch.Record((dispatchedAt - submittedAt).TotalSeconds);

    /// <summary>Publishes the latest queue depth for the gauge to report.</summary>
    public void ReportQueueDepth(int depth) => Interlocked.Exchange(ref _queueDepth, depth);

    public void Dispose() => _meter.Dispose();
}
