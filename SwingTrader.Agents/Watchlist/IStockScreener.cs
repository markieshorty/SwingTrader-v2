using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Watchlist;

// FailedQuoteCount - symbols in the universe whose GetQuoteAsync call threw
// (rate limit, transient network error, etc.) and were silently skipped
// rather than failing the whole screen. Surfaced so the caller can warn if a
// systemic Finnhub problem quietly shrank the candidate pool, the same way
// ResearchConsumerFunction already reports "N of M symbol(s) could not be
// rescored" for its own per-symbol failures.
public record ScreenResult(List<ScreenedCandidate> Candidates, int UniverseCount, int FailedQuoteCount);

public interface IStockScreener
{
    Task<ScreenResult> ScreenAsync(int accountId, IFinnhubClient finnhub, CancellationToken ct = default);
}
