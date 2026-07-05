using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Functions;

public class RefinementConsumerFunction(IJobLogRepository jobLog, ILogger<RefinementConsumerFunction> logger)
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
            logger.LogInformation(
                "Refinement job {JobId} for account {AccountId} — pipeline not yet implemented",
                message.JobId, message.AccountId);

            await jobLog.MarkCompletedAsync(message.AccountId, "Refinement", message.EvaluationDate, ct);
        }
        catch (Exception ex)
        {
            await jobLog.MarkFailedAsync(message.AccountId, "Refinement", message.EvaluationDate, ex.Message, ct);
            throw;
        }
    }
}
