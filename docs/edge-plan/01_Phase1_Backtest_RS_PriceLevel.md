# Phase 1 — Relative Strength + Price Level in the Backtester

**Status: Built 10 Jul 2026 — awaiting user verification (510 tests green).**
Notes from the build: WarmupBars is now `HistoricBacktester.WarmupBars = 85`
(single public constant, referenced by BacktestConsumerFunction and the console
BacktestEngine — the stale 60 copies are gone); the console engine was ported to
the shared calculators (RS neutral when sector-ETF CSVs absent from its data
dir); SPY + sector ETFs are excluded from backtest watchlists (benchmarks, not
trade candidates).

## ⚠ VERIFY results (resolved)

- ✅ Tiingo returns full daily bars incl. adjClose for all 6 ETFs (XLK, SMH, XLV,
  XLF, XLY, XLP) — verified with real API calls against the production key.
- ✅ Live stock candles store ADJUSTED values (`ResearchPipeline.cs:238-242` maps
  AdjOpen/High/Low/Close/Volume into StockCandle), matching `HistoricalCandle`
  (also adj per `CandleSyncService`). Both sides adjusted → parity holds.
- ✅ Decision: `HistoricBacktester.WarmupBars` raised 60 → 85 (live PriceLevel's
  120-calendar-day window ≈ 82–84 trading bars). Backtest windows start ~25 bars
  later than before; all previous absolute results are superseded — re-baseline
  after ship.

**Objective:** take `HistoricBacktester` from 4/8 to 6/8 live conviction components
by implementing Relative Strength and Price Level historically, with proven parity
against the live services. Unlocks: the Lab's historic mode and the Optimizer sweep
can then tune 6 dials honestly instead of 4, and the A/B "locked dials" shrink to
Sentiment + Fundamental momentum only.

## Verified assumptions (pre-checked, with citations)

- **RS algorithm** (`RelativeStrengthService.cs`): stock 5-day return
  `(close[-1] - close[-5]) / close[-5] * 100` vs the same for the sector ETF
  (adjClose), `relative = stock - etf`, scored by the piecewise-linear bands in
  `ScoreRelativeReturn` (≥3%→1.0, 1–3%→0.8–1.0 lerp, 0–1%→0.6–0.8, −1–0%→0.4–0.6,
  −3–−1%→0.2–0.4, <−3%→0.0). Returns **null** (not 0.5) when <5 candles.
- **ETF selection** (`SectorEtfMap.cs`): hardcoded 22-symbol map → {XLK, SMH, XLV,
  XLF, XLY, XLP}; **every other symbol → SPY**. The backtest must reproduce this
  exactly, including the SPY fallback — do NOT "improve" the mapping here (see
  Deliberate non-goals).
- **PriceLevel algorithm** (`PriceLevelService.cs` + `PriceLevelConfig`): 120-day
  lookback, min 20 candles else `InsufficientData` (score 0.5 blended, null
  persisted); swing highs/lows = strictly greater/less than 2 candles each side;
  cluster levels within 1.5% (descending sort, keep first per cluster); contexts in
  priority order: breakout (yesterday close < level < today price AND today volume ≥
  1.3× 20-day avg volume) → 1.0; within 2% above nearest support → 0.85; within 2%
  below nearest resistance → 0.15; no resistance above → 0.6 (AtNewHigh); else 0.5
  (BetweenLevels).
- **Backtest data**: `HistoricalCandles` holds 5y daily bars for SPY + S&P 1500
  (`CandleSyncService`, `HistoryYears = 5`); the engine indexes bars per symbol by
  date (`HistoricBacktester.RunAsync`), scoring day *d*, entering at *d+1* open.
- **Conviction blend**: `ConvictionScorer.Calculate` already accepts
  `relativeStrengthScore`/`priceLevelScore` parameters (defaulted 0.5); the
  backtester's `ScoreAsync` simply doesn't pass them today.
- **Candle sync symbols**: `CandleSyncService.SyncAsync` builds its list from
  `universe.GetUniverseAsync()` + SPY — ETFs are not in the index universe, so they
  must be explicitly appended.

## ⚠ VERIFY at build time

- ⚠ Tiingo returns daily bars for XLK/SMH/XLV/XLF/XLY/XLP on the Power plan via the
  same `/tiingo/daily/{ticker}/prices` endpoint (expected yes — ETFs are standard
  EOD coverage — but confirm with one real request before relying on it).
- ⚠ Live RS uses **adjClose** for the ETF but the stock side reads `StockCandle.Close`
  from `ICandleRepository` (which stores Tiingo **adjusted** values — confirm what
  `SaveCandlesAsync` writes: if live stock candles are adjClose, the backtest's
  `HistoricalCandle.Close` (adjClose per `CandleSyncService` mapping) matches; if
  not, document the discrepancy in the parity test).
