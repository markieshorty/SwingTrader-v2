using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Infrastructure.Market;

public record RelativeStrengthResult(
    string SectorEtf,
    decimal StockReturn5d,
    decimal EtfReturn5d,
    decimal RelativeReturn,
    decimal Score,
    string Label);

public interface IRelativeStrengthService
{
    // Returns null when relative strength can't be computed (insufficient
    // candles, ETF fetch failure) rather than a synthetic neutral 0.5 - a
    // fake score stored on the signal is indistinguishable from a genuine
    // 0.5 and pollutes the Refinement agent's score/outcome correlations.
    // Callers treat null as neutral for conviction purposes.
    Task<RelativeStrengthResult?> CalculateAsync(ITiingoClient tiingo, string symbol, CancellationToken ct);
}
