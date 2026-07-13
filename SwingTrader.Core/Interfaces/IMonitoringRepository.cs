namespace SwingTrader.Core.Interfaces;

// Database-sourced half of the admin monitoring dashboard. The external
// sources (Service Bus queue depths, App Insights telemetry) are composed on
// top of this in the API layer - see MonitoringService - so a missing RBAC
// grant on either of those degrades gracefully without touching these DB reads.

public record MonitoringWorker(
    string Name,
    string LastResult,          // "Success" | "Warning" | "Failed" | "Skipped"
    DateTime LastHeartbeatAt,
    string? Message);

public record MonitoringJobTypeCount(string JobType, int Completed, int Failed);

public record MonitoringJobs(
    int Failed24h,
    int Completed24h,
    int Processing,
    int Enqueued,
    List<MonitoringJobTypeCount> ByType);

// A cross-account SystemEvent worth an operator's attention - circuit-breaker
// trips, execution failures, the intent-first "Order Not Placed" cancellations.
public record MonitoringSystemEvent(
    DateTime OccurredAt,
    int AccountId,
    string Title,
    string Result,
    string? Message);

public record MonitoringTradingState(
    int OpenPositions,
    int PendingIntents,     // intent-first placements not yet resolved
    int CancelledToday,     // intents reconciled as never-placed today
    int BuysToday,          // entry orders placed today (by OpenedAt)
    int ExitsToday);        // positions closed today (by ClosedAt) - sells were invisible before

public record MonitoringDbSnapshot(
    List<MonitoringWorker> Workers,
    MonitoringJobs Jobs,
    List<MonitoringSystemEvent> SystemEvents,
    MonitoringTradingState Trading);

public interface IMonitoringRepository
{
    Task<MonitoringDbSnapshot> GetSnapshotAsync(CancellationToken ct = default);
}
