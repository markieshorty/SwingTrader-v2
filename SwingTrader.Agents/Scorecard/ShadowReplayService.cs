using Microsoft.Extensions.Logging;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Agents.Scorecard;

public sealed record ShadowReplayResult(
    string DialSetVersion, int DatasetVersion,
    int Considered, int Replayed, int Skipped, int NoBars, string Summary);

public interface IShadowReplayService
{
    Task<ShadowReplayResult> ReplayLiveSignalsAsync(
        int accountId, DateOnly from, bool force = false, CancellationToken ct = default);
}

// P0 of the scoring engine rebuild (docs/scoring-engine-plan SPEC §3).
//
// The live tables hold ~2,499 scored signals and 27 outcomes, because only 27
// were ever filled. Every downstream piece - the per-setup calibration, the dial
// sweeps, and the pre-cutover validation gates - needs a population, not 27
// rows. This service builds it.
//
// This pass covers signals the live pipeline actually scored. The much larger
// synthetic population (running detection back over years of bars) builds on
// the same row shape and lands next.
public class ShadowReplayService(
    ISignalRepository signals,
    IShadowOutcomeRepository outcomes,
    IHistoricalCandleRepository candles,
    ISetupTacticsRepository setupTactics,
    IAccountRiskProfileRepository riskProfiles,
    ILogger<ShadowReplayService> logger) : IShadowReplayService
{
    // Bars are fetched per chunk of symbols rather than per symbol: the Basic
    // tier makes a whole-table load a 300s-timeout query, and one request per
    // symbol is thousands of round trips.
    private const int SymbolChunk = 60;

    public async Task<ShadowReplayResult> ReplayLiveSignalsAsync(
        int accountId, DateOnly from, bool force = false, CancellationToken ct = default)
    {
        var profile = await riskProfiles.GetAsync(accountId, ct);
        var tactics = await setupTactics.GetAllAsync(accountId, ct);
        var dials = DialSet.FromAccount(tactics, profile);
        var dataset = await candles.GetDatasetVersionAsync(ct);

        // One row per (symbol, date, setup) - the same signal scored on several
        // accounts is one market event, and replaying it four times would
        // quadruple its weight in the calibration.
        var all = (await signals.GetSinceDateAsync(accountId, from))
            .Where(s => s.SetupType != Core.Enums.SetupType.Unknown)
            .GroupBy(s => (s.Symbol, s.SignalDate, s.SetupType))
            .Select(g => g.First())
            .ToList();

        var stored = force
            ? []
            : await outcomes.GetStoredKeysAsync(dials.Version, dataset, ct);

        var todo = all
            .Where(s => force || !stored.Contains(
                $"{s.Symbol}|{s.SignalDate:yyyyMMdd}|{(int)s.SetupType}"))
            .ToList();

        int replayed = 0, noBars = 0;

        // Sector ETF bars, loaded once. The sector-relative factor is the
        // highest-value free input in the new engine, and Q7 - whether a
        // sector-wide dip is a BETTER or WORSE reversion candidate - has to be
        // answered from this column rather than from intuition. The measured
        // hint points against intuition: six of nine loose-setup losers were
        // semis or EV names in a sector drawdown, and they kept falling.
        var earliest = todo.Count > 0 ? todo.Min(s => s.SignalDate).AddDays(-10) : from;
        var etfBars = await candles.GetForSymbolsAsync(
            Infrastructure.Market.SectorEtfMap.AllEtfs().ToList(), earliest, ct);

        foreach (var chunk in todo.Select(s => s.Symbol).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Chunk(SymbolChunk))
        {
            ct.ThrowIfCancellationRequested();

            // Bars from well before the earliest signal: the replay walks
            // forward, but the 40-bar horizon needs room on the far side and the
            // entry bar itself must exist.
            var barsBySymbol = await candles.GetForSymbolsAsync(chunk, earliest, ct);

            var batch = new List<ShadowOutcome>();
            foreach (var s in todo.Where(s => chunk.Contains(s.Symbol, StringComparer.OrdinalIgnoreCase)))
            {
                if (!barsBySymbol.TryGetValue(s.Symbol, out var bars) || bars.Count == 0)
                {
                    noBars++;
                    continue;
                }

                var d = dials.For(s.SetupType);
                var walk = CounterfactualReplay.Run(
                    bars, s.SignalDate, d.StopLossPct, d.TargetPct, d.GuideHoldDays,
                    d.TrailingActivationPct, d.TrailingDistancePct);
                var path = ForwardPathStats.Compute(bars, s.SignalDate);

                // No entry bar at all: nothing to say about this signal, and a
                // row of nulls would dilute every statistic built on the table.
                if (walk is null && path is null) { noBars++; continue; }

                batch.Add(new ShadowOutcome
                {
                    AccountId = accountId,
                    Source = ShadowSource.Live,
                    SignalId = s.Id,
                    Symbol = s.Symbol,
                    SignalDate = s.SignalDate,
                    SetupType = s.SetupType,
                    // Null, not 1.0: these came from the old boolean detector,
                    // and inventing a graded value would fabricate precision the
                    // signal never had.
                    Membership = null,
                    DialSetVersion = dials.Version,
                    DatasetVersion = dataset,
                    StopLossPct = d.StopLossPct,
                    TargetPct = d.TargetPct,
                    GuideHoldDays = d.GuideHoldDays,
                    TrailingActivationPct = d.TrailingActivationPct,
                    TrailingDistancePct = d.TrailingDistancePct,
                    EntryDate = walk?.EntryDate ?? path?.EntryDate,
                    EntryPrice = path?.EntryPrice,
                    ExitDate = walk?.ExitDate,
                    ExitPrice = null,
                    ExitReason = walk?.ExitReason,
                    ReturnPct = walk?.ReturnPct,
                    TradingDaysHeld = walk?.TradingDaysHeld,
                    StillOpen = walk?.StillOpen ?? false,
                    Fwd5Pct = path?.Fwd5Pct,
                    Fwd20Pct = path?.Fwd20Pct,
                    Fwd40Pct = path?.Fwd40Pct,
                    MaxFavorablePct = path?.MaxFavorablePct,
                    MaxAdversePct = path?.MaxAdversePct,
                    HitPlus25Within40 = path?.HitPlus25Within40,
                    HitMinus25Within40 = path?.HitMinus25Within40,
                    SectorFwd40Pct = SectorForward(s.Symbol, s.SignalDate),
                    SectorMoveAtSignalPct = SectorMoveAtSignal(s.Symbol, s.SignalDate),
                });
            }

            if (batch.Count > 0)
            {
                replayed += await outcomes.UpsertRangeAsync(batch, ct);
            }
            continue;

            // The sector ETF's own forward move over the same horizon, so a
            // name's return can be read net of what its sector did anyway.
            decimal? SectorForward(string symbol, DateOnly signalDate)
            {
                var etf = Infrastructure.Market.SectorEtfMap.GetEtf(symbol);
                return etfBars.TryGetValue(etf, out var eb) && eb.Count > 0
                    ? ForwardPathStats.Compute(eb, signalDate)?.Fwd40Pct
                    : null;
            }

            // How far the sector had already fallen INTO the dip - the input to
            // "did this name fall alone, or with everything around it". Measured
            // over the same 5-bar lookback the dip-start dial defaults to, and
            // strictly on bars at or before the signal so it carries no
            // lookahead.
            decimal? SectorMoveAtSignal(string symbol, DateOnly signalDate)
            {
                var etf = Infrastructure.Market.SectorEtfMap.GetEtf(symbol);
                if (!etfBars.TryGetValue(etf, out var eb) || eb.Count == 0) return null;

                var upTo = eb.Where(b => b.Date <= signalDate).ToList();
                if (upTo.Count < 6) return null;

                var latest = upTo[^1].Close;
                var priorClose = upTo[^6].Close;
                return priorClose > 0
                    ? Math.Round((latest - priorClose) / priorClose * 100m, 4)
                    : null;
            }
        }

        var summary =
            $"Shadow replay [{dials.Version} / dataset {dataset}]: {all.Count} signals considered, " +
            $"{replayed} replayed, {all.Count - todo.Count} already stored, {noBars} without usable bars.";
        logger.LogInformation("{Summary}", summary);

        return new ShadowReplayResult(
            dials.Version, dataset, all.Count, replayed, all.Count - todo.Count, noBars, summary);
    }
}
