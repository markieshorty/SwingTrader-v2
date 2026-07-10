namespace SwingTrader.Infrastructure.Market;

// The relative-strength ALGORITHM, extracted pure so the live service
// (RelativeStrengthService, fetching fresh candles) and the historic
// backtester (HistoricBacktester, replaying stored bars) run the identical
// code - parity by construction, not by duplicated reimplementation.
//
// Inputs are ADJUSTED closes on both sides (live stock candles store
// AdjClose - ResearchPipeline maps Tiingo Adj* into StockCandle - and the
// backtest's HistoricalCandles are adjusted too).
public static class RelativeStrengthCalculator
{
    public const int WindowDays = 5;

    /// <summary>
    /// Stock 5-day return vs sector-ETF 5-day return, scored on the
    /// piecewise-linear bands below. Closes must be oldest-first. Returns
    /// null when either side has fewer than 5 closes ("couldn't compute" -
    /// callers blend a neutral 0.5 but persist null, never a fake score).
    /// </summary>
    public static RelativeStrengthOutcome? Compute(
        IReadOnlyList<decimal> stockCloses, IReadOnlyList<decimal> etfCloses)
    {
        if (stockCloses.Count < WindowDays || etfCloses.Count < WindowDays)
            return null;

        var stockFrom = stockCloses[^WindowDays];
        var stockTo = stockCloses[^1];
        if (stockFrom == 0m) return null;
        var stockReturn5d = (stockTo - stockFrom) / stockFrom * 100m;

        var etfFrom = etfCloses[^WindowDays];
        var etfTo = etfCloses[^1];
        if (etfFrom == 0m) return null;
        var etfReturn5d = (etfTo - etfFrom) / etfFrom * 100m;

        var relativeReturn = stockReturn5d - etfReturn5d;
        return new RelativeStrengthOutcome(
            stockReturn5d, etfReturn5d, relativeReturn, ScoreRelativeReturn(relativeReturn));
    }

    // Bands (verbatim from the original live service):
    //   rel >= +3%          -> 1.00
    //   +1% .. +3%          -> 0.80..1.00 (linear)
    //    0% .. +1%          -> 0.60..0.80
    //   -1% ..  0%          -> 0.40..0.60
    //   -3% .. -1%          -> 0.20..0.40
    //   rel <  -3%          -> 0.00
    public static decimal ScoreRelativeReturn(decimal rel) =>
        rel >= 3.0m ? 1.00m
            : rel >= 1.0m ? Lerp(rel, 1.0m, 3.0m, 0.80m, 1.00m)
            : rel >= 0.0m ? Lerp(rel, 0.0m, 1.0m, 0.60m, 0.80m)
            : rel >= -1.0m ? Lerp(rel, -1.0m, 0.0m, 0.40m, 0.60m)
            : rel >= -3.0m ? Lerp(rel, -3.0m, -1.0m, 0.20m, 0.40m)
            : 0.00m;

    private static decimal Lerp(decimal value, decimal min, decimal max, decimal outMin, decimal outMax)
    {
        var t = (value - min) / (max - min);
        return outMin + t * (outMax - outMin);
    }
}

// Score + the raw returns behind it - the live service wraps this with the
// ETF name and a human label; the backtester only needs the score.
public record RelativeStrengthOutcome(
    decimal StockReturn5d,
    decimal EtfReturn5d,
    decimal RelativeReturn,
    decimal Score);
