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

        // 200-day MA of ~20 sessions ago, for trend confirmation: a shallow
        // dip below a RISING 200dma is a pullback; below a FALLING one is
        // structure breaking down. Null when there isn't enough history.
        decimal? spyMa200Prior = closes.Count >= 220
            ? closes.Skip(closes.Count - 220).Take(200).Average()
            : null;

        // No fabricated VIX: the old `?? 20m` fallback silently invented a
        // reading when the quote failed, and quietly steered classification.
        // Unknown VIX now means the VIX conditions simply don't apply.
        decimal? vix = null;
        try
        {
            var vixQuote = await finnhub.GetQuoteAsync("VIX");
            if (vixQuote.CurrentPrice is > 0) vix = vixQuote.CurrentPrice;
        }
        catch { /* price-structure classification still works without VIX */ }

        var regime = ClassifyRegime(spyPrice, spyMa50, spyMa200, spyMa200Prior, vix);
        var vixText = vix is null ? "VIX n/a" : $"VIX {vix:F1}";
        var label = regime switch
        {
            MarketRegime.Crisis => $"Crisis — {vixText} (circuit breaker territory)",
            MarketRegime.Bear => $"Bear — SPY vs 200-day MA {(spyPrice / spyMa200 - 1) * 100:+0.0;-0.0}%, 200-day MA {(spyMa200Prior is null ? "trend n/a" : spyMa200 < spyMa200Prior ? "falling" : "rising")}, {vixText}",
            MarketRegime.Neutral => $"Neutral — SPY vs 50-day MA {(spyPrice / spyMa50 - 1) * 100:+0.0;-0.0}%, {vixText}",
            _ => $"Bull — SPY above 50-day MA, {vixText}",
        };

        var result = new MarketRegimeResult(regime, spyPrice, spyMa50, spyMa200, vix ?? 0m, label);
        cache.Set(CacheKey, result, CacheDuration);

        logger.LogInformation("Market regime detected: {Regime} ({Label})", regime, label);
        return result;
    }

    // Bear requires STRUCTURE, not a touch: the old `price < ma200 || vix > 25`
    // called a 0.1% dip below the 200dma - or a routine volatility spike during
    // a bull correction - a bear market, which made regime-driven behaviour
    // (and now the bear autopause) flap on noise. Bear = price below the 200dma
    // AND at least one confirmation: the 200dma itself is falling, the breach
    // is deep (>3%), or the 50dma has crossed under the 200dma (death cross).
    // Shallow breaches above a rising 200dma classify Neutral, as do elevated-
    // but-not-extreme VIX readings. Crisis stays VIX-driven (>35, real reading
    // only). The strict-entry/looser-exit asymmetry doubles as hysteresis for
    // the bear autopause: it takes structural damage to pause, and only a
    // reclaim of the 200dma (or trend repair) to resume.
    // Classifies the regime at a HISTORICAL point from SPY closes ending on the
    // target date (oldest→newest). Used by the backtester to switch regime books
    // per simulated day. VIX history isn't available, so this is price-structure
    // only: Crisis (VIX-driven) can't be detected here - callers that need it
    // must supply VIX. Returns null when there isn't enough history (<200 bars),
    // so the caller can fall back to a default regime.
    public static MarketRegime? ClassifyFromCloses(IReadOnlyList<decimal> closes, decimal? vix = null)
    {
        if (closes.Count < 200) return null;
        var spyPrice = closes[^1];
        var spyMa50 = closes.Skip(closes.Count - 50).Take(50).Average();
        var spyMa200 = closes.Skip(closes.Count - 200).Take(200).Average();
        decimal? spyMa200Prior = closes.Count >= 220
            ? closes.Skip(closes.Count - 220).Take(200).Average()
            : null;
        return ClassifyRegime(spyPrice, spyMa50, spyMa200, spyMa200Prior, vix);
    }

    public static MarketRegime ClassifyRegime(
        decimal spyPrice, decimal spyMa50, decimal spyMa200, decimal? spyMa200Prior, decimal? vix)
    {
        if (vix > 35m) return MarketRegime.Crisis;

        if (spyPrice < spyMa200)
        {
            var ma200Falling = spyMa200Prior.HasValue && spyMa200 < spyMa200Prior.Value;
            var deepBreach = spyPrice < spyMa200 * 0.97m;
            var deathCross = spyMa50 < spyMa200;
            if (ma200Falling || deepBreach || deathCross) return MarketRegime.Bear;
            return MarketRegime.Neutral; // shallow dip below a rising 200dma = pullback
        }

        if (spyPrice < spyMa50 || vix > 20m) return MarketRegime.Neutral;
        return MarketRegime.Bull;
    }
}
