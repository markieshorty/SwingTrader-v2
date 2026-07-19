using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Backtesting;

// Single source of truth for turning a backtest request (weights + optional
// rule overrides) plus the account's live risk profile / setup tactics into
// the resolved HistoricConfig the engine runs. Extracted from
// BacktestConsumerFunction so the API-side strategy-share service resolves
// EXACTLY the same config the consumer does - the config fingerprint that
// gates sharing is only meaningful if both sides build configs identically.
public static class BacktestConfigFactory
{
    public static HistoricConfig ToConfig(
        HistoricBacktestWeights w, decimal buyThreshold, bool excludeBreakout, bool autopauseDuringBear,
        AccountRiskProfile profile, IReadOnlyDictionary<SetupType, HistoricSetupTactics> accountTactics,
        HistoricTradingRules? rules = null) =>
        new(new StrategyWeights
        {
            RsiWeight = w.Rsi, MacdWeight = w.Macd, VolumeWeight = w.Volume,
            SetupQualityWeight = w.SetupQuality, RelativeStrengthWeight = w.RelativeStrength,
            PriceLevelWeight = w.PriceLevel,
        }, buyThreshold, excludeBreakout,
        // SPY-below-200dma entry pause approximates the live bear autopause.
        // Per-request/candidate (a Lab dial), not the profile setting - the
        // baseline candidate snapshots the profile value at queue time.
        RegimeFilter: autopauseDuringBear,
        // Trading rules: explicit Lab overrides win; otherwise the account's
        // live risk profile - including the trailing shape, which previously
        // sat as hardcoded 5%/3% constants in the engine and silently
        // diverged from live whenever the profile differed.
        MaxOpenPositions: rules?.MaxOpenPositions ?? profile.MaxOpenPositions,
        MaxHoldDays: rules?.MaxHoldDays ?? profile.MaxHoldDays,
        ExcludedSetups: ParseSetups(rules?.ExcludedSetups),
        TrailingActivationPct: rules?.TrailingActivationPct ?? (decimal)profile.TrailingActivationPct,
        TrailingDistancePct: rules?.TrailingDistancePct ?? (decimal)profile.TrailingDistancePct,
        // Null rule = the live risk-profile setting (the tables are gone), so
        // an untouched Lab run simulates exactly what production will do.
        StopLossPct: rules?.StopLossPct ?? profile.StopLossPct,
        TargetPct: rules?.TargetPct ?? profile.TargetPct,
        SimulateProbation: rules?.SimulateProbation ?? true,
        MinHoldDays: rules?.MinHoldDays ?? profile.MinHoldDays,
        MomentumHealthThreshold: rules?.MomentumHealthThreshold ?? profile.MomentumHealthThreshold,
        // Flat sizing mirrors live: each position is FlatPositionPct of equity.
        PositionFraction: rules?.PositionFraction ?? profile.FlatPositionPct,
        // Locked-capital reserve caps total deployment to the un-locked share,
        // mirroring live. Lab override wins; else the book's live value.
        LockedCapitalPct: rules?.LockedCapitalPct ?? profile.LockedCapitalPct,
        // Lab-only pool-sizing sim (no live equivalent since the tier ladder
        // was removed): null keeps flat sizing; the two dials below only apply
        // when a Lab run explicitly sets ActiveCapitalPct.
        ActiveCapitalPct: rules?.ActiveCapitalPct,
        MaxPositionPctOfActive: rules?.MaxPositionPctOfActive ?? 0.33m,
        // Per-setup tactics: the account's live set, with any Lab overrides
        // overlaid. Built once per candidate from data already in memory - no
        // DB touch.
        SetupTactics: MergeTactics(accountTactics, rules));

    // Attaches the LIVE regime books' exposure envelopes to a config - the
    // Mixed frame with no per-regime overrides, i.e. exactly how the account
    // trades when the Default master book is off. Used by BOTH the consumer's
    // evidence stamping and the API's live-settings fingerprint so the two
    // sides can never derive different envelopes from the same books. Default
    // is excluded (it's never detected day-to-day, only forced).
    public static HistoricConfig WithLiveRegimeBooks(
        HistoricConfig cfg, IReadOnlyDictionary<MarketRegime, AccountRiskProfile> books) =>
        cfg with
        {
            RegimeBooks = books
                .Where(kv => kv.Key != MarketRegime.Default)
                .ToDictionary(
                    kv => kv.Key,
                    kv => new RegimeEnvelope(
                        kv.Value.AutopauseTrading, kv.Value.MaxOpenPositions,
                        kv.Value.FlatPositionPct, kv.Value.LockedCapitalPct)),
        };

    // Builds the per-setup tactics map applied to a candidate, layering two
    // kinds of override onto the account's live baseline:
    //   1. UNIFORM rule overrides (rules.StopLossPct/TargetPct/MaxHoldDays/
    //      Trailing*) - a single value the optimizer's rule-search or the Lab's
    //      global fields set. Applied to EVERY setup, so "Stop 5%" means 5% on
    //      all of them. Without this, per-setup tactics would silently swallow
    //      those candidates and the rule search would be inert.
    //   2. PER-SETUP overrides (rules.SetupTactics) - the Lab's tactics editor
    //      / per-setup optimizer search, a full replace for each named setup.
    // Null rules (an untouched baseline run) leaves the account map as-is, so
    // the run mirrors live exactly.
    public static IReadOnlyDictionary<SetupType, HistoricSetupTactics> MergeTactics(
        IReadOnlyDictionary<SetupType, HistoricSetupTactics> baseline,
        HistoricTradingRules? rules)
    {
        if (rules is null) return baseline;
        var merged = new Dictionary<SetupType, HistoricSetupTactics>(baseline);

        // 1. Uniform rule fields (only the ones this candidate actually set).
        if (rules.StopLossPct is not null || rules.TargetPct is not null || rules.MaxHoldDays is not null
            || rules.TrailingActivationPct is not null || rules.TrailingDistancePct is not null)
        {
            foreach (var setup in merged.Keys.ToList())
            {
                var b = merged[setup];
                merged[setup] = b with
                {
                    StopLossPct = rules.StopLossPct ?? b.StopLossPct,
                    TargetPct = rules.TargetPct ?? b.TargetPct,
                    GuideHoldDays = rules.MaxHoldDays ?? b.GuideHoldDays,
                    TrailingActivationPct = rules.TrailingActivationPct ?? b.TrailingActivationPct,
                    TrailingDistancePct = rules.TrailingDistancePct ?? b.TrailingDistancePct,
                };
            }
        }

        // 2. Per-setup overrides win over the uniform layer.
        foreach (var o in rules.SetupTactics ?? [])
        {
            if (!Enum.TryParse<SetupType>(o.Setup, ignoreCase: true, out var setup)) continue;
            merged[setup] = new HistoricSetupTactics(
                o.StopLossPct, o.TargetPct, o.GuideHoldDays,
                o.TrailingActivationPct, o.TrailingDistancePct);
        }
        return merged;
    }

    // Unknown names are ignored rather than failing the run - the list comes
    // from the UI, but the request JSON is stored and could be replayed after
    // an enum rename.
    public static IReadOnlyCollection<SetupType>? ParseSetups(List<string>? names) =>
        names is null
            ? null
            : names.Select(n => Enum.TryParse<SetupType>(n, ignoreCase: true, out var s) ? s : (SetupType?)null)
                .Where(s => s.HasValue)
                .Select(s => s!.Value)
                .ToList();
}
