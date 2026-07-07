namespace SwingTrader.Core.Models;

// Generic activity/notification log for all account-relevant events:
// worker job runs, user actions (approvals, settings changes), and
// system events (circuit breaker, exit signals, trades placed).
public class ActivityLog : BaseEntity
{
    public DateTime OccurredAt { get; set; }

    // "WorkerRun" | "UserAction" | "SystemEvent"
    public string Category { get; set; } = "";

    // Human-readable source/title: "Research", "Trade Approved", "Circuit Breaker", etc.
    public string Title { get; set; } = "";

    // "Success" | "Warning" | "Failed" | "Info"
    public string Result { get; set; } = "Info";

    public string? Message { get; set; }
}
