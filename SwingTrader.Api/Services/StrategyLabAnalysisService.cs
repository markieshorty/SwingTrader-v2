using System.Text.Json;
using Microsoft.Extensions.Options;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Api.Contracts;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;

namespace SwingTrader.Api.Services;

// "Analyse this run": sends a completed Lab run to Claude for a plain-English
// read and an optional next-config-worth-testing. Advisory only - the shared
// prompt contract (LabAnalysisPrompts) forbids causal certainty and demands
// fragility call-outs; the suggestion is a hypothesis the user must simulate
// themselves before believing.
public class StrategyLabAnalysisService(
    IUserHttpClientFactory clientFactory,
    IBacktestRunRepository runs,
    IOptions<ClaudeConfig> claudeConfig,
    ILogger<StrategyLabAnalysisService> logger)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task<(LabAnalyseResponse? Response, string? Error)> AnalyseAsync(
        int accountId, LabAnalyseRequest req, CancellationToken ct)
    {
        var weights = new HistoricBacktestWeights(
            req.Weights.Rsi, req.Weights.Macd, req.Weights.Volume, req.Weights.Sentiment,
            req.Weights.SetupQuality, req.Weights.RelativeStrength, req.Weights.PriceLevel, req.Weights.FundamentalMomentum);

        string userPrompt;
        if (req.DataSource.Equals("historic", StringComparison.OrdinalIgnoreCase))
        {
            if (req.BacktestRunId is not { } runId)
                return (null, "backtestRunId is required for historic analysis.");
            var run = await runs.GetByIdAsync(accountId, runId);
            if (run?.ResultJson is null || run.Status != "Completed")
                return (null, "That backtest run has no completed result to analyse.");

            using (var doc = JsonDocument.Parse(run.ResultJson))
            {
                if (doc.RootElement.TryGetProperty("mode", out _))
                    return (null, "Comparison and sweep runs carry their own explanation — per-run analysis applies to single simulations.");
            }

            var result = JsonSerializer.Deserialize<HistoricResult>(run.ResultJson, Web);
            if (result is null) return (null, "Stored result could not be read.");
            userPrompt = LabAnalysisPrompts.BuildHistoricRunPrompt(weights, req.BuyThreshold, req.ExcludeBreakout, result);
        }
        else
        {
            if (req.OwnResult is not { } own)
                return (null, "ownResult is required for own-data analysis.");
            userPrompt = LabAnalysisPrompts.BuildOwnDataRunPrompt(
                weights, req.BuyThreshold, req.ExcludeBreakout,
                own.TotalClosedTrades, own.TradesKept, own.DroppedWinners, own.DroppedLosers,
                own.ActualAvgReturnPct, own.SimAvgReturnPct, own.ActualWinRate, own.SimWinRate);
        }

        try
        {
            var claude = await clientFactory.CreateClaudeAsync<IClaudeClient>(accountId, ct);
            var cfg = claudeConfig.Value;
            var response = await claude.SendMessageAsync(new ClaudeRequest(
                cfg.RefinementModel ?? cfg.Model, cfg.MaxTokens,
                LabAnalysisPrompts.SystemPrompt,
                [new ClaudeMessage("user", userPrompt)]));

            var raw = response.Content.FirstOrDefault(c => c.Type == "text")?.Text;
            if (string.IsNullOrWhiteSpace(raw))
                return (null, "The analysis service returned an empty response — try again.");

            var (analysis, suggested) = LabAnalysisPrompts.ParseResponse(raw);
            var suggestion = suggested is null
                ? null
                : new LabAnalyseSuggestion(
                    suggested.Rationale,
                    new LabWeights(
                        suggested.Weights.Rsi, suggested.Weights.Macd, suggested.Weights.Volume, suggested.Weights.Sentiment,
                        suggested.Weights.SetupQuality, suggested.Weights.RelativeStrength, suggested.Weights.PriceLevel,
                        suggested.Weights.FundamentalMomentum),
                    suggested.BuyThreshold, suggested.ExcludeBreakout);

            return (new LabAnalyseResponse(analysis, suggestion), null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Lab analysis Claude call failed for account {AccountId}", accountId);
            return (null, "The analysis service is unavailable right now — the simulation result itself is unaffected. Try again shortly.");
        }
    }
}
