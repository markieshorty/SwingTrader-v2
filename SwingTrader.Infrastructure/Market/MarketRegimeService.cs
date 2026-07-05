using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SwingTrader.Core.Enums;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Infrastructure.Market;

// Regime (SPY vs its moving averages, VIX level) is shared market data, not
// account-specific, so the cache is global regardless of which account's
// Finnhub/Tiingo client happened to populate it.
public class MarketRegimeService(
    IMemoryCache cache,
    ILogger<MarketRegimeService> logger) : IMarketRegimeService
{
    private const string CacheKey = "market_regime_current";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(4);

    public async Task<MarketRegimeResult> GetCurrentRegimeAsync(ITiingoClient tiingo, IFinnhubClient finnhub, CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out MarketRegimeResult? cached) && cached is not null)
            return cached;

        var endDate = DateTime.UtcNow.Date;
        var startDate = endDate.AddDays(-320);

        var prices = await tiingo.GetDailyPricesAsync(
            "SPY", startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"));

        var closes = prices.OrderBy(p => p.Date).Select(p => p.Close).ToList();
        if (closes.Count < 200)
            throw new InvalidOperationException($"Not enough SPY history to detect regime — got {closes.Count} bars, need 200.");

        var spyPrice = closes[^1];
        var spyMa50 = closes.TakeLast(50).Average();
        var spyMa200 = closes.TakeLast(200).Average();

        var vixQuote = await finnhub.GetQuoteAsync("VIX");
        var vix = vixQuote.CurrentPrice ?? 20m;

        var regime = ClassifyRegime(spyPrice, spyMa50, spyMa200, vix);
        var label = regime switch
        {
            MarketRegime.Crisis => $"Crisis — VIX {vix:F1} (circuit breaker territory)",
            MarketRegime.Bear => $"Bear — SPY vs 200-day MA {(spyPrice / spyMa200 - 1) * 100:+0.0;-0.0}%, VIX {vix:F1}",
            MarketRegime.Neutral => $"Neutral — SPY vs 50-day MA {(spyPrice / spyMa50 - 1) * 100:+0.0;-0.0}%, VIX {vix:F1}",
            _ => $"Bull — SPY above 50-day MA, VIX {vix:F1}",
        };

        var result = new MarketRegimeResult(regime, spyPrice, spyMa50, spyMa200, vix, label);
        cache.Set(CacheKey, result, CacheDuration);

        logger.LogInformation("Market regime detected: {Regime} ({Label})", regime, label);
        return result;
    }

    private static MarketRegime ClassifyRegime(decimal spyPrice, decimal spyMa50, decimal spyMa200, decimal vix)
    {
        if (vix > 35m) return MarketRegime.Crisis;
        if (spyPrice < spyMa200 || vix > 25m) return MarketRegime.Bear;
        if (spyPrice < spyMa50 || vix > 20m) return MarketRegime.Neutral;
        return MarketRegime.Bull;
    }
}
