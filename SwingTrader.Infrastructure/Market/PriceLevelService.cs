using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;

namespace SwingTrader.Infrastructure.Market;

// Thin live wrapper: fetches the candle window, delegates the algorithm to
// PriceLevelCalculator - the exact same code the historic backtester runs,
// so live and backtest support/resistance can never drift apart.
public class PriceLevelService(
    ICandleRepository candleRepo,
    IOptions<PriceLevelConfig> config,
    ILogger<PriceLevelService> logger) : IPriceLevelService
{
    public async Task<PriceLevelResult> AnalyseAsync(
        string symbol, decimal currentPrice, CancellationToken ct,
        IReadOnlyList<StockCandle>? stockCandles = null)
    {
        try
        {
            var cfg = config.Value;
            var since = DateTime.UtcNow.AddDays(-cfg.LookbackDays);

            // Reuse the caller's in-memory bars when supplied (Research already
            // loaded them), else read from the DB.
            var source = stockCandles is not null
                ? stockCandles.Where(c => c.Timestamp >= since)
                : await candleRepo.GetCandlesAsync(symbol, "D", since, DateTime.UtcNow);
            var candles = source
                .OrderBy(c => c.Timestamp)
                .Select(c => new PriceBar(c.High, c.Low, c.Close, c.Volume))
                .ToList();

            return PriceLevelCalculator.Compute(candles, currentPrice, cfg);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Price level analysis failed for {Symbol} — using neutral fallback", symbol);
            return new PriceLevelResult(PriceLevelContext.InsufficientData, null, null, 0.5m, "Price level analysis unavailable");
        }
    }
}
