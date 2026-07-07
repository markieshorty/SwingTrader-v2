using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Refinement;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Functions;

// Consumer half of the Scheduler/Consumer pair (refinement-jobs queue): runs
// the monthly component-correlation analysis and emails a suggestion. Never
// applies the suggested weights itself - that's a manual action from the
// /refinement dashboard (ApplyRefinementService), same as Execution/Monitor's
// exit-closing being kept manual.
public class RefinementConsumerFunction(
    IJobLogRepository jobLog,
    IRefinementService refinementService,
    IWorkerHeartbeatRepository heartbeats,
    IUserHttpClientFactory clientFactory,
    ILogger<RefinementConsumerFunction> logger)
{
    [Function("RefinementConsumer")]
    public async Task Run(
        [ServiceBusTrigger("refinement-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<RefinementJobMessage>(messageBody)!;
        await jobLog.MarkProcessingAsync(message.AccountId, "Refinement", message.EvaluationDate, ct);

        try
        {
            var claude = await clientFactory.CreateClaudeAsync<IClaudeClient>(message.AccountId, ct);
            var suggestion = await refinementService.RunAsync(message.AccountId, claude, ct);

            logger.LogInformation(
                "Refinement job {JobId} for account {AccountId} — {Result}",
                message.JobId, message.AccountId,
                suggestion is null ? "skipped (insufficient trade history)" : $"suggestion #{suggestion.Id} generated");

            await heartbeats.UpsertAsync(message.AccountId, "Refinement", "Success",
                suggestion is null ? "Skipped — insufficient trade history" : $"Suggestion #{suggestion.Id} generated");
            await jobLog.MarkCompletedAsync(message.AccountId, "Refinement", message.EvaluationDate, ct);
        }
        catch (Exception ex)
        {
            await heartbeats.UpsertAsync(message.AccountId, "Refinement", "Failed", ex.Message);
            await jobLog.MarkFailedAsync(message.AccountId, "Refinement", message.EvaluationDate, ex.Message, ct);
            throw;
        }
    }
}
