using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Infrastructure.Configuration;

namespace SwingTrader.Infrastructure.Market;

// Universe is shared market data (which stocks exist in an index), not
// account-specific, so the cache is global - same pattern as MarketRegimeService.
public class MarketUniverseService(
    IMemoryCache cache,
    IWikipediaIndexClient wikipedia,
    IOptions<WatchlistConfig> config,
    ILogger<MarketUniverseService> logger) : IMarketUniverseService
{
    private const string CacheKey = "market_universe";

    public async Task<List<string>> GetUniverseAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out List<string>? cached) && cached is not null)
        {
            logger.LogDebug("Returning cached universe ({Count} symbols)", cached.Count);
            return cached;
        }

        var cfg = config.Value;
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The full S&P Composite 1500 (large + mid + small cap) plus Nasdaq-100.
        // Mid/small caps (S&P 400/600) are the point: mega-caps rarely swing the
        // 8-12% this strategy targets, so a large-cap-only universe was fishing
        // in the calmest pool. Each lookup is independent - one failing just
        // narrows the pool for that build, never aborts the whole universe.
        await TryAddConstituentsAsync(symbols, "S&P 500", wikipedia.GetSp500ConstituentsAsync, ct);
        await TryAddConstituentsAsync(symbols, "S&P 400 (MidCap)", wikipedia.GetSp400ConstituentsAsync, ct);
        await TryAddConstituentsAsync(symbols, "S&P 600 (SmallCap)", wikipedia.GetSp600ConstituentsAsync, ct);
        await TryAddConstituentsAsync(symbols, "Nasdaq-100", wikipedia.GetNasdaq100ConstituentsAsync, ct);

        if (symbols.Count == 0)
        {
            logger.LogError("Universe fetch returned zero symbols — all index lookups failed");
            return [];
        }

        var result = symbols.ToList();

        cache.Set(CacheKey, result, TimeSpan.FromDays(cfg.UniverseCacheDays));

        logger.LogInformation("Universe built: {Count} symbols from S&P 1500 + Nasdaq-100 (via Wikipedia)", result.Count);

        return result;
    }

    private async Task TryAddConstituentsAsync(
        HashSet<string> symbols, string indexName, Func<CancellationToken, Task<List<string>>> fetch, CancellationToken ct)
    {
        try
        {
            var constituents = await fetch(ct);
            foreach (var s in constituents.Where(IsValidSymbol))
                symbols.Add(s.ToUpperInvariant());

            logger.LogInformation("Fetched {Count} constituents from {Index}", constituents.Count, indexName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch constituents for {Index} — skipping", indexName);
            // Continue with the other index — never fail entirely on one lookup.
        }
    }

    // Excludes non-standard share classes (BRK.B, BF-B) and anything that
    // isn't a plain US-exchange ticker.
    private static bool IsValidSymbol(string symbol) =>
        !symbol.Contains('.')
        && !symbol.Contains('-')
        && symbol.Length is >= 1 and <= 5
        && symbol.All(char.IsLetter);
}
