using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Monitor;

public record MomentumHealthResult(
    decimal Score,
    string Verdict, // "Confirmed", "Borderline", "Exit"
    string Reasoning,
    decimal RsiDirectionScore,
    decimal VolumeScore,
    decimal PriceDirectionScore,
    decimal RelativeStrengthScore);

public interface IMomentumHealthService
{
    // Pure calculation, no Claude call — reuses today's StockSignal for the
    // symbol plus the entry-time signal already linked via Trade.SignalId.
    Task<MomentumHealthResult> CalculateAsync(
        int accountId,
        Trade trade,
        CancellationToken ct = default);
}
