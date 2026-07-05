using Microsoft.Extensions.Logging;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Infrastructure.Services;

public class MarketDataService(ILogger<MarketDataService> logger) : IMarketDataService
{
    public async Task<QuoteData?> GetQuoteAsync(IFinnhubClient client, string symbol)
    {
        try
        {
            var dto = await client.GetQuoteAsync(symbol);
            if (dto.CurrentPrice is null or 0) return null;
            return new QuoteData(
                symbol,
                dto.CurrentPrice.Value,
                dto.Change ?? 0m,
                dto.PercentChange ?? 0m,
                dto.High ?? 0m,
                dto.Low ?? 0m,
                dto.Open ?? 0m,
                dto.PreviousClose ?? 0m,
                DateTimeOffset.FromUnixTimeSeconds(dto.Timestamp).UtcDateTime);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get quote for {Symbol}", symbol);
            return null;
        }
    }

    public async Task<IReadOnlyList<CandleData>> GetDailyCandlesAsync(IFinnhubClient client, string symbol, DateTime from, DateTime to)
    {
        try
        {
            var fromTs = new DateTimeOffset(from, TimeSpan.Zero).ToUnixTimeSeconds();
            var toTs = new DateTimeOffset(to, TimeSpan.Zero).ToUnixTimeSeconds();
            var dto = await client.GetCandlesAsync(symbol, "D", fromTs, toTs);

            if (dto.Status != "ok" || dto.Timestamps.Count == 0)
                return Array.Empty<CandleData>();

            return dto.Timestamps.Select((ts, i) => new CandleData(
                DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime,
                dto.Open[i], dto.High[i], dto.Low[i], dto.Close[i], dto.Volume[i]
            )).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get candles for {Symbol}", symbol);
            return Array.Empty<CandleData>();
        }
    }

    public async Task<IReadOnlyList<NewsItem>> GetCompanyNewsAsync(IFinnhubClient client, string symbol, DateTime from, DateTime to)
    {
        try
        {
            var items = await client.GetCompanyNewsAsync(symbol,
                from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
            return items.Select(n => new NewsItem(
                n.Headline, n.Summary, n.Source,
                DateTimeOffset.FromUnixTimeSeconds(n.Datetime).UtcDateTime, n.Url
            )).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get company news for {Symbol}", symbol);
            return Array.Empty<NewsItem>();
        }
    }

    public async Task<IReadOnlyList<NewsItem>> GetMarketNewsAsync(IFinnhubClient client, string category = "general")
    {
        try
        {
            var items = await client.GetMarketNewsAsync(category);
            return items.Select(n => new NewsItem(
                n.Headline, n.Summary, n.Source,
                DateTimeOffset.FromUnixTimeSeconds(n.Datetime).UtcDateTime, n.Url
            )).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get market news");
            return Array.Empty<NewsItem>();
        }
    }
}
