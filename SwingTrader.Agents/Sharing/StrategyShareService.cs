using System.Text.Json;
using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Agents.Refinement;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Sharing;

// Strategy sharing between account owners: snapshot builder, live-settings
// fingerprint (evidence gate), and the apply that overwrites a recipient's
// weights + risk books + setup tactics from a frozen snapshot. Watchlists are
// deliberately out of scope.
public class StrategyShareService(
    IStrategyWeightsRepository weightsRepo,
    IAccountRiskProfileRepository riskRepo,
    ISetupTacticsRepository tacticsRepo,
    IRefinementSuggestionRepository suggestionRepo,
    IApplyRefinementService applyService,
    IAccountRepository accountRepo,
    ILogger<StrategyShareService> logger) : IStrategyShareService
{
    // Storage convention: camelCase, same as SuggestedRiskRulesJson.
    public static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<StrategySnapshot> BuildSnapshotAsync(int accountId, CancellationToken ct = default)
    {
        var weights = await weightsRepo.GetActiveWeightsAsync(accountId)
            ?? throw new InvalidOperationException($"Account {accountId} has no active strategy weights.");
        var books = await riskRepo.GetAllAsync(accountId, ct);
        var tactics = await tacticsRepo.GetAllAsync(accountId, ct);

        return new StrategySnapshot(
            new SnapshotWeights(
                weights.RsiWeight, weights.MacdWeight, weights.VolumeWeight,
                weights.SetupQualityWeight, weights.RelativeStrengthWeight, weights.PriceLevelWeight,
                weights.ForwardSentimentWeight, weights.ForwardFundamentalWeight, weights.ForwardFilingWeight,
                weights.BuyThreshold, weights.WatchThreshold, weights.StopLossPctDefault),
            books.Select(b => new SnapshotRiskBook(
                b.Regime.ToString(), b.Enabled, b.AutopauseTrading,
                b.LockedCapitalPct, b.MaxOpenPositions, b.DailyLossCircuitBreakerPct,
                b.MaxHoldDays, b.TrailingActivationPct, b.TrailingDistancePct,
                b.EarningsGateDays, b.MinHoldDays, b.MomentumHealthThreshold,
                b.StopLossPct, b.TargetPct,
                b.SizingMode.ToString(), b.FlatPositionPct, b.SizingAggressiveness, b.ForwardVetoFloor)).ToList(),
            tactics.Select(t => new SnapshotSetupTactic(
                t.SetupType.ToString(), t.Enabled,
                t.StopLossPct, t.TargetPct, t.GuideHoldDays,
                t.TrailingActivationPct, t.TrailingDistancePct)).ToList());
    }

    // Mirrors EXACTLY how the backtest consumer resolves an untouched Lab
    // baseline (StrategyLabEndpoints.SnapshotBaselineAsync feeding
    // BacktestConfigFactory.ToConfig): same weights source, same
    // Default-vs-Neutral book choice, same disabled-setup exclusion path.
    // Any divergence here silently breaks the evidence gate, so both sides go
    // through the shared factory.
    public async Task<string> ComputeLiveFingerprintAsync(int accountId, CancellationToken ct = default)
    {
        var weights = await weightsRepo.GetActiveWeightsAsync(accountId)
            ?? throw new InvalidOperationException($"Account {accountId} has no active strategy weights.");

        var defaultOn = await riskRepo.IsDefaultRegimeEnabledAsync(accountId, ct);
        var profile = await riskRepo.GetAsync(accountId, defaultOn ? MarketRegime.Default : MarketRegime.Neutral, ct);
        var bearBook = await riskRepo.GetAsync(accountId, MarketRegime.Bear, ct);

        var tacticsMap = (await tacticsRepo.GetAllAsync(accountId, ct)).ToDictionary(
            t => t.SetupType,
            t => new HistoricSetupTactics(
                t.StopLossPct, t.TargetPct, t.GuideHoldDays,
                (decimal)t.TrailingActivationPct, (decimal)t.TrailingDistancePct));

        HistoricTradingRules? rules = null;
        var disabled = await tacticsRepo.GetDisabledSetupsAsync(accountId, ct);
        if (disabled.Count > 0)
            rules = new HistoricTradingRules(ExcludedSetups: disabled.Select(s => s.ToString()).ToList());

        var cfg = BacktestConfigFactory.ToConfig(
            new HistoricBacktestWeights(
                weights.RsiWeight, weights.MacdWeight, weights.VolumeWeight,
                weights.SetupQualityWeight, weights.RelativeStrengthWeight, weights.PriceLevelWeight),
            weights.BuyThreshold,
            excludeBreakout: false,
            autopauseDuringBear: bearBook.AutopauseTrading,
            profile, tacticsMap, rules);

        // Default book off = live trades the Mixed frame, so the fingerprint
        // must cover every regime book's exposure envelope - a Bull-book
        // sizing change invalidates evidence just like a weight change.
        if (!defaultOn)
        {
            var books = (await riskRepo.GetAllAsync(accountId, ct)).ToDictionary(b => b.Regime);
            cfg = BacktestConfigFactory.WithLiveRegimeBooks(cfg, books);
        }

        return ConfigFingerprint.Compute(cfg);
    }

    public async Task ApplySnapshotAsync(int accountId, StrategySnapshot snapshot, string sourceDescription, CancellationToken ct = default)
    {
        var account = await accountRepo.GetAsync(accountId, ct)
            ?? throw new InvalidOperationException($"Account {accountId} not found.");
        var current = await weightsRepo.GetActiveWeightsAsync(accountId);

        // 1. Weights - through the refinement audit trail so the change shows
        //    up on the Refinement page like every other production weight
        //    change, with its own SharedStrategy origin.
        var w = snapshot.Weights;
        var suggested = new StrategyWeights
        {
            AccountId = accountId,
            RsiWeight = w.RsiWeight, MacdWeight = w.MacdWeight, VolumeWeight = w.VolumeWeight,
            SetupQualityWeight = w.SetupQualityWeight, RelativeStrengthWeight = w.RelativeStrengthWeight,
            PriceLevelWeight = w.PriceLevelWeight,
            ForwardSentimentWeight = w.ForwardSentimentWeight,
            ForwardFundamentalWeight = w.ForwardFundamentalWeight,
            ForwardFilingWeight = w.ForwardFilingWeight,
            BuyThreshold = w.BuyThreshold, WatchThreshold = w.WatchThreshold,
            StopLossPctDefault = w.StopLossPctDefault,
        };

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var suggestion = await suggestionRepo.AddAsync(new RefinementSuggestion
        {
            AccountId = accountId,
            TradingMode = account.TradingMode,
            Origin = RefinementOrigin.SharedStrategy,
            GeneratedAt = DateTime.UtcNow,
            AnalysisPeriodStart = today,
            AnalysisPeriodEnd = today,
            CurrentWeightsJson = JsonSerializer.Serialize(current ?? suggested),
            SuggestedWeightsJson = JsonSerializer.Serialize(suggested),
            ComponentAnalysisJson = "[]",
            AssessmentSummary = sourceDescription,
            ConfidenceLevel = RefinementConfidenceLevel.Medium,
            Status = RefinementStatus.Pending,
            IsShadowMode = false,
        });

        var applied = await applyService.ApplyAsync(accountId, suggestion.Id, specificRegime: null, ct);
        if (!applied.Success)
            throw new InvalidOperationException($"Applying shared weights failed: {applied.Error}");

        // 2. Every regime risk book in the snapshot overwrites the matching
        //    book. Unknown regime names (post-rename snapshots) are skipped
        //    rather than failing the whole apply.
        foreach (var book in snapshot.RiskBooks)
        {
            if (!Enum.TryParse<MarketRegime>(book.Regime, ignoreCase: true, out var regime)) continue;
            var target = await riskRepo.GetAsync(accountId, regime, ct);
            target.Enabled = book.Enabled;
            target.AutopauseTrading = book.AutopauseTrading;
            target.LockedCapitalPct = book.LockedCapitalPct;
            target.MaxOpenPositions = book.MaxOpenPositions;
            target.DailyLossCircuitBreakerPct = book.DailyLossCircuitBreakerPct;
            target.MaxHoldDays = book.MaxHoldDays;
            target.TrailingActivationPct = book.TrailingActivationPct;
            target.TrailingDistancePct = book.TrailingDistancePct;
            target.EarningsGateDays = book.EarningsGateDays;
            target.MinHoldDays = book.MinHoldDays;
            target.MomentumHealthThreshold = book.MomentumHealthThreshold;
            target.StopLossPct = book.StopLossPct;
            target.TargetPct = book.TargetPct;
            if (Enum.TryParse<PositionSizingMode>(book.SizingMode, ignoreCase: true, out var mode))
                target.SizingMode = mode;
            target.FlatPositionPct = book.FlatPositionPct;
            target.SizingAggressiveness = book.SizingAggressiveness;
            target.ForwardVetoFloor = book.ForwardVetoFloor;
            await riskRepo.UpdateAsync(target, ct); // validates
        }

        // 3. Setup tactics, including the live on/off toggles.
        var rows = (await tacticsRepo.GetAllAsync(accountId, ct)).ToDictionary(r => r.SetupType);
        foreach (var tactic in snapshot.SetupTactics)
        {
            if (!Enum.TryParse<SetupType>(tactic.SetupType, ignoreCase: true, out var setup)) continue;
            if (!rows.TryGetValue(setup, out var row)) continue;
            row.Enabled = tactic.Enabled;
            row.StopLossPct = tactic.StopLossPct;
            row.TargetPct = tactic.TargetPct;
            row.GuideHoldDays = tactic.GuideHoldDays;
            row.TrailingActivationPct = tactic.TrailingActivationPct;
            row.TrailingDistancePct = tactic.TrailingDistancePct;
            await tacticsRepo.UpdateAsync(row, ct); // validates
        }

        logger.LogInformation(
            "Strategy snapshot applied to account {AccountId} ({Books} risk books, {Tactics} setup tactics): {Source}",
            accountId, snapshot.RiskBooks.Count, snapshot.SetupTactics.Count, sourceDescription);
    }
}
