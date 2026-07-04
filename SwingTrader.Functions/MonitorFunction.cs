using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace SwingTrader.Functions;

// Ports v1's Monitor BackgroundService — pipeline body lands with the
// Agents porting work in a later phase. Body must check market hours before
// doing anything, since this fires every 5 minutes around the clock.
public class MonitorFunction(ILogger<MonitorFunction> logger)
{
    [Function("Monitor")]
    public Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Monitor function fired at {Time} — not yet implemented", DateTime.UtcNow);
        return Task.CompletedTask;
    }
}
