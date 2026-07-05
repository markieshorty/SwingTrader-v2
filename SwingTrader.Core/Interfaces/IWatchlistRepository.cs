namespace SwingTrader.Core.Interfaces;

public interface IWatchlistRepository
{
    // Seeds the starter watchlist for a brand-new account. Full watchlist
    // CRUD lands with the Agents/Infrastructure business logic porting.
    Task SeedDefaultAsync(int accountId, CancellationToken ct = default);
}
