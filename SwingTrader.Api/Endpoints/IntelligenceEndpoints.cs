using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SwingTrader.Agents.Filings;
using SwingTrader.Api.Services;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Configuration;

namespace SwingTrader.Api.Endpoints;

// The Intelligence page (docs/intelligence-page-plan): read-only surfaces for
// the shadow evidence that otherwise lives only in daily report emails or
// unrendered DB columns. Zero new data collection, zero Claude cost - every
// endpoint reads existing tables.
public static class IntelligenceEndpoints
{
    public static RouteGroupBuilder MapIntelligenceEndpoints(this RouteGroupBuilder api)
    {
        // Tab 1 - Funnel shadow: THE evidence Mark reviews before flipping
        // Research:FunnelEnabled. Aggregated stats + the divergence table +
        // veto candidates, over a selectable window.
        api.MapGet("/intelligence/funnel-shadow", async (
            int? days, ISignalRepository signals, IAccountContext ctx, CancellationToken ct) =>
        {
            var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-(days ?? 30)));
            var scored = (await signals.GetSinceDateAsync(ctx.AccountId, from))
                .Where(s => s.GateScore is not null)
                .OrderByDescending(s => s.SignalDate)
                .ToList();

            var divergent = scored
                .Where(s => (s.Recommendation == Recommendation.Buy) != (FunnelDecision(s) == "Buy"))
                .Select(s => new
                {
                    s.SignalDate,
                    s.Symbol,
                    s.CompanyName,
                    LegacyConviction = s.ConvictionScore,
                    s.GateScore,
                    s.ForwardScore,
                    LegacyDecision = s.Recommendation.ToString(),
                    GateDecision = FunnelDecision(s),
                })
                .ToList();

            var vetoCandidates = scored
                .Where(s => FunnelDecision(s) == "Buy" && s.WouldBeVetoed)
                .Select(s => new
                {
                    s.SignalDate,
                    s.Symbol,
                    s.CompanyName,
                    s.GateScore,
                    s.ForwardScore,
                    Sentiment = s.SentimentComponentScore,
                    Fundamentals = s.FundamentalMomentumScore,
                })
                .ToList();

            return Results.Ok(new
            {
                WindowDays = days ?? 30,
                Scored = scored.Count,
                LegacyBuys = scored.Count(s => s.Recommendation == Recommendation.Buy),
                GateWouldBuy = scored.Count(s => FunnelDecision(s) == "Buy"),
                DivergentCount = divergent.Count,
                WouldVetoCount = vetoCandidates.Count,
                Divergent = divergent,
                VetoCandidates = vetoCandidates,
            });
        });

        // Tab 2 - Filings: the FD1 audit trail. Negative deltas are the
        // actionable half for a long-only book (Lazy Prices: language-
        // changers underperform), so held/Buy/Watch symbols are flagged and
        // the UI defaults to Warnings ordering. Every row carries EDGAR
        // coordinates so verifying Claude's reading is one click.
        api.MapGet("/intelligence/filings", async (
            int? days,
            IFilingRepository filings,
            ITradeRepository trades,
            ISignalRepository signals,
            IAccountRepository accounts,
            IAccountContext ctx,
            // Explicit [FromServices]: IOptions<> is registered as an open
            // generic, which minimal APIs' service inference doesn't see - left
            // implicit it gets inferred as a body parameter and the app throws
            // at startup ("Body was inferred but the method does not allow
            // inferred body parameters") because this is a GET.
            [FromServices] IOptions<FilingDeltaConfig> filingCfg,
            CancellationToken ct) =>
        {
            var cfg = filingCfg.Value;
            var since = DateTime.UtcNow.AddDays(-(days ?? 90));
            var views = await filings.GetDeltaViewsSinceAsync(since, ct);
            var checkedCount = await filings.CountFilingsSinceAsync(since, ct);

            var account = await accounts.GetAsync(ctx.AccountId, ct);
            var heldSymbols = account is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : (await trades.GetOpenTradesAsync(ctx.AccountId, account.TradingMode))
                    .Select(t => t.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var activeToday = (await signals.GetByDateAsync(ctx.AccountId, today))
                .Where(s => s.Recommendation is Recommendation.Buy or Recommendation.Watch)
                .Select(s => s.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var rows = views
                .Where(v => v.Delta.Delta != 0m)
                .Select(v => new
                {
                    v.Delta.Symbol,
                    v.Delta.FiledAt,
                    v.FilingType,
                    v.Delta.Direction,
                    v.Delta.Materiality,
                    v.Delta.Delta,
                    Categories = v.Delta.Categories?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [],
                    v.Delta.Summary,
                    EffectiveScore = FilingDeltaMath.EffectiveScore(v.Delta.Delta, v.Delta.FiledAt, today, cfg.HalfLifeTradingDays),
                    IsHeld = heldSymbols.Contains(v.Delta.Symbol),
                    IsActiveToday = activeToday.Contains(v.Delta.Symbol),
                    // Same URL shape EdgarClient fetches: unpadded CIK,
                    // dash-less accession number.
                    EdgarUrl = $"https://www.sec.gov/Archives/edgar/data/{v.Cik.TrimStart('0')}/{v.AccessionNumber.Replace("-", "")}/{v.PrimaryDocument}",
                })
                .OrderBy(r => r.Delta) // most negative first - the Warnings default
                .ToList();

            return Results.Ok(new
            {
                WindowDays = days ?? 90,
                FilingsChecked = checkedCount,
                Changed = rows.Count,
                Unchanged = views.Count - rows.Count,
                Deltas = rows,
            });
        });

        // Tab 3 - Second-hop: recent transmissions with their provenance
        // summaries - the hallucination check happens by reading these.
        api.MapGet("/intelligence/second-hop", async (
            int? days, ISignalRepository signals, IAccountContext ctx, CancellationToken ct) =>
        {
            var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-(days ?? 14)));
            var rows = (await signals.GetSinceDateAsync(ctx.AccountId, from))
                .Where(s => s.SecondHopScore is not null)
                .OrderByDescending(s => Math.Abs(s.SecondHopScore!.Value))
                .ThenByDescending(s => s.SignalDate)
                .Select(s => new
                {
                    s.SignalDate,
                    s.Symbol,
                    s.CompanyName,
                    Score = s.SecondHopScore,
                    s.SecondHopSummary,
                })
                .ToList();

            return Results.Ok(new { WindowDays = days ?? 14, Transmissions = rows });
        });

        return api;
    }

    // What FunnelEnabled would ACTUALLY recommend for this signal - the raw
    // WouldPassGate threshold plus the overlay rules DetermineRecommendationAsync
    // applies regardless of which score feeds it. Without these the divergence
    // table overstates: e.g. a Breakout with gate 8.4 shows "gate says Buy" when
    // the flip would still cap it at Watch. Mirrors ResearchPipeline order:
    // held -> Hold, RSI>75 -> Avoid, Breakout cap -> Watch, then threshold.
    internal static string FunnelDecision(StockSignal s)
    {
        if (!s.WouldPassGate) return "No";
        // A gate-passing signal with a legacy Hold means the held-position
        // overlay fired (thresholds alone would have said Buy/Watch) - the
        // funnel would Hold it too.
        if (s.Recommendation == Recommendation.Hold) return "Hold";
        if (s.Rsi14 > 75) return "Avoid";
        if (s.SetupType == SetupType.Breakout) return "Watch";
        return "Buy";
    }
}
