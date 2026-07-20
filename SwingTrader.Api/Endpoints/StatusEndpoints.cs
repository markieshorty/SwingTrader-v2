using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;

namespace SwingTrader.Api.Endpoints;

public static class StatusEndpoints
{
    public static RouteGroupBuilder MapStatusEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/status", async (IActivityLogRepository activityLog, IAccountRepository accounts, IAccountContext ctx, CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(ctx.AccountId, ct);
            if (account is null) return Results.NotFound();

            var entries = await activityLog.GetRecentAsync(ctx.AccountId, account.TradingMode);
            var runs = entries.Select(e => new
            {
                e.Category,
                e.Title,
                e.Result,
                e.Message,
                e.OccurredAt,
            });
            return Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow, runs });
        });

        // Long-running job feed for the toolbar indicator: in-flight backtest
        // runs (with candidate progress where the mode reports it) and today's
        // in-flight worker jobs, plus anything that finished in the last 10
        // minutes so "done" is visible even after navigating away. Polled
        // (~15s while something runs), so payload columns stay out of the
        // queries.
        api.MapGet("/jobs/active", async (
            IBacktestRunRepository backtests,
            IJobLogRepository jobLog,
            IWorkerHeartbeatRepository heartbeats,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            var since = DateTime.UtcNow.AddMinutes(-10);
            var jobs = new List<object>();

            foreach (var run in await backtests.GetActiveOrRecentAsync(ctx.AccountId, since, ct))
            {
                string? mode = null;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(run.RequestJson);
                    if (doc.RootElement.TryGetProperty("Mode", out var m)) mode = m.GetString();
                }
                catch { /* unreadable request - generic label */ }

                var label = mode switch
                {
                    "sweep" => "Optimizer",
                    "ab" => "A/B backtest",
                    "validate" => "Validation",
                    "montecarlo" => "Monte Carlo",
                    "ablation" => "Setup contribution",
                    "regime" => "Regime comparison",
                    "setupsearch" => "Setup search",
                    _ => "Backtest",
                };
                jobs.Add(new
                {
                    kind = "backtest",
                    label,
                    status = run.Status,          // Queued | Running | Completed | Failed
                    startedAt = run.StartedAt,
                    completedAt = run.CompletedAt,
                    progressCompleted = run.CompletedCandidates,
                    progressTotal = run.TotalCandidates,
                    detail = (string?)null,
                });
            }

            foreach (var entry in await jobLog.GetActiveOrRecentAsync(ctx.AccountId, since, ct))
            {
                var label = entry.JobType switch
                {
                    "Research" => "Research",
                    "ResearchMidday" => "Midday rescore",
                    "Watchlist" => "Watchlist refresh",
                    "Report" => "Report",
                    "Execution" => "Execution",
                    "Refinement" => "Refinement",
                    _ => entry.JobType,
                };
                // The running watchlist chip carries its live STAGE (the
                // consumer's heartbeat breadcrumb) so a 10-15 minute run is
                // never a silent spinner.
                string? detail = null;
                if (entry.JobType == "Watchlist" && entry.Status == JobStatus.Processing)
                {
                    var hb = await heartbeats.GetAsync("Watchlist");
                    if (hb?.LastRunResult == "Running") detail = hb.LastRunMessage;
                }
                jobs.Add(new
                {
                    kind = "worker",
                    label,
                    status = entry.Status switch
                    {
                        JobStatus.Enqueued => "Queued",
                        JobStatus.Processing => "Running",
                        JobStatus.Failed => "Failed",
                        _ => "Completed",
                    },
                    startedAt = (DateTime?)entry.EnqueuedAt,
                    completedAt = entry.CompletedAt,
                    progressCompleted = (int?)null,
                    progressTotal = (int?)null,
                    detail,
                });
            }

            return Results.Ok(new { jobs });
        });

        // Market open/closed for the dashboard capsule. Calendar-aware (the
        // same holiday calendar hold-period accounting uses), regular session
        // only (9:30-16:00 ET) - the platform doesn't trade pre/after-market.
        // Times go out as UTC instants; the client renders them in the
        // viewer's local zone alongside ET.
        api.MapGet("/market-status", (SwingTrader.Infrastructure.Market.IMarketCalendarService calendar) =>
        {
            var et = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");
            var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, et);
            var today = DateOnly.FromDateTime(nowEt);

            var isOpen = calendar.IsMarketDay(today)
                && nowEt.TimeOfDay >= new TimeSpan(9, 30, 0)
                && nowEt.TimeOfDay < new TimeSpan(16, 0, 0);

            DateTime ToUtc(DateOnly d, int h, int m) =>
                TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(d.ToDateTime(new TimeOnly(h, m)), DateTimeKind.Unspecified), et);

            if (isOpen)
                return Results.Ok(new { isOpen = true, changesAtUtc = ToUtc(today, 16, 0) });

            // Next open: today 9:30 if the session hasn't started yet on a
            // market day, otherwise the next market day's open.
            var next = calendar.IsMarketDay(today) && nowEt.TimeOfDay < new TimeSpan(9, 30, 0)
                ? today
                : NextMarketDay(calendar, today);
            return Results.Ok(new { isOpen = false, changesAtUtc = ToUtc(next, 9, 30) });

            static DateOnly NextMarketDay(SwingTrader.Infrastructure.Market.IMarketCalendarService cal, DateOnly from)
            {
                var d = from.AddDays(1);
                while (!cal.IsMarketDay(d)) d = d.AddDays(1);
                return d;
            }
        });

        // Next scheduled run per job type, for the Dashboard's per-job cards -
        // mirrors SchedulerFunction's windows (see JobScheduleInfo). Research's
        // start is per-account now (6:30 free-tier Tiingo / 7:30 platform
        // Power), so the caller's account decides the Research label.
        api.MapGet("/jobs/next-runs", async (IAccountRepository accounts, IAccountContext ctx, CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(ctx.AccountId, ct);
            return Results.Ok(JobScheduleInfo.GetNextRuns(DateTime.UtcNow, account?.UsePlatformTiingo == true));
        });

        return api;
    }
}
