using SwingTrader.Core.Enums;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;

namespace SwingTrader.Infrastructure.Market;

public record EarningsContext(
    EarningsSetupType SetupType,
    bool HasUpcomingEarnings,
    int? DaysUntilEarnings,
    bool HasRecentEarnings,
    int? DaysSinceEarnings,
    decimal? EpsSurprisePct,
    bool BeatEstimate,
    List<FinnhubEarningsEvent>? EarningsHistory = null);

public interface IEarningsService
{
    Task<EarningsContext> GetEarningsContextAsync(IFinnhubClient finnhub, string symbol, CancellationToken ct, int? gateDays = null);
}
