using Refit;
using SwingTrader.Infrastructure.HttpClients.Dtos;

namespace SwingTrader.Infrastructure.HttpClients;

public interface ITiingoClient
{
    [Get("/tiingo/daily/{ticker}/prices")]
    Task<List<TiingoDailyPrice>> GetDailyPricesAsync(
        string ticker,
        [AliasAs("startDate")] string startDate,
        [AliasAs("endDate")] string endDate,
        [AliasAs("resampleFreq")] string resampleFreq = "daily");

    // IEX intraday bars (Power plan). resampleFreq accepts Min/Hour variants
    // only ("5min", "60min") - "1day" is rejected by the API, which is why
    // volume baselines are built by summing 60min bars per day. An unknown
    // ticker returns {"detail":"Not found."} (an object, not an array), which
    // fails deserialization - callers treat any exception as "unavailable".
    // Ticker-tagged news (Power plan). Newest-first; default page size is 100,
    // limit + startDate both honoured (verified with real calls 2026-07-10).
    // History is capped at ~3 months on Power - fine for the 3-day sentiment
    // lookback; the long-term archive is our own table, not this endpoint.
    [Get("/tiingo/news")]
    Task<List<TiingoNewsItem>> GetNewsAsync(
        [AliasAs("tickers")] string tickers,
        [AliasAs("startDate")] string startDate,
        [AliasAs("limit")] int limit = 20);

    [Get("/iex/{ticker}/prices")]
    Task<List<TiingoIexPrice>> GetIexIntradayAsync(
        string ticker,
        [AliasAs("startDate")] string startDate,
        [AliasAs("resampleFreq")] string resampleFreq = "5min",
        [AliasAs("columns")] string columns = "open,high,low,close,volume");
}
