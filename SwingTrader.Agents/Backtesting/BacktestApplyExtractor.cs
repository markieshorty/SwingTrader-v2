using System.Text.Json;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Backtesting;

// Pulls the applyable configuration out of a completed BacktestRun's stored
// JSON so the Strategy Lab history tabs can re-apply it to live settings:
//   A/B    -> candidates[0] (the user's own dials) from ResultJson, with the
//             risk-rule overrides from RequestJson candidates[0].Rules.
//   sweep  -> the validated winner from ResultJson, including its Rules when a
//             rule candidate won ("search for optimal trading rules" / the
//             per-setup guide-hold search).
// The returned Rules (HistoricTradingRules) carry both uniform overrides and
// per-setup SetupTactics; the apply endpoint feeds them through
// BacktestRiskRuleMapper (profile-level) and SetupTacticsRuleMapper (per-setup)
// so a winning tactic lands where live execution reads it.
// RequestJson is serialized PascalCase, ResultJson camelCase - all lookups
// here are case-insensitive so both parse. Pure and defensive: any missing/
// malformed shape returns null rather than throwing, so a half-written or
// legacy run just can't be applied.
public static class BacktestApplyExtractor
{
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    public sealed record BacktestStats(
        int Trades, decimal WinRatePct, decimal TotalReturnPct,
        decimal MaxDrawdownPct, decimal ProfitFactor, decimal ExpectancyPct);

    public sealed record ApplyableConfig(
        string Label, HistoricBacktestWeights Weights, decimal BuyThreshold,
        HistoricTradingRules? Rules, BacktestStats Stats);

    public static ApplyableConfig? Extract(string? mode, string? requestJson, string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            var root = doc.RootElement;

            if (string.Equals(mode, "ab", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryProp(root, "candidates", out var cands) || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0)
                    return null;
                var c0 = cands[0];
                if (!TryParseWeights(c0, out var weights) || !TryProp(c0, "result", out var result)) return null;
                var rules = ExtractAbRules(requestJson);
                return new ApplyableConfig(
                    Label(c0, "A"), weights, Decimal(c0, "buyThreshold"), rules, ParseStats(result));
            }

            if (string.Equals(mode, "sweep", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryProp(root, "winner", out var winner) || !TryParseWeights(winner, out var weights)) return null;
                // The winner's headline stats live directly on the candidate
                // record, as do its rules when the sweep ran with "search for
                // optimal trading rules" and a rule candidate won.
                var rules = TryProp(winner, "rules", out var r) && r.ValueKind == JsonValueKind.Object
                    ? r.Deserialize<HistoricTradingRules>(CaseInsensitive)
                    : null;
                return new ApplyableConfig(
                    Label(winner, "Winner"), weights, Decimal(winner, "buyThreshold"), rules, ParseStats(winner));
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The risk-rule overrides that were tested alongside config A - only
    // present on A/B runs, read from the REQUEST (they're not echoed into the
    // result). Null when the run set no overrides (it used production rules).
    private static HistoricTradingRules? ExtractAbRules(string? requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            if (!TryProp(doc.RootElement, "candidates", out var cands)
                || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0)
                return null;
            if (!TryProp(cands[0], "rules", out var rules) || rules.ValueKind == JsonValueKind.Null)
                return null;
            return rules.Deserialize<HistoricTradingRules>(CaseInsensitive);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BacktestStats ParseStats(JsonElement e) => new(
        (int)Decimal(e, "trades"),
        Decimal(e, "winRate"),
        Decimal(e, "totalReturnPct"),
        Decimal(e, "maxDrawdownPct"),
        Decimal(e, "profitFactor"),
        Decimal(e, "expectancyPct"));

    private static bool TryParseWeights(JsonElement parent, out HistoricBacktestWeights weights)
    {
        weights = default!;
        if (!TryProp(parent, "weights", out var w) || w.ValueKind != JsonValueKind.Object) return false;
        var parsed = w.Deserialize<HistoricBacktestWeights>(CaseInsensitive);
        if (parsed is null) return false;
        weights = parsed;
        return true;
    }

    private static string Label(JsonElement e, string fallback) =>
        TryProp(e, "label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()! : fallback;

    private static decimal Decimal(JsonElement e, string name) =>
        TryProp(e, name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    // Case-insensitive property lookup so PascalCase (request) and camelCase
    // (result) both resolve without two code paths.
    private static bool TryProp(JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
