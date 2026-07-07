using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Execution;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Functions;

// Consumer half of the Scheduler/Consumer pair (execution-jobs queue): places
// real Trading212 market orders for today's approved Buy signals, gated by
// Account.ApprovalRequired (TradeApproval token flow) and the account's own
// TradingMode (Demo vs Live T212 endpoint, chosen by IUserHttpClientFactory).
public class ExecutionConsumerFunction(
    IJobLogRepository jobLog,
    IExecutionService executionService,
    IWorkerHeartbeatRepository heartbeats,
    IActivityLogRepository activityLog,
    IUserHttpClientFactory clientFactory,
    ILogger<ExecutionConsumerFunction> logger)
{
    [Function("ExecutionConsumer")]
    public async Task Run(
        [ServiceBusTrigger("execution-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<ExecutionJobMessage>(messageBody)!;
        await jobLog.MarkProcessingAsync(message.AccountId, "Execution", message.TradeDate, ct);
        await activityLog.LogAsync(message.AccountId, "WorkerRun", "Execution", "Started", "Placing today's approved orders…", ct);

        try
        {
            var finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(message.AccountId, ct);
            var tiingo = await clientFactory.CreateTiingoAsync<ITiingoClient>(message.AccountId, ct);
            var t212 = await clientFactory.CreateTrading212Async<ITrading212Client>(message.AccountId, ct);

            var result = await executionService.RunAsync(message.AccountId, finnhub, tiingo, t212, message.TradeDate, ct);

            var t212Blocked = result.Summary.Contains("unavailable") || result.Summary.Contains("invalid");
            var heartbeatResult = result.OrdersPlaced > 0 ? "Success"
                : result.OrdersFailed > 0 || t212Blocked ? "Warning"
                : "Info";
            await heartbeats.UpsertAsync(message.AccountId, "Execution", heartbeatResult, result.Summary);
            if (result.OrdersPlaced > 0)
                await activityLog.LogAsync(message.AccountId, "SystemEvent", "Trades Placed", "Info", result.Summary);
            else if (t212Blocked)
                await activityLog.LogAsync(message.AccountId, "SystemEvent", "Execution Failed", "Warning", result.Summary);
            logger.LogInformation("Execution job {JobId} for account {AccountId} — {Summary}", message.JobId, message.AccountId, result.Summary);

            if (t212Blocked)
                await jobLog.MarkFailedAsync(message.AccountId, "Execution", message.TradeDate, result.Summary, ct);
            else
                await jobLog.MarkCompletedAsync(message.AccountId, "Execution", message.TradeDate, ct);
        }
        catch (Exception ex)
        {
            await heartbeats.UpsertAsync(message.AccountId, "Execution", "Failed", ex.Message);
            await jobLog.MarkFailedAsync(message.AccountId, "Execution", message.TradeDate, ex.Message, ct);
            throw;
        }
    }
}
