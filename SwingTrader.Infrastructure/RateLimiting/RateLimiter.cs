namespace SwingTrader.Infrastructure.RateLimiting;

// One concrete implementation registered separately per provider (see
// ITiingoRateLimiter/IFinnhubRateLimiter) - each DI registration constructs
// its own instance, so the two providers never share a semaphore/bucket.
public class RateLimiter : ITiingoRateLimiter, IFinnhubRateLimiter
{
    private readonly SemaphoreSlim _semaphore = new(5, 5);
    private readonly int _minDelayMs;

    public RateLimiter(int maxCallsPerMinute)
    {
        // distribute calls evenly across a minute
        _minDelayMs = (int)Math.Ceiling(60_000.0 / maxCallsPerMinute);
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
