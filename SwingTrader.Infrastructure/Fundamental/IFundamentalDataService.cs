using SwingTrader.Core.Enums;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;

namespace SwingTrader.Infrastructure.Fundamental;

public record FundamentalSnapshot(
    string Symbol,
    AnalystTrend AnalystTrend,
    InsiderActivity InsiderActivity,
    EarningsConsistency EarningsConsistency,
    RevenueDirection RevenueDirection,
    int AnalystCount,
    int InsiderBuyerCount,
    int InsiderSellerCount,
    decimal? NetInsiderShares,
    DateTime FetchedAt,
    // Finnhub's aggregated Monthly Share Purchase Ratio (-100..100) averaged
    // over the lookback; null = endpoint unavailable (the classification then
    // rests on transaction clustering alone).
    decimal? InsiderMspr = null,
    // Earnings surprise ACCELERATION: recent-half minus older-half average
    // surprise %. Positive = beats getting bigger. Null = fewer than 3
    // usable quarters. Tilts the earnings sub-score, bounded by config.
    decimal? SurpriseTrendPct = null);

public interface IFundamentalDataService
{
    // earningsHistory is passed in from EarningsService (already fetched, reused here to
    // avoid a duplicate Finnhub call).
    Task<FundamentalSnapshot> GetSnapshotAsync(
        IFinnhubClient finnhub, string symbol, List<FinnhubEarningsEvent> earningsHistory, CancellationToken ct);
}
