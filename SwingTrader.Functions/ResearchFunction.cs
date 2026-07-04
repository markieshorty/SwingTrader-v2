using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace SwingTrader.Functions;

// Ports v1's Research BackgroundService — pipeline body lands with the
// Agents porting work in a later phase.
public class ResearchFunction(ILogger<ResearchFunction> logger)
{
    [Function("Research")]
    public Task Run([TimerTrigger("0 0 11 * * 1-5")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Research function fired at {Time} — not yet implemented", DateTime.UtcNow);
        return Task.CompletedTask;
    }
}
