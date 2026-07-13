using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;

namespace SwingTrader.Data.Repositories;

public class MonitoringRepository(SwingTraderDbContext db) : IMonitoringRepository
{
    public async Task<MonitoringDbSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var oneDayAgo = now.AddHours(-24);
        var sevenDaysAgo = now.AddDays(-7);
        var todayUtc = now.Date;

        // ── Worker heartbeats (one row per Functions worker) ──────────────────
        var workers = await db.WorkerHeartbeats
            .OrderBy(w => w.WorkerName)
            .Select(w => new MonitoringWorker(w.WorkerName, w.LastRunResult, w.LastHeartbeatAt, w.LastRunMessage))
            .ToListAsync(ct);

        // ── Job status counts (last 24h) + per-type breakdown (last 7d) ───────
        var recentJobs = await db.JobLogEntries
            .Where(j => j.EnqueuedAt >= oneDayAgo)
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int CountFor(JobStatus s) => recentJobs.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

        // Monitor is excluded from the per-type breakdown: JobLogEntry is keyed
        // per (account, type, DAY), so a continuous 5-minute poll like Monitor
        // re-uses one day-row (flipping Processing<->Completed each cycle) and
        // its ~288 daily runs can never be counted here - "Monitor: 2 completed"
        // would mean "2 day-rows", not 2 runs, which reads as broken. Monitor's
        // liveness is shown by its worker heartbeat instead. The daily batch jobs
        // below are genuinely one row per run, so their counts are meaningful.
        var byTypeRaw = await db.JobLogEntries
            .Where(j => j.EnqueuedAt >= sevenDaysAgo && j.JobType != "Monitor")
            .GroupBy(j => new { j.JobType, j.Status })
            .Select(g => new { g.Key.JobType, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);
        var byType = byTypeRaw
            .GroupBy(x => x.JobType)
            .Select(g => new MonitoringJobTypeCount(
                g.Key,
                g.Where(x => x.Status == JobStatus.Completed).Sum(x => x.Count),
                g.Where(x => x.Status == JobStatus.Failed).Sum(x => x.Count)))
            .OrderBy(x => x.JobType)
            .ToList();

        var jobs = new MonitoringJobs(
            CountFor(JobStatus.Failed), CountFor(JobStatus.Completed),
            CountFor(JobStatus.Processing), CountFor(JobStatus.Enqueued), byType);

        // ── Cross-account SystemEvents needing attention (Warning/Failed, 7d) ─
        var systemEvents = await db.ActivityLogs
            .Where(a => a.Category == "SystemEvent"
                && (a.Result == "Warning" || a.Result == "Failed")
                && a.OccurredAt >= sevenDaysAgo)
            .OrderByDescending(a => a.OccurredAt)
            .Take(50)
            .Select(a => new MonitoringSystemEvent(a.OccurredAt, a.AccountId, a.Title, a.Result, a.Message))
            .ToListAsync(ct);

        // ── Live trading-state snapshot (intent-first states surfaced here) ───
        var openPositions = await db.Trades.CountAsync(t => t.Status == TradeStatus.Open, ct);
        var pendingIntents = await db.Trades.CountAsync(t => t.Status == TradeStatus.Pending, ct);
        var cancelledToday = await db.Trades.CountAsync(
            t => t.Status == TradeStatus.Cancelled && t.UpdatedAt >= todayUtc, ct);
        // Buys and exits counted separately: a sell closing a days-old
        // position is a real order placed today, but the old single
        // "orders placed" metric (entries only) read 0 for it.
        var buysToday = await db.Trades.CountAsync(
            t => t.EntryOrderId != null && t.OpenedAt >= todayUtc, ct);
        var exitsToday = await db.Trades.CountAsync(
            t => t.Status == TradeStatus.Closed && t.ClosedAt != null && t.ClosedAt >= todayUtc, ct);

        var trading = new MonitoringTradingState(openPositions, pendingIntents, cancelledToday, buysToday, exitsToday);

        return new MonitoringDbSnapshot(workers, jobs, systemEvents, trading);
    }
}
