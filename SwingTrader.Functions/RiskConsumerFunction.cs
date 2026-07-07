using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Risk;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Functions;

// Consumer half of the Scheduler/Consumer pair (risk-jobs queue): monthly
// capital-tier evaluation (unlock/downgrade based on trailing win rate and
// return). Only ever changes how much of the account's own capital future
// trades may use — no orders placed.
public class RiskConsumerFunction(
    IJobLogRepository jobLog,
    ITierEvaluationService tierEvaluation,
    IWorkerHeartbeatRepository heartbeats,
    IUserHttpClientFactory clientFactory,
    ILogger<RiskConsumerFunction> logger)
{
    [Function("RiskConsumer")]
    public async Task Run(
        [ServiceBusTrigger("risk-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<RiskJobMessage>(messageBody)!;
        await jobLog.MarkProcessingAsync(message.AccountId, "Risk", message.EvaluationDate, ct);

        try
        {
            var claude = await clientFactory.CreateClaudeAsync<IClaudeClient>(message.AccountId, ct);
            var record = await tierEvaluation.EvaluateAsync(message.AccountId, claude, ct);

            logger.LogInformation(
                "Risk job {JobId} for account {AccountId} — tier {Current} -> {Suggested} (applied: {Applied})",
                message.JobId, message.AccountId, record.CurrentTier, record.SuggestedTier, record.WasApplied);

            await heartbeats.UpsertAsync(message.AccountId, "Risk", "Success",
                record.WasApplied ? $"Tier updated: {record.CurrentTier} → {record.SuggestedTier}" : $"Tier unchanged: {record.CurrentTier}");
            await jobLog.MarkCompletedAsync(message.AccountId, "Risk", message.EvaluationDate, ct);
        }
        catch (Exception ex)
        {
            await heartbeats.UpsertAsync(message.AccountId, "Risk", "Failed", ex.Message);
            await jobLog.MarkFailedAsync(message.AccountId, "Risk", message.EvaluationDate, ex.Message, ct);
            throw;
        }
    }
}
