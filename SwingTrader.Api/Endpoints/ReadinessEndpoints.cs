using Microsoft.Extensions.Options;
using SwingTrader.Agents.Readiness;
using SwingTrader.Api.Contracts;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;

namespace SwingTrader.Api.Endpoints;

public static class ReadinessEndpoints
{
    public static RouteGroupBuilder MapReadinessEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/readiness", async (
            IReadinessAssessmentService readiness,
            IOptions<RefinementConfig> refinementConfig,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            var report = await readiness.AssessAsync(ctx.AccountId, ct);
            var minRegimeSample = Math.Max(1, refinementConfig.Value.MinRegimeSampleSize);

            int RegimeProgress(MarketRegime regime) =>
                Math.Min(100, report.RegimeTradeCount.GetValueOrDefault(regime, 0) * 100 / minRegimeSample);

            string? FormatRange(DateTime? low, DateTime? high) =>
                low.HasValue && high.HasValue
                    ? $"~{low.Value.AddDays((high.Value - low.Value).TotalDays / 2):d MMM yyyy} (range: {low:d MMM}–{high:d MMM yyyy})"
                    : null;

            var features = report.Features.Select(f => new
            {
                featureName = f.FeatureName,
                status = f.Status,
                riskLevel = f.RiskLevel,
                criteria = f.Criteria.Select(c => new { label = c.Description, met = c.Met }),
                assessment = f.Assessment,
                estimatedReadyDateRange = FormatRange(f.EstimatedReadyDateLow, f.EstimatedReadyDateHigh),
                actionHint = f.Recommendation ?? string.Empty,
            });

            // Snapshots are daily; collapse to one representative point per ISO week
            // for the trajectory chart, since that's the cadence the frontend expects.
            var weeklySnapshots = report.TrajectoryHistory
                .OrderBy(s => s.SnapshotDate)
                .GroupBy(s => (s.SnapshotDate.Year, System.Globalization.ISOWeek.GetWeekOfYear(s.SnapshotDate.ToDateTime(TimeOnly.MinValue))))
                .Select(g => g.Last())
                .ToList();

            var trajectory = new List<object>();
            ReadinessSnapshot? prevSnap = null;
            foreach (var snap in weeklySnapshots)
            {
                var weekStart = snap.SnapshotDate.AddDays(-(int)snap.SnapshotDate.DayOfWeek + (snap.SnapshotDate.DayOfWeek == DayOfWeek.Sunday ? -6 : 1));
                var tradeCount = prevSnap is null ? snap.ScoredClosedTrades : snap.ScoredClosedTrades - prevSnap.ScoredClosedTrades;
                var speed = prevSnap is null ? "Flat"
                    : snap.ScoredClosedTrades > prevSnap.ScoredClosedTrades ? "Up"
                    : snap.ScoredClosedTrades < prevSnap.ScoredClosedTrades ? "Down" : "Flat";
                trajectory.Add(new { weekStarting = weekStart, tradeCount, winRate = snap.ObservedWinRate, speedIndicator = speed });
                prevSnap = snap;
            }

            var milestones = report.UpcomingMilestones.Select(m => new
            {
                label = m.Title,
                estimatedDateRange = m.EstimatedDateRange,
                completed = m.Status == MilestoneStatus.Completed,
                status = m.Status,
            });

            return Results.Ok(new
            {
                maturityLevel = report.OverallMaturity,
                scoredClosedTrades = report.ScoredClosedTrades,
                observedWinRate = report.WinRate.ObservedRate,
                winRateConfidenceIntervalLow = report.WinRate.ConfidenceLow,
                winRateConfidenceIntervalHigh = report.WinRate.ConfidenceHigh,
                features,
                regimeBullProgress = RegimeProgress(MarketRegime.Bull),
                regimeNeutralProgress = RegimeProgress(MarketRegime.Neutral),
                regimeBearProgress = RegimeProgress(MarketRegime.Bear),
                trajectory,
                milestones,
            });
        });

        api.MapPost("/readiness/complete-checklist", async (
            CompleteChecklistRequest req,
            ISystemChecklistRepository checklist,
            IAccountContext ctx) =>
        {
            await checklist.CompleteAsync(ctx.AccountId, req.CheckName, req.Notes);
            return Results.Ok();
        });

        return api;
    }
}
