namespace SwingTrader.Agents.Research;

// Slot-aware research gate (docs/on-demand-research P1): decides whether an
// account can act on a new Buy today. Pending intents count as occupied -
// they either become positions or release within minutes, and double-counting
// a slot is the failure mode that matters. Paused entries mean zero usable
// slots regardless of the count.
public static class SlotGate
{
    public const string SlotSkipSummary = "Skipped — portfolio full (slot-aware stage-2 skip).";

    public static bool IsPortfolioFull(int openCount, int pendingCount, int maxOpenPositions, bool entriesPaused) =>
        entriesPaused || openCount + pendingCount >= maxOpenPositions;
}
