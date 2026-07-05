using Refit;
using SwingTrader.Infrastructure.HttpClients.Dtos;

namespace SwingTrader.Infrastructure.HttpClients;

public interface IFinnhubClient
{
    [Get("/quote")]
    Task<FinnhubQuoteResponse> GetQuoteAsync([AliasAs("symbol")] string symbol);

    [Get("/stock/candle")]
    Task<FinnhubCandlesResponse> GetCandlesAsync(
        [AliasAs("symbol")] string symbol,
        [AliasAs("resolution")] string resolution,
        [AliasAs("from")] long from,
        [AliasAs("to")] long to);

    [Get("/company-news")]
    Task<List<FinnhubNewsItem>> GetCompanyNewsAsync(
        [AliasAs("symbol")] string symbol,
        [AliasAs("from")] string from,
        [AliasAs("to")] string to);

    [Get("/news")]
    Task<List<FinnhubNewsItem>> GetMarketNewsAsync([AliasAs("category")] string category);

    [Get("/calendar/earnings")]
    Task<FinnhubEarningsCalendarResponse> GetEarningsCalendarAsync(
        [AliasAs("from")] string from,
        [AliasAs("to")] string to,
        [AliasAs("symbol")] string symbol);

    [Get("/stock/earnings")]
    Task<List<FinnhubEarningsEvent>> GetEarningsHistoryAsync(
        [AliasAs("symbol")] string symbol,
        [AliasAs("limit")] int limit = 4);

    [Get("/stock/market-gainers")]
    Task<List<MarketMoverItem>> GetTopGainersAsync();

    [Get("/stock/market-losers")]
    Task<List<MarketMoverItem>> GetTopLosersAsync();

    [Get("/stock/market-most-active")]
    Task<List<MarketMoverItem>> GetMostActiveAsync();

    [Get("/stock/recommendation")]
    Task<List<AnalystRecommendation>> GetAnalystRecommendationsAsync([AliasAs("symbol")] string symbol);

    [Get("/stock/insider-transactions")]
    Task<InsiderTransactionsResponse> GetInsiderTransactionsAsync([AliasAs("symbol")] string symbol);

    [Get("/stock/revenue-estimate")]
    Task<RevenueEstimateResponse> GetRevenueEstimatesAsync(
        [AliasAs("symbol")] string symbol,
        [AliasAs("freq")] string freq = "quarterly");
}
