namespace SwingTrader.Infrastructure.Market;

public interface IForexService
{
    /// <summary>
    /// Returns the GBP amount per 1 USD (i.e. multiply a USD price by this to get GBP).
    /// Cached for 60 minutes; never throws — falls back to a sane default on failure.
    /// </summary>
    Task<decimal> GetGbpUsdRateAsync(CancellationToken ct);
}
