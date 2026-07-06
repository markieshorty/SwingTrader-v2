using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Interfaces;

// Adapted from the spec's UserId-based design to this codebase's
// AccountId-scoped reality: trading data (mode/trades/win rate), risk
// label, and watchlist count all live on the person's Account, not the
// AppUser row itself. KeyStatuses/FailedJobsLast48h/DataMaturity are
// deferred - each would need an extra per-user service call this first
// admin pass doesn't justify.
public record AdminUserSummary(
    string UserId,
    string Email,
    string DisplayName,
    AccountRole Role,
    int AccountId,
    DateTime FirstLoginAt,
    DateTime LastLoginAt,
    bool IsOnboarded,
    bool IsApproved,
    bool IsSuspended,
    string? SuspendReason,
    TradingMode TradingMode,
    int TotalTrades,
    decimal? WinRate,
    string RiskLabel,
    int EnabledWatchlistCount);

public record AdminStats(
    int TotalUsers,
    int ActiveUsersLast7Days,
    int TotalTradesAllTime,
    decimal AverageWinRateAllUsers,
    int UsersInDemoMode,
    int UsersInLiveMode,
    int UsersNotOnboarded,
    int TotalJobFailuresLast24h);

public record AdminJobFailure(
    int JobLogId,
    int AccountId,
    string? OwnerEmail,
    string JobType,
    DateOnly JobDate,
    string? ErrorMessage,
    int AttemptCount);

public interface IAdminRepository
{
    Task<AdminStats> GetStatsAsync(CancellationToken ct = default);
    Task<List<AdminUserSummary>> GetUsersAsync(CancellationToken ct = default);
    Task<AdminUserSummary?> GetUserAsync(string userId, CancellationToken ct = default);
    Task<List<AdminJobFailure>> GetJobFailuresAsync(TimeSpan lookback, CancellationToken ct = default);

    // Re-enqueues a failed job by clearing its JobLogEntry row so the next
    // Scheduler tick (every 5 minutes) treats it as never-enqueued and
    // fires it again - no direct Service Bus access needed from the admin
    // API layer.
    Task<bool> RetryJobAsync(int jobLogId, CancellationToken ct = default);

    // Dismisses a failed job without waiting for it to be re-enqueued -
    // same underlying row removal as RetryJobAsync (that's the only way to
    // clear a Failed JobLogEntry from GetJobFailuresAsync's query), just
    // without the "this will run again soon" implication.
    Task<bool> DeleteJobFailureAsync(int jobLogId, CancellationToken ct = default);
}
