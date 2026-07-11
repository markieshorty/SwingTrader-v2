using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Core.Enums;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.Fundamental;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.RateLimiting;

namespace SwingTrader.Agents.Research;

public class FundamentalScoringService(
    IOptions<ClaudeConfig> claudeConfig,
    IOptions<FundamentalConfig> fundamentalConfig,
    IClaudeRateLimiter claudeRateLimiter,
    ILogger<FundamentalScoringService> logger) : IFundamentalScoringService
{
    public async Task<FundamentalScore> ScoreAsync(IClaudeClient claude, string symbol, FundamentalSnapshot snapshot, CancellationToken ct)
    {
        // Score is always the deterministic rule-based calculation — Claude only supplies the
        // human-readable narrative. Keeping the number deterministic means the Refinement
        // Agent can reliably correlate FundamentalMomentumScore with trade outcomes; a score
        // that varied with Claude's interpretation would lose statistical interpretability.
        var score = CalculateScore(snapshot, fundamentalConfig.Value);
        var reasoning = await GetNarrativeAsync(claude, symbol, snapshot, score, ct);
        return new FundamentalScore(score, reasoning);
    }

    private static decimal CalculateScore(FundamentalSnapshot s, FundamentalConfig cfg)
    {
        var analystScore = s.AnalystTrend switch
        {
            AnalystTrend.StronglyBullish => 1.00m,
            AnalystTrend.Bullish => 0.75m,
            AnalystTrend.Neutral => 0.50m,
            AnalystTrend.Bearish => 0.25m,
            AnalystTrend.StronglyBearish => 0.00m,
            _ => 0.50m,
        };

        var insiderScore = s.InsiderActivity switch
        {
            InsiderActivity.StrongBuying => 1.00m,
            InsiderActivity.Buying => 0.75m,
            InsiderActivity.Neutral => 0.50m,
            InsiderActivity.ClusterSelling => 0.15m,
            _ => 0.50m,
        };

        var earningsScore = s.EarningsConsistency switch
        {
            EarningsConsistency.ConsistentBeater => 1.00m,
            EarningsConsistency.RecentBeater => 0.75m,
            EarningsConsistency.Mixed => 0.50m,
            EarningsConsistency.RecentMiss => 0.25m,
            EarningsConsistency.ConsistentMisser => 0.00m,
            _ => 0.50m,
        };

        // Surprise-acceleration tilt: beats getting bigger nudge the earnings
        // sub-score up, shrinking beats nudge it down. A ±20pp trend
        // saturates the (config-bounded) adjustment, so the trend can tilt
        // but never override the beat-count tier. Still fully deterministic.
        if (s.SurpriseTrendPct is { } trend)
        {
            var tilt = Math.Clamp(trend / 20m, -1m, 1m) * cfg.SurpriseAccelerationMaxAdjust;
            earningsScore = Math.Clamp(earningsScore + tilt, 0m, 1m);
        }

        var revenueScore = s.RevenueDirection switch
        {
            RevenueDirection.Accelerating => 1.00m,
            RevenueDirection.Stable => 0.50m,
            RevenueDirection.Decelerating => 0.00m,
            _ => 0.50m,
        };

        return (analystScore * cfg.AnalystSubWeight) +
               (insiderScore * cfg.InsiderSubWeight) +
               (earningsScore * cfg.EarningsSubWeight) +
               (revenueScore * cfg.RevenueSubWeight);
    }

    private async Task<string> GetNarrativeAsync(IClaudeClient claude, string symbol, FundamentalSnapshot s, decimal score, CancellationToken ct)
    {
        try
        {
            var systemPrompt =
                "You are a financial analyst writing a brief fundamental assessment for a systematic trading system. " +
                "Write exactly one sentence. Maximum 30 words. Be specific about what the data shows. " +
                "State whether the fundamental picture supports, contradicts, or is neutral toward a swing trade entry. " +
                "Return only the sentence. No preamble. No punctuation at the end other than a full stop.";

            var userPrompt =
                $"Symbol: {symbol}\n" +
                $"Analyst consensus: {s.AnalystTrend} ({s.AnalystCount} analysts, revision velocity over 3 months)\n" +
                $"Insider activity (90 days): {s.InsiderActivity} ({s.InsiderBuyerCount} buyers, {s.InsiderSellerCount} sellers" +
                (s.InsiderMspr is { } mspr ? $", MSPR {mspr:+0.0;-0.0}" : "") + ")\n" +
                $"Earnings track record (4 quarters): {s.EarningsConsistency}" +
                (s.SurpriseTrendPct is { } trend ? $" (surprise trend {trend:+0.0;-0.0}pp)" : "") + "\n" +
                $"Revenue estimate direction: {s.RevenueDirection}\n" +
                $"Fundamental score: {score:F2}/1.0\n\n" +
                "Write one sentence assessing whether this fundamental picture supports a swing trade entry right now.";

            var request = new ClaudeRequest(
                claudeConfig.Value.Model,
                claudeConfig.Value.MaxTokens,
                systemPrompt,
                [new ClaudeMessage("user", userPrompt)]);

            await claudeRateLimiter.WaitAsync(ct);
            var response = await claude.SendMessageAsync(request);
            var text = response.Content.FirstOrDefault(c => c.Type == "text")?.Text;

            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("Claude returned no text content");

            return text.Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fundamental narrative Claude call failed for {Symbol} — using template fallback", symbol);
            return BuildFallbackReasoning(s);
        }
    }

    private static string BuildFallbackReasoning(FundamentalSnapshot s)
    {
        var parts = new List<string>();

        if (s.AnalystTrend != AnalystTrend.Insufficient)
            parts.Add($"analyst consensus is {s.AnalystTrend.ToString().Replace("Strongly", "strongly ").ToLowerInvariant()}");

        if (s.InsiderActivity != InsiderActivity.Neutral)
            parts.Add($"insider activity shows {s.InsiderActivity.ToString().Replace("Strong", "strong ").Replace("Cluster", "cluster ").ToLowerInvariant()}");

        if (s.EarningsConsistency != EarningsConsistency.Mixed && s.EarningsConsistency != EarningsConsistency.Insufficient)
            parts.Add($"earnings track record is {s.EarningsConsistency.ToString().Replace("Consistent", "consistently ").Replace("Recent", "recently ").ToLowerInvariant()}");

        if (s.RevenueDirection != RevenueDirection.Stable && s.RevenueDirection != RevenueDirection.Insufficient)
            parts.Add($"revenue estimates are {s.RevenueDirection.ToString().ToLowerInvariant()}");

        return parts.Count > 0
            ? $"Fundamentally: {string.Join(", ", parts)}."
            : "Insufficient fundamental data available for this symbol.";
    }
}
