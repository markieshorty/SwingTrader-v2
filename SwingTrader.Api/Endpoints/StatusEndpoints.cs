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
