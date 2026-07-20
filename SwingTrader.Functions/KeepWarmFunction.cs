using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SwingTrader.Functions;

// The Container App scales to zero (containerapp.bicep minReplicas: 0), so a
// request after any idle period pays a cold-start penalty. Pinging a cheap,
// DB-free endpoint on the same cadence as the Scheduler keeps a replica
// warm without adding real load.
public class KeepWarmFunction(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<KeepWarmFunction> logger)
{
    [Function("KeepWarm")]
    public async Task Run([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var apiBaseUrl = config["ApiBaseUrl"];
        if (string.IsNullOrEmpty(apiBaseUrl))
        {
            logger.LogDebug("KeepWarm fired but ApiBaseUrl is not configured - skipping.");
            return;
        }

        // Overnight quiet window (20 Jul 2026): keeping a replica warm 24/7
        // was ~30% of the Container Apps bill for hours nobody uses. Between
        // 23:00 and 06:00 UTC the app scales to zero; the first morning
        // request pays a single cold start.
        var hourUtc = DateTime.UtcNow.Hour;
        if (hourUtc >= 23 || hourUtc < 6)
        {
            logger.LogDebug("KeepWarm skipped - overnight quiet window (23:00-06:00 UTC).");
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient("KeepWarm");
            var response = await client.GetAsync($"{apiBaseUrl}/health/live", ct);
            logger.LogInformation("KeepWarm ping to {Url} returned {StatusCode}", apiBaseUrl, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "KeepWarm ping to {Url} failed", apiBaseUrl);
        }
    }
}
