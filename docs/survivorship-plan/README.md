# Survivorship-Free Historic Dataset

Status: **SPEC — agreed 4 Aug 2026, not yet built**

## Problem

The shared `HistoricalCandles` dataset is built from the CURRENT liquid-US
universe synced backwards, so every symbol that delisted, went bankrupt or was
acquired during the window is absent. For an oversold-recovery strategy this
is a targeted distortion, not background noise: the system's job is buying
sharp drawdowns, and sharp-drawdowns is where the corpses lived. Every
backtest to date could only dip-buy the dippers that survived. Literature
puts survivorship inflation at 1–4% CAGR for broad strategies; for dip-buying
assume the high end — the sim's ~22% CAGR is plausibly 15–18% unbiased.

## What already works in our favour

- **Point-in-time screening exists.** `HistoricBacktester.BuildWatchlist`
  re-screens the bar set every simulated Monday using only data ≤ that date
  (price band, |change| 1–15%, 20-day dollar volume ≥ MinDollarVolume). Once
  dead symbols' bars are IN the dataset, they enter simulated watchlists
  while alive and drop out when their bars end — no selection changes needed.
- **Tiingo already has the data.** The free `supported_tickers.csv` lists
  every US ticker with start/end dates — delisted included — and the EOD
  endpoint serves delisted symbols' history on the Power key we already pay
  for. No new subscription.

## P1 — Delisted-universe sync

Extend `CandleSyncService` with a one-time (then yearly-ish) delisted backfill:

1. Download `supported_tickers.csv`; select US equities (exchange NYSE/NASDAQ/
   AMEX, assetType Stock) whose `endDate` falls inside the dataset window and
   whose listing lasted ≥ 6 months.
2. **Pre-filter before syncing bars** (the DB cap is the binding constraint):
   fetch each candidate's bars, keep the symbol only if it EVER passed the
   engine's own screen thresholds ($15–500 price, ≥ $10M 20-day dollar
   volume). Symbols that never screened in can never affect a backtest —
   don't store them.
3. Store bars in `HistoricalCandles` as normal. New table `SymbolLifecycle`
   (Symbol PK, ListedAt, DelistedAt, EndReason nullable) built from the CSV —
   the engine and UI read delisting dates from here rather than inferring
   from bar gaps.
4. **DB size gate first**: Basic tier caps at 2 GB. Before any backfill, run
   `sp_spaceused`; the job aborts with a clear activity-log entry if
   projected size exceeds ~1.6 GB. Mitigations in order: raise the liquidity
   floor for dead symbols, shorten the dead-symbol window (e.g. 7y), or (last
   resort, Mark's call) a tier bump.

Trigger: manual (`sync-data`-style owner endpoint / admin button), NOT the
weekly Saturday sync — dead symbols don't grow new bars. Expected one-time
cost: a few thousand Tiingo calls on the Power key (~1–3 hours), zero Claude.

## P2 — Delisting semantics in the engine

1. **Forced exit at last bar.** A position whose symbol has no bar for
   `DelistingGraceDays` (default 5) consecutive trading days after its last
   bar force-exits at the last close, exit reason `Delisted`. This is
   approximately right for acquisitions (final price ≈ deal price).
2. **Bankruptcy haircut (config, default on).** If `SymbolLifecycle.EndReason`
   is unknown/bankruptcy-like, apply `DelistingHaircutPct` (default 25%) to
   the last close — the last print before a halt usually precedes further
   loss. Acquisition-tagged ends take the last close untouched. (The CSV
   doesn't carry reasons; P2 ships with reason=null → haircut applies. A
   later enrichment pass could tag acquisitions from the biggest names.)
3. **`Delisted` exit bucket** in the by-exit results table so the damage is
   visible, not smeared into other buckets.

## Dataset versioning (honesty requirement)

Every result stored after the backfill is incomparable with results before
it. Add `DatasetVersion` (int, from a new `HistoricalDatasetInfo` single-row
table, bumped by the backfill job) to:
- `ConfigFingerprint` (so evidence-tied sharing/apply distinguishes runs),
- the sweep/Optimizer History rows and the Lab's data-status line
  ("3.9M bars · v2 (survivorship-free) · up to …").
Old stored results keep rendering; they're just fingerprint-distinct.

## What to expect (set expectations BEFORE running)

- Every headline number drops; the stop-loss and probation buckets worsen;
  the new Delisted bucket is pure loss. That is the point.
- Relative comparisons made on v1 (weights ranking, ATR/ceiling/target-mode
  verdicts) likely still hold directionally, but the four killed hypotheses
  are cheap to re-run on v2 via the sweep's standing counterfactuals.
- SPY buy-and-hold benchmark is unaffected (SPY survived).

## Phases

- **P1** data: lifecycle table + gated delisted backfill + dataset version.
- **P2** engine: delisting exits + haircut + exit bucket + fingerprint/UI
  stamping.
- Run order after ship: baseline re-run first (the new honest 100%-flat
  numbers), then one full sweep — the standing counterfactual candidates
  re-audit ATR/ceiling/target-mode against the unbiased data for free.

## Test plan

- Unit: lifecycle CSV parsing/filtering; delisting-exit trigger (grace days,
  haircut on/off, acquisition passthrough); dataset-version fingerprint.
- Integration (Demo): backfill dry-run mode that reports symbol count and
  projected DB growth WITHOUT writing; then the gated real run.
