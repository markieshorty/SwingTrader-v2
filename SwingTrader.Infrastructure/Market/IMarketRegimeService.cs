using SwingTrader.Core.Enums;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Infrastructure.Market;

public record MarketRegimeResult(
    MarketRegime Regime,
    decimal SpyPrice,
    decimal SpyMa50,
    decimal SpyMa200,
    decimal Vix,
    string Label);

public interface IMarketRegimeService
{
    Task<MarketRegimeResult> GetCurrentRegimeAsync(ITiingoClient tiingo, IFinnhubClient finnhub, CancellationToken ct = default);
}
