using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Infrastructure.Services;

public record QuoteData(string Symbol, decimal Price, decimal Change, decimal PercentChange,
    decimal High, decimal Low, decimal Open, decimal PreviousClose, DateTime Timestamp);

public record CandleData(DateTime Timestamp, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

public record NewsItem(string Headline, string Summary, string Source, DateTime PublishedAt, string Url);

// Every method takes the caller's already-resolved per-account IFinnhubClient
// (from IUserHttpClientFactory) rather than having one injected, since this
// service is a stateless singleton shared across every account's requests.
public interface IMarketDataService
{
    Task<QuoteData?> GetQuoteAsync(IFinnhubClient client, string symbol);
    Task<IReadOnlyList<CandleData>> GetDailyCandlesAsync(IFinnhubClient client, string symbol, DateTime from, DateTime to);
    Task<IReadOnlyList<NewsItem>> GetCompanyNewsAsync(IFinnhubClient client, string symbol, DateTime from, DateTime to);
    Task<IReadOnlyList<NewsItem>> GetMarketNewsAsync(IFinnhubClient client, string category = "general");
}
