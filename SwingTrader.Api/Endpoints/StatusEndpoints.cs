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
