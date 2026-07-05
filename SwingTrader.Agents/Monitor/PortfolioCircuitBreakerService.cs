using Microsoft.Extensions.Logging;
using SwingTrader.Core.Constants;
using SwingTrader.Core.Interfaces;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Monitor;

public class PortfolioCircuitBreakerService(
    IPortfolioRepository portfolioRepo,
    ILogger<PortfolioCircuitBreakerService> logger) : IPortfolioCircuitBreakerService
{
    public async Task<bool> ShouldTriggerAsync(int accountId, ITrading212Client t212, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Find the snapshot taken at the start of today's trading session
        var snapshots = await portfolioRepo.GetSnapshotHistoryAsync(accountId, today, today);
        var baseline = snapshots.OrderBy(s => s.CreatedAt).FirstOrDefault();

        if (baseline is null)
        {
            logger.LogDebug("No baseline snapshot for today (account {AccountId}) — circuit breaker skipped", accountId);
            return false;
        }

        // Live portfolio value: cash + open position market values
        decimal currentValue;
        try
        {
            var summary = await t212.GetAccountSummaryAsync();
            var portfolio = await t212.GetPortfolioAsync();
            var positionsValue = portfolio.Sum(p => p.Quantity * p.CurrentPrice);
            currentValue = summary.Cash.AvailableToTrade + positionsValue;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch live account value for circuit breaker check (account {AccountId}) — skipping", accountId);
            return false;
        }

        if (baseline.TotalCapital <= 0)
            return false;

        var drawdownPct = (baseline.TotalCapital - currentValue) / baseline.TotalCapital;

        if (drawdownPct >= CapitalRules.DailyLossCircuitBreakerPct)
        {
            logger.LogCritical(
                "CIRCUIT BREAKER TRIGGERED for account {AccountId} — portfolio down {DrawdownPct:P1} today " +
                "(baseline={Baseline:F2}, current={Current:F2}, threshold={Threshold:P0})",
                accountId, drawdownPct, baseline.TotalCapital, currentValue,
                CapitalRules.DailyLossCircuitBreakerPct);
            return true;
        }

        return false;
    }
}
