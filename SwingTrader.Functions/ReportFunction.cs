using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace SwingTrader.Functions;

// Ports v1's Report BackgroundService — pipeline body lands with the
// Agents porting work in a later phase.
public class ReportFunction(ILogger<ReportFunction> logger)
{
    [Function("Report")]
    public Task Run([TimerTrigger("0 30 11 * * 1-5")] TimerInfo timer, CancellationToken ct)
    {
        logger.LogInformation("Report function fired at {Time} — not yet implemented", DateTime.UtcNow);
        return Task.CompletedTask;
    }
}
