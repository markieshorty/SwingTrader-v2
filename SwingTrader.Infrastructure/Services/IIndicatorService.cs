using SwingTrader.Core.Models;

namespace SwingTrader.Infrastructure.Services;

public record IndicatorResult(
    decimal? Rsi14,
    decimal? Macd,
    decimal? MacdSignal,
    decimal? MacdHistogram,
    decimal? BollingerUpper,
    decimal? BollingerLower,
    decimal? BollingerMid,
    decimal? Ema9,
    decimal? Ema21,
    decimal? VolumeRatio
);

public interface IIndicatorService
{
    IndicatorResult Calculate(IReadOnlyList<CandleData> candles);

    Task<decimal?> GetRsiAsync(IEnumerable<StockCandle> candles, int period = 14);
    Task<(decimal? Macd, decimal? Signal, decimal? Histogram)> GetMacdAsync(
        IEnumerable<StockCandle> candles, int fast = 12, int slow = 26, int signal = 9);
    Task<(decimal? Upper, decimal? Mid, decimal? Lower)> GetBollingerBandsAsync(
        IEnumerable<StockCandle> candles, int period = 20, decimal stdDev = 2);
    Task<decimal?> GetEmaAsync(IEnumerable<StockCandle> candles, int period);
    Task<decimal?> GetVolumeRatioAsync(IEnumerable<StockCandle> candles, int avgPeriod = 20);
    Task<IndicatorResult> CalculateAllAsync(IEnumerable<StockCandle> candles);

    decimal? CalculateSharpeRatio(IEnumerable<decimal> periodReturns, decimal riskFreeRate = 0.05m);
    decimal CalculateMaxDrawdown(IEnumerable<decimal> equityCurve);
}
