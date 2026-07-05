using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Functions;

public class WatchlistConsumerFunction(IJobLogRepository jobLog, ILogger<WatchlistConsumerFunction> logger)
{
    [Function("WatchlistConsumer")]
    public async Task Run(
        [ServiceBusTrigger("watchlist-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<WatchlistJobMessage>(messageBody)!;
        var jobDate = DateOnly.FromDateTime(message.ScheduledFor);
        await jobLog.MarkProcessingAsync(message.AccountId, "Watchlist", jobDate, ct);

        try
        {
            logger.LogInformation(
                "Watchlist job {JobId} for account {AccountId} — pipeline not yet implemented",
                message.JobId, message.AccountId);

            await jobLog.MarkCompletedAsync(message.AccountId, "Watchlist", jobDate, ct);
        }
        catch (Exception ex)
        {
            await jobLog.MarkFailedAsync(message.AccountId, "Watchlist", jobDate, ex.Message, ct);
            throw;
        }
    }
}
