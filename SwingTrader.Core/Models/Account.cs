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

    // Pause switch for new-position executions, held per mode so pausing Demo
    // (e.g. to sit out a rough market) doesn't also freeze Live and vice
    // versa. Only ExecutionService's buy path honours this - Monitor keeps
    // running so open positions still get their stop-loss/target/time exits
    // enforced while trading is paused.
    public bool ExecutionPausedDemo { get; set; }
    public bool ExecutionPausedLive { get; set; }

    // True when new executions are paused for the given mode.
    public bool IsExecutionPaused(TradingMode mode) =>
        mode == TradingMode.Live ? ExecutionPausedLive : ExecutionPausedDemo;
    // Soft-delete: the account's own children (WatchlistItems,
    // StrategyWeights, etc.) carry a Restrict FK to Accounts, so a hard
    // delete would require cascading through every scoped table. Marking
    // IsDeleted instead blocks re-login (UserRegistrationMiddleware) and
    // hides the account without touching trading history.
    public bool IsDeleted { get; set; }
}
