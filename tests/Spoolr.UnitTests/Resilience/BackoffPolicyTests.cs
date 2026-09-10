using Spoolr.Core.Resilience;

namespace Spoolr.UnitTests.Resilience;

public sealed class BackoffPolicyTests
{
    [Theory]
    [InlineData(1, 1_000)]
    [InlineData(2, 2_000)]
    [InlineData(3, 4_000)]
    [InlineData(4, 8_000)]
    public void CeilingForAttempt_DoublesPerAttempt(int attempt, int expectedMs)
    {
        var policy = new BackoffPolicy(
            baseDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), policy.CeilingForAttempt(attempt));
    }

    [Fact]
    public void CeilingForAttempt_StopsGrowingAtMaxDelay()
    {
        var policy = new BackoffPolicy(
            baseDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(8), policy.CeilingForAttempt(4));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.CeilingForAttempt(5));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.CeilingForAttempt(50));
    }

    [Fact]
    public void CeilingForAttempt_SurvivesAnAbsurdAttemptNumber()
    {
        // Math.Pow overflows to infinity well before this, which would otherwise blow up
        // TimeSpan.FromMilliseconds rather than simply clamping.
        var policy = new BackoffPolicy(maxDelay: TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromMinutes(5), policy.CeilingForAttempt(int.MaxValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CeilingForAttempt_RejectsNonPositiveAttemptNumbers(int attempt) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackoffPolicy().CeilingForAttempt(attempt));

    [Fact]
    public void DelayForAttempt_NeverExceedsTheCeiling()
    {
        var policy = new BackoffPolicy(
            baseDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromSeconds(30),
            jitter: new Random(Seed: 20260910));

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var ceiling = policy.CeilingForAttempt(attempt);

            for (var sample = 0; sample < 200; sample++)
            {
                var delay = policy.DelayForAttempt(attempt);

                Assert.InRange(delay, TimeSpan.Zero, ceiling);
            }
        }
    }

    [Fact]
    public void DelayForAttempt_SpreadsRetriesAcrossTheWindow()
    {
        // The point of full jitter is that a batch of jobs failing together does not come
        // back together. Sampling the same attempt repeatedly should not yield one value.
        var policy = new BackoffPolicy(
            baseDelay: TimeSpan.FromSeconds(10),
            jitter: new Random(Seed: 7));

        var samples = Enumerable.Range(0, 100)
            .Select(_ => policy.DelayForAttempt(3))
            .ToList();

        Assert.True(samples.Distinct().Count() > 90, "Full jitter should spread retries, not cluster them.");
        Assert.True(samples.Max() - samples.Min() > TimeSpan.FromSeconds(20), "Samples should cover the window.");
    }

    [Fact]
    public void DelayForAttempt_WithAFixedSeed_IsReproducible()
    {
        var first = new BackoffPolicy(jitter: new Random(Seed: 42)).DelayForAttempt(3);
        var second = new BackoffPolicy(jitter: new Random(Seed: 42)).DelayForAttempt(3);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(-2.0)]
    public void Constructor_RejectsAMultiplierThatDoesNotGrow(double multiplier) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackoffPolicy(multiplier: multiplier));

    [Fact]
    public void Constructor_RejectsAMaxDelayBelowTheBaseDelay() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackoffPolicy(
            baseDelay: TimeSpan.FromMinutes(1),
            maxDelay: TimeSpan.FromSeconds(1)));

    [Fact]
    public void Constructor_RejectsANonPositiveBaseDelay() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackoffPolicy(baseDelay: TimeSpan.Zero));
}
