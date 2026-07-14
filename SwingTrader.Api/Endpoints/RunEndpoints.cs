using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Mvc;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Api.Endpoints;

public static class RunEndpoints
{
    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder app)
    {
        // Manual "run now" triggers for the dashboard's per-job buttons. Sends
        // straight onto the same Service Bus queue the Scheduler enqueues onto, so
        // the existing Consumer Functions pick it up identically. The JobLog
        // per-day idempotency row never BLOCKS a manual run (re-triggering in one
        // day always works; the Consumer Functions no-op their JobLog Mark* calls
        // when no matching row exists) - but since the scheduler's self-healing
        // windows (14 Jul 2026), once-per-day jobs DO claim the day's slot after
        // sending. Without that, a manual run that filled in for a missed
        // scheduled slot left no record, and the scheduler's catch-up re-ran the
        // job minutes after deploy (observed 14 Jul: research ran twice).
        // Execution/Monitor are excluded: Execution's row is deliberately managed
        // by the approve endpoint (deleted to re-fire), Monitor has no dedup.
        var runGroup = app.MapGroup("/run").RequireAuthorization().RequireRateLimiting(RateLimitPolicies.ClaudeJobs);
        runGroup.MapPost("/{jobType}", async (
            string jobType,
            [FromServices] ServiceBusClient? serviceBus,
            IJobLogRepository jobLog,
            IAccountContext ctx) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();
            if (serviceBus is null)
                return Results.Problem("Service Bus is not configured on this environment.", statusCode: StatusCodes.Status503ServiceUnavailable);

            var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York"));
            var today = DateOnly.FromDateTime(nowEt);
            var jobId = Guid.NewGuid().ToString("N");

            (string QueueName, object Message)? job = jobType.ToLowerInvariant() switch
            {
                // ForceRescore: a manual trigger is a deliberate refresh, so it
                // re-scores everything even when today's signals already exist
                // (scheduler messages resume/skip instead - see JobMessages).
                "research" => ("research-jobs", new ResearchJobMessage(ctx.AccountId, jobId, today, nowEt, ForceRescore: true)),
                "watchlist" => ("watchlist-jobs", new WatchlistJobMessage(ctx.AccountId, jobId, nowEt)),
                "report" => ("report-jobs", new ReportJobMessage(ctx.AccountId, jobId, today)),
                "execution" => ("execution-jobs", new ExecutionJobMessage(ctx.AccountId, jobId, today)),
                "monitor" => ("monitor-jobs", new MonitorJobMessage(ctx.AccountId, jobId, nowEt)),
                "risk" => ("risk-jobs", new RiskJobMessage(ctx.AccountId, jobId, today)),
                "refinement" => ("refinement-jobs", new RefinementJobMessage(ctx.AccountId, jobId, today)),
                "readiness" => ("readiness-jobs", new ReadinessJobMessage(ctx.AccountId, jobId, today)),
                _ => null,
            };

            if (job is null)
                return Results.BadRequest(new { message = $"Unknown job type '{jobType}'." });

            await using var sender = serviceBus.CreateSender(job.Value.QueueName);
            await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(job.Value.Message)));

            // Claim the day's slot for once-per-day jobs so the scheduler's
            // catch-up window sees them as done. TryCreate ignores "already
            // claimed" - the manual message was sent regardless.
            var jobLogType = jobType.ToLowerInvariant() switch
            {
                "research" => "Research",
                "report" => "Report",
                "risk" => "Risk",
                "refinement" => "Refinement",
                "readiness" => "Readiness",
                "watchlist" => "Watchlist",
                _ => null,
            };
            if (jobLogType is not null)
                await jobLog.TryCreateEnqueuedAsync(ctx.AccountId, jobLogType, today, default);

            return Results.Ok(new { jobId, jobType, queuedAt = nowEt });
        });

        return app;
    }
}
