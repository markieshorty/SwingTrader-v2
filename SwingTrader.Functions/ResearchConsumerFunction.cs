using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Functions;

// Consumer half of the Scheduler/Consumer pair (research-jobs queue).
// Pipeline body lands with the Agents porting work in a later phase - for
// now this only proves the enqueue -> dequeue -> JobLog lifecycle works.
public class ResearchConsumerFunction(IJobLogRepository jobLog, ILogger<ResearchConsumerFunction> logger)
{
    [Function("ResearchConsumer")]
    public async Task Run(
        [ServiceBusTrigger("research-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<ResearchJobMessage>(messageBody)!;
        await jobLog.MarkProcessingAsync(message.AccountId, "Research", message.TradeDate, ct);

        try
        {
            logger.LogInformation(
                "Research job {JobId} for account {AccountId} — pipeline not yet implemented",
                message.JobId, message.AccountId);

            await jobLog.MarkCompletedAsync(message.AccountId, "Research", message.TradeDate, ct);
        }
        catch (Exception ex)
        {
            await jobLog.MarkFailedAsync(message.AccountId, "Research", message.TradeDate, ex.Message, ct);
            throw; // Re-throw so Service Bus retries, then dead-letters after maxDeliveryCount.
        }
    }
}
