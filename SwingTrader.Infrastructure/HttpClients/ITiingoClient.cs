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
    [Get("/iex/{ticker}/prices")]
    Task<List<TiingoIexPrice>> GetIexIntradayAsync(
        string ticker,
        [AliasAs("startDate")] string startDate,
        [AliasAs("resampleFreq")] string resampleFreq = "5min",
        [AliasAs("columns")] string columns = "open,high,low,close,volume");
}
