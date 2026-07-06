using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Constants;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Agents.Risk;

// Tier changes only ever widen/narrow how much of the account's own capital
// future trades are allowed to use - they don't place orders, so (unlike
// Execution/Monitor exits) this is safe to apply automatically when
// RiskManagement:Active is on, matching legacy behaviour.
public class TierEvaluationService(
    ITradeRepository tradeRepo,
    IPortfolioRepository portfolioRepo,
    ITierEvaluationRepository evaluationRepo,
    IAccountRiskProfileRepository riskProfileRepo,
    IIndicatorService indicators,
    INotificationRecipientRepository recipients,
    IEmailService email,
    IOptions<RiskManagementConfig> riskConfig,
    IOptions<ClaudeConfig> claudeConfig,
    ILogger<TierEvaluationService> logger) : ITierEvaluationService
{
    public async Task<TierEvaluationRecord> EvaluateAsync(int accountId, IClaudeClient claude, CancellationToken ct = default)
    {
        var cfg = riskConfig.Value;
        var riskProfile = await riskProfileRepo.GetAsync(accountId, ct);
        var prefix = cfg.Active ? string.Empty : cfg.ShadowModeLogPrefix;
        var now = DateTime.UtcNow;
        var from = now.AddDays(-90);

        logger.LogInformation("{Prefix}Tier evaluation starting for account {AccountId} (period {From:yyyy-MM-dd} to {To:yyyy-MM-dd})",
            prefix, accountId, from, now);

        // ── Step 1: gather closed trades and base statistics ──────────────────
        var history = await tradeRepo.GetTradeHistoryAsync(accountId, from, now);
        var closed = history
            .Where(t => t.Status != TradeStatus.Open && t.ClosedAt.HasValue && t.RealizedPnl.HasValue)
            .OrderBy(t => t.ClosedAt!.Value)
            .ToList();

        var snapshot = await portfolioRepo.GetLatestSnapshotAsync(accountId);
        var currentTier = snapshot?.CurrentTier ?? CapitalTier.Tier1;

        var totalTrades = closed.Count;
        var winRate = totalTrades > 0
            ? (decimal)closed.Count(t => t.RealizedPnl > 0) / totalTrades
            : 0m;
        var avgReturnPct = totalTrades > 0
            ? Math.Round(closed.Average(t => ReturnPct(t)), 4)
            : 0m;

        // ── Step 2: risk metrics (Sharpe + max drawdown) ──────────────────────
        var weeklyReturns = closed
            .GroupBy(t => (t.ClosedAt!.Value - DateTime.MinValue).Days / 7)
            .OrderBy(g => g.Key)
            .Select(g => g.Sum(t => ReturnPct(t)))
            .ToList();
        var sharpe = indicators.CalculateSharpeRatio(weeklyReturns);

        var equityCurve = new List<decimal> { 0m };
        var running = 0m;
        foreach (var t in closed)
        {
            running += t.RealizedPnl ?? 0m;
            equityCurve.Add(running);
        }
        var maxDrawdown = indicators.CalculateMaxDrawdown(equityCurve);

        // ── Step 3: unlock / downgrade decision ───────────────────────────────
        var (unlockMet, suggestedTier) = EvaluateTierChange(currentTier, totalTrades, winRate, riskProfile);

        // Downgrade check on the most recent 30 closed trades
        var recent30 = closed.OrderByDescending(t => t.ClosedAt!.Value).Take(30).ToList();
        if (recent30.Count >= 30)
        {
            var recentWinRate = (decimal)recent30.Count(t => t.RealizedPnl > 0) / recent30.Count;
            var recentAvgReturn = recent30.Average(t => ReturnPct(t));
            if (recentWinRate < CapitalRules.DowngradeWinRateThreshold
                || recentAvgReturn < CapitalRules.DowngradeAvgReturnThreshold)
            {
                suggestedTier = Downgrade(currentTier);
                unlockMet = false;
            }
        }

        // ── Step 4: Claude commentary (best-effort) ───────────────────────────
        var notes = await GetClaudeCommentaryAsync(
            claude, currentTier, suggestedTier, totalTrades, winRate, avgReturnPct, sharpe, maxDrawdown, ct);

        // ── Step 5: apply (live) or record shadow ─────────────────────────────
        var actualTierAfter = currentTier;
        var wasApplied = false;

        if (suggestedTier != currentTier)
        {
            if (cfg.Active)
            {
                if (snapshot is not null)
                {
                    snapshot.CurrentTier = suggestedTier;
                    await portfolioRepo.UpdateAsync(snapshot);
                }
                actualTierAfter = suggestedTier;
                wasApplied = true;
                logger.LogInformation("Tier change applied for account {AccountId}: {From} -> {To}", accountId, currentTier, suggestedTier);
            }
            else
            {
                logger.LogInformation("{Prefix}Would change tier for account {AccountId}: {From} -> {To}",
                    prefix, accountId, currentTier, suggestedTier);
            }
        }
        else
        {
            logger.LogInformation("{Prefix}No tier change suggested for account {AccountId} (staying {Tier})", prefix, accountId, currentTier);
        }

        var record = new TierEvaluationRecord
        {
            AccountId = accountId,
            EvaluatedAt = now,
            EvaluationPeriodStart = DateOnly.FromDateTime(from),
            EvaluationPeriodEnd = DateOnly.FromDateTime(now),
            CurrentTier = currentTier,
            TotalTrades = totalTrades,
            WinRate = Math.Round(winRate, 4),
            AvgReturnPct = avgReturnPct,
            SharpeRatio = sharpe,
            MaxDrawdownPct = maxDrawdown,
            UnlockCriteriaMet = unlockMet,
            SuggestedTier = suggestedTier,
            ActualTierAfter = actualTierAfter,
            WasApplied = wasApplied,
            Notes = notes,
        };

        var saved = await evaluationRepo.AddAsync(record);

        await SendEmailAsync(accountId, saved, cfg.Active, prefix);

        return saved;
    }

    private static decimal ReturnPct(Trade t)
    {
        var cost = t.EntryPrice * t.Quantity;
        if (cost == 0m) return 0m;
        return Math.Round((t.RealizedPnl ?? 0m) / cost * 100m, 4);
    }

    private static (bool unlockMet, CapitalTier suggested) EvaluateTierChange(
        CapitalTier current, int totalTrades, decimal winRate, AccountRiskProfile riskProfile)
    {
        switch (current)
        {
            case CapitalTier.Tier1:
                if (totalTrades >= riskProfile.Tier1UnlockMinTrades
                    && winRate >= riskProfile.Tier1UnlockMinWinRate)
                    return (true, CapitalTier.Tier2);
                return (false, CapitalTier.Tier1);
            case CapitalTier.Tier2:
                if (totalTrades >= riskProfile.Tier2UnlockMinTrades
                    && winRate >= riskProfile.Tier2UnlockMinWinRate)
                    return (true, CapitalTier.Tier3);
                return (false, CapitalTier.Tier2);
            default:
                return (false, current);
        }
    }

    private static CapitalTier Downgrade(CapitalTier current) => current switch
    {
        CapitalTier.Tier3 => CapitalTier.Tier2,
        CapitalTier.Tier2 => CapitalTier.Tier1,
        _ => CapitalTier.Tier1
    };

    private async Task<string?> GetClaudeCommentaryAsync(
        IClaudeClient claude, CapitalTier current, CapitalTier suggested, int totalTrades, decimal winRate,
        decimal avgReturnPct, decimal? sharpe, decimal maxDrawdown, CancellationToken ct)
    {
        try
        {
            var systemPrompt =
                "You are a risk management analyst for a swing trading system. " +
                "Provide a concise 2-3 sentence assessment. No markdown, no preamble.";
            var userPrompt =
                $"Current tier: {current}. Suggested tier: {suggested}.\n" +
                $"Trades evaluated: {totalTrades}. Win rate: {winRate:P1}. " +
                $"Avg return per trade: {avgReturnPct:F2}%. " +
                $"Annualised Sharpe: {(sharpe.HasValue ? sharpe.Value.ToString("F2") : "n/a")}. " +
                $"Max drawdown: {maxDrawdown:P1}.\n" +
                "Briefly assess whether this tier decision looks sound.";

            var request = new ClaudeRequest(
                claudeConfig.Value.Model,
                claudeConfig.Value.MaxTokens,
                systemPrompt,
                [new ClaudeMessage("user", userPrompt)]);

            var response = await claude.SendMessageAsync(request);
            var text = response.Content.FirstOrDefault(c => c.Type == "text")?.Text;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Claude tier commentary failed — continuing without it");
            return null;
        }
    }

    private async Task SendEmailAsync(int accountId, TierEvaluationRecord record, bool active, string prefix)
    {
        try
        {
            var mode = active ? "LIVE" : "SHADOW";
            var subject = $"{prefix}Tier Evaluation ({mode}) — {record.EvaluatedAt:dd MMM yyyy}";
            var md =
                $"# Tier Evaluation ({mode})\n\n" +
                $"- Period: {record.EvaluationPeriodStart} to {record.EvaluationPeriodEnd}\n" +
                $"- Current tier: **{record.CurrentTier}**\n" +
                $"- Suggested tier: **{record.SuggestedTier}**\n" +
                $"- Applied: {record.WasApplied}\n" +
                $"- Trades: {record.TotalTrades}\n" +
                $"- Win rate: {record.WinRate:P1}\n" +
                $"- Avg return/trade: {record.AvgReturnPct:F2}%\n" +
                $"- Sharpe: {(record.SharpeRatio.HasValue ? record.SharpeRatio.Value.ToString("F2") : "n/a")}\n" +
                $"- Max drawdown: {record.MaxDrawdownPct:P1}\n" +
                $"- Unlock criteria met: {record.UnlockCriteriaMet}\n\n" +
                (record.Notes is not null ? $"## Notes\n\n{record.Notes}\n" : string.Empty);

            var toAddresses = (await recipients.ListAsync(accountId))
                .Where(r => r.Categories.HasFlag(NotificationCategory.MonthlySummary))
                .Select(r => r.Email)
                .ToList();

            if (toAddresses.Count > 0)
                await email.SendSimpleEmailAsync(toAddresses, md, subject);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send tier evaluation email for account {AccountId}", accountId);
        }
    }
}
