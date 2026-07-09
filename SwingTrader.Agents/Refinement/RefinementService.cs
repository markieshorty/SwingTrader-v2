using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.RateLimiting;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Agents.Refinement;

public class RefinementService(
    ITradeRepository tradeRepo,
    ISignalRepository signalRepo,
    IStrategyWeightsRepository weightsRepo,
    IRefinementSuggestionRepository suggestionRepo,
    IAccountRepository accountRepo,
    IComponentCorrelationService correlationService,
    INotificationRecipientRepository recipients,
    IEmailService email,
    IClaudeRateLimiter claudeRateLimiter,
    IOptions<RefinementConfig> refinementConfig,
    IOptions<ClaudeConfig> claudeConfig,
    ILogger<RefinementService> logger) : IRefinementService
{
    public async Task<RefinementSuggestion?> RunAsync(int accountId, IClaudeClient claude, CancellationToken ct = default)
    {
        var cfg = refinementConfig.Value;
        var prefix = cfg.Active ? string.Empty : cfg.ShadowModeLogPrefix;
        var now = DateTime.UtcNow;
        var from = now.AddDays(-cfg.AnalysisPeriodDays);

        var account = await accountRepo.GetAsync(accountId, ct)
            ?? throw new InvalidOperationException($"Account {accountId} not found.");

        logger.LogInformation("{Prefix}Refinement analysis starting for account {AccountId} (period {From:yyyy-MM-dd} to {To:yyyy-MM-dd})",
            prefix, accountId, from, now);

        var history = await tradeRepo.GetTradeHistoryAsync(accountId, account.TradingMode, from, now);
        var closed = history
            .Where(t => t.Status != TradeStatus.Open && t.ClosedAt.HasValue && t.RealizedPnl.HasValue && t.RealizedPnl != 0m)
            .ToList();

        if (closed.Count < cfg.MinTradesRequired)
        {
            logger.LogInformation(
                "{Prefix}Refinement skipped for account {AccountId} — only {Count} closed trades, need {Min}",
                prefix, accountId, closed.Count, cfg.MinTradesRequired);
            return null;
        }

        // Join trades to the signals that generated them, so component scores can be correlated.
        var matched = new List<(Trade Trade, StockSignal Signal, bool IsWinner)>();
        foreach (var trade in closed)
        {
            if (trade.SignalId is null) continue;
            var signal = await signalRepo.GetByIdAsync(accountId, trade.SignalId.Value);
            if (signal is null) continue;
            matched.Add((trade, signal, trade.RealizedPnl > 0m));
        }

        // Outcome = market-adjusted return % (falls back to raw P&L% for
        // trades that predate SPY-return capture) - see IComponentCorrelationService.
        var scoredTrades = matched.Select(m =>
        {
            var cost = m.Trade.EntryPrice * m.Trade.Quantity;
            var rawPct = cost == 0m ? 0m : m.Trade.RealizedPnl!.Value / cost * 100m;
            var returnPct = m.Trade.SpyReturnDuringTrade.HasValue
                ? rawPct - m.Trade.SpyReturnDuringTrade.Value
                : rawPct;
            return (m.Signal, ReturnPct: returnPct);
        }).ToList();

        if (scoredTrades.Count < cfg.MinCorrelationSampleSize)
        {
            logger.LogInformation(
                "{Prefix}Refinement skipped for account {AccountId} — only {Count} trades have linked scored signals, need {Min}",
                prefix, accountId, scoredTrades.Count, cfg.MinCorrelationSampleSize);
            return null;
        }

        var currentWeights = await weightsRepo.GetActiveWeightsAsync(accountId)
            ?? throw new InvalidOperationException($"No active StrategyWeights found for account {accountId}.");

        var analysis = correlationService.Analyse(scoredTrades, currentWeights, cfg.MaxWeightAdjustmentPerCycle);

        var winnerCount = closed.Count(t => t.RealizedPnl > 0m);
        var loserCount = closed.Count - winnerCount;
        var winRate = closed.Count > 0 ? (decimal)winnerCount / closed.Count : 0m;

        var confidence = scoredTrades.Count switch
        {
            >= 100 => RefinementConfidenceLevel.High,
            >= 60 => RefinementConfidenceLevel.Medium,
            _ => RefinementConfidenceLevel.Low
        };

        // Regime-split analysis, off by default (Refinement:RegimeAnalysisEnabled).
        RegimeCorrelationResult? regimeResult = null;
        if (cfg.RegimeAnalysisEnabled)
        {
            regimeResult = correlationService.AnalyseByRegime(
                matched, currentWeights, cfg.MaxWeightAdjustmentPerCycle, cfg.MinRegimeSampleSize, cfg.AnalysisPeriodDays);
        }

        var summary = await GetClaudeNarrativeAsync(claude, analysis, winRate, scoredTrades.Count, confidence, regimeResult, ct);

        await suggestionRepo.SupersedeAllPendingAsync(accountId, account.TradingMode);

        var suggestion = new RefinementSuggestion
        {
            AccountId = accountId,
            TradingMode = account.TradingMode,
            GeneratedAt = now,
            AnalysisPeriodStart = DateOnly.FromDateTime(from),
            AnalysisPeriodEnd = DateOnly.FromDateTime(now),
            TradeCountAnalysed = scoredTrades.Count,
            WinnerCount = winnerCount,
            LoserCount = loserCount,
            OverallWinRate = Math.Round(winRate, 4),
            CurrentWeightsJson = JsonSerializer.Serialize(currentWeights),
            SuggestedWeightsJson = JsonSerializer.Serialize(analysis.SuggestedWeights),
            ComponentAnalysisJson = JsonSerializer.Serialize(analysis.Findings),
            AssessmentSummary = summary,
            ConfidenceLevel = confidence,
            Status = RefinementStatus.Pending,
            IsShadowMode = !cfg.Active,
            RegimeBreakdownJson = regimeResult is null ? null : JsonSerializer.Serialize(regimeResult.RegimeBreakdown),
            SuggestedRegimeWeightsJson = regimeResult is null || regimeResult.SuggestedRegimeWeights.Count == 0
                ? null : JsonSerializer.Serialize(regimeResult.SuggestedRegimeWeights),
            MarketAdjustedWinRate = regimeResult is null ? 0m : Math.Round(regimeResult.MarketAdjustedWinRate, 4),
            UnusualMarketConditions = regimeResult?.UnusualMarketConditions ?? false,
            MarketConditionWarning = regimeResult?.MarketConditionWarning
        };

        var saved = await suggestionRepo.AddAsync(suggestion);

        await SendEmailAsync(accountId, saved, analysis.Findings, cfg.Active, prefix, regimeResult);

        return saved;
    }

    private async Task<string?> GetClaudeNarrativeAsync(
        IClaudeClient claude, CorrelationAnalysisResult analysis, decimal winRate, int sampleSize,
        RefinementConfidenceLevel confidence, RegimeCorrelationResult? regimeResult, CancellationToken ct)
    {
        try
        {
            var systemPrompt =
                "You are a quantitative strategy analyst reviewing a swing trading system's component weights. " +
                "Provide a concise 3-4 sentence assessment of the proposed weight changes. No markdown, no preamble.";
            var findingsText = string.Join("\n", analysis.Findings.Select(f =>
                $"{f.ComponentName}: weight {f.CurrentWeight:F2} -> {f.SuggestedWeight:F2} (r={f.Correlation:F2})"));
            var userPrompt =
                $"Sample size: {sampleSize} trades. Win rate: {winRate:P1}. Confidence: {confidence}.\n" +
                $"Component correlation findings:\n{findingsText}\n" +
                "Briefly assess whether these proposed weight adjustments look sound.";

            if (regimeResult is not null)
            {
                var regimeText = string.Join("\n", regimeResult.RegimeBreakdown.Select(kv =>
                {
                    var r = kv.Value;
                    return r.HasSufficientData
                        ? $"{r.Regime} ({r.TradeCount} trades, SPY avg {r.AvgSpyReturn:+0.0;-0.0}% during trades): " +
                          $"win rate {r.WinRate:P0}, top component {r.Findings.OrderByDescending(f => Math.Abs(f.Correlation)).First().ComponentName} " +
                          $"(r={r.Findings.OrderByDescending(f => Math.Abs(f.Correlation)).First().Correlation:F2})"
                        : $"{r.Regime}: {r.InsufficientDataReason}";
                }));
                userPrompt += $"\n\nMarket-adjusted win rate: {regimeResult.MarketAdjustedWinRate:P1}.\n" +
                              $"Market regime breakdown:\n{regimeText}\n";
                if (regimeResult.UnusualMarketConditions)
                    userPrompt += $"\nMarket condition note: {regimeResult.MarketConditionWarning}\n";
                userPrompt += "\nAlso address: do regime-specific findings suggest different components work in " +
                              "different market conditions, are any components universal across regimes, and " +
                              "should regime-specific weights be applied or is general data sufficient? Max 5 sentences total.";
            }

            var request = new ClaudeRequest(
                claudeConfig.Value.RefinementModel ?? claudeConfig.Value.Model,
                claudeConfig.Value.MaxTokens,
                systemPrompt,
                [new ClaudeMessage("user", userPrompt)]);

            await claudeRateLimiter.WaitAsync(ct);
            var response = await claude.SendMessageAsync(request);
            var text = response.Content.FirstOrDefault(c => c.Type == "text")?.Text;
            return string.IsNullOrWhiteSpace(text) ? FallbackSummary(analysis, winRate, sampleSize) : text.Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Claude refinement narrative failed — using fallback summary");
            return FallbackSummary(analysis, winRate, sampleSize);
        }
    }

    private static string FallbackSummary(CorrelationAnalysisResult analysis, decimal winRate, int sampleSize)
    {
        var biggestMover = analysis.Findings.OrderByDescending(f => Math.Abs(f.WeightDelta)).FirstOrDefault();
        var moverText = biggestMover is null
            ? "No significant weight changes suggested."
            : $"Largest suggested change: {biggestMover.ComponentName} ({biggestMover.WeightDelta:+0.000;-0.000}).";
        return $"Analysed {sampleSize} trades with a {winRate:P1} win rate. {moverText}";
    }

    private async Task SendEmailAsync(int accountId, RefinementSuggestion suggestion, List<ComponentFinding> findings, bool active, string prefix, RegimeCorrelationResult? regimeResult)
    {
        try
        {
            var mode = active ? "LIVE" : "SHADOW";
            var subject = $"{prefix}Strategy Refinement Suggestion ({mode}) — {suggestion.GeneratedAt:dd MMM yyyy}";
            var findingsMd = string.Join("\n", findings.Select(f =>
                $"- **{f.ComponentName}**: {f.CurrentWeight:F3} -> {f.SuggestedWeight:F3} ({f.WeightDelta:+0.000;-0.000}) — {f.Reasoning}"));

            var regimeMd = string.Empty;
            if (regimeResult is not null)
            {
                var lines = regimeResult.RegimeBreakdown.Select(kv =>
                {
                    var r = kv.Value;
                    return r.HasSufficientData
                        ? $"- **{r.Regime}** ({r.TradeCount} trades, SPY avg {r.AvgSpyReturn:+0.0;-0.0}%): win rate {r.WinRate:P0}, market-adjusted win rate n/a for group"
                        : $"- **{r.Regime}**: {r.InsufficientDataReason}";
                });
                regimeMd = $"## Market regime breakdown\n\n{string.Join("\n", lines)}\n\n" +
                           $"Market-adjusted win rate (vs holding SPY): {regimeResult.MarketAdjustedWinRate:P1}\n\n" +
                           (regimeResult.UnusualMarketConditions ? $"**Market condition note:** {regimeResult.MarketConditionWarning}\n\n" : string.Empty);
            }

            var md =
                $"# Strategy Refinement Suggestion ({mode})\n\n" +
                $"- Period: {suggestion.AnalysisPeriodStart} to {suggestion.AnalysisPeriodEnd}\n" +
                $"- Trades analysed: {suggestion.TradeCountAnalysed} (winners {suggestion.WinnerCount}, losers {suggestion.LoserCount})\n" +
                $"- Win rate: {suggestion.OverallWinRate:P1}\n" +
                $"- Confidence: {suggestion.ConfidenceLevel}\n\n" +
                $"## Component findings\n\n{findingsMd}\n\n" +
                regimeMd +
                (suggestion.AssessmentSummary is not null ? $"## Assessment\n\n{suggestion.AssessmentSummary}\n\n" : string.Empty) +
                (active
                    ? "Review and apply or reject this suggestion from the /refinement dashboard.\n"
                    : "This is a shadow-mode suggestion — no weights have been changed. Enable Refinement:Active to allow applying suggestions.\n");

            var toAddresses = (await recipients.ListAsync(accountId))
                .Where(r => r.Categories.HasFlag(NotificationCategory.MonthlySummary))
                .Select(r => r.Email)
                .ToList();

            if (toAddresses.Count > 0)
                await email.SendSimpleEmailAsync(toAddresses, md, subject);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send refinement suggestion email for account {AccountId}", accountId);
        }
    }
}
