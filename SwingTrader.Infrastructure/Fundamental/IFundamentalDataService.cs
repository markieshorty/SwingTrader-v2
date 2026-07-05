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
    DateTime FetchedAt);

public interface IFundamentalDataService
{
    // earningsHistory is passed in from EarningsService (already fetched, reused here to
    // avoid a duplicate Finnhub call).
    Task<FundamentalSnapshot> GetSnapshotAsync(
        IFinnhubClient finnhub, string symbol, List<FinnhubEarningsEvent> earningsHistory, CancellationToken ct);
}
