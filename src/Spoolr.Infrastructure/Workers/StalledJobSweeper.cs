using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spoolr.Core.Abstractions;
using Spoolr.Infrastructure.Configuration;

namespace Spoolr.Infrastructure.Workers;

/// <summary>
/// Returns jobs to the queue when the printer that claimed them never acknowledged.
/// </summary>
/// <remarks>
/// A printer can take a job and then lose power, lose the network, or crash. Nothing else
/// in the system moves that job: the printer will not report on it, and the submitter sees
/// a job that claims to be dispatched forever. This sweep is the only thing that notices.
///
/// It is safe to run on every replica. The recovery writes go through the same concurrency
/// token as the claim, so a sweep that races a live acknowledgement loses and backs off.
/// </remarks>
public sealed class StalledJobSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<DispatchOptions> options,
    TimeProvider timeProvider,
    ILogger<StalledJobSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var interval = TimeSpan.FromSeconds(settings.SweepIntervalSeconds);
        var dispatchTimeout = TimeSpan.FromSeconds(settings.DispatchTimeoutSeconds);
        var requeueDelay = TimeSpan.FromSeconds(settings.RequeueDelaySeconds);

        logger.LogInformation(
            "Stalled-job sweep started. Interval {Interval}, dispatch timeout {Timeout}.",
            interval,
            dispatchTimeout);

        using var timer = new PeriodicTimer(interval, timeProvider);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                await SweepOnceAsync(dispatchTimeout, requeueDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed sweep must not take the host down. The next tick tries again,
                // and the jobs stay recoverable in the meantime.
                logger.LogError(ex, "Stalled-job sweep failed. Retrying on the next tick.");
            }
        }

        logger.LogInformation("Stalled-job sweep stopped.");
    }

    private async Task SweepOnceAsync(
        TimeSpan dispatchTimeout,
        TimeSpan requeueDelay,
        CancellationToken cancellationToken)
    {
        // The store is scoped, so each pass gets its own DbContext rather than accumulating
        // tracked entities in one long-lived context for the life of the process.
        await using var scope = scopeFactory.CreateAsyncScope();

        var jobs = scope.ServiceProvider.GetRequiredService<IPrintJobStore>();

        var recovered = await jobs.ReclaimStalledAsync(
            dispatchTimeout,
            requeueDelay,
            timeProvider.GetUtcNow(),
            cancellationToken);

        if (recovered > 0)
        {
            logger.LogWarning("Recovered {Count} stalled job(s).", recovered);
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
