namespace SwingTrader.Agents.Backtesting;

// Factor-tilt sleeve backtest (docs/sleeves-plan P2a): a monthly-rebalanced
// momentum + quality rotation, the strategy family with decades of published
// out-of-sample evidence. Deliberately parameter-light - there is nothing to
// sweep, so the whole run is walk-forward by construction: every rebalance
// decision uses only bars up to that day. The train/holdout numbers below are
// therefore two SEGMENTS of one honest curve, not a tune/test split.
//
// Rules (fixed, pre-declared in the spec):
// - Rebalance on the first trading day of each month.
// - Momentum = total return over the past 12 months SKIPPING the most recent
//   month (the classic 12-1: the last month is short-term reversal noise).
// - Quality/liquidity screen: price >= $5, 20-day average dollar volume >=
//   $10M at rank time, >= 7 of the last 12 monthly returns positive
//   (consistency - avoids one-gap wonders).
// - DUAL momentum, deliberately: only symbols with POSITIVE 12-1 momentum
//   are candidates, so in a broad bear the sleeve holds fewer names or goes
//   entirely to cash rather than buying the least-bad losers. Note this when
//   reading bear-year rows - "0 holdings" months are the design working.
// - Hold the top N equal-weight; an existing holding is only REPLACED when it
//   falls out of the top 3xN ranks (turnover control - the evidence says
//   churn, not selection, is where retail momentum dies).
// - 0.15% per-side transaction cost on all traded value.
// - A held symbol whose bars end (delisting) sells at its last close.
public static class FactorBacktester
{
    public const int TopN = 15;
    private const int LookbackDays = 252;
    private const int SkipDays = 21;
    private const decimal MinPrice = 5m;
    private const decimal MinDollarVolume = 10_000_000m;
    private const int MinPositiveMonths = 7;
    private const decimal CostPerSide = 0.0015m;
    private const int KeepWithinRank = TopN * 3;

    public sealed record FactorPeriod(
        string Label, string From, string To,
        decimal ReturnPct, decimal SpyReturnPct, decimal MaxDrawdownPct, decimal SpyMaxDrawdownPct);

    public sealed record FactorResult(
        string Mode,                       // "factor"
        decimal TotalReturnPct,
        decimal SpyReturnPct,
        decimal MaxDrawdownPct,
        decimal SpyMaxDrawdownPct,
        int Rebalances,
        decimal AvgMonthlyTurnoverPct,
        FactorPeriod Train,
        FactorPeriod Holdout,
        bool HeldUp,                       // holdout beats SPY with market-like DD
        string Verdict,
        List<FactorPeriod> ByYear,
        List<string> FinalHoldings);

    // Momentum rank inputs for one symbol at one date index. Internal for tests.
    internal static decimal? MomentumAt(DailyBar[] bars, int index)
    {
        if (index < LookbackDays) return null;
        var past = bars[index - LookbackDays].Close;
        var recent = bars[index - SkipDays].Close;
        if (past <= 0) return null;
        return recent / past - 1m;
    }

    internal static bool PassesScreen(DailyBar[] bars, int index)
    {
        if (index < LookbackDays) return false;
        var close = bars[index].Close;
        if (close < MinPrice) return false;

        decimal volSum = 0;
        for (var j = index - 19; j <= index; j++) volSum += bars[j].Volume;
        if (volSum / 20m * close < MinDollarVolume) return false;

        // Consistency: months (21-day blocks) with a positive return.
        var positive = 0;
        for (var m = 0; m < 12; m++)
        {
            var from = bars[index - (m + 1) * 21].Close;
            var to = bars[index - m * 21].Close;
            if (from > 0 && to > from) positive++;
        }
        return positive >= MinPositiveMonths;
    }

    // Benchmark + sector ETFs live in the candle store for the RS component;
    // a momentum rank must not "discover" XLK as a stock pick.
    private static readonly HashSet<string> ExcludedInstruments = new(
        SwingTrader.Infrastructure.Market.SectorEtfMap.AllEtfs().Concat(["SPY", "VIX"]),
        StringComparer.OrdinalIgnoreCase);

