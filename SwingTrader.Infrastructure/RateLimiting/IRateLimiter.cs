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

// Claude/Anthropic. Research fans out one Claude call per watchlist symbol
// (plus report/refinement/tier narratives), and accounts share the platform
// fallback Claude key, so a host-wide pacer keeps that shared key under
// Anthropic's per-minute request limit rather than bursting into 429s.
public interface IClaudeRateLimiter : IRateLimiter { }
