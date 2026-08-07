namespace SwingTrader.Agents.Scorecard;

public interface IAlmostTradesService
{
    // Signals the system wanted to buy but couldn't, with their replayed
    // outcomes. See AlmostTradesService for why this is separate from the
    // forward scorecard's blocked-Buy panel.
    Task<AlmostTradesResult> BuildAsync(int accountId, int windowDays, CancellationToken ct = default);
}
