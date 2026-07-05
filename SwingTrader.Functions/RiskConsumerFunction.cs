using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Functions;

public class RiskConsumerFunction(IJobLogRepository jobLog, ILogger<RiskConsumerFunction> logger)
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
            logger.LogInformation(
                "Risk job {JobId} for account {AccountId} — pipeline not yet implemented",
                message.JobId, message.AccountId);

            await jobLog.MarkCompletedAsync(message.AccountId, "Risk", message.EvaluationDate, ct);
        }
        catch (Exception ex)
        {
            await jobLog.MarkFailedAsync(message.AccountId, "Risk", message.EvaluationDate, ex.Message, ct);
            throw;
        }
    }
}
