using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Core.Enums;
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
    Microsoft.Extensions.Configuration.IConfiguration config,
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
                // 7:30 ET (was 4:00): the 4 AM slot existed only because the
                // free-tier Tiingo cap (50/hr) made a full rescore take ~90
                // minutes. With config-driven pacing on a Power key the run
                // takes minutes, so research now uses fresh pre-market data
                // (morning earnings/news included) and still finishes well
                // before Report at 8:30 ET.
                if (isWeekday && InWindow(nowEt, 7, 30, 7, 35))
                    await TryEnqueueAsync(account.Id, "Research", today, "research-jobs",
                        new ResearchJobMessage(account.Id, Guid.NewGuid().ToString("N"), today, nowEt), ct);

                // Optional midday rescore (config Research:MiddayRescoreEnabled,
                // default off): re-scores the same SignalDate so afternoon
                // execution re-runs (position closes freeing capital, late
                // approvals) buy from scores that reflect the morning session.
                // Distinct job-log key ("ResearchMidday") so the morning run's
                // dedup row doesn't block it; signals upsert in place and
                // WasExecuted survives a rescore, so no double-buys.
                if (isWeekday && MiddayRescoreEnabled && InWindow(nowEt, 12, 30, 12, 35))
                    await TryEnqueueAsync(account.Id, "ResearchMidday", today, "research-jobs",
                        new ResearchJobMessage(account.Id, Guid.NewGuid().ToString("N"), today, nowEt, "ResearchMidday"), ct);

                if (nowEt.DayOfWeek == DayOfWeek.Sunday && InWindow(nowEt, 20, 0, 20, 5))
                    await TryEnqueueAsync(account.Id, "Watchlist", today, "watchlist-jobs",
                        new WatchlistJobMessage(account.Id, Guid.NewGuid().ToString("N"), nowEt), ct);

                // 8:30 ET (was 6:30) - follows Research at 7:30 with ~55 min of
                // margin; the approval email lands ~8:35 ET / 13:35 UK.
                if (isWeekday && InWindow(nowEt, 8, 30, 8, 35))
                    await TryEnqueueAsync(account.Id, "Report", today, "report-jobs",
                        new ReportJobMessage(account.Id, Guid.NewGuid().ToString("N"), today), ct);

                // Window covers the full trading day so a late approval triggers
                // execution within 5 minutes regardless of when the user approves.
                // TryEnqueueAsync's job-log dedup means only one execution fires
                // per day — the approve endpoint deletes the job log entry to
                // allow re-enqueue after a late approval.
                if (isWeekday && InWindow(nowEt, 9, 20, 15, 55))
                    await TryEnqueueAsync(account.Id, "Execution", today, "execution-jobs",
                        new ExecutionJobMessage(account.Id, Guid.NewGuid().ToString("N"), today), ct);

                // Monitor is a continuous poll, not a once-daily batch job like
                // the others - it needs to fire every 5-minute tick throughout
                // market hours. JobLogEntry has a DB-level UNIQUE index on
                // (AccountId, JobType, JobDate), so routing it through
                // TryEnqueueAsync's per-day dedup (like every other job type)
                // meant only the first tick of the day ever actually enqueued
                // Monitor - every later tick that day saw the existing row and
                // silently skipped, so open positions only got checked once at
                // market open instead of all day. EnqueueEveryTickAsync bypasses
                // that dedup entirely for Monitor specifically.
                if (isWeekday && InWindow(nowEt, 9, 30, 16, 0))
                    await EnqueueEveryTickAsync("monitor-jobs",
                        new MonitorJobMessage(account.Id, Guid.NewGuid().ToString("N"), nowEt), ct);

                if (nowEt.Day == 1 && InWindow(nowEt, 9, 0, 9, 5))
                    await TryEnqueueAsync(account.Id, "Risk", today, "risk-jobs",
                        new RiskJobMessage(account.Id, Guid.NewGuid().ToString("N"), today), ct);

                if (nowEt.Day == 15 && InWindow(nowEt, 8, 0, 8, 5))
                    await TryEnqueueAsync(account.Id, "Refinement", today, "refinement-jobs",
                        new RefinementJobMessage(account.Id, Guid.NewGuid().ToString("N"), today), ct);

                // Daily (every day, not just weekdays) so the readiness
                // trajectory chart accrues an unbroken day-over-day history -
                // system-running-days and trade-rate progress advance on
                // weekends too. 8:45 ET (was 7:00) keeps it after Report,
                // which moved to 8:30, so weekday snapshots reflect the day's
                // fresh signals.
                if (InWindow(nowEt, 8, 45, 8, 50))
                    await TryEnqueueAsync(account.Id, "Readiness", today, "readiness-jobs",
                        new ReadinessJobMessage(account.Id, Guid.NewGuid().ToString("N"), today), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler failed for account {AccountId}", account.Id);
                // Continue to the next account - one account's failure shouldn't block the rest.
            }
        }

        // Platform-level weekly candle sync (the shared HistoricalCandles
        // table serves every account's historic backtests) - one job under the
        // system account, not per user. Saturday morning: markets closed, the
        // week's bars are final, and it lands before Sunday's watchlist run.
        try
        {
            if (nowEt.DayOfWeek == DayOfWeek.Saturday && InWindow(nowEt, 8, 0, 8, 5))
                await TryEnqueueAsync(Data.SwingTraderDbContext.SystemAccountId, "CandleSync", today, "candlesync-jobs",
                    new CandleSyncJobMessage(Data.SwingTraderDbContext.SystemAccountId, Guid.NewGuid().ToString("N")), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduler failed to enqueue the weekly candle sync");
        }

        // Platform-level daily filing sync (docs/filing-delta-plan): shared
        // Filings/FilingDeltas tables, one job under the system account.
        // 18:00 ET weekdays - after the market close so the day's filings
        // (most land after hours) are captured before the NEXT morning's
        // research reads them.
        try
        {
            if (isWeekday && InWindow(nowEt, 18, 0, 18, 5))
                await TryEnqueueAsync(Data.SwingTraderDbContext.SystemAccountId, "FilingSync", today, "filingsync-jobs",
                    new FilingSyncJobMessage(Data.SwingTraderDbContext.SystemAccountId, Guid.NewGuid().ToString("N")), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduler failed to enqueue the daily filing sync");
        }
    }

    private bool MiddayRescoreEnabled =>
        bool.TryParse(config["Research:MiddayRescoreEnabled"], out var b) && b;

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
        if (existing is null)
        {
            // Not yet run today — fall through to enqueue.
        }
        else if (existing.Status == JobStatus.Failed)
        {
            // Failed entries are retryable — clear so the scheduler re-enqueues
            // automatically on the next tick without requiring admin intervention.
            await jobLog.DeleteAsync(accountId, jobType, jobDate, ct);
        }
        else
        {
            return; // Already enqueued/processing/completed today.
        }

        // Claim the job slot BEFORE sending. The old order (send, then record)
        // was a check-then-act race: two overlapping scheduler executions (host
        // restart mid-tick / missed-schedule catch-up firing beside the regular
        // tick) both passed the Find above and both sent - observed live on
        // 2026-07-09 as two Execution messages in the same 5-minute window and
        // a duplicate pair of activity-log rows. Claiming first makes the
        // UNIQUE index the arbiter: the loser's insert returns false and it
        // never sends.
        if (!await jobLog.TryCreateEnqueuedAsync(accountId, jobType, jobDate, ct))
        {
            logger.LogInformation(
                "Skipped enqueue of {JobType} for account {AccountId} — a concurrent scheduler execution claimed it first",
                jobType, accountId);
            return;
        }

        try
        {
            await using var sender = serviceBus!.CreateSender(queueName);
            await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(message)), ct);
        }
        catch
        {
            // Send failed after claiming - release the claim so the next tick
            // retries, instead of the day's job silently never running.
            await jobLog.DeleteAsync(accountId, jobType, jobDate, ct);
            throw;
        }
    }

    // No JobLog dedup - intentionally fires on every matching tick. Used for
    // jobs (currently only Monitor) that need to run repeatedly through a
    // window rather than once per day. MonitorConsumerFunction's own JobLog
    // Mark* calls no-op safely when no matching row exists, so per-run
    // status is tracked via the WorkerHeartbeat row instead.
    private async Task EnqueueEveryTickAsync<T>(string queueName, T message, CancellationToken ct)
    {
        await using var sender = serviceBus!.CreateSender(queueName);
        await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(message)), ct);
    }
}
