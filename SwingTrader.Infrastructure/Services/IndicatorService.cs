using Skender.Stock.Indicators;
using SwingTrader.Core.Models;

namespace SwingTrader.Infrastructure.Services;

public class IndicatorService : IIndicatorService
{
    public IndicatorResult Calculate(IReadOnlyList<CandleData> candles)
    {
        var stockCandles = candles.Select(c => new StockCandle
        {
            Symbol = string.Empty,
            Timestamp = c.Timestamp,
            Open = c.Open,
            High = c.High,
            Low = c.Low,
            Close = c.Close,
            Volume = c.Volume,
        }).ToList();

        return CalculateAllAsync(stockCandles).GetAwaiter().GetResult();
    }

    public Task<decimal?> GetRsiAsync(IEnumerable<StockCandle> candles, int period = 14)
    {
        var quotes = ToQuotes(candles);
        if (quotes.Count < period + 1)
            return Task.FromResult<decimal?>(null);

        var result = quotes.GetRsi(period).LastOrDefault(r => r.Rsi.HasValue);
        return Task.FromResult(result?.Rsi.HasValue == true ? (decimal?)result.Rsi!.Value : null);
    }

    public Task<(decimal? Macd, decimal? Signal, decimal? Histogram)> GetMacdAsync(
        IEnumerable<StockCandle> candles, int fast = 12, int slow = 26, int signal = 9)
    {
        var quotes = ToQuotes(candles);
        if (quotes.Count < slow + signal)
            return Task.FromResult<(decimal?, decimal?, decimal?)>((null, null, null));

        var result = quotes.GetMacd(fast, slow, signal).LastOrDefault(r => r.Macd.HasValue);
        if (result == null)
            return Task.FromResult<(decimal?, decimal?, decimal?)>((null, null, null));

        return Task.FromResult<(decimal?, decimal?, decimal?)>((
            result.Macd.HasValue ? (decimal?)result.Macd!.Value : null,
            result.Signal.HasValue ? (decimal?)result.Signal!.Value : null,
            result.Histogram.HasValue ? (decimal?)result.Histogram!.Value : null
        ));
    }

    public Task<(decimal? Upper, decimal? Mid, decimal? Lower)> GetBollingerBandsAsync(
        IEnumerable<StockCandle> candles, int period = 20, decimal stdDev = 2)
    {
        var quotes = ToQuotes(candles);
        if (quotes.Count < period)
            return Task.FromResult<(decimal?, decimal?, decimal?)>((null, null, null));

        var result = quotes.GetBollingerBands(period, (double)stdDev).LastOrDefault(r => r.UpperBand.HasValue);
        if (result == null)
            return Task.FromResult<(decimal?, decimal?, decimal?)>((null, null, null));

        return Task.FromResult<(decimal?, decimal?, decimal?)>((
            result.UpperBand.HasValue ? (decimal?)result.UpperBand!.Value : null,
            result.Sma.HasValue ? (decimal?)result.Sma!.Value : null,
            result.LowerBand.HasValue ? (decimal?)result.LowerBand!.Value : null
        ));
    }

    public Task<decimal?> GetEmaAsync(IEnumerable<StockCandle> candles, int period)
    {
        var quotes = ToQuotes(candles);
        if (quotes.Count < period)
            return Task.FromResult<decimal?>(null);

        var result = quotes.GetEma(period).LastOrDefault(r => r.Ema.HasValue);
        return Task.FromResult(result?.Ema.HasValue == true ? (decimal?)result.Ema!.Value : null);
    }

    public Task<decimal?> GetVolumeRatioAsync(IEnumerable<StockCandle> candles, int avgPeriod = 20)
    {
        var list = candles.OrderBy(c => c.Timestamp).ToList();
        if (list.Count < 2)
            return Task.FromResult<decimal?>(null);

        var recent = list[^1].Volume;
        var lookback = Math.Min(avgPeriod, list.Count - 1);
        var avg = list.TakeLast(lookback + 1).Take(lookback).Average(c => (double)c.Volume);
        var ratio = avg > 0 ? (decimal)(recent / avg) : null as decimal?;
        return Task.FromResult(ratio);
    }

    public async Task<IndicatorResult> CalculateAllAsync(IEnumerable<StockCandle> candles)
    {
        var list = candles.OrderBy(c => c.Timestamp).ToList();

        var rsi = await GetRsiAsync(list);
        var (macd, macdSignal, macdHist) = await GetMacdAsync(list);
        var (upper, mid, lower) = await GetBollingerBandsAsync(list);
        var ema9 = await GetEmaAsync(list, 9);
        var ema21 = await GetEmaAsync(list, 21);
        var volRatio = await GetVolumeRatioAsync(list);

        return new IndicatorResult(rsi, macd, macdSignal, macdHist, upper, lower, mid, ema9, ema21, volRatio);
    }

    public decimal? CalculateSharpeRatio(IEnumerable<decimal> periodReturns, decimal riskFreeRate = 0.05m)
    {
        var returns = periodReturns.ToList();
        if (returns.Count < 10) return null;

        var periodsPerYear = 52m;
        var annualRiskFree = riskFreeRate / periodsPerYear;
        var excessReturns = returns.Select(r => r - annualRiskFree).ToList();
        var mean = excessReturns.Average();
        var variance = excessReturns.Select(r => (r - mean) * (r - mean)).Average();
        var stdDev = (decimal)Math.Sqrt((double)variance);
        if (stdDev == 0) return null;
        return Math.Round(mean / stdDev * (decimal)Math.Sqrt((double)periodsPerYear), 4);
    }

    public decimal CalculateMaxDrawdown(IEnumerable<decimal> equityCurve)
    {
        var curve = equityCurve.ToList();
        if (curve.Count < 2) return 0m;

        decimal peak = curve[0];
        decimal maxDrawdown = 0m;

        foreach (var value in curve)
        {
            if (value > peak) peak = value;
            if (peak > 0)
            {
                var drawdown = (peak - value) / peak;
                if (drawdown > maxDrawdown) maxDrawdown = drawdown;
            }
        }
        return Math.Round(maxDrawdown, 4);
    }

    private static List<Quote> ToQuotes(IEnumerable<StockCandle> candles) =>
        candles.OrderBy(c => c.Timestamp).Select(c => new Quote
        {
            Date = c.Timestamp,
            Open = c.Open,
            High = c.High,
            Low = c.Low,
            Close = c.Close,
            Volume = c.Volume,
        }).ToList();
}
