using SwingTrader.Core.Constants;
using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

// The tenant/billing unit. All scoped trading data hangs off AccountId.
// Multiple AppUsers can belong to one Account (owner + invited members).
public class Account : UnscopedEntity
{
    public string Name { get; set; } = "My Account";
    public string? T212AccountId { get; set; }
    public bool GlobalRefinementOptIn { get; set; } = true;
    public TradingMode TradingMode { get; set; } = TradingMode.Demo;
    public bool ApprovalRequired { get; set; } = true;

    // How many symbols Claude selects for the weekly AI-managed watchlist
    // refresh (WatchlistSelectionService). Account-level, not per-regime: the
    // watchlist universe doesn't change with the market regime. See
    // CapitalRules.DefaultTargetWatchlistSize for the tradeoff (more symbols =
    // more Research/Monitor API calls per cycle).
    public int TargetWatchlistSize { get; set; } = CapitalRules.DefaultTargetWatchlistSize;

    // How many symbols Claude picks for the weekly QUALITATIVE AI watchlist
    // (QualitativeWatchlistService). Account-level like TargetWatchlistSize and
    // independent of it - the qualitative list is a separate, smaller probe.
    // See CapitalRules.DefaultQualitativeWatchlistSize.
    public int QualitativeWatchlistSize { get; set; } = CapitalRules.DefaultQualitativeWatchlistSize;

    // Pause switch for new-position executions ("entries"), held per mode so
    // pausing Demo (e.g. to sit out a rough market) doesn't also freeze Live
    // and vice versa. Only ExecutionService's buy path honours this - Monitor
    // keeps running so open positions still get their stop-loss/target/time
    // exits enforced while entries are paused. Reason/PausedAt distinguish a
    // manual pause from a circuit-breaker auto-pause and drive the dashboard
    // capsule; they're only meaningful while the matching Paused flag is set.
    public bool ExecutionPausedDemo { get; set; }
    public bool ExecutionPausedLive { get; set; }
    public ExecutionPauseReason ExecutionPauseReasonDemo { get; set; }
    public ExecutionPauseReason ExecutionPauseReasonLive { get; set; }
    public DateTime? ExecutionPausedAtDemo { get; set; }
    public DateTime? ExecutionPausedAtLive { get; set; }

    // Set (to the ET trading date) when the owner manually resumes entries
    // that an AUTO pause had stopped (circuit breaker or regime autopause).
    // While today's ET date matches, Monitor's auto-pause paths stand down:
    // a deliberate manual resume is the owner overruling the machine for the
    // rest of that trading day, not an invitation to re-pause five minutes
    // later. Manual pauses are unaffected, and the veto expires by itself at
    // the next ET day rollover.
    public DateOnly? AutoPauseVetoDateDemo { get; set; }
    public DateOnly? AutoPauseVetoDateLive { get; set; }

    // The last market regime Monitor detected (MarketRegimeService). Persisted
    // so it crosses the API/Functions process boundary (separate memory
    // caches): the risk-profile repository resolves the ACTIVE regime book
    // from this, making every live consumer regime-aware without threading
    // market-data clients through them. Market-wide, but stored per account
    // for simplicity - each account's Monitor writes the same value. Defaults
    // to Neutral (the baseline book) until Monitor first runs.
    public MarketRegime CurrentMarketRegime { get; set; } = MarketRegime.Neutral;
    public DateTime? RegimeUpdatedAt { get; set; }

    // True when new executions are paused for the given mode.
    public bool IsExecutionPaused(TradingMode mode) =>
        mode == TradingMode.Live ? ExecutionPausedLive : ExecutionPausedDemo;

    public ExecutionPauseReason ExecutionPauseReasonFor(TradingMode mode) =>
        mode == TradingMode.Live ? ExecutionPauseReasonLive : ExecutionPauseReasonDemo;

    public DateTime? ExecutionPausedAtFor(TradingMode mode) =>
        mode == TradingMode.Live ? ExecutionPausedAtLive : ExecutionPausedAtDemo;

    public bool IsAutoPauseVetoed(TradingMode mode, DateOnly todayEt) =>
        (mode == TradingMode.Live ? AutoPauseVetoDateLive : AutoPauseVetoDateDemo) == todayEt;

    // Pause entries for a mode, recording why and when. No-op on the
    // reason/timestamp if already paused, so a circuit-breaker trip can't
    // overwrite an existing manual pause and a re-trip every Monitor cycle
    // doesn't keep bumping the timestamp.
    public void PauseExecution(TradingMode mode, ExecutionPauseReason reason, DateTime now)
    {
        if (IsExecutionPaused(mode)) return;
        if (mode == TradingMode.Live)
        {
            ExecutionPausedLive = true;
            ExecutionPauseReasonLive = reason;
            ExecutionPausedAtLive = now;
        }
        else
        {
            ExecutionPausedDemo = true;
            ExecutionPauseReasonDemo = reason;
            ExecutionPausedAtDemo = now;
        }
    }

    // vetoAutoPauseForEtDay: pass today's ET date on a MANUAL resume - if the
    // pause being cleared was an auto pause, auto-pausing is vetoed for the
    // rest of that trading day. Auto-resumes (regime release) pass null.
    public void ResumeExecution(TradingMode mode, DateOnly? vetoAutoPauseForEtDay = null)
    {
        var wasAutoPause = IsExecutionPaused(mode)
            && ExecutionPauseReasonFor(mode) is ExecutionPauseReason.CircuitBreaker or ExecutionPauseReason.RegimeAutopause;

        if (mode == TradingMode.Live)
        {
            ExecutionPausedLive = false;
            ExecutionPausedAtLive = null;
            if (wasAutoPause && vetoAutoPauseForEtDay is { } dLive) AutoPauseVetoDateLive = dLive;
        }
        else
        {
            ExecutionPausedDemo = false;
            ExecutionPausedAtDemo = null;
            if (wasAutoPause && vetoAutoPauseForEtDay is { } dDemo) AutoPauseVetoDateDemo = dDemo;
        }
    }
    // Soft-delete: the account's own children (WatchlistItems,
    // StrategyWeights, etc.) carry a Restrict FK to Accounts, so a hard
    // delete would require cascading through every scoped table. Marking
    // IsDeleted instead blocks re-login (UserRegistrationMiddleware) and
    // hides the account without touching trading history.
    public bool IsDeleted { get; set; }
}