    public static FactorResult Run(IReadOnlyDictionary<string, DailyBar[]> bars)
    {
        if (!bars.TryGetValue("SPY", out var spy) || spy.Length < LookbackDays + 42)
            throw new InvalidOperationException("Factor backtest needs SPY history beyond the 12-month lookback.");

        // Per-symbol date -> index lookup, built once.
        var indexBySymbol = bars
            .Where(kv => !ExcludedInstruments.Contains(kv.Key))
            .ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select((b, i) => (b.Date, i)).ToDictionary(x => x.Date, x => x.i),
                StringComparer.OrdinalIgnoreCase);

        // Explicit cash + per-holding VALUES: equity is always cash + sum of
        // holding values, so nothing can be double-counted or invented.
        var cash = 1m;
        var equityCurve = new List<(DateTime Date, decimal Equity)>();
        var holdings = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase); // symbol -> current value
        var lastMonth = -1;
        var rebalances = 0;
        var turnoverSum = 0m;

        for (var d = LookbackDays; d < spy.Length; d++)
        {
            var date = spy[d].Date;

            // Mark to market: apply each holding's daily return; a symbol with
            // no bar today keeps yesterday's value; a symbol whose SERIES has
            // ended sells at its last close (delisting exit to cash).
            foreach (var symbol in holdings.Keys.ToList())
            {
                var series = bars[symbol];
                if (!indexBySymbol[symbol].TryGetValue(date, out var i))
                {
                    if (series[^1].Date < date)
                    {
                        cash += holdings[symbol]; // value already marked at its last close
                        holdings.Remove(symbol);
                    }
                    continue;
                }
                if (i > 0 && series[i - 1].Close > 0)
                {
                    var ret = series[i].Close / series[i - 1].Close - 1m;
                    holdings[symbol] *= 1m + ret;
                }
            }
            var equity = cash + holdings.Values.Sum();

            // First trading day of a new month: re-rank and rebalance.
            if (date.Month != lastMonth)
            {
                lastMonth = date.Month;

                var ranked = new List<(string Symbol, decimal Momentum)>();
                foreach (var (symbol, series) in bars)
                {
                    if (ExcludedInstruments.Contains(symbol)) continue;
                    if (!indexBySymbol[symbol].TryGetValue(date, out var i)) continue;
                    var momentum = MomentumAt(series, i);
                    if (momentum is null || momentum <= 0) continue;
                    if (!PassesScreen(series, i)) continue;
                    ranked.Add((symbol, momentum.Value));
                }
                ranked.Sort((a, b) => b.Momentum.CompareTo(a.Momentum));

                var rankOf = ranked.Select((r, i) => (r.Symbol, Rank: i + 1))
                    .ToDictionary(x => x.Symbol, x => x.Rank, StringComparer.OrdinalIgnoreCase);

                // Keep current holdings still within the top 3xN; fill the
                // remaining slots from the top of the ranks.
                var keep = holdings.Keys
                    .Where(s => rankOf.TryGetValue(s, out var r) && r <= KeepWithinRank)
                    .ToList();
                var target = new List<string>(keep);
                foreach (var (symbol, _) in ranked)
                {
                    if (target.Count >= TopN) break;
                    if (!target.Contains(symbol, StringComparer.OrdinalIgnoreCase)) target.Add(symbol);
                }

                var per = target.Count > 0 ? equity / target.Count : 0m;
                var traded = 0m;
                foreach (var symbol in target)
                    traded += Math.Abs(per - (holdings.TryGetValue(symbol, out var v) ? v : 0m));
                traded += holdings.Where(kv => !target.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).Sum(kv => kv.Value);

                var cost = traded * CostPerSide;
                equity -= cost;
                // Fully invested after costs; per-slot recomputed so costs are
                // genuinely paid rather than invented back.
                per = target.Count > 0 ? equity / target.Count : 0m;
                holdings = target.ToDictionary(s => s, _ => per, StringComparer.OrdinalIgnoreCase);
                cash = equity - holdings.Values.Sum(); // 0 when invested; all-cash when no candidates

                rebalances++;
                if (equity > 0) turnoverSum += traded / equity * 100m;
            }

            equityCurve.Add((date, cash + holdings.Values.Sum()));
        }

        // Segments: same walk-forward curve, reported over the sweep's own
        // train/holdout boundary so the verdict is directly comparable with
        // every other Lab check.
        var cutoff = spy[(int)(spy.Length * 0.70)].Date;
        var full = Period("Full", equityCurve, spy);
        var train = Period("Train", equityCurve.Where(p => p.Date < cutoff).ToList(), spy);
        var holdout = Period("Holdout", equityCurve.Where(p => p.Date >= cutoff).ToList(), spy);

        var byYear = equityCurve.GroupBy(p => p.Date.Year)
            .Where(g => g.Count() > 30)
            .Select(g => Period(g.Key.ToString(), g.ToList(), spy))
            .ToList();

        var heldUp = holdout.ReturnPct > holdout.SpyReturnPct
                     && holdout.MaxDrawdownPct <= holdout.SpyMaxDrawdownPct * 1.5m;
        var verdict = heldUp
            ? $"HELD UP: beat SPY on the holdout ({holdout.ReturnPct:0.#}% vs {holdout.SpyReturnPct:0.#}%) with market-like drawdown ({holdout.MaxDrawdownPct:0.#}% vs SPY {holdout.SpyMaxDrawdownPct:0.#}%)."
            : $"Did NOT meet the bar: holdout {holdout.ReturnPct:0.#}% vs SPY {holdout.SpyReturnPct:0.#}%, max DD {holdout.MaxDrawdownPct:0.#}% vs SPY {holdout.SpyMaxDrawdownPct:0.#}% (needs: beat SPY with DD <= 1.5x SPY's).";

        return new FactorResult(
            "factor",
            full.ReturnPct, full.SpyReturnPct, full.MaxDrawdownPct, full.SpyMaxDrawdownPct,
            rebalances,
            rebalances > 0 ? Math.Round(turnoverSum / rebalances, 1) : 0m,
            train, holdout, heldUp, verdict, byYear,
            holdings.Keys.OrderBy(s => s).ToList());
    }

    private static FactorPeriod Period(string label, List<(DateTime Date, decimal Equity)> curve, DailyBar[] spy)
    {
        if (curve.Count < 2)
            return new FactorPeriod(label, "", "", 0m, 0m, 0m, 0m);

        var ret = curve[0].Equity > 0 ? (curve[^1].Equity / curve[0].Equity - 1m) * 100m : 0m;

        decimal peak = 0m, maxDd = 0m;
        foreach (var (_, e) in curve)
        {
            if (e > peak) peak = e;
            else if (peak > 0) maxDd = Math.Max(maxDd, (peak - e) / peak * 100m);
        }

        var spySlice = spy.Where(b => b.Date >= curve[0].Date && b.Date <= curve[^1].Date).ToArray();
        var spyRet = spySlice.Length > 1 && spySlice[0].Close > 0
            ? (spySlice[^1].Close / spySlice[0].Close - 1m) * 100m : 0m;
        decimal spyPeak = 0m, spyDd = 0m;
        foreach (var b in spySlice)
        {
            if (b.Close > spyPeak) spyPeak = b.Close;
            else if (spyPeak > 0) spyDd = Math.Max(spyDd, (spyPeak - b.Close) / spyPeak * 100m);
        }

        return new FactorPeriod(label,
            curve[0].Date.ToString("yyyy-MM-dd"), curve[^1].Date.ToString("yyyy-MM-dd"),
            Math.Round(ret, 1), Math.Round(spyRet, 1), Math.Round(maxDd, 1), Math.Round(spyDd, 1));
    }
}
