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
    Task<RelativeStrengthResult> CalculateAsync(ITiingoClient tiingo, string symbol, CancellationToken ct);
}
