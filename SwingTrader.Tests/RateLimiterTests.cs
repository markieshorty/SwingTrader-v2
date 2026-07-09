using System.Diagnostics;
using FluentAssertions;
using SwingTrader.Infrastructure.RateLimiting;
using Xunit;

namespace SwingTrader.Tests;

// This class is the sole guard against every provider rate-limit incident
// hit this session (Tiingo misconfigured 60x too permissive, Finnhub
// competing with Tiingo on one shared bucket) - worth locking down its
// actual timing behaviour, not just that it compiles.
public class RateLimiterTests
{
    [Fact]
    public async Task WaitAsync_CalledTwice_SpacesCallsByAtLeastTheComputedDelay()
    {
        // 10 calls/second = 100ms minimum spacing between any two calls.
        var limiter = new RateLimiter(maxCallsPerMinute: 600);

        var sw = Stopwatch.StartNew();
        await limiter.WaitAsync();
        await limiter.WaitAsync();
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(90); // small tolerance below the 100ms floor
    }

    [Fact]
    public async Task WaitAsync_CustomPeriodOverload_UsesThatPeriodNotOneMinute()
    {
        // Tiingo's real free-tier limit is 50/hour, not 50/minute - this
        // overload exists specifically so that isn't silently 60x too
        // permissive again. 4 calls/200ms = 50ms minimum spacing.
        var limiter = new RateLimiter(maxCalls: 4, period: TimeSpan.FromMilliseconds(200));

        var sw = Stopwatch.StartNew();
        await limiter.WaitAsync();
        await limiter.WaitAsync();
        await limiter.WaitAsync();
        sw.Stop();

        // Two waits' worth of the ~50ms floor, allowing for scheduler jitter.
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(90);
    }

    [Fact]
    public async Task WaitAsync_MinuteAndEquivalentPeriodOverload_ProduceTheSameSpacing()
    {
        // RateLimiter(maxCallsPerMinute) delegates to the (maxCalls, period)
        // overload with TimeSpan.FromMinutes(1) - same effective delay for
        // an equivalent per-minute rate expressed either way.
        var perMinute = new RateLimiter(maxCallsPerMinute: 1200); // 50ms floor
        var viaPeriod = new RateLimiter(maxCalls: 1200, period: TimeSpan.FromMinutes(1));

        var sw1 = Stopwatch.StartNew();
        await perMinute.WaitAsync();
        await perMinute.WaitAsync();
        sw1.Stop();

        var sw2 = Stopwatch.StartNew();
        await viaPeriod.WaitAsync();
        await viaPeriod.WaitAsync();
        sw2.Stop();

        // Generous tolerance - this is comparing two independently-measured
        // wall-clock durations, so scheduler jitter compounds across both.
        // Only needs to confirm the two overloads are roughly equivalent,
        // not exact millisecond parity.
        Math.Abs(sw1.ElapsedMilliseconds - sw2.ElapsedMilliseconds).Should().BeLessThan(150);
    }

    [Fact]
    public async Task WaitAsync_CancelledToken_ThrowsAndReleasesSemaphore()
    {
        var limiter = new RateLimiter(maxCallsPerMinute: 60);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.WaitAsync(cts.Token));

        // Semaphore must have been released despite the cancellation, or
        // every subsequent call would deadlock waiting on a permit that
        // never comes back.
        using var freshCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await limiter.WaitAsync(freshCts.Token);
    }

    [Fact]
    public async Task WaitAsync_FiveConcurrentCallers_AllCompleteWithoutDeadlock()
    {
        // Verifies concurrent waiters don't deadlock on the serialization gate.
        var limiter = new RateLimiter(maxCallsPerMinute: 6000); // ~10ms floor, keeps the test fast

        var all = Task.WhenAll(Enumerable.Range(0, 5).Select(_ => limiter.WaitAsync()));
        var completed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(5)));

        completed.Should().Be(all, "all 5 calls should finish well within the 5s timeout guard, not deadlock on the gate");
    }

    [Fact]
    public async Task WaitAsync_ConcurrentCallers_ArePacedAtAggregateRateNotPerSlot()
    {
        // Regression: the old SemaphoreSlim(5) + per-slot Task.Delay let ~5x the
        // configured rate through when callers were concurrent (the 489-symbol
        // screener fired ~250/min against a 50/min Finnhub limiter and got
        // 429-ed). Correct pacing releases one caller per floor regardless of
        // how many are queued.
        var limiter = new RateLimiter(maxCallsPerMinute: 600); // 100ms floor

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => limiter.WaitAsync()));
        sw.Stop();

        // 6 calls = 5 gaps of ~100ms = ~500ms. The old 5-concurrent-slot impl
        // would finish this in ~200ms, so ≥400ms proves it's genuinely paced.
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(400);
    }
}
