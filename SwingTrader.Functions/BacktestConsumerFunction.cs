using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;

namespace SwingTrader.Functions;

// Runs Strategy Lab historic-market simulations: loads the shared
// HistoricalCandles dataset once, then depending on the request mode runs a
// single config, an A/B pair (user dials vs the production baseline snapshot),
// or an optimizer sweep (candidates evaluated on a train window, winner
// validated out-of-sample). Stores the result on the BacktestRun row the UI
// polls. Deliberately does NOT rethrow on simulation failure - the error lands
// on the run row for the user; redelivering a broken request would just fail
// again.
public class BacktestConsumerFunction(
    IBacktestRunRepository runs,
    IHistoricalCandleRepository candles,
    IAccountRiskProfileRepository riskProfileRepo,
    IUserHttpClientFactory clientFactory,
    IOptions<ClaudeConfig> claudeConfig,
    ILogger<BacktestConsumerFunction> logger)
{
    // Must match the API's camelCase JSON output - this string gets embedded
    // verbatim into the poll response, so it can't be re-cased downstream.
    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

    private const int EngineWarmupBars = 60; // keep in sync with HistoricBacktester.WarmupBars

    [Function("BacktestConsumer")]
    public async Task Run(
        [ServiceBusTrigger("backtest-jobs", Connection = "ServiceBusConnection")] string messageBody,
        CancellationToken ct)
    {
        var message = JsonSerializer.Deserialize<BacktestJobMessage>(messageBody)!;
        var run = await runs.GetByIdAsync(message.AccountId, message.BacktestRunId);
        if (run is null)
        {
            logger.LogWarning("Backtest run {RunId} not found for account {AccountId} — dropping", message.BacktestRunId, message.AccountId);
            return;
        }
        if (run.Status is "Completed" or "Failed") return; // redelivery of a finished run

        run.Status = "Running";
        run.StartedAt = DateTime.UtcNow;
        await runs.UpdateAsync(run);

        try
        {
            var request = JsonSerializer.Deserialize<HistoricBacktestRequest>(run.RequestJson)
                ?? throw new InvalidOperationException("Unreadable backtest request.");

            var bySymbol = await candles.GetAllBySymbolAsync(ct);
            if (bySymbol.Count == 0)
                throw new InvalidOperationException("No historic market data synced yet — run a candle sync first.");

            var bars = bySymbol.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(c => new DailyBar(
                    c.Date.ToDateTime(TimeOnly.MinValue), c.Open, c.High, c.Low, c.Close, c.Volume)).ToArray(),
                StringComparer.OrdinalIgnoreCase);

            // The engine mirrors the account's live risk settings (hold cap,
            // position slots, bear-market entry pause) so the Lab tests the
            // strategy the account actually runs, not a hardcoded variant.
            var profile = await riskProfileRepo.GetAsync(message.AccountId, ct);

            run.ResultJson = request.Mode switch
            {
                "ab" => await RunAbAsync(request, bars, profile, ct),
                "sweep" => await RunSweepAsync(message.AccountId, request, bars, profile, ct),
                _ => await RunSingleAsync(request, bars, profile, ct),
            };
            run.Status = "Completed";
            run.CompletedAt = DateTime.UtcNow;
            await runs.UpdateAsync(run);
            logger.LogInformation("Backtest run {RunId} ({Mode}) completed", run.Id, request.Mode ?? "single");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Backtest run {RunId} failed", run.Id);
            run.Status = "Failed";
            run.Error = ex.Message;
            run.CompletedAt = DateTime.UtcNow;
            await runs.UpdateAsync(run);
        }
    }

    private static HistoricConfig ToConfig(
        HistoricBacktestWeights w, decimal buyThreshold, bool excludeBreakout, AccountRiskProfile profile) =>
        new(new StrategyWeights
        {
            RsiWeight = w.Rsi, MacdWeight = w.Macd, VolumeWeight = w.Volume, SentimentWeight = w.Sentiment,
            SetupQualityWeight = w.SetupQuality, RelativeStrengthWeight = w.RelativeStrength,
            PriceLevelWeight = w.PriceLevel, FundamentalMomentumWeight = w.FundamentalMomentum,
        }, buyThreshold, excludeBreakout,
        // SPY-below-200dma entry pause approximates the live bear autopause.
        RegimeFilter: profile.AutopauseDuringBear,
        MaxOpenPositions: profile.MaxOpenPositions,
        MaxHoldDays: profile.MaxHoldDays);

    private static async Task<string> RunSingleAsync(
        HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars, AccountRiskProfile profile, CancellationToken ct)
    {
        var result = await HistoricBacktester.RunAsync(
            bars, ToConfig(request.Weights, request.BuyThreshold, request.ExcludeBreakout, profile), ct);
        // Trade log stays out of the stored JSON - it can be thousands of
        // rows; the headline stats + buckets are what the UI shows.
        return JsonSerializer.Serialize(result with { TradeLog = [] }, CamelCase);
    }

    // A/B: both configs over the identical full window. Candidates carry their
    // labels ("Your dials" / "Production baseline") from queue time.
    private static async Task<string> RunAbAsync(
        HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars, AccountRiskProfile profile, CancellationToken ct)
    {
        var candidates = request.Candidates
            ?? throw new InvalidOperationException("A/B request carries no candidates.");

        var results = new List<object>();
        foreach (var c in candidates)
        {
            var r = await HistoricBacktester.RunAsync(bars, ToConfig(c.Weights, c.BuyThreshold, c.ExcludeBreakout, profile), ct);
            results.Add(new
            {
                label = c.Label,
                weights = c.Weights,
                buyThreshold = c.BuyThreshold,
                excludeBreakout = c.ExcludeBreakout,
                result = r with { TradeLog = [] },
            });
        }
        return JsonSerializer.Serialize(new { mode = "ab", candidates = results }, CamelCase);
    }

    // Sweep: candidates generated around the baseline, evaluated on the train
    // window (earlier ~70%), best eligible one validated on the held-out
    // remainder it never saw. Claude explanation is best-effort - a missing
    // writeup never fails the sweep.
    private async Task<string> RunSweepAsync(
        int accountId, HistoricBacktestRequest request, Dictionary<string, DailyBar[]> bars, AccountRiskProfile profile, CancellationToken ct)
    {
        var baseline = request.Candidates?.FirstOrDefault()
            ?? throw new InvalidOperationException("Sweep request carries no baseline candidate.");

        var candidates = SweepOptimizer.GenerateCandidates(baseline);
        var (train, holdout) = SweepOptimizer.SplitBars(bars, EngineWarmupBars);
        var trainSpy = train["SPY"];
        var holdoutSpy = holdout["SPY"];

        // Baseline first - its drawdown sets the ceiling for everyone else.
        var baselineTrain = await HistoricBacktester.RunAsync(
            train, ToConfig(baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout, profile), ct);
        var baselineSummary = SweepOptimizer.Summarise(candidates[0], baselineTrain, trainSpy, baselineTrain.MaxDrawdownPct);

        var summaries = new List<SweepCandidateResult> { baselineSummary };
        var trainResults = new Dictionary<string, HistoricResult> { [baselineSummary.Label] = baselineTrain };
        foreach (var c in candidates.Skip(1))
        {
            ct.ThrowIfCancellationRequested();
            var r = await HistoricBacktester.RunAsync(train, ToConfig(c.Weights, c.BuyThreshold, c.ExcludeBreakout, profile), ct);
            summaries.Add(SweepOptimizer.Summarise(c, r, trainSpy, baselineTrain.MaxDrawdownPct));
            trainResults[c.Label] = r;
            logger.LogInformation("Sweep candidate '{Label}': {Trades} trades, {Adj}% adjusted expectancy", c.Label, r.Trades, summaries[^1].AdjustedExpectancyPct);
        }

        var winnerSummary = summaries
            .Where(s => s.MetConstraints)
            .OrderByDescending(s => s.AdjustedExpectancyPct)
            .FirstOrDefault()
            ?? baselineSummary; // nothing eligible - baseline "wins" by default

        // Out-of-sample validation: winner and baseline on the held-out window.
        var winnerHoldout = await HistoricBacktester.RunAsync(
            holdout, ToConfig(winnerSummary.Weights, winnerSummary.BuyThreshold, winnerSummary.ExcludeBreakout, profile), ct);
        var baselineHoldout = winnerSummary.Label == baselineSummary.Label
            ? winnerHoldout
            : await HistoricBacktester.RunAsync(
                holdout, ToConfig(baseline.Weights, baseline.BuyThreshold, baseline.ExcludeBreakout, profile), ct);

        var validation = SweepOptimizer.BuildValidation(
            trainResults[winnerSummary.Label], winnerHoldout, baselineHoldout, trainSpy, holdoutSpy);

        var explanation = await TryExplainAsync(accountId, baselineSummary, winnerSummary, validation, summaries, ct);

        var sweep = new SweepResult("sweep", baselineSummary, winnerSummary, validation, summaries, explanation);
        return JsonSerializer.Serialize(sweep, CamelCase);
    }

    private async Task<string?> TryExplainAsync(
        int accountId, SweepCandidateResult baseline, SweepCandidateResult winner,
        SweepValidation validation, List<SweepCandidateResult> candidates, CancellationToken ct)
    {
        try
        {
            var claude = await clientFactory.CreateClaudeAsync<IClaudeClient>(accountId, ct);
            var cfg = claudeConfig.Value;
            var response = await claude.SendMessageAsync(new ClaudeRequest(
                cfg.RefinementModel ?? cfg.Model, cfg.MaxTokens,
                LabAnalysisPrompts.SystemPrompt,
                [new ClaudeMessage("user", LabAnalysisPrompts.BuildSweepExplanationPrompt(baseline, winner, validation, candidates))]));
            var raw = response.Content.FirstOrDefault(c => c.Type == "text")?.Text;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var (analysis, _) = LabAnalysisPrompts.ParseResponse(raw);
            return analysis;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Sweep explanation failed for account {AccountId} — result ships without a writeup", accountId);
            return null;
        }
    }
}
