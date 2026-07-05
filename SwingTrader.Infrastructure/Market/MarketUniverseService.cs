using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Infrastructure.Market;

// Universe is shared market data (which stocks exist in an index), not
// account-specific, so the cache is global regardless of which account's
// Finnhub client happened to populate it - same pattern as MarketRegimeService.
public class MarketUniverseService(
    IMemoryCache cache,
    IOptions<WatchlistConfig> config,
    ILogger<MarketUniverseService> logger) : IMarketUniverseService
{
    private const string CacheKey = "market_universe";

    public async Task<List<string>> GetUniverseAsync(IFinnhubClient finnhub, CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out List<string>? cached) && cached is not null)
        {
            logger.LogDebug("Returning cached universe ({Count} symbols)", cached.Count);
            return cached;
        }

        var cfg = config.Value;
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var index in cfg.IndexSymbols)
        {
            try
            {
                var response = await finnhub.GetIndexConstituentsAsync(index);

                foreach (var s in response.Constituents.Where(IsValidSymbol))
                    symbols.Add(s.ToUpperInvariant());

                logger.LogInformation("Fetched {Count} constituents from {Index}", response.Constituents.Count, index);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch constituents for {Index} — skipping", index);
                // Continue with other indices — never fail entirely on one index.
            }
        }

        if (symbols.Count == 0)
        {
            logger.LogError("Universe fetch returned zero symbols — all index calls failed");
            return [];
        }

        var result = symbols.ToList();

        cache.Set(CacheKey, result, TimeSpan.FromDays(cfg.UniverseCacheDays));

        logger.LogInformation(
            "Universe built: {Count} symbols from {Indices} indices",
            result.Count, string.Join(", ", cfg.IndexSymbols));

        return result;
    }

    // Excludes non-standard share classes (BRK.B, BF-B) and anything that
    // isn't a plain US-exchange ticker.
    private static bool IsValidSymbol(string symbol) =>
        !symbol.Contains('.')
        && !symbol.Contains('-')
        && symbol.Length is >= 1 and <= 5
        && symbol.All(char.IsLetter);
}
