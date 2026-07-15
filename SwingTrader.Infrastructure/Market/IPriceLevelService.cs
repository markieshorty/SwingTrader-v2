using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Infrastructure.Market;

public record PriceLevelResult(
    PriceLevelContext Context,
    decimal? NearestSupport,
    decimal? NearestResistance,
    decimal Score,
    string Label);

public interface IPriceLevelService
{
    // Pass <paramref name="stockCandles"/> (the symbol's already-loaded bars) to
    // skip a redundant DB read - Research has them in memory already.
    Task<PriceLevelResult> AnalyseAsync(
        string symbol, decimal currentPrice, CancellationToken ct,
        IReadOnlyList<StockCandle>? stockCandles = null);
}
