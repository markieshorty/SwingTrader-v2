using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace SwingTrader.Functions;

// Ports v1's Refinement BackgroundService — pipeline body lands with the
// Agents porting work in a later phase.
public class RefinementFunction(ILogger<RefinementFunction> logger)
{
    [Function("Refinement")]
    public Task Run([TimerTrigger("0 0 13 15 * *")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Refinement function fired at {Time} — not yet implemented", DateTime.UtcNow);
        return Task.CompletedTask;
    }
}
