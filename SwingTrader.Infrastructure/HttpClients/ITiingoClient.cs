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
}
