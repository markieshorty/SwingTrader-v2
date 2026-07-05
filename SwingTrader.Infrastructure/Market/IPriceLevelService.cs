using SwingTrader.Core.Enums;

namespace SwingTrader.Infrastructure.Market;

public record PriceLevelResult(
    PriceLevelContext Context,
    decimal? NearestSupport,
    decimal? NearestResistance,
    decimal Score,
    string Label);

public interface IPriceLevelService
{
    Task<PriceLevelResult> AnalyseAsync(string symbol, decimal currentPrice, CancellationToken ct);
}
