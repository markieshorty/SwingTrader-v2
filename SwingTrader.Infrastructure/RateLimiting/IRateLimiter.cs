namespace SwingTrader.Infrastructure.RateLimiting;

public interface IRateLimiter
{
    Task WaitAsync(CancellationToken ct = default);
}
