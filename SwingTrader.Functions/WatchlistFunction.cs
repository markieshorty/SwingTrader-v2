using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace SwingTrader.Functions;

// Ports v1's Watchlist BackgroundService — pipeline body lands with the
// Agents porting work in a later phase.
public class WatchlistFunction(ILogger<WatchlistFunction> logger)
{
    [Function("Watchlist")]
    public Task Run([TimerTrigger("0 0 1 * * 0")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Watchlist function fired at {Time} — not yet implemented", DateTime.UtcNow);
        return Task.CompletedTask;
    }
}
