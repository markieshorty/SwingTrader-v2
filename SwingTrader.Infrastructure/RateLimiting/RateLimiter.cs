namespace SwingTrader.Infrastructure.RateLimiting;

// One concrete implementation registered separately per provider (see
// ITiingoRateLimiter/IFinnhubRateLimiter) - each DI registration constructs
// its own instance, so the two providers never share a semaphore/bucket.
public class RateLimiter : ITiingoRateLimiter, IFinnhubRateLimiter
{
    private readonly SemaphoreSlim _semaphore = new(5, 5);
    private readonly int _minDelayMs;

    public RateLimiter(int maxCallsPerMinute) : this(maxCallsPerMinute, TimeSpan.FromMinutes(1))
    {
    }

    // maxCalls is spread evenly across the given period - use this overload for
    // providers whose real plan limit isn't per-minute (e.g. Tiingo's free tier
    // is 50 requests/HOUR, not /minute; the per-minute-only constructor above
    // previously left Tiingo's limiter effectively 60x too permissive).
    public RateLimiter(int maxCalls, TimeSpan period)
    {
        _minDelayMs = (int)Math.Ceiling(period.TotalMilliseconds / maxCalls);
    }

    public async Task WaitAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await Task.Delay(_minDelayMs, ct);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
