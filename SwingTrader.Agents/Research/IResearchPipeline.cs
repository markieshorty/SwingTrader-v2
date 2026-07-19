using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Research;

public interface IResearchPipeline
{
    // riskProfile and freshCandlesBySymbol are fetched once per job by the
    // caller and passed in rather than looked up per-symbol here - RunAsync
    // is invoked for several symbols concurrently (see ResearchConsumerFunction's
    // semaphore-bounded batch), and a per-symbol DB read at the very top of
    // this method (with no rate-limiter delay ahead of it to naturally space
    // concurrent calls out) hit EF Core's "second operation started on this
    // context instance" concurrency guard almost every time - twice now,
    // once for riskProfile and once for the candle-freshness check.
    Task<StockSignal?> RunAsync(
        int accountId,
        IFinnhubClient finnhub,
        ITiingoClient tiingo,
        IClaudeClient claude,
        string symbol,
        AccountRiskProfile riskProfile,
        IReadOnlyDictionary<string, IReadOnlyList<StockCandle>>? freshCandlesBySymbol = null,
        string? companyName = null,
        // Watchlist-pick cross-sectional percentile, stamped onto the signal
        // as shadow metadata (see CrossSectionalRanker).
        decimal? selectionPercentile = null,
        CancellationToken ct = default);
}
