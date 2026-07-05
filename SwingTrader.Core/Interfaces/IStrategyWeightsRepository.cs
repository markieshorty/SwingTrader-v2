namespace SwingTrader.Core.Interfaces;

public interface IStrategyWeightsRepository
{
    // Seeds the default strategy weights row for a brand-new account. Full
    // weights CRUD lands with the Agents/Infrastructure business logic
    // porting.
    Task SeedDefaultAsync(int accountId, CancellationToken ct = default);
}
