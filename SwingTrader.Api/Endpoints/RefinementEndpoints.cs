using System.Text.Json;
using Microsoft.Extensions.Options;
using SwingTrader.Agents.Refinement;
using SwingTrader.Api.Contracts;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.Market;

namespace SwingTrader.Api.Endpoints;

public static class RefinementEndpoints
{
    public static RouteGroupBuilder MapRefinementEndpoints(this RouteGroupBuilder api)
    {
        // Regime is shared market data (cached globally in MarketRegimeService), but
        // still needs one account's Finnhub/Tiingo keys to fetch it with.
        api.MapGet("/refinement/current-regime", async (
            IMarketRegimeService regimeService,
            IUserHttpClientFactory clientFactory,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            var finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(ctx.AccountId, ct);
            var tiingo = await clientFactory.CreateTiingoAsync<ITiingoClient>(ctx.AccountId, ct);
            var result = await regimeService.GetCurrentRegimeAsync(tiingo, finnhub, ct);
            return Results.Ok(new { regime = result.Regime, detectedAt = DateTime.UtcNow });
        }).RequireRateLimiting(RateLimitPolicies.ExternalRead);

        api.MapGet("/refinement/status", async (
            IStrategyWeightsRepository weightsRepo,
            IRefinementSuggestionRepository suggestionRepo,
            IOptions<RefinementConfig> refinementConfig,
            ISignalRepository signalRepo,
            ITradeRepository tradeRepo,
            IAccountRepository accounts,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(ctx.AccountId, ct);
            if (account is null) return Results.NotFound();

            var activeWeights = await weightsRepo.GetActiveWeightsAsync(ctx.AccountId);
            var latest = await suggestionRepo.GetLatestAsync(ctx.AccountId, account.TradingMode);
            var history = (await suggestionRepo.GetHistoryAsync(ctx.AccountId, account.TradingMode)).ToList();

            // Mirrors RefinementService's own sample-size gate (closed trades with a
            // linked, scored signal) so the "progress toward next suggestion" bar
            // reflects the same count that would actually unblock a new one.
            var from = DateTime.UtcNow.AddDays(-refinementConfig.Value.AnalysisPeriodDays);
            var closed = (await tradeRepo.GetTradeHistoryAsync(ctx.AccountId, account.TradingMode, from, DateTime.UtcNow))
                .Where(t => t.Status != TradeStatus.Open && t.SignalId.HasValue);
            var tradesScoredSoFar = 0;
            foreach (var t in closed)
            {
                var signal = await signalRepo.GetByIdAsync(ctx.AccountId, t.SignalId!.Value);
                if (signal?.RsiScore is not null) tradesScoredSoFar++;
            }

            return Results.Ok(new
            {
                currentWeights = activeWeights is null ? new() : WeightsDict(activeWeights),
                latestSuggestion = latest is null ? null : MapSuggestion(latest),
                history = history.Select(MapSuggestion),
                minTradesRequired = refinementConfig.Value.MinCorrelationSampleSize,
                tradesScoredSoFar,
            });
        });

        api.MapPost("/refinement/apply", async (
            ApplyRefinementRequest req,
            IApplyRefinementService applyService,
            IAccountContext ctx) =>
        {
            var result = await applyService.ApplyAsync(ctx.AccountId, req.SuggestionId);
            return result.Success
                ? Results.Ok(new { success = true, message = "Applied" })
                : Results.BadRequest(new { success = false, message = result.Error });
        });

        api.MapPost("/refinement/reject", async (
            RejectRefinementRequest req,
            IApplyRefinementService applyService,
            IAccountContext ctx) =>
        {
            var result = await applyService.RejectAsync(ctx.AccountId, req.SuggestionId, req.Note);
            return result.Success ? Results.Ok() : Results.BadRequest(new { message = result.Error });
        });

        return api;
    }

    static Dictionary<string, decimal> WeightsDict(StrategyWeights w) => new()
    {
        ["rsi"] = w.RsiWeight,
        ["macd"] = w.MacdWeight,
        ["volume"] = w.VolumeWeight,
        ["sentiment"] = w.SentimentWeight,
        ["setupQuality"] = w.SetupQualityWeight,
        ["relativeStrength"] = w.RelativeStrengthWeight,
        ["priceLevel"] = w.PriceLevelWeight,
        ["fundamentalMomentum"] = w.FundamentalMomentumWeight,
    };

    static object MapSuggestion(RefinementSuggestion s)
    {
        var currentWeights = JsonSerializer.Deserialize<StrategyWeights>(s.CurrentWeightsJson);
        var suggestedWeights = JsonSerializer.Deserialize<StrategyWeights>(s.SuggestedWeightsJson);
        var findings = JsonSerializer.Deserialize<List<ComponentFinding>>(s.ComponentAnalysisJson) ?? [];

        return new
        {
            id = s.Id,
            generatedAt = s.GeneratedAt,
            analysisPeriodStart = s.AnalysisPeriodStart,
            analysisPeriodEnd = s.AnalysisPeriodEnd,
            tradeCountAnalysed = s.TradeCountAnalysed,
            winnerCount = s.WinnerCount,
            loserCount = s.LoserCount,
            overallWinRate = s.OverallWinRate,
            currentWeights = currentWeights is null ? new() : WeightsDict(currentWeights),
            suggestedWeights = suggestedWeights is null ? new() : WeightsDict(suggestedWeights),
            componentFindings = findings.Select(f => new
            {
                componentName = f.ComponentName,
                currentWeight = f.CurrentWeight,
                winnerAvgScore = f.WinnerAvgScore,
                loserAvgScore = f.LoserAvgScore,
                correlation = f.Correlation,
                suggestedWeight = f.SuggestedWeight,
                weightDelta = f.WeightDelta,
                reasoning = f.Reasoning,
            }),
            assessmentSummary = s.AssessmentSummary,
            confidenceLevel = s.ConfidenceLevel,
            status = s.Status,
            isShadowMode = s.IsShadowMode,
            marketAdjustedWinRate = s.MarketAdjustedWinRate,
            unusualMarketConditions = s.UnusualMarketConditions,
            marketConditionWarning = s.MarketConditionWarning,
        };
    }
}
