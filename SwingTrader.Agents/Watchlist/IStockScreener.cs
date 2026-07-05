using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Watchlist;

public interface IStockScreener
{
    Task<List<ScreenedCandidate>> ScreenAsync(int accountId, IFinnhubClient finnhub, CancellationToken ct = default);
}
