using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

// The tenant/billing unit. All scoped trading data hangs off AccountId.
// Multiple AppUsers can belong to one Account (owner + invited members).
public class Account : UnscopedEntity
{
    public string Name { get; set; } = "My Account";
    public string? T212AccountId { get; set; }
    public bool GlobalRefinementOptIn { get; set; } = false;
    public TradingMode TradingMode { get; set; } = TradingMode.Demo;
    public bool ApprovalRequired { get; set; } = true;
    // Soft-delete: the account's own children (WatchlistItems,
    // StrategyWeights, etc.) carry a Restrict FK to Accounts, so a hard
    // delete would require cascading through every scoped table. Marking
    // IsDeleted instead blocks re-login (UserRegistrationMiddleware) and
    // hides the account without touching trading history.
    public bool IsDeleted { get; set; }
}
