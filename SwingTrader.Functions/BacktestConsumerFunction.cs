using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Functions;

// Runs a Strategy Lab historic-market simulation: loads the shared
// HistoricalCandles dataset, replays the requested dials through the shared
// HistoricBacktester engine, and stores the result on the BacktestRun row the
// UI is polling. Deliberately does NOT rethrow on simulation failure - the
// error lands on the run row for the user; redelivering a broken request
// would just fail again.
public class BacktestConsumerFunction(
    IBacktestRunRepository runs,
    IHistoricalCandleRepository candles,
    ILogger<BacktestConsumerFunction> logger)
{
    // Must match the API's camelCase JSON output - this string gets embedded
    // verbatim into the poll response, so it can't be re-cased downstream.
    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

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

            var weights = new StrategyWeights
            {
                RsiWeight = request.Weights.Rsi, MacdWeight = request.Weights.Macd,
                VolumeWeight = request.Weights.Volume, SentimentWeight = request.Weights.Sentiment,
                SetupQualityWeight = request.Weights.SetupQuality, RelativeStrengthWeight = request.Weights.RelativeStrength,
                PriceLevelWeight = request.Weights.PriceLevel, FundamentalMomentumWeight = request.Weights.FundamentalMomentum,
            };

            var result = await HistoricBacktester.RunAsync(
                bars, new HistoricConfig(weights, request.BuyThreshold, request.ExcludeBreakout), ct);

            // Trade log stays out of the stored JSON - it can be thousands of
            // rows; the headline stats + buckets are what the UI shows.
            run.ResultJson = JsonSerializer.Serialize(result with { TradeLog = [] }, CamelCase);
            run.Status = "Completed";
            run.CompletedAt = DateTime.UtcNow;
            await runs.UpdateAsync(run);
            logger.LogInformation("Backtest run {RunId} completed: {Trades} trades, {Return}% total", run.Id, result.Trades, result.TotalReturnPct);
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
}
