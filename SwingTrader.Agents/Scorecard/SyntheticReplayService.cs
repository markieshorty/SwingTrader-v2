using Microsoft.Extensions.Logging;
using SwingTrader.Agents.Research;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Agents.Scorecard;

public sealed record SyntheticReplayResult(
    string DialSetVersion, int DatasetVersion,
    int SymbolsProcessed, int SymbolsSkipped, int SignalsFound, int Written, string Summary);

public interface ISyntheticReplayService
{
    Task<SyntheticReplayResult> GenerateAsync(
        int accountId, DateOnly from, DateOnly? to = null,
        int? symbolLimit = null, CancellationToken ct = default);

    // Re-walk an existing population under different exit dials.
    Task<SyntheticReplayResult> ReplayVariantAsync(
        int accountId, string baselineVersion, SetupDials dials, string label,
        CancellationToken ct = default);

    // Sweep many dial sets over one population, reporting aggregates only.
    Task<List<VariantStats>> SweepAsync(
        string baselineVersion, IReadOnlyList<(string Label, SetupDials Dials)> variants,
        CancellationToken ct = default);
}

public sealed record VariantStats(
    string Label, int Closed, int StillOpen,
    decimal WinPct, decimal AvgWin, decimal AvgLoss, decimal ExpectancyPct,
    decimal ControlExpectancyPct, decimal SetupExpectancyPct);

