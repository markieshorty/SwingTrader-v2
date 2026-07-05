namespace SwingTrader.Infrastructure.RateLimiting;

public class RateLimiter : IRateLimiter
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