- ⚠ `HistoricBacktester.ScoreAsync` slices exactly 60 bars (`WarmupBars`) of history.
  PriceLevel wants ~120 calendar days ≈ ~83 trading days. Decide explicitly: raise
  the engine's warmup to ~85 bars (changes result windows slightly — re-baseline) or
  run PriceLevel on up to 60 bars (weaker parity: live uses 120d). **Recommendation:
  raise WarmupBars to 85** and accept the re-baseline; document in the results
  header that history windows shifted. Do not silently choose.

## Design

1. **Extract pure calculators.** The live services are I/O + algorithm mixed. Pull
   the algorithms into static pure functions so live and backtest call the *same
   code* (parity by construction, not by duplication):
   - `RelativeStrengthCalculator.Compute(IReadOnlyList<decimal> stockCloses,
     IReadOnlyList<decimal> etfCloses)` → `(score, relativeReturn, …) | null`
   - `PriceLevelCalculator.Compute(IReadOnlyList<Bar> bars, decimal currentPrice,
     PriceLevelConfig cfg)` → `PriceLevelResult`
   Live services become thin wrappers (fetch data → call calculator). This is the
   accuracy guarantee: one algorithm, two data feeds.
2. **Candle sync**: append `SectorEtfMap.AllEtfs()` ∪ {XLK, SMH, XLV, XLF, XLY, XLP}
   to the sync symbol list (SPY already present). 6 extra tickers × 5y ≈ ~7,500 rows.
3. **HistoricBacktester**:
   - `ScoreAsync` gains RS: look up the symbol's ETF via `SectorEtfMap.GetEtf`,
     take the ETF's bars ≤ today (need ≥5), compute via the shared calculator; if
     ETF bars missing → component null → neutral 0.5 in blend (mirrors live-null
     behaviour exactly).
   - `ScoreAsync` gains PriceLevel via the shared calculator on the symbol's bars ≤
     today (warmup slice), currentPrice = today's close (mirrors live, which scores
     on the latest close during pre-market research).
   - Pass both into `ConvictionScorer.Calculate` instead of the 0.5 defaults.
4. **Lab UI**: `noHistoricDataKeys` shrinks to `['sentiment', 'fundamentalMomentum']`;
   banner text updated; Optimizer's `LiveIndices` in `SweepOptimizer` grows to
   {0,1,2,4,5,6} (RSI, MACD, Volume, SetupQuality, RelativeStrength, PriceLevel) and
   `DeadIndices` shrinks to {3,7}. Candidate-generation tests updated accordingly.
5. **Console backtester** (`SwingTrader.Backtest`): port the same two component
   calls (it's the offline twin — keep it in lockstep or mark it deprecated; decide
   at build time, note the decision here).

## Test plan (required, written with the change)

- `RelativeStrengthCalculatorTests`: band boundaries (3.0/1.0/0.0/−1.0/−3.0 exact
  values), lerp midpoints, <5 candles → null, identical returns → 0.6.
- `PriceLevelCalculatorTests`: swing-point detection (flat series → none; known
  fixture → expected levels), clustering at exactly 1.5%, breakout requires all
  three conditions, proximity boundaries at exactly 2%, priority order
  (breakout beats near-support), <20 candles → InsufficientData.
- **Parity tests** (the point of the extraction): feed the same fixture through the
  live service (with stubbed repo/client returning the fixture) and through the
  calculator directly; assert identical score/context/levels. One each for RS and
  PriceLevel.
- **Lookahead test**: construct bars where a huge swing high exists at day d+1;
  assert scoring at day d does not see it (levels computed from ≤d only).
- `HistoricBacktesterTests` (new): a fixture universe where a symbol's RS/PL scores
  are hand-computable; assert conviction shifts by exactly
  `weight × (score − 0.5) × 10` vs a run with those weights zeroed.
- `SweepOptimizerTests`: updated LiveIndices/DeadIndices invariants (dead = sentiment
  + fundamental only; live candidates now perturb 6 dials).
- `CandleSyncService`: ETFs included in the sync list (unit test on list assembly if
  extractable, else integration assertion).

## Acceptance criteria

- A historic Lab run shows RS/PL dials unlocked; moving them changes results.
- Re-run of the production baseline records the new 6-component numbers as the new
  reference (expect them to differ from the 4-component era — that's the point).
- Full suite green; parity + lookahead tests green.

## Deliberate non-goals

- **Do not widen `SectorEtfMap` in this phase.** Improving map coverage changes LIVE
  scoring too, mid-comparison. It's a good follow-up (map WatchlistItem.Sector →
  ETF), but it must land as its own change with its own before/after, or Phase 1's
  backtest-vs-live parity claim becomes unverifiable.

## Rollback

Feature is additive; reverting the commit restores 4-component scoring. ETF rows in
HistoricalCandles are harmless if left behind.
