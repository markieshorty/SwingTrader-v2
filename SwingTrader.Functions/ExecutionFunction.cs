using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace SwingTrader.Functions;

// Ports v1's Execution BackgroundService — pipeline body lands with the
// Agents porting work in a later phase.
public class ExecutionFunction(ILogger<ExecutionFunction> logger)
{
    [Function("Execution")]
    public Task Run([TimerTrigger("0 20 14 * * 1-5")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Execution function fired at {Time} — not yet implemented", DateTime.UtcNow);
        return Task.CompletedTask;
    }
}
