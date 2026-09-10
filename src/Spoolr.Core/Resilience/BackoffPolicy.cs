namespace Spoolr.Core.Resilience;

/// <summary>
/// Computes how long a failed print job waits before it becomes claimable again.
/// </summary>
/// <remarks>
/// Uses exponential growth with full jitter. Without jitter, a printer that drops offline
/// and takes fifty queued jobs down with it sends all fifty back at the same instant, and
/// they fail together again the moment it is still down. Spreading each retry uniformly
/// across its backoff window breaks that synchronisation.
/// </remarks>
public sealed class BackoffPolicy
{
    private readonly Random _jitter;

    /// <param name="baseDelay">Ceiling of the first retry window.</param>
    /// <param name="maxDelay">Upper bound the window grows to, however many attempts fail.</param>
    /// <param name="multiplier">Growth factor applied per attempt.</param>
    /// <param name="jitter">Injected so tests can pin the sequence. Defaults to a shared source.</param>
    public BackoffPolicy(
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        double multiplier = 2.0,
        Random? jitter = null)
    {
        BaseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        MaxDelay = maxDelay ?? TimeSpan.FromMinutes(5);
        Multiplier = multiplier;
        _jitter = jitter ?? Random.Shared;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(BaseDelay, TimeSpan.Zero, nameof(baseDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDelay, BaseDelay, nameof(maxDelay));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(multiplier, 1.0);
    }

    public TimeSpan BaseDelay { get; }

    public TimeSpan MaxDelay { get; }

    public double Multiplier { get; }

    /// <summary>
    /// The uncapped, unjittered window for a given attempt. Exposed mainly so tests and
    /// operators can reason about the schedule without sampling randomness.
    /// </summary>
    /// <param name="attemptNumber">One-based number of the attempt that just failed.</param>
    public TimeSpan CeilingForAttempt(int attemptNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptNumber);

        // Math.Pow on a large attemptNumber overflows to infinity, which then produces a
        // TimeSpan the runtime refuses to construct. Clamp before converting.
        var scaled = BaseDelay.TotalMilliseconds * Math.Pow(Multiplier, attemptNumber - 1);

        return double.IsFinite(scaled) && scaled < MaxDelay.TotalMilliseconds
            ? TimeSpan.FromMilliseconds(scaled)
            : MaxDelay;
    }

    /// <summary>
    /// Picks the actual delay for an attempt: a uniform sample from zero up to the
    /// capped exponential ceiling.
    /// </summary>
    /// <param name="attemptNumber">One-based number of the attempt that just failed.</param>
    public TimeSpan DelayForAttempt(int attemptNumber)
    {
        var ceiling = CeilingForAttempt(attemptNumber);

        return TimeSpan.FromMilliseconds(_jitter.NextDouble() * ceiling.TotalMilliseconds);
    }
}