// Runs setup detection back over the candle store and replays every signal it
// finds (docs/scoring-engine-plan SPEC §3, "synthetic population generator").
//
// WHY THIS EXISTS, and why replaying live signals was never enough: the live
// tables cover 5 Jul - 12 Aug 2026, five weeks. The calibration horizon is 40
// TRADING days, about eight weeks, so the first backfill produced 730 rows and
// ZERO complete horizons. Every downstream number - the per-setup calibration,
// the base-rate lift gate, the sector question - needs outcomes that exist, and
// the only place they exist is history.
//
// The store holds 2016-2026 across 2,671 symbols, 43% of them delisted. That
// delisted share is the point, not a defect: a replay that quietly drops them
// reproduces exactly the survivorship artefact that retired the loose setup
// (+1.64% on survivors against -0.19% over 409 trades on the full universe).
public class SyntheticReplayService(
    IShadowOutcomeRepository outcomes,
    IHistoricalCandleRepository candles,
    ISetupTacticsRepository setupTactics,
    IAccountRiskProfileRepository riskProfiles,
    IIndicatorService indicators,
    ILogger<SyntheticReplayService> logger) : ISyntheticReplayService
{
    // Indicators need the same warm-up the live screen gets from the blob store.
    private const int WarmupBars = 85;

    // Symbols per blob fetch. Small enough to keep the working set bounded -
    // the whole store is 4.8M bars and will not fit comfortably in memory.
    private const int SymbolChunk = 25;

    private const int WriteBatch = 500;

    // Sweeps many dial sets over one detected population WITHOUT persisting
    // anything. A 16-point grid would otherwise write half a million rows into a
    // database already at 1.6 GB of a 2 GB cap - and the rows would be
    // write-once-read-once, since only the aggregate matters when choosing
    // dials. Bars are loaded once per symbol and every variant walks them.
    public async Task<List<VariantStats>> SweepAsync(
        string baselineVersion, IReadOnlyList<(string Label, SetupDials Dials)> variants,
        CancellationToken ct = default)
    {
        var dataset = await candles.GetDatasetVersionAsync(ct);
        var baseline = await outcomes.GetForCalibrationAsync(baselineVersion, dataset, ct);
        if (baseline.Count == 0) return [];

        // label -> running totals. Control and setups tracked separately: the
        // question is never just "is this profitable" but "does it beat buying
        // on a random day", and one number cannot answer that.
        var acc = variants.ToDictionary(v => v.Label, _ => new Accumulator(), StringComparer.Ordinal);

        var bySymbol = baseline.GroupBy(o => o.Symbol, StringComparer.OrdinalIgnoreCase).ToList();
        var earliest = baseline.Min(o => o.SignalDate).AddDays(-10);

        foreach (var chunk in bySymbol.Chunk(SymbolChunk))
        {
            ct.ThrowIfCancellationRequested();
            var barsBySymbol = await candles.GetForSymbolsAsync(
                chunk.Select(g => g.Key).ToList(), earliest, ct);

            foreach (var group in chunk)
            {
                if (!barsBySymbol.TryGetValue(group.Key, out var bars) || bars.Count == 0) continue;

                foreach (var row in group)
                {
                    var isControl = row.SetupType == SetupType.Unknown;
                    foreach (var (label, d) in variants)
                    {
                        var walk = CounterfactualReplay.Run(
                            bars, row.SignalDate, d.StopLossPct, d.TargetPct,
                            d.GuideHoldDays, d.TrailingActivationPct, d.TrailingDistancePct);
                        acc[label].Add(walk, isControl);
                    }
                }
            }
        }

        return variants.Select(v => acc[v.Label].ToStats(v.Label)).ToList();
    }

    private sealed class Accumulator
    {
        private int _closed, _open, _wins;
        private decimal _winSum, _lossSum, _all;
        private int _controlN, _setupN;
        private decimal _controlSum, _setupSum;

        public void Add(CounterfactualReplay.Outcome? o, bool isControl)
        {
            // StillOpen never reached an exit, so its number is a
            // mark-to-market rather than a result - counted, not scored.
            if (o is null || o.StillOpen) { _open++; return; }

            _closed++;
            _all += o.ReturnPct;
            if (o.ReturnPct > 0) { _wins++; _winSum += o.ReturnPct; } else { _lossSum += o.ReturnPct; }
            if (isControl) { _controlN++; _controlSum += o.ReturnPct; }
            else { _setupN++; _setupSum += o.ReturnPct; }
        }

        public VariantStats ToStats(string label)
        {
            static decimal Div(decimal a, int b) => b == 0 ? 0m : Math.Round(a / b, 2);
            return new VariantStats(
                label, _closed, _open,
                _closed == 0 ? 0m : Math.Round(100m * _wins / _closed, 2),
                Div(_winSum, _wins), Div(_lossSum, _closed - _wins), Div(_all, _closed),
                Div(_controlSum, _controlN), Div(_setupSum, _setupN));
        }
    }

    // Re-runs the exit walk over an ALREADY-DETECTED population under new dials.
    //
    // Detection is the expensive half (an indicator calculation per symbol-day)
    // and it does not depend on the exit rules at all, so a dial sweep has no
    // business repeating it. The rule-free path statistics are copied across
    // untouched for the same reason: they are properties of the price, and a
    // different stop cannot change what the price did.
    //
    // Each variant lands under its own DialSetVersion, so variants never mix.
    public async Task<SyntheticReplayResult> ReplayVariantAsync(
        int accountId, string baselineVersion, SetupDials dials, string label,
        CancellationToken ct = default)
    {
        var dataset = await candles.GetDatasetVersionAsync(ct);
        var baseline = await outcomes.GetForCalibrationAsync(baselineVersion, dataset, ct);
        if (baseline.Count == 0)
        {
            return new SyntheticReplayResult(label, dataset, 0, 0, 0, 0,
                $"No baseline rows for {baselineVersion} / dataset {dataset}.");
        }

        var bySymbol = baseline.GroupBy(o => o.Symbol, StringComparer.OrdinalIgnoreCase).ToList();
        var earliest = baseline.Min(o => o.SignalDate).AddDays(-10);

        int written = 0;
        var batch = new List<ShadowOutcome>(WriteBatch);

        foreach (var chunk in bySymbol.Chunk(SymbolChunk))
        {
            ct.ThrowIfCancellationRequested();
            var barsBySymbol = await candles.GetForSymbolsAsync(
                chunk.Select(g => g.Key).ToList(), earliest, ct);

            foreach (var group in chunk)
            {
                if (!barsBySymbol.TryGetValue(group.Key, out var bars) || bars.Count == 0) continue;

                foreach (var row in group)
                {
                    var walk = CounterfactualReplay.Run(
                        bars, row.SignalDate, dials.StopLossPct, dials.TargetPct,
                        dials.GuideHoldDays, dials.TrailingActivationPct, dials.TrailingDistancePct);

                    batch.Add(new ShadowOutcome
                    {
                        AccountId = accountId,
                        Source = ShadowSource.Synthetic,
                        Symbol = row.Symbol,
                        SignalDate = row.SignalDate,
                        SetupType = row.SetupType,
                        Membership = row.Membership,
                        DialSetVersion = label,
                        DatasetVersion = dataset,
                        StopLossPct = dials.StopLossPct,
                        TargetPct = dials.TargetPct,
                        GuideHoldDays = dials.GuideHoldDays,
                        TrailingActivationPct = dials.TrailingActivationPct,
                        TrailingDistancePct = dials.TrailingDistancePct,
                        EntryDate = row.EntryDate,
                        EntryPrice = row.EntryPrice,
                        ExitDate = walk?.ExitDate,
                        ExitReason = walk?.ExitReason,
                        ReturnPct = walk?.ReturnPct,
                        TradingDaysHeld = walk?.TradingDaysHeld,
                        StillOpen = walk?.StillOpen ?? false,
                        // Rule-free: unchanged by definition.
                        Fwd5Pct = row.Fwd5Pct,
                        Fwd20Pct = row.Fwd20Pct,
                        Fwd40Pct = row.Fwd40Pct,
                        MaxFavorablePct = row.MaxFavorablePct,
                        MaxAdversePct = row.MaxAdversePct,
                        HitPlus25Within40 = row.HitPlus25Within40,
                        HitMinus25Within40 = row.HitMinus25Within40,
                        SectorFwd40Pct = row.SectorFwd40Pct,
                        SectorMoveAtSignalPct = row.SectorMoveAtSignalPct,
                    });

                    if (batch.Count >= WriteBatch)
                    {
                        written += await outcomes.UpsertRangeAsync(batch, ct);
                        batch.Clear();
                    }
                }
            }
        }

        if (batch.Count > 0) written += await outcomes.UpsertRangeAsync(batch, ct);

        var summary = $"Variant [{label} / dataset {dataset}] from {baselineVersion}: " +
                      $"{baseline.Count} baseline rows, {written} re-walked.";
        logger.LogInformation("{Summary}", summary);
        return new SyntheticReplayResult(label, dataset, bySymbol.Count, 0, baseline.Count, written, summary);
    }

    public async Task<SyntheticReplayResult> GenerateAsync(
        int accountId, DateOnly from, DateOnly? to = null,
        int? symbolLimit = null, CancellationToken ct = default)
    {
        var profile = await riskProfiles.GetAsync(accountId, ct);
        var tactics = await setupTactics.GetAllAsync(accountId, ct);
        var dials = DialSet.FromAccount(tactics, profile);
        var dataset = await candles.GetDatasetVersionAsync(ct);

        var latestDates = await candles.GetLatestDatesAsync(ct);
        var storeMax = latestDates.Count > 0 ? latestDates.Values.Max() : from;

        // A signal needs a complete 40-bar forward window or its rule-free
        // statistics are null and it contributes nothing to the calibration.
        // ~58 calendar days covers 40 trading days with room for holidays.
        var lastUsable = to ?? storeMax.AddDays(-58);

        var symbols = latestDates.Keys
            .Where(s => !s.Equals("SPY", StringComparison.OrdinalIgnoreCase)
                        && !s.Equals("VIX", StringComparison.OrdinalIgnoreCase)
                        && !SectorEtfMap.AllEtfs().Contains(s, StringComparer.OrdinalIgnoreCase))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Take(symbolLimit ?? int.MaxValue)
            .ToList();

        // Sector ETFs once, with a real lookback so the at-signal sector move is
        // computable rather than null.
        //
        // SPY must be in this list. SectorEtfMap.GetEtf() falls back to SPY for
        // anything it cannot map, but AllEtfs() does not include it - so the
        // first run resolved 98.7% of symbols to a benchmark whose bars were
        // never loaded, and wrote null into every sector column.
        var benchmarks = SectorEtfMap.AllEtfs()
            .Append("SPY")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var etfBars = await candles.GetForSymbolsAsync(benchmarks, from.AddDays(-120), ct);

        int processed = 0, skipped = 0, found = 0, written = 0;
        var batch = new List<ShadowOutcome>(WriteBatch);

        foreach (var chunk in symbols.Chunk(SymbolChunk))
        {
            ct.ThrowIfCancellationRequested();

            // Fetch with warm-up room: detection at `from` reads 85 bars back.
            var barsBySymbol = await candles.GetForSymbolsAsync(chunk, from.AddDays(-150), ct);

            foreach (var symbol in chunk)
            {
                if (!barsBySymbol.TryGetValue(symbol, out var bars) || bars.Count <= WarmupBars)
                {
                    skipped++;
                    continue;
                }
                processed++;

                // Reused across every date for this symbol; the indicator call
                // wants CandleData, the detector wants StockCandle.
                for (var i = WarmupBars; i < bars.Count; i++)
                {
                    var date = bars[i].Date;
                    if (date < from || date > lastUsable) continue;

                    var window = bars.GetRange(i - WarmupBars + 1, WarmupBars);
                    var ind = indicators.Calculate(window.Select(b =>
                        new CandleData(b.Date.ToDateTime(TimeOnly.MinValue),
                            b.Open, b.High, b.Low, b.Close, (long)b.Volume)).ToList());

                    var setup = SetupDetector.Detect(ind, window.Select(b => new StockCandle
                    {
                        Symbol = symbol, Timestamp = b.Date.ToDateTime(TimeOnly.MinValue),
                        Open = b.Open, High = b.High, Low = b.Low, Close = b.Close, Volume = (long)b.Volume,
                    }).ToList());

                    // TrendFollowing is excluded because it is being demoted to a
                    // context factor (SPEC D5) - it fires on a state rather than
                    // an event, so including it would add hundreds of thousands
                    // of rows describing the same ongoing fact over and over.
                    if (setup is SetupType.TrendFollowing) continue;

                    // A CONTROL COHORT, stored as SetupType.Unknown.
                    //
                    // Without it the base rates are uninterpretable. The first
                    // run showed every setup hitting +25% far more often than
                    // the 7.37% figure quoted from an earlier session - but that
                    // was measured on a different definition, over a different
                    // period, on a different universe. A "2x lift" against an
                    // incomparable number is not evidence.
                    //
                    // The control is a random sample of ordinary days on the
                    // same symbols, same window, same 40-bar horizon, same
                    // intraday-touch definition. Whatever a setup's hit rate
                    // means, it means it relative to this.
                    if (setup is SetupType.Unknown)
                    {
                        // ~1 in 40 non-setup days. Enough for a tight interval
                        // without swamping the setups it exists to calibrate.
                        if (!IsControlSample(symbol, date)) continue;
                    }
                    found++;

                    var d = dials.For(setup);
                    var walk = CounterfactualReplay.Run(
                        bars, date, d.StopLossPct, d.TargetPct, d.GuideHoldDays,
                        d.TrailingActivationPct, d.TrailingDistancePct);
                    var path = ForwardPathStats.Compute(bars, date);
                    if (path is null) continue;

                    batch.Add(new ShadowOutcome
                    {
                        AccountId = accountId,
                        Source = ShadowSource.Synthetic,
                        SignalId = null,
                        Symbol = symbol,
                        SignalDate = date,
                        SetupType = setup,
                        Membership = null, // graded membership arrives with SPEC P1
                        DialSetVersion = dials.Version,
                        DatasetVersion = dataset,
                        StopLossPct = d.StopLossPct,
                        TargetPct = d.TargetPct,
                        GuideHoldDays = d.GuideHoldDays,
                        TrailingActivationPct = d.TrailingActivationPct,
                        TrailingDistancePct = d.TrailingDistancePct,
                        EntryDate = path.EntryDate,
                        EntryPrice = path.EntryPrice,
                        ExitDate = walk?.ExitDate,
                        ExitReason = walk?.ExitReason,
                        ReturnPct = walk?.ReturnPct,
                        TradingDaysHeld = walk?.TradingDaysHeld,
                        StillOpen = walk?.StillOpen ?? false,
                        Fwd5Pct = path.Fwd5Pct,
                        Fwd20Pct = path.Fwd20Pct,
                        Fwd40Pct = path.Fwd40Pct,
                        MaxFavorablePct = path.MaxFavorablePct,
                        MaxAdversePct = path.MaxAdversePct,
                        HitPlus25Within40 = path.HitPlus25Within40,
                        HitMinus25Within40 = path.HitMinus25Within40,
                        SectorFwd40Pct = SectorForward(symbol, date),
                        SectorMoveAtSignalPct = SectorMoveAtSignal(symbol, date),
                    });

                    if (batch.Count >= WriteBatch)
                    {
                        written += await outcomes.UpsertRangeAsync(batch, ct);
                        batch.Clear();
                    }
                }
            }

            logger.LogInformation(
                "Synthetic replay: {Processed}/{Total} symbols, {Found} signals, {Written} written",
                processed, symbols.Count, found, written);
        }

        if (batch.Count > 0) written += await outcomes.UpsertRangeAsync(batch, ct);

        var summary =
            $"Synthetic replay [{dials.Version} / dataset {dataset}] {from:yyyy-MM-dd}..{lastUsable:yyyy-MM-dd}: " +
            $"{processed} symbols processed, {skipped} skipped (insufficient bars), " +
            $"{found} signals detected, {written} outcomes written.";
        logger.LogInformation("{Summary}", summary);

        return new SyntheticReplayResult(
            dials.Version, dataset, processed, skipped, found, written, summary);

        // Deterministic 1-in-40 sample. Deliberately NOT Random: a re-run must
        // select the same control days, or the upsert writes a different cohort
        // each time and the control silently drifts under the comparison.
        static bool IsControlSample(string symbol, DateOnly date)
        {
            var h = 17;
            foreach (var c in symbol) h = h * 31 + c;
            h = h * 31 + date.DayNumber;
            return (uint)h % 40 == 0;
        }

        decimal? SectorForward(string symbol, DateOnly date)
        {
            var etf = SectorEtfMap.GetEtf(symbol);
            return etfBars.TryGetValue(etf, out var eb) && eb.Count > 0
                ? ForwardPathStats.Compute(eb, date)?.Fwd40Pct
                : null;
        }

        decimal? SectorMoveAtSignal(string symbol, DateOnly date)
        {
            var etf = SectorEtfMap.GetEtf(symbol);
            if (!etfBars.TryGetValue(etf, out var eb) || eb.Count == 0) return null;

            // Strictly bars at or before the signal - no lookahead.
            var idx = eb.FindLastIndex(b => b.Date <= date);
            if (idx < 5) return null;

            var prior = eb[idx - 5].Close;
            return prior > 0 ? Math.Round((eb[idx].Close - prior) / prior * 100m, 4) : null;
        }
    }
}
