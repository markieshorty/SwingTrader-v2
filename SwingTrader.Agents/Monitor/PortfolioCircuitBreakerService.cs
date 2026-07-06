using Microsoft.Extensions.Logging;
using SwingTrader.Core.Interfaces;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Agents.Monitor;

public class PortfolioCircuitBreakerService(
    IPortfolioRepository portfolioRepo,
    IAccountRiskProfileRepository riskProfileRepo,
    ILogger<PortfolioCircuitBreakerService> logger) : IPortfolioCircuitBreakerService
{
    public async Task<bool> ShouldTriggerAsync(int accountId, ITrading212Client t212, CancellationToken ct = default)
    {
        var riskProfile = await riskProfileRepo.GetAsync(accountId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Find the snapshot taken at the start of today's trading session
        var snapshots = await portfolioRepo.GetSnapshotHistoryAsync(accountId, today, today);
        var baseline = snapshots.OrderBy(s => s.CreatedAt).FirstOrDefault();

        if (baseline is null)
        {
            logger.LogDebug("No baseline snapshot for today (account {AccountId}) — circuit breaker skipped", accountId);
            return false;
        }

        // TotalValue is already in the account's base currency (GBP),
        // computed by T212 itself.
        decimal currentValue;
        try
        {
            var summary = await t212.GetAccountSummaryAsync();
            currentValue = summary.TotalValue;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch live account value for circuit breaker check (account {AccountId}) — skipping", accountId);
            return false;
        }

        // Defensive: a real account with open positions is never actually
        // worth £0. Kept as a sanity check even after fixing the DTO
        // mismatch that used to make this reliably true (Cash.Total didn't
        // match any real T212 field and always deserialized to 0) - a
        // future degraded/incomplete 200 response should still be treated
        // as bad data rather than a real 100% drawdown.
        if (currentValue <= 0)
        {
            logger.LogWarning(
                "T212 account summary returned a non-positive total ({Current:F2}) for account {AccountId} — " +
                "treating as bad data and skipping circuit breaker check", currentValue, accountId);
            return false;
        }

        if (baseline.TotalCapital <= 0)
            return false;

        var drawdownPct = (baseline.TotalCapital - currentValue) / baseline.TotalCapital;

        if (drawdownPct >= riskProfile.DailyLossCircuitBreakerPct)
        {
            logger.LogCritical(
                "CIRCUIT BREAKER TRIGGERED for account {AccountId} — portfolio down {DrawdownPct:P1} today " +
                "(baseline={Baseline:F2}, current={Current:F2}, threshold={Threshold:P0})",
                accountId, drawdownPct, baseline.TotalCapital, currentValue,
                riskProfile.DailyLossCircuitBreakerPct);
            return true;
        }

        return false;
    }
}
