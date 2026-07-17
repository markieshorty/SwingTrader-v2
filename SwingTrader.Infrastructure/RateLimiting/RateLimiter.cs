namespace SwingTrader.Infrastructure.RateLimiting;

// One concrete implementation registered separately per provider (see
// ITiingoRateLimiter/IFinnhubRateLimiter) - each DI registration constructs
// its own instance, so the two providers never share a pacer.
//
// Paces call *issuance* to exactly maxCalls per period. The previous version
// used a SemaphoreSlim(5) plus a per-call Task.Delay(period/maxCalls), but
// because the delay ran inside each of the 5 concurrent slots the real
// throughput was ~5x the configured rate - a 50/min Finnhub limiter actually
// let ~250/min through, blowing past the free tier's 60/min and 429-ing most
// of a 489-symbol screener run. This serializes issuance instead: one caller
// is released no sooner than minDelay after the previous, so the aggregate
// rate is the configured rate no matter how many callers are queued. Callers
// manage their own in-flight concurrency after WaitAsync returns.
public class RateLimiter : ITiingoRateLimiter, ITiingoPowerRateLimiter, IFinnhubRateLimiter, IClaudeRateLimiter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _minDelayMs;
    private DateTime _nextAllowedUtc = DateTime.MinValue;

    public RateLimiter(int maxCallsPerMinute) : this(maxCallsPerMinute, TimeSpan.FromMinutes(1))
    {
    }

    // maxCalls is spread evenly across the given period - use this overload for
    // providers whose real plan limit isn't per-minute (e.g. Tiingo's free tier
    // is 50 requests/HOUR, not /minute).
    public RateLimiter(int maxCalls, TimeSpan period)
    {
        _minDelayMs = (int)Math.Ceiling(period.TotalMilliseconds / maxCalls);
    }

    // Exposed so config-plumbing tests can assert the computed spacing without
    // timing-sensitive stopwatch asserts (see the flakiness note in RateLimiterTests).
    public int MinDelayMs => _minDelayMs;

    public async Task WaitAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var waitMs = (int)Math.Max(0, (_nextAllowedUtc - DateTime.UtcNow).TotalMilliseconds);
            if (waitMs > 0) await Task.Delay(waitMs, ct);
            _nextAllowedUtc = DateTime.UtcNow.AddMilliseconds(_minDelayMs);
        }
        finally
        {
            _gate.Release();
        }
    }
}
