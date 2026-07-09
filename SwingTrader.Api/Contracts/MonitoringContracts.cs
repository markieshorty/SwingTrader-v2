using SwingTrader.Core.Interfaces;

namespace SwingTrader.Api.Contracts;

// The full admin monitoring payload: DB-sourced sections (from
// IMonitoringRepository) plus the two external sources, each of which carries
// its own Available/Error so a missing RBAC grant degrades one card rather
// than failing the whole dashboard.
public record MonitoringDashboard(
    DateTime GeneratedAt,
    IReadOnlyList<MonitoringWorkerView> Workers,
    QueueHealthSection Queues,
    MonitoringJobs Jobs,
    IReadOnlyList<AdminJobFailure> RecentJobFailures,
    InsightsSection Insights,
    IReadOnlyList<MonitoringSystemEvent> SystemEvents,
    MonitoringTradingState Trading);

public record MonitoringWorkerView(
    string Name,
    string LastResult,
    DateTime LastHeartbeatAt,
    double MinutesSinceHeartbeat,
    bool IsStale,
    string? Message);

public record QueueHealthSection(
    bool Available,
    string? Error,
    IReadOnlyList<QueueDepth> Queues,
    long TotalDeadLettered);

public record QueueDepth(string Name, long Active, long DeadLettered, long Scheduled);

public record InsightsSection(
    bool Available,
    string? Error,
    long Requests24h,
    double FailedRequestPct,
    long ServerExceptions24h,
    long DependencyFailures24h,
    long ClaudeRateLimited24h,
    IReadOnlyList<NamedCount> TopExceptions);

public record NamedCount(string Name, long Count);

// A single App Insights record for the drill-down list (exceptions /
// dependency failures / Claude 429s), normalised to a common shape so the
// detail page renders uniformly regardless of source table.
public record InsightsEvent(
    DateTimeOffset Timestamp,
    string Category,
    string Title,
    string? Detail,
    string? Operation,
    // Origin in application code (top SwingTrader stack frame, "file.cs:line").
    // Populated for exceptions; null for dependency failures / 429s.
    string? Location);

public record InsightsDetailSection(
    bool Available,
    string? Error,
    string Kind,
    IReadOnlyList<InsightsEvent> Events);
