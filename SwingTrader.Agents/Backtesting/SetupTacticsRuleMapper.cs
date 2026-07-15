using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Backtesting;

// Applies a Lab run's tactic overrides onto the account's live SetupTactics
// rows when the owner ticks "Apply risk settings" on a winning A/B / optimizer
// run. Live execution reads SetupTactics (the risk profile's stop/target/hold
// are only a fallback/seed since Phase 2), so a per-setup or uniform tactic
// winner has to land HERE to actually change trading.
//
// Precedence mirrors the backtester's ToConfig/MergeTactics so what gets
// applied is exactly what was simulated:
//   1. Uniform rule fields (StopLossPct/TargetPct/MaxHoldDays/Trailing*) - a
//      single value tested across every setup - overwrite that field on every
//      row.
//   2. Per-setup overrides (Rules.SetupTactics) win over the uniform layer for
//      the setups they name.
// Rows the run didn't touch are left exactly as they were. The caller is
// responsible for Validate()+persisting each returned row.
public static class SetupTacticsRuleMapper
{
    // Mutates matching rows in-place and returns the distinct rows that changed
    // (so the caller only writes what moved). Unknown per-setup names are
    // skipped, matching the backtester.
    public static IReadOnlyList<SetupTactics> Apply(
        IReadOnlyList<SetupTactics> rows, HistoricTradingRules rules)
    {
        var changed = new HashSet<SetupTactics>();

        var hasUniform = rules.StopLossPct is not null || rules.TargetPct is not null
            || rules.MaxHoldDays is not null || rules.TrailingActivationPct is not null
            || rules.TrailingDistancePct is not null;
        if (hasUniform)
        {
            foreach (var row in rows)
            {
                if (rules.StopLossPct is { } s) row.StopLossPct = s;
                if (rules.TargetPct is { } t) row.TargetPct = t;
                if (rules.MaxHoldDays is { } h) row.GuideHoldDays = h;
                if (rules.TrailingActivationPct is { } ta) row.TrailingActivationPct = (double)ta;
                if (rules.TrailingDistancePct is { } td) row.TrailingDistancePct = (double)td;
                changed.Add(row);
            }
        }

        foreach (var o in rules.SetupTactics ?? [])
        {
            if (!Enum.TryParse<SetupType>(o.Setup, ignoreCase: true, out var setup)) continue;
            var row = rows.FirstOrDefault(r => r.SetupType == setup);
            if (row is null) continue;
            row.StopLossPct = o.StopLossPct;
            row.TargetPct = o.TargetPct;
            row.GuideHoldDays = o.GuideHoldDays;
            row.TrailingActivationPct = (double)o.TrailingActivationPct;
            row.TrailingDistancePct = (double)o.TrailingDistancePct;
            changed.Add(row);
        }

        return changed.ToList();
    }
}
