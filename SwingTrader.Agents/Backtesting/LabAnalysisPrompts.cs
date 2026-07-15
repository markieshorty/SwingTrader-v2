using System.Text;
using System.Text.Json;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Backtesting;

// Shared prompt-building + response parsing for the Strategy Lab's Claude
// calls: the user-triggered "Analyse this run" (API) and the sweep-result
// explanation (Functions). One honesty contract for both:
//   - observations stated as observations, causal readings marked as conjecture
//   - fragility (small samples, concentrated edges) called out, not written past
//   - plain language: every number explained in the same breath it's stated
public static class LabAnalysisPrompts
{
    public const string SystemPrompt =
        "You are a careful quantitative analyst helping a retail swing trader interpret backtest results. " +
        "Rules you must follow strictly: " +
        "(1) Plain English a non-quant understands; whenever you state a number, say what it means in the same sentence. " +
        "(2) Never invent causal stories. You may describe what the data shows; any 'why' must be explicitly marked as a possible reading, not a fact. " +
        "(3) Call out fragility: buckets with few trades, results driven by a handful of outliers, and the general risk of overfitting to one historical period. " +
        "(4) A suggestion is a hypothesis to TEST, never a proven improvement. Word it as 'worth testing', not 'this will improve returns'. " +
        "(5) Respond with ONLY a JSON object, no markdown fences, in this exact shape: " +
        "{\"analysis\": \"2-3 short paragraphs of plain-text analysis\", " +
        "\"suggestion\": {\"rsi\": 0.17, \"macd\": 0.09, \"volume\": 0.21, \"sentiment\": 0.16, \"setupQuality\": 0.12, " +
        "\"relativeStrength\": 0.10, \"priceLevel\": 0.05, \"fundamentalMomentum\": 0.10, \"buyThreshold\": 6.0, " +
        "\"excludeBreakout\": true, \"rationale\": \"one sentence: what this tests and why\"} } " +
        "The 8 weight fields must sum to 1.0 (±0.01). If the data doesn't justify suggesting any change, set \"suggestion\" to null.";

    public static string DescribeConfig(
        HistoricBacktestWeights w, decimal buyThreshold, bool excludeBreakout, bool? autopauseDuringBear = null) =>
        $"RSI {w.Rsi:P0}, MACD {w.Macd:P0}, Volume {w.Volume:P0}, " +
        $"Setup quality {w.SetupQuality:P0}, Relative strength {w.RelativeStrength:P0}, " +
        $"Price level {w.PriceLevel:P0}; " +
        $"Buy threshold {buyThreshold:0.0}; Breakout setups {(excludeBreakout ? "excluded" : "allowed")}" +
        (autopauseDuringBear is null ? "" : $"; Bear-market entry pause (no entries while SPY < 200dma) {(autopauseDuringBear.Value ? "ON" : "OFF")}");

    public static string DescribeBuckets(string title, IEnumerable<BucketStat> rows) =>
        $"{title}:\n" + string.Join("\n", rows.Select(r =>
            $"  {r.Key}: {r.Count} trades, {r.WinRate:P0} win rate, {r.AvgReturnPct:F2}% avg raw return/trade"));

    public static string BuildHistoricRunPrompt(
        HistoricBacktestWeights weights, decimal buyThreshold, bool excludeBreakout, bool autopauseDuringBear, HistoricResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A historic-market backtest (S&P 1500 daily bars, full strategy replay) just completed.");
        sb.AppendLine($"Config: {DescribeConfig(weights, buyThreshold, excludeBreakout, autopauseDuringBear)}.");
        sb.AppendLine($"Period {r.From:yyyy-MM-dd} to {r.To:yyyy-MM-dd}: {r.Trades} trades, {r.WinRate:P1} win rate, " +
                      $"avg win {r.AvgWinPct:F1}% / avg loss {r.AvgLossPct:F1}%, expectancy {r.ExpectancyPct:F2}%/trade (raw), " +
                      $"profit factor {r.ProfitFactor:F2}, total return {r.TotalReturnPct:F1}% (SPY buy-and-hold {r.SpyReturnPct:F1}%), " +
                      $"max drawdown {r.MaxDrawdownPct:F1}%.");
        sb.AppendLine(DescribeBuckets("By setup type", r.BySetup));
        sb.AppendLine(DescribeBuckets("By conviction score (floored)", r.ByConviction));
        sb.AppendLine(DescribeBuckets("By exit reason", r.ByExitReason));
        sb.AppendLine("Caveats baked into this backtest: survivorship-biased universe (today's index membership), " +
                      "no AI stock selection, sentiment and fundamental momentum fixed neutral — results are for comparing configs, not predicting returns.");
        sb.AppendLine("Analyse this run and, if the data justifies it, suggest ONE next configuration worth testing.");
        return sb.ToString();
    }

