using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace SwingTrader.Functions;

// Ports v1's Risk (tier evaluation) BackgroundService — pipeline body lands
// with the Agents porting work in a later phase.
public class RiskFunction(ILogger<RiskFunction> logger)
{
    [Function("Risk")]
    public Task Run([TimerTrigger("0 0 14 1 * *")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Risk function fired at {Time} — not yet implemented", DateTime.UtcNow);
        return Task.CompletedTask;
    }
}
