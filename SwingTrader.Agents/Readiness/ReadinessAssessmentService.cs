using Microsoft.Extensions.Options;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;

namespace SwingTrader.Agents.Readiness;

public class ReadinessAssessmentService(
    ITradeRepository tradeRepo,
    ISignalRepository signalRepo,
    IWorkerHeartbeatRepository heartbeatRepo,
    IApprovalRepository approvalRepo,
    IRefinementSuggestionRepository refinementSuggestionRepo,
    IReadinessSnapshotRepository snapshotRepo,
    IPortfolioRepository portfolioRepo,
    IAccountRepository accountRepo,
    IOptions<RefinementConfig> refinementConfig,
    IOptions<RiskManagementConfig> riskConfig) : IReadinessAssessmentService
{
    private const int RefinementMinTrades = 40;
    private const int RefinementMinDays = 30;
    private const int RefinementMinShadowCycles = 2;
    private const int RiskMgmtMinTrades = 30;
    private const decimal RiskMgmtMinWinRateCiLow = 0.40m;
    private const int LiveTradingMinDays = 14;
    private const int LiveTradingMinTrades = 20;
    private const int MediumTermMinTrades = 60;
    private const decimal MediumTermMinCapital = 500m;

    public async Task<ReadinessReport> AssessAsync(int accountId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var account = await accountRepo.GetAsync(accountId, ct)
            ?? throw new InvalidOperationException($"Account {accountId} not found.");
        var isLiveTrading = account.TradingMode == TradingMode.Live;

        // Step 1 — raw data. Heartbeats are process-wide (one Functions app
        // serving every account), not per-account.
        var heartbeats = (await heartbeatRepo.GetAllAsync()).ToList();
        var systemRunningDays = heartbeats.Count == 0
            ? 0
            : Math.Max(0, (int)(now - heartbeats.Min(h => h.CreatedAt)).TotalDays);

        var allSignals = (await signalRepo.GetAllAsync(accountId)).ToList();
        var totalSignalsGenerated = allSignals.Count;
        var signalsById = allSignals.ToDictionary(s => s.Id);

        var allTrades = (await tradeRepo.GetTradeHistoryAsync(accountId, account.TradingMode, DateTime.MinValue, now)).ToList();
        var closedTrades = allTrades.Where(t => t.Status != TradeStatus.Open).ToList();
        var totalClosedTrades = closedTrades.Count;

        var scoredClosedTrades = closedTrades.Count(t =>
            t.SignalId.HasValue && signalsById.TryGetValue(t.SignalId.Value, out var s) && s.RsiScore.HasValue);

        // Step 2 — win rate with Wilson interval
        var winners = closedTrades.Count(t => t.RealizedPnl > 0m);
        var observedRate = totalClosedTrades > 0 ? winners / (decimal)totalClosedTrades : 0m;
        var winRate = scoredClosedTrades < 10
            ? new WinRateAssessment(observedRate, null, null, false, "Too few trades for reliable estimate — need at least 10")
            : BuildWinRateAssessment(winners, totalClosedTrades);

        // Step 3 — trade rate (weighted)
        var recentTrades = closedTrades.Count(t => t.ClosedAt >= now.AddDays(-14));
        var recentPerWeek = recentTrades / 2.0m;
        var historicalPerWeek = totalClosedTrades / Math.Max(systemRunningDays / 7.0m, 1.0m);
        var weightedPerWeek = (recentPerWeek * 0.7m) + (historicalPerWeek * 0.3m);
        var hasReliableRate = weightedPerWeek >= 0.5m && systemRunningDays >= 14;
        var tradeRate = new TradeRateAssessment(weightedPerWeek, historicalPerWeek, recentPerWeek, hasReliableRate);

        // Step 4 — regime trade counts, all regimes present even at zero
        var regimeTradeCount = closedTrades
            .Where(t => t.MarketRegimeAtEntry.HasValue)
            .GroupBy(t => t.MarketRegimeAtEntry!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var regime in Enum.GetValues<MarketRegime>())
            if (!regimeTradeCount.ContainsKey(regime))
                regimeTradeCount[regime] = 0;

        // Step 5 — shadow suggestion count
        var suggestionHistory = (await refinementSuggestionRepo.GetHistoryAsync(accountId, account.TradingMode, 1000)).ToList();
        var shadowSuggestions = suggestionHistory.Count(s => s.IsShadowMode && s.Status != RefinementStatus.Superseded);

        var noFailedHeartbeatIn7Days = !heartbeats.Any(h =>
            h.LastRunResult == "Failed" && h.LastHeartbeatAt >= now.AddDays(-7));
        // PositionExitService.ClosePositionAsync (the only code path that
        // actually closes a position) always writes TradeStatus.Closed
        // regardless of exit reason - StoppedOut/TargetHit are unused values
        // nothing ever sets, so checking for them here could never be true
        // even after Monitor had genuinely closed several positions.
        var monitorClosedAtLeastOnePosition = closedTrades.Any(t => t.Status == TradeStatus.Closed);
        var approvalFlowTested = await approvalRepo.AnyApprovedAsync(accountId, account.TradingMode);

        var latestPortfolio = await portfolioRepo.GetLatestSnapshotAsync(accountId, account.TradingMode);

        // Step 6 — assess each feature
        var features = new List<FeatureReadiness>
        {
            BuildRefinementFeature(scoredClosedTrades, systemRunningDays, shadowSuggestions, tradeRate, now),
            BuildRiskManagementFeature(totalClosedTrades, winRate, now),
            BuildLiveTradingFeature(systemRunningDays, noFailedHeartbeatIn7Days, monitorClosedAtLeastOnePosition,
                approvalFlowTested, totalClosedTrades, tradeRate, now, isLiveTrading),
        };
        features.AddRange(BuildRegimeFeatures(regimeTradeCount, refinementConfig.Value, tradeRate, now));
        features.Add(BuildTopMoversFeature());
        features.Add(BuildMediumTermFeature(isLiveTrading, totalClosedTrades, latestPortfolio));

        var overallMaturity = scoredClosedTrades switch
        {
            >= 100 => DataMaturityLevel.Mature,
            >= 60 => DataMaturityLevel.Established,
            >= 30 => DataMaturityLevel.Developing,
            _ => DataMaturityLevel.EarlyStage
        };

        // Step 7/8 — milestones + trajectory
        var milestones = BuildMilestones(features, regimeTradeCount, tradeRate, now);
        var trajectory = await snapshotRepo.GetRecentAsync(accountId, account.TradingMode, 30);

        return new ReadinessReport(
            now, overallMaturity, systemRunningDays, totalSignalsGenerated, totalClosedTrades, scoredClosedTrades,
            winRate, tradeRate, regimeTradeCount, features, milestones, trajectory);
    }

    private static WinRateAssessment BuildWinRateAssessment(int wins, int total)
    {
        var observed = total > 0 ? wins / (decimal)total : 0m;
        var (low, high) = WilsonScoreInterval.Calculate(wins, total, 0.90m);
        return new WinRateAssessment(observed, low, high, true, $"{observed:P0} (90% CI: {low:P0}–{high:P0})");
    }

    private static (DateTime? Low, DateTime? High) EstimateDateRange(int itemsNeeded, TradeRateAssessment rate, DateTime now)
    {
        if (itemsNeeded <= 0) return (now, now);
        if (!rate.HasReliableEstimate) return (null, null);

        var weeksNeeded = (double)itemsNeeded / (double)rate.WeightedTradesPerWeek;
        var low = now.AddDays(weeksNeeded * 0.8 * 7);
        var high = now.AddDays(weeksNeeded * 1.3 * 7);
        return (low, high);
    }

    private FeatureReadiness BuildRefinementFeature(
        int scoredClosedTrades, int systemRunningDays, int shadowSuggestions, TradeRateAssessment tradeRate, DateTime now)
    {
        var criteria = new List<ReadinessCriteria>
        {
            new("At least 40 scored closed trades", scoredClosedTrades >= RefinementMinTrades,
                scoredClosedTrades.ToString(), RefinementMinTrades.ToString(),
                "Correlation analysis on fewer than 40 trades reflects noise rather than signal. Weight changes based on 20 trades could make the strategy worse."),
            new("System running 30+ days", systemRunningDays >= RefinementMinDays,
                systemRunningDays.ToString(), RefinementMinDays.ToString(),
                "The system needs time across varied market days, not just a concentration of activity in one period."),
            new("At least 2 shadow refinement cycles", shadowSuggestions >= RefinementMinShadowCycles,
                shadowSuggestions.ToString(), RefinementMinShadowCycles.ToString(),
                "Two shadow cycles lets you check whether suggestions are consistent (real signal) or changing direction each month (noise).")
        };

        var allMet = criteria.All(c => c.Met);
        var status = refinementConfig.Value.Active
            ? ReadinessStatus.AlreadyEnabled
            : allMet ? ReadinessStatus.Ready
            : scoredClosedTrades >= 0.7m * RefinementMinTrades ? ReadinessStatus.Approaching
            : ReadinessStatus.NotReady;

        var (low, high) = EstimateDateRange(Math.Max(0, RefinementMinTrades - scoredClosedTrades), tradeRate, now);

        var assessment = status switch
        {
            ReadinessStatus.AlreadyEnabled => "Refinement is currently active.",
            ReadinessStatus.Ready => "Sufficient trade history exists for the refinement engine to produce reliable weight suggestions. Review the shadow analyses before activating to confirm the suggested changes align with your observations.",
            _ => $"The refinement engine needs more closed trade history before its weight suggestions are statistically meaningful. Currently {scoredClosedTrades} of {RefinementMinTrades} required scored trades."
        };

        return new FeatureReadiness(
            "Refinement Agent", "Refinement:Active", "true", status, assessment, null, criteria,
            low, high, refinementConfig.Value.Active, FeatureRiskLevel.Medium);
    }

    private FeatureReadiness BuildRiskManagementFeature(int totalClosedTrades, WinRateAssessment winRate, DateTime now)
    {
        var ciLowMet = winRate.ConfidenceLow.HasValue && winRate.ConfidenceLow.Value >= RiskMgmtMinWinRateCiLow;
        var criteria = new List<ReadinessCriteria>
        {
            new("At least 30 closed trades", totalClosedTrades >= RiskMgmtMinTrades,
                totalClosedTrades.ToString(), RiskMgmtMinTrades.ToString(),
                "The tier evaluation needs enough trades to assess win rate reliably — not just a lucky streak."),
            new("Win rate has sufficient sample", winRate.HasSufficientData,
                winRate.HasSufficientData.ToString(), "true",
                "Win rate below 10 scored trades is too noisy to evaluate against the 55% threshold."),
            new("Win rate confidence lower bound >= 40%", ciLowMet,
                winRate.ConfidenceLow?.ToString("P0") ?? "n/a", "40%",
                "The lower bound of the confidence interval being above 40% means we can be reasonably confident the true win rate is not below breakeven, even accounting for sample variance.")
        };

        var allMet = criteria.All(c => c.Met);
        var status = riskConfig.Value.Active
            ? ReadinessStatus.AlreadyEnabled
            : allMet ? ReadinessStatus.Ready
            : totalClosedTrades >= 20 && winRate.HasSufficientData ? ReadinessStatus.Approaching
            : ReadinessStatus.NotReady;

        var assessment = status switch
        {
            ReadinessStatus.AlreadyEnabled => "Risk management is currently active.",
            ReadinessStatus.Ready => "Enough trade history exists to trust the tier evaluation's win-rate assessment.",
            _ => $"Risk management needs more closed trades and a tighter win-rate confidence interval. Currently {totalClosedTrades} of {RiskMgmtMinTrades} required trades."
        };

        return new FeatureReadiness(
            "Risk Management", "RiskManagement:Active", "true", status, assessment, null, criteria,
            null, null, riskConfig.Value.Active, FeatureRiskLevel.Medium);
    }

    private static FeatureReadiness BuildLiveTradingFeature(
        int systemRunningDays, bool noFailedHeartbeatIn7Days, bool monitorClosedAtLeastOnePosition,
        bool approvalFlowTested, int totalClosedTrades, TradeRateAssessment tradeRate,
        DateTime now, bool isLiveTrading)
    {
        var criteria = new List<ReadinessCriteria>
        {
            new("System running 14+ days", systemRunningDays >= LiveTradingMinDays,
                systemRunningDays.ToString(), LiveTradingMinDays.ToString(),
                "Two weeks of unattended operation confirms the scheduling, API connections, and worker stability are reliable."),
            new("No failed worker heartbeats in the last 7 days", noFailedHeartbeatIn7Days,
                noFailedHeartbeatIn7Days.ToString(), "true",
                "Recent failures suggest the system isn't stable enough to trust with real money."),
            new("Monitor Agent has closed at least one position", monitorClosedAtLeastOnePosition,
                monitorClosedAtLeastOnePosition.ToString(), "true",
                "The Monitor Agent must have demonstrated it can close positions. An untested exit mechanism is a real risk."),
            new("Approval flow tested at least once", approvalFlowTested,
                approvalFlowTested.ToString(), "true",
                "The approval link must have been clicked and confirmed working before real money depends on it."),
            new("At least 20 closed demo trades", totalClosedTrades >= LiveTradingMinTrades,
                totalClosedTrades.ToString(), LiveTradingMinTrades.ToString(),
                "20 demo trades gives a basic read on whether the system is selecting and exiting positions correctly before real capital is involved.")
        };

        var allMet = criteria.All(c => c.Met);
        var status = isLiveTrading
            ? ReadinessStatus.AlreadyEnabled
            : allMet ? ReadinessStatus.Ready
            : systemRunningDays >= 7 && totalClosedTrades >= 10 ? ReadinessStatus.Approaching
            : ReadinessStatus.NotReady;

        var (low, high) = EstimateDateRange(Math.Max(0, LiveTradingMinTrades - totalClosedTrades), tradeRate, now);

        var assessment = status switch
        {
            ReadinessStatus.AlreadyEnabled => "Live trading is currently active.",
            ReadinessStatus.Ready => "All readiness criteria are met. Complete the live trading confirmation flow when you're ready — the config change and restart remain a deliberate manual step.",
            _ => "The system needs more unattended operating time and demonstrated stability before live trading is worth considering."
        };

        return new FeatureReadiness(
            "Live Trading", null, null, status, assessment, null, criteria,
            low, high, isLiveTrading, FeatureRiskLevel.High);
    }

    private static List<FeatureReadiness> BuildRegimeFeatures(
        Dictionary<MarketRegime, int> regimeTradeCount, RefinementConfig cfg, TradeRateAssessment tradeRate, DateTime now)
    {
        var results = new List<FeatureReadiness>();
        // Crisis is deliberately excluded entirely — it will almost never accumulate enough
        // trades, and showing 0/N there is misleading rather than informative.
        foreach (var regime in new[] { MarketRegime.Bull, MarketRegime.Neutral, MarketRegime.Bear })
        {
            var count = regimeTradeCount.GetValueOrDefault(regime, 0);
            var met = count >= cfg.MinRegimeSampleSize && cfg.Active;
            var criteria = new List<ReadinessCriteria>
            {
                new($"{regime} regime has {cfg.MinRegimeSampleSize}+ trades", count >= cfg.MinRegimeSampleSize,
                    count.ToString(), cfg.MinRegimeSampleSize.ToString(), null),
                new("General refinement is active", cfg.Active, cfg.Active.ToString(), "true",
                    "Regime analysis only makes sense once general refinement is already running — it extends that analysis.")
            };

            var status = cfg.RegimeAnalysisEnabled
                ? ReadinessStatus.AlreadyEnabled
                : met ? ReadinessStatus.Ready
                : ReadinessStatus.NotReady;

            string assessment;
            DateTime? low = null, high = null;
            if (regime == MarketRegime.Bear && !met)
            {
                assessment = "Bear regime data depends on market conditions — may take 12+ months or never reach threshold in a stable market. General weights cover bear conditions in the meantime. This is acceptable.";
            }
            else if (!met)
            {
                assessment = $"{count}/{cfg.MinRegimeSampleSize} trades in {regime} conditions so far.";
                (low, high) = EstimateDateRange(Math.Max(0, cfg.MinRegimeSampleSize - count), tradeRate, now);
            }
            else
            {
                assessment = $"{regime} has enough trades for regime-specific weight suggestions.";
            }

            results.Add(new FeatureReadiness(
                $"Regime Analysis — {regime}", "Refinement:RegimeAnalysisEnabled", "true", status, assessment, null,
                criteria, low, high, cfg.RegimeAnalysisEnabled, FeatureRiskLevel.Medium, regime));
        }
        return results;
    }

    private static FeatureReadiness BuildTopMoversFeature() => new(
        "Top Movers Watchlist", "Watchlists page — per-watchlist toggle", "Enabled", ReadinessStatus.NoDataRequirement,
        "Purely additive — expands the candidate pool without changing how signals are scored or traded. Toggle per-account from the default watchlist's card on the Watchlists page.",
        null, [], null, null, false, FeatureRiskLevel.Low);

    private static FeatureReadiness BuildMediumTermFeature(bool isLiveTrading, int totalClosedTrades, PortfolioSnapshot? latestPortfolio)
    {
        var capital = latestPortfolio?.TotalCapital ?? 0m;
        var criteria = new List<ReadinessCriteria>
        {
            new("Live trading active", isLiveTrading, isLiveTrading.ToString(), "true", null),
            new("At least 60 closed trades", totalClosedTrades >= MediumTermMinTrades,
                totalClosedTrades.ToString(), MediumTermMinTrades.ToString(), null),
            new("Total capital >= £500", capital >= MediumTermMinCapital,
                $"£{capital:F0}", "£500", null)
        };

        return new FeatureReadiness(
            "Phase 8 — Medium Term Strategy", null, null, ReadinessStatus.NotReady,
            "Phase 8 adds a parallel medium-term strategy (1-3 month holds) alongside the existing swing trades. It makes sense once the short-term strategy is validated and capital has grown enough to split meaningfully.",
            "Requires a code build, not a config toggle — shown as a future milestone only.",
            criteria, null, null, false, FeatureRiskLevel.High);
    }

    private static List<DataMilestone> BuildMilestones(
        List<FeatureReadiness> features, Dictionary<MarketRegime, int> regimeTradeCount, TradeRateAssessment tradeRate, DateTime now)
    {
        string FormatRange(DateTime? low, DateTime? high) =>
            low.HasValue && high.HasValue
                ? $"~{low.Value.AddDays(((high.Value - low.Value).TotalDays) / 2):d MMM yyyy} (range: {low:d MMM}–{high:d MMM yyyy})"
                : "Cannot estimate — trade frequency too low or system too new";

        var milestones = new List<DataMilestone>();

        DataMilestone MilestoneFor(string title, string description, FeatureReadiness? feature)
        {
            if (feature is null || feature.Status == ReadinessStatus.AlreadyEnabled)
                return new DataMilestone(title, description, now, "Already active", MilestoneStatus.Completed);
            return new DataMilestone(title, description, feature.EstimatedReadyDateLow,
                FormatRange(feature.EstimatedReadyDateLow, feature.EstimatedReadyDateHigh), MilestoneStatus.Estimated);
        }

        milestones.Add(MilestoneFor("Risk Management activatable", "Enough trades to trust the win-rate evaluation.",
            features.FirstOrDefault(f => f.FeatureName == "Risk Management")));
        milestones.Add(MilestoneFor("Refinement engine activatable", "Enough scored trades and shadow cycles.",
            features.FirstOrDefault(f => f.FeatureName == "Refinement Agent")));
        milestones.Add(MilestoneFor("Live trading ready", "All live-trading criteria met.",
            features.FirstOrDefault(f => f.FeatureName == "Live Trading")));
        milestones.Add(MilestoneFor("Bull regime analysis ready", "Enough Bull-regime trades for specific weights.",
            features.FirstOrDefault(f => f.FeatureName == "Regime Analysis — Bull")));
        milestones.Add(MilestoneFor("Neutral regime analysis ready", "Enough Neutral-regime trades for specific weights.",
            features.FirstOrDefault(f => f.FeatureName == "Regime Analysis — Neutral")));
        milestones.Add(new DataMilestone("Bear regime analysis ready",
            "Enough Bear-regime trades for specific weights.", null,
            "Market-dependent — may take 12+ months or longer", MilestoneStatus.MarketDependent));

        var mediumTerm = features.FirstOrDefault(f => f.FeatureName == "Phase 8 — Medium Term Strategy");
        milestones.Add(new DataMilestone("Medium term strategy viable",
            mediumTerm?.Assessment ?? "Requires code build.", null, null, MilestoneStatus.RequiresCode));

        return milestones;
    }
}