    public static string BuildOwnDataRunPrompt(
        HistoricBacktestWeights weights, decimal buyThreshold, bool excludeBreakout,
        int totalTrades, int kept, int droppedWinners, int droppedLosers,
        decimal actualAvgReturnPct, decimal simAvgReturnPct, decimal actualWinRate, decimal simWinRate)
    {
        var sb = new StringBuilder();
        sb.AppendLine("An own-history simulation just completed: the user's real closed trades were re-scored under " +
                      "candidate dials to see which would still have been taken.");
        sb.AppendLine($"Config: {DescribeConfig(weights, buyThreshold, excludeBreakout)}.");
        sb.AppendLine($"Of {totalTrades} closed trades, these dials keep {kept} and drop {totalTrades - kept} " +
                      $"({droppedWinners} winners and {droppedLosers} losers among the dropped).");
        sb.AppendLine($"Average market-adjusted return/trade: {actualAvgReturnPct:F2}% actually vs {simAvgReturnPct:F2}% for the kept subset. " +
                      $"Win rate: {actualWinRate:P1} actually vs {simWinRate:P1} for the kept subset.");
        sb.AppendLine("Critical structural caveat you must reflect: this mode can only FILTER trades that were actually " +
                      "taken (taken because the production weights scored them above threshold). It cannot evaluate trades " +
                      "different dials would have taken instead. Treat any conclusion as weaker than a historic-market backtest.");
        sb.AppendLine("Analyse this run and, if the data justifies it, suggest ONE next configuration worth testing.");
        return sb.ToString();
    }

