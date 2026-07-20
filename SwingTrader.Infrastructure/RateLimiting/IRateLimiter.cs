namespace SwingTrader.Infrastructure.RateLimiting;

public interface IRateLimiter
{
    Task WaitAsync(CancellationToken ct = default);
}

// Separate limiter per provider - previously every Tiingo AND Finnhub call
// across the whole app shared one IRateLimiter instance, so a heavy Finnhub
// run (earnings, sentiment, fundamentals) could eat into the budget Tiingo
// needed for candles and vice versa. Each provider now gets its own bucket.
public interface IFinnhubRateLimiter : IRateLimiter { }

// The shared platform Tiingo POWER key's pacer (RateLimiting:TiingoPowerMaxPerHour,
// default 3600 = 1 req/s - far under Power's 10k/hr ceiling). Accounts with
// Account.UsePlatformTiingo ride this bucket; everyone else stays on the
// legacy ITiingoRateLimiter (their own key at free-tier pacing). Host-wide
// singleton because every flagged account shares the ONE platform key.
public interface ITiingoPowerRateLimiter : IRateLimiter { }

// Claude/Anthropic. Research fans out one Claude call per watchlist symbol
// (plus report/refinement/tier narratives), and accounts share the platform
// fallback Claude key, so a host-wide pacer keeps that shared key under
// Anthropic's per-minute request limit rather than bursting into 429s.
public interface IClaudeRateLimiter : IRateLimiter { }
