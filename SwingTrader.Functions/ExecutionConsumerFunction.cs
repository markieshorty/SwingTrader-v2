using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Functions;

public class ExecutionConsumerFunction(IJobLogRepository jobLog, ILogger<ExecutionConsumerFunction> logger)
{
    [Function("ExecutionConsumer")]
    public async Task Run(
        [ServiceBusTrigger("execution-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<ExecutionJobMessage>(messageBody)!;
        await jobLog.MarkProcessingAsync(message.AccountId, "Execution", message.TradeDate, ct);

        try
        {
            logger.LogInformation(
                "Execution job {JobId} for account {AccountId} — pipeline not yet implemented",
                message.JobId, message.AccountId);

            await jobLog.MarkCompletedAsync(message.AccountId, "Execution", message.TradeDate, ct);
        }
        catch (Exception ex)
        {
            await jobLog.MarkFailedAsync(message.AccountId, "Execution", message.TradeDate, ex.Message, ct);
            throw;
        }
    }
}
