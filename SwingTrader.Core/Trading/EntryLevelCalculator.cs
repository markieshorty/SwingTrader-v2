using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Trading;

// Shared by ReportGenerationService (which computes StockSignal.CalculatedStopLoss/
// CalculatedTarget for display, off whatever price is live when Report runs ~6:30 ET)
// and ExecutionService (which used to just trust those hours-old absolute price
// levels when actually placing the order). The percentage table itself only
// depends on SetupType/ConvictionScore, not on any particular price snapshot -
// so both callers get the same distances, each applied to their own live price.
public static class EntryLevelCalculator
{
    // Rewritten 2026-07-12: the old per-setup stop table (5% default / 6%
    // Breakout / 4% VolumeSpike) and per-conviction target table (+8/10/12%)
    // are gone - stop and target are now plain risk-profile settings
    // (StopLossPct / TargetPct, defaults 5% / 8% ~ the old tables' common
    // case). Two reasons: (a) the account's own data showed conviction does
    // not rank above the buy gate, so conviction-scaled targets were unearned
    // complexity; (b) the Lab-validated flat-exit config (7%/10%, held up
    // out-of-sample at 1.44%/trade vs production's 0.25%) needed to be
    // runnable live, and settings the Lab can mirror keep backtest and live
    // in lockstep.
    public static (decimal StopLoss, decimal Target) Calculate(
        decimal price, decimal stopLossPct, decimal targetPct)
    {
        return (Math.Round(price * (1 - stopLossPct), 2), Math.Round(price * (1 + targetPct), 2));
    }
}