    public static string BuildAbComparisonPrompt(
        IReadOnlyList<(string Label, HistoricBacktestWeights Weights, decimal BuyThreshold, bool ExcludeBreakout, bool AutopauseDuringBear, HistoricResult Result)> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A head-to-head historic backtest just completed: the configurations below were replayed over the " +
                      "IDENTICAL market window (S&P 1500 daily bars, full strategy replay), so every difference in outcome " +
                      "comes from the dials alone.");
        foreach (var c in candidates)
        {
            var r = c.Result;
            sb.AppendLine($"--- {c.Label} ---");
            sb.AppendLine($"Config: {DescribeConfig(c.Weights, c.BuyThreshold, c.ExcludeBreakout, c.AutopauseDuringBear)}.");
            sb.AppendLine($"Period {r.From:yyyy-MM-dd} to {r.To:yyyy-MM-dd}: {r.Trades} trades, {r.WinRate:P1} win rate, " +
                          $"avg win {r.AvgWinPct:F1}% / avg loss {r.AvgLossPct:F1}%, expectancy {r.ExpectancyPct:F2}%/trade (raw), " +
                          $"profit factor {r.ProfitFactor:F2}, total return {r.TotalReturnPct:F1}% (SPY buy-and-hold {r.SpyReturnPct:F1}%), " +
                          $"max drawdown {r.MaxDrawdownPct:F1}%.");
            sb.AppendLine(DescribeBuckets("By setup type", r.BySetup));
            sb.AppendLine(DescribeBuckets("By conviction score (floored)", r.ByConviction));
            sb.AppendLine(DescribeBuckets("By exit reason", r.ByExitReason));
        }
        sb.AppendLine("Caveats baked into this backtest: survivorship-biased universe, no AI stock selection, sentiment " +
                      "and fundamental momentum fixed neutral — results compare configs, they don't predict returns.");
        sb.AppendLine("Compare the runs: where do the outcomes actually differ (which setups/exits drive the gap), is the " +
                      "difference large enough to matter given the trade counts, and — if the data justifies it — suggest " +
                      "ONE next configuration worth testing.");
        return sb.ToString();
    }

    public static string BuildSweepExplanationPrompt(
        SweepCandidateResult baseline, SweepCandidateResult winner, SweepValidation validation,
        IReadOnlyList<SweepCandidateResult> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("An optimizer sweep just completed: multiple dial configurations were evaluated on a TRAIN window " +
                      "(the earlier ~70% of a multi-year historic backtest), and the best one was then validated on the " +
                      "HELD-OUT remainder it was never tuned on.");
        sb.AppendLine($"Baseline (current production): {DescribeConfig(baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout, baseline.AutopauseDuringBear)} — " +
                      $"train window: {baseline.Trades} trades, {baseline.AdjustedExpectancyPct:F2}% market-adjusted expectancy/trade, " +
                      $"max drawdown {baseline.MaxDrawdownPct:F1}%.");
        sb.AppendLine($"Winner ({winner.Label}): {DescribeConfig(winner.Weights, winner.BuyThreshold, winner.ExcludeBreakout, winner.AutopauseDuringBear)} — " +
                      $"train window: {winner.Trades} trades, {winner.AdjustedExpectancyPct:F2}% adjusted expectancy/trade, " +
                      $"max drawdown {winner.MaxDrawdownPct:F1}%.");
        sb.AppendLine($"Out-of-sample validation: winner's adjusted expectancy {validation.HoldoutAdjustedExpectancyPct:F2}%/trade on the " +
                      $"held-out window (train was {validation.TrainAdjustedExpectancyPct:F2}%); the baseline scored " +
                      $"{validation.BaselineHoldoutAdjustedExpectancyPct:F2}% on that same held-out window. " +
                      $"Verdict: {(validation.HeldUp ? "held up" : "did NOT hold up")}.");
        var ranked = candidates.OrderByDescending(c => c.AdjustedExpectancyPct).ToList();
        var shown = ranked.Take(25).ToList();
        sb.AppendLine($"Top candidates of {candidates.Count} tried (label: adjusted expectancy on train, trades, eligible?):");
        foreach (var c in shown)
            sb.AppendLine($"  {c.Label}: {c.AdjustedExpectancyPct:F2}%/trade, {c.Trades} trades" +
                          (c.MetConstraints ? "" : $" — rejected: {c.RejectionReason}"));
        if (ranked.Count > shown.Count)
            sb.AppendLine($"  … plus {ranked.Count - shown.Count} more candidates ranked below these (worst: " +
                          $"{ranked[^1].AdjustedExpectancyPct:F2}%/trade).");
        sb.AppendLine("Write the analysis explaining which dials moved vs the baseline and what was OBSERVED across " +
                      "candidates (patterns in which direction scored better). Remember: with ~25 configs differing in " +
                      "several weights at once, you cannot establish WHY a direction won — any causal reading must be " +
                      "worded as conjecture. If the winner did not hold up out-of-sample, say plainly that applying it " +
                      "is not recommended and why. Set \"suggestion\" to null — the sweep already produced the candidate config.");
        return sb.ToString();
    }

    // Claude is asked for bare JSON, but models sometimes wrap it in fences or
    // preamble anyway - find the outermost object before parsing, and fall
    // back to treating the whole reply as prose analysis if parsing fails.
    public static (string Analysis, LabSuggestedConfig? Suggestion) ParseResponse(string raw)
    {
        try
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) return (raw.Trim(), null);

            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            var analysis = root.TryGetProperty("analysis", out var a) ? a.GetString() ?? raw.Trim() : raw.Trim();

            LabSuggestedConfig? suggestion = null;
            if (root.TryGetProperty("suggestion", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                decimal W(string name) => s.TryGetProperty(name, out var v) && v.TryGetDecimal(out var d) ? d : 0m;
                var weights = new HistoricBacktestWeights(
                    W("rsi"), W("macd"), W("volume"),
                    W("setupQuality"), W("relativeStrength"), W("priceLevel"));
                var sum = weights.Rsi + weights.Macd + weights.Volume
                    + weights.SetupQuality + weights.RelativeStrength + weights.PriceLevel;
                if (Math.Abs(sum - 1.0m) <= 0.02m)
                {
                    suggestion = new LabSuggestedConfig(
                        weights,
                        W("buyThreshold") is var bt && bt >= 3.0m && bt <= 9.0m ? bt : 6.0m,
                        s.TryGetProperty("excludeBreakout", out var eb) && eb.ValueKind is JsonValueKind.True or JsonValueKind.False
                            ? eb.GetBoolean() : true,
                        s.TryGetProperty("rationale", out var r) ? r.GetString() ?? "" : "");
                }
            }
            return (analysis, suggestion);
        }
        catch (JsonException)
        {
            return (raw.Trim(), null);
        }
    }
}

// A structured "next config worth testing" extracted from Claude's analysis -
// a hypothesis for the user to load and run, never something applied for them.
public record LabSuggestedConfig(
    HistoricBacktestWeights Weights, decimal BuyThreshold, bool ExcludeBreakout, string Rationale);
