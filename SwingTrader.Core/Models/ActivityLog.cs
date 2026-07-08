using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Models;

// Generic activity/notification log for all account-relevant events:
// worker job runs, user actions (approvals, settings changes), and
// system events (circuit breaker, exit signals, trades placed).
public class ActivityLog : BaseEntity
{
    // The account's TradingMode at the time this entry was logged - kept
    // separate so a Demo test run's activity feed never interleaves with
    // the audit trail of what actually happened to real money (see
    // PortfolioSnapshot.TradingMode). Meaningless for SystemAccountId rows,
    // which aren't tied to any one account's mode.
    public TradingMode TradingMode { get; set; }
    public DateTime OccurredAt { get; set; }

    // "WorkerRun" | "UserAction" | "SystemEvent"
    public string Category { get; set; } = "";

    // Human-readable source/title: "Research", "Trade Approved", "Circuit Breaker", etc.
    public string Title { get; set; } = "";

    // "Success" | "Warning" | "Failed" | "Info"
    public string Result { get; set; } = "Info";

    public string? Message { get; set; }
}
