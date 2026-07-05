using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Infrastructure.Market;

/// <summary>
/// Provides the GBP/USD conversion rate for display purposes. US-listed
/// instruments (from both T212 per-position fields and Finnhub quotes) are
/// priced in USD; the T212 account base currency is GBP, so per-share prices
/// must be multiplied by this rate to read in GBP like the T212 app shows.
///
/// Rate source is Frankfurter (ECB reference rates, free, no API key) —
/// Finnhub's forex data (both the dedicated /forex/rates endpoint and forex
/// quotes via /quote) returned 403 on this account's plan tier.
/// </summary>
public class ForexService(
    IExchangeRateClient exchangeRates,
    IMemoryCache cache,
    ILogger<ForexService> logger) : IForexService
{
    private const string CacheKey = "forex_gbp_usd";
    private const string FailureCacheKey = "forex_gbp_usd:failure";
    private const decimal FallbackRate = 0.79m;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(60);

    // Failing forever without backoff means every caller re-hits the rate API
    // and re-logs a warning — same anti-pattern already fixed for T212. Cache
    // the failure briefly so a sustained outage doesn't spam calls/logs.
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(5);

    public async Task<decimal> GetGbpUsdRateAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out decimal cached))
            return cached;

        if (cache.TryGetValue(FailureCacheKey, out bool _))
            return FallbackRate;

        try
        {
            var response = await exchangeRates.GetLatestRatesAsync("USD", "GBP");

            var rate = response.Rates is null
                ? 0m
                : response.Rates
                    .FirstOrDefault(kvp => kvp.Key.Equals("GBP", StringComparison.OrdinalIgnoreCase))
                    .Value;

            if (rate <= 0m)
            {
                logger.LogWarning("Exchange rate response had no positive GBP rate — using fallback {Rate}", FallbackRate);
                cache.Set(FailureCacheKey, true, FailureBackoff);
                return FallbackRate;
            }

            cache.Set(CacheKey, rate, CacheDuration);
            return rate;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch GBP/USD rate — using fallback {Rate}", FallbackRate);
            cache.Set(FailureCacheKey, true, FailureBackoff);
            return FallbackRate;
        }
    }
}
