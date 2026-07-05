using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Functions;

// Replaces the old per-job timer functions: one 5-minute timer checks every
// account against each job's time window and enqueues at most one message
// per (account, job type, day) via JobLogEntry idempotency.
public class SchedulerFunction(
    ServiceBusClient? serviceBus,
    IAccountRepository accounts,
    IJobLogRepository jobLog,
    ILogger<SchedulerFunction> logger)
{
    private static readonly TimeZoneInfo EasternTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    [Function("Scheduler")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        if (serviceBus is null)
        {
            logger.LogWarning("Scheduler fired but no Service Bus namespace is configured - skipping.");
            return;
        }

        var nowEt = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, EasternTimeZone);
        var today = DateOnly.FromDateTime(nowEt);
        var isWeekday = nowEt.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;

        var activeAccounts = await accounts.ListActiveAsync(ct);

        foreach (var account in activeAccounts)
        {
            try
            {
                if (isWeekday && InWindow(nowEt, 6, 0, 6, 5))
                    await TryEnqueueAsync(account.Id, "Research", today, "research-jobs",
                        new ResearchJobMessage(account.Id, Guid.NewGuid().ToString("N"), today, nowEt), ct);

                if (nowEt.DayOfWeek == DayOfWeek.Sunday && InWindow(nowEt, 20, 0, 20, 5))
                    await TryEnqueueAsync(account.Id, "Watchlist", today, "watchlist-jobs",
                        new WatchlistJobMessage(account.Id, Guid.NewGuid().ToString("N"), nowEt), ct);

                if (isWeekday && InWindow(nowEt, 6, 30, 6, 35))
                    await TryEnqueueAsync(account.Id, "Report", today, "report-jobs",
                        new ReportJobMessage(account.Id, Guid.NewGuid().ToString("N"), today), ct);

                if (isWeekday && InWindow(nowEt, 9, 20, 9, 25))
                    await TryEnqueueAsync(account.Id, "Execution", today, "execution-jobs",
                        new ExecutionJobMessage(account.Id, Guid.NewGuid().ToString("N"), today), ct);

                // NOTE: JobLog idempotency is keyed per calendar day, so this
                // enqueues Monitor exactly once per day (whichever 5-minute
                // tick lands in the window first) rather than repeatedly
                // through market hours. Revisit the idempotency key once the
                // real per-cycle monitoring pipeline is ported.
                if (isWeekday && InWindow(nowEt, 9, 30, 16, 0))
                    await TryEnqueueAsync(account.Id, "Monitor", today, "monitor-jobs",
                        new MonitorJobMessage(account.Id, Guid.NewGuid().ToString("N"), nowEt), ct);

                if (nowEt.Day == 1 && InWindow(nowEt, 9, 0, 9, 5))
                    await TryEnqueueAsync(account.Id, "Risk", today, "risk-jobs",
                        new RiskJobMessage(account.Id, Guid.NewGuid().ToString("N"), today), ct);

                if (nowEt.Day == 15 && InWindow(nowEt, 8, 0, 8, 5))
                    await TryEnqueueAsync(account.Id, "Refinement", today, "refinement-jobs",
                        new RefinementJobMessage(account.Id, Guid.NewGuid().ToString("N"), today), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler failed for account {AccountId}", account.Id);
                // Continue to the next account - one account's failure shouldn't block the rest.
            }
        }
    }

    private static bool InWindow(DateTime nowEt, int startHour, int startMin, int endHour, int endMin)
    {
        var start = nowEt.Date.AddHours(startHour).AddMinutes(startMin);
        var end = nowEt.Date.AddHours(endHour).AddMinutes(endMin);
        return nowEt >= start && nowEt < end;
    }

    private async Task TryEnqueueAsync<T>(
        int accountId, string jobType, DateOnly jobDate, string queueName, T message, CancellationToken ct)
    {
        var existing = await jobLog.FindAsync(accountId, jobType, jobDate, ct);
        if (existing is not null) return; // Already enqueued today - avoids double-firing across overlapping ticks.

        await using var sender = serviceBus!.CreateSender(queueName);
        await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(message)), ct);
        await jobLog.CreateEnqueuedAsync(accountId, jobType, jobDate, ct);
    }
}
