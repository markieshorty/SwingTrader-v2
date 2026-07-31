namespace SwingTrader.Core.Trading;

// Average True Range - the volatility unit behind ATR risk-parity sizing and
// ATR-anchored stops (docs: sizing-style toggle, 31 Jul 2026). Pure and
// generic over the caller's bar type so live (StockCandle) and the backtester
// (DailyBar) share one implementation.
public static class AtrCalculator
{
    public const int DefaultPeriod = 14;

    // Simple moving average of true range over the `period` bars ENDING at
    // endIndexInclusive. True range needs the previous close, so the window
    // requires period + 1 bars of history; returns null when there aren't
    // enough (or any input is non-positive) rather than a misleading number.
    public static decimal? Compute<T>(
        IReadOnlyList<T> bars,
        Func<T, decimal> high, Func<T, decimal> low, Func<T, decimal> close,
        int endIndexInclusive, int period = DefaultPeriod)
    {
        if (period <= 0 || endIndexInclusive < period || endIndexInclusive >= bars.Count)
            return null;

        decimal sum = 0m;
        for (var i = endIndexInclusive - period + 1; i <= endIndexInclusive; i++)
        {
            var h = high(bars[i]);
            var l = low(bars[i]);
            var prevClose = close(bars[i - 1]);
            if (h <= 0 || l <= 0 || prevClose <= 0 || h < l) return null;

            var tr = Math.Max(h - l, Math.Max(Math.Abs(h - prevClose), Math.Abs(l - prevClose)));
            sum += tr;
        }
        return sum / period;
    }
}
