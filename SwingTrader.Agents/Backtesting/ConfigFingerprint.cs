using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SwingTrader.Core.Enums;

namespace SwingTrader.Agents.Backtesting;

// Deterministic fingerprint of everything a single-book backtest actually
// exercises. Stamped onto validate / Monte Carlo BacktestRuns by the consumer
// (from the RESOLVED user config) and computed again from an account's live
// settings at strategy-share time: the two matching is the structural proof
// that the evidence quoted in a share email was produced by the exact
// settings being shared, not by an earlier tweak.
//
// Canonical form, not JSON: fixed field order, invariant-culture formatting,
// sorted collections - so dictionary ordering, culture, or serializer changes
// can never silently break the match. Since 20 Jul 2026 the per-day regime
// envelopes (RegimeBooks) ARE hashed when present - a Mixed-frame account's
// evidence must be invalidated by a Bull-book sizing change just as surely
// as by a weight change. Simulator-only pool sizing dials beyond the
// resolved values stay excluded.
public static class ConfigFingerprint
{
    public static string Compute(HistoricConfig cfg)
    {
        var sb = new StringBuilder(512);

        // Decimals carry their scale (0.05m != 0.0500m textually), and a value
        // read back from SQL often has a different scale than the same value
        // straight from a request DTO - format without trailing zeros so equal
        // values always hash equal. Doubles get round-trip formatting.
        void Num(string key, object value)
        {
            var text = value switch
            {
                decimal d => d.ToString("0.############################", CultureInfo.InvariantCulture),
                double db => db.ToString("R", CultureInfo.InvariantCulture),
                bool b => b ? "1" : "0",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture),
            };
            sb.Append(key).Append('=').Append(text).Append(';');
        }

        var w = cfg.Weights;
        Num("rsi", w.RsiWeight); Num("macd", w.MacdWeight); Num("vol", w.VolumeWeight);
        Num("sq", w.SetupQualityWeight); Num("rs", w.RelativeStrengthWeight); Num("pl", w.PriceLevelWeight);
        Num("buy", cfg.BuyThreshold);
        Num("regimeFilter", cfg.RegimeFilter);
        Num("maxOpen", cfg.MaxOpenPositions);
        Num("maxHold", cfg.MaxHoldDays);
        Num("trailAct", cfg.TrailingActivationPct);
        Num("trailDist", cfg.TrailingDistancePct);
        Num("stop", cfg.StopLossPct);
        Num("target", cfg.TargetPct);
        Num("probation", cfg.SimulateProbation);
        Num("minHold", cfg.MinHoldDays);
        Num("health", cfg.MomentumHealthThreshold);
        Num("posFrac", cfg.PositionFraction);
        Num("locked", cfg.LockedCapitalPct);
        Num("activeCap", cfg.ActiveCapitalPct ?? -1m);
        Num("maxPosOfActive", cfg.MaxPositionPctOfActive);

        // Excluded setups, RESOLVED the way the engine resolves them: an
        // explicit list wins; null falls back to the legacy ExcludeBreakout
        // toggle. Sorted so list order never matters.
        var excluded = cfg.ExcludedSetups
            ?? (cfg.ExcludeBreakout ? [SetupType.Breakout] : Array.Empty<SetupType>());
        sb.Append("excluded=")
          .Append(string.Join(',', excluded.Select(s => s.ToString()).OrderBy(s => s, StringComparer.Ordinal)))
          .Append(';');

        // Per-setup tactics sorted by setup name. A missing map hashes as
        // empty (every setup on the flat fallbacks above).
        foreach (var kv in (cfg.SetupTactics ?? new Dictionary<SetupType, HistoricSetupTactics>())
                     .OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal))
        {
            var t = kv.Value;
            sb.Append("tac:").Append(kv.Key).Append('=');
            Num("s", t.StopLossPct); Num("t", t.TargetPct); Num("h", t.GuideHoldDays);
            Num("ta", t.TrailingActivationPct); Num("td", t.TrailingDistancePct);
        }

        // Per-regime exposure envelopes (Mixed frame), sorted by regime name.
        // Absent = single-book config; the section is simply omitted so all
        // pre-existing single-book stamps keep their hashes.
        if (cfg.RegimeBooks is not null)
        {
            foreach (var kv in cfg.RegimeBooks.OrderBy(k => k.Key.ToString(), StringComparer.Ordinal))
            {
                var e = kv.Value;
                sb.Append("rb:").Append(kv.Key).Append('=');
                Num("ap", e.Autopause); Num("mo", e.MaxOpenPositions);
                Num("pf", e.PositionFraction); Num("lc", e.LockedCapitalPct);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
