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
        // the existing Consumer Functions pick it up identically - but deliberately
        // skips the JobLog per-day idempotency row the Scheduler writes, so a manual
        // run can't collide with (or get silently swallowed by) an automatic run
        // that already fired today, and testing can re-trigger a job repeatedly in
        // one day. The Consumer Functions no-op their JobLog Mark* calls when no
        // matching row exists, so this is safe.
        var runGroup = app.MapGroup("/run").RequireAuthorization().RequireRateLimiting(RateLimitPolicies.ClaudeJobs);
        runGroup.MapPost("/{jobType}", async (
            string jobType,
            [FromServices] ServiceBusClient? serviceBus,
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
                "research" => ("research-jobs", new ResearchJobMessage(ctx.AccountId, jobId, today, nowEt)),
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

            return Results.Ok(new { jobId, jobType, queuedAt = nowEt });
        });

        return app;
    }
}
