using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class AdminRepository(SwingTraderDbContext db) : IAdminRepository
{
    // Deleting a user (Owner) soft-deletes their Account rather than removing
    // the AppUser row (RemoveAsync is only used for Members) - without this
    // exclusion, a "deleted" user kept showing up in every admin list/count
    // forever, since nothing ever filtered on the account's IsDeleted flag.
    private async Task<List<int>> GetDeletedAccountIdsAsync(CancellationToken ct) =>
        await db.Accounts.Where(a => a.IsDeleted).Select(a => a.Id).ToListAsync(ct);

    public async Task<AdminStats> GetStatsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var sevenDaysAgo = now.AddDays(-7);
        var oneDayAgo = now.AddHours(-24);

        var deletedAccountIds = await GetDeletedAccountIdsAsync(ct);
        var totalUsers = await db.AppUsers.CountAsync(u => u.AccountId == null || !deletedAccountIds.Contains(u.AccountId.Value), ct);
        var activeLast7Days = await db.AppUsers.CountAsync(
            u => u.LastLoginAt >= sevenDaysAgo && (u.AccountId == null || !deletedAccountIds.Contains(u.AccountId.Value)), ct);
        var notOnboarded = await db.AppUsers.CountAsync(
            u => !u.IsOnboarded && (u.AccountId == null || !deletedAccountIds.Contains(u.AccountId.Value)), ct);

        var closedTrades = await db.Trades
            .Where(t => t.Status != TradeStatus.Open && t.RealizedPnl.HasValue)
            .Select(t => t.RealizedPnl!.Value)
            .ToListAsync(ct);
        var totalTrades = closedTrades.Count;
        var avgWinRate = totalTrades > 0
            ? Math.Round((decimal)closedTrades.Count(pnl => pnl > 0) / totalTrades, 4)
            : 0m;

        var accounts = await db.Accounts.Where(a => !a.IsDeleted).ToListAsync(ct);
        var demoCount = accounts.Count(a => a.TradingMode == TradingMode.Demo);
        var liveCount = accounts.Count(a => a.TradingMode == TradingMode.Live);

        var jobFailures24h = await db.JobLogEntries
            .CountAsync(j => j.Status == JobStatus.Failed && j.EnqueuedAt >= oneDayAgo, ct);

        return new AdminStats(
            totalUsers, activeLast7Days, totalTrades, avgWinRate,
            demoCount, liveCount, notOnboarded, jobFailures24h);
    }

    public async Task<List<AdminUserSummary>> GetUsersAsync(CancellationToken ct = default)
    {
        // Deliberately not filtered by deleted-account status (unlike
        // GetStatsAsync's counts) - a leftover row from before the delete
        // cleanup fix needs to stay visible so admin can click Delete on it
        // to actually clean it up, rather than it being invisibly stuck.
        var users = await db.AppUsers.ToListAsync(ct);
        var summaries = new List<AdminUserSummary>(users.Count);
        foreach (var user in users)
            summaries.Add(await BuildSummaryAsync(user, ct));
        return summaries;
    }

    public async Task<AdminUserSummary?> GetUserAsync(string userId, CancellationToken ct = default)
    {
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserId == userId, ct);
        return user is null ? null : await BuildSummaryAsync(user, ct);
    }

    private async Task<AdminUserSummary> BuildSummaryAsync(AppUser user, CancellationToken ct)
    {
        var accountId = user.AccountId ?? 0;
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);

        var closedTrades = await db.Trades
            .Where(t => t.AccountId == accountId && t.Status != TradeStatus.Open && t.RealizedPnl.HasValue)
            .Select(t => t.RealizedPnl!.Value)
            .ToListAsync(ct);
        var totalTrades = closedTrades.Count;
        decimal? winRate = totalTrades > 0
            ? Math.Round((decimal)closedTrades.Count(pnl => pnl > 0) / totalTrades, 4)
            : null;

        var riskProfile = await db.AccountRiskProfiles.FirstOrDefaultAsync(p => p.AccountId == accountId, ct);
        var enabledWatchlistCount = await db.Watchlists.CountAsync(w => w.AccountId == accountId && w.IsEnabled, ct);

        return new AdminUserSummary(
            user.UserId,
            user.Email,
            user.DisplayName,
            user.Role,
            accountId,
            user.FirstLoginAt,
            user.LastLoginAt,
            user.IsOnboarded,
            user.IsApproved,
            user.IsSuspended,
            user.SuspendReason,
            account?.TradingMode ?? TradingMode.Demo,
            totalTrades,
            winRate,
            riskProfile?.RiskLabel ?? "Unknown",
            enabledWatchlistCount,
            account?.IsDeleted ?? false);
    }

    public async Task<List<AdminJobFailure>> GetJobFailuresAsync(TimeSpan lookback, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow - lookback;
        var failures = await db.JobLogEntries
            .Where(j => j.Status == JobStatus.Failed && j.EnqueuedAt >= since)
            .OrderByDescending(j => j.EnqueuedAt)
            .ToListAsync(ct);

        var accountIds = failures.Select(f => f.AccountId).Distinct().ToList();
        var owners = await db.AppUsers
            .Where(u => u.AccountId != null && accountIds.Contains(u.AccountId.Value) && u.Role == AccountRole.Owner)
            .ToDictionaryAsync(u => u.AccountId!.Value, u => u.Email, ct);

        return failures.Select(f => new AdminJobFailure(
            f.Id, f.AccountId, owners.GetValueOrDefault(f.AccountId), f.JobType, f.JobDate, f.ErrorMessage, f.AttemptCount)).ToList();
    }

    public async Task<bool> RetryJobAsync(int jobLogId, CancellationToken ct = default)
    {
        var entry = await db.JobLogEntries.FirstOrDefaultAsync(j => j.Id == jobLogId, ct);
        if (entry is null || entry.Status != JobStatus.Failed) return false;

        db.JobLogEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteJobFailureAsync(int jobLogId, CancellationToken ct = default)
    {
        var entry = await db.JobLogEntries.FirstOrDefaultAsync(j => j.Id == jobLogId, ct);
        if (entry is null || entry.Status != JobStatus.Failed) return false;

        db.JobLogEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
