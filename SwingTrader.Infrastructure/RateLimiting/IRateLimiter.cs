namespace SwingTrader.Infrastructure.RateLimiting;

public interface IRateLimiter
{
    Task WaitAsync(CancellationToken ct = default);
}

// Separate limiter per provider - previously every Tiingo AND Finnhub call
// across the whole app shared one IRateLimiter instance, so a heavy Finnhub
// run (earnings, sentiment, fundamentals) could eat into the budget Tiingo
// needed for candles and vice versa. Each provider now gets its own bucket.
public interface ITiingoRateLimiter : IRateLimiter { }
public interface IFinnhubRateLimiter : IRateLimiter { }
