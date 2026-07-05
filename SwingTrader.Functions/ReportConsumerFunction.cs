using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Functions;

public class ReportConsumerFunction(IJobLogRepository jobLog, ILogger<ReportConsumerFunction> logger)
{
    [Function("ReportConsumer")]
    public async Task Run(
        [ServiceBusTrigger("report-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<ReportJobMessage>(messageBody)!;
        await jobLog.MarkProcessingAsync(message.AccountId, "Report", message.ReportDate, ct);

        try
        {
            logger.LogInformation(
                "Report job {JobId} for account {AccountId} — pipeline not yet implemented",
                message.JobId, message.AccountId);

            await jobLog.MarkCompletedAsync(message.AccountId, "Report", message.ReportDate, ct);
        }
        catch (Exception ex)
        {
            await jobLog.MarkFailedAsync(message.AccountId, "Report", message.ReportDate, ex.Message, ct);
            throw;
        }
    }
}
