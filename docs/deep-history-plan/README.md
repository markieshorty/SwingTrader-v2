# Deep History: Extending the Dataset to ~2000

Status: **SPEC — drafted 5 Aug 2026, not built.**
Prerequisite: blob candle store live (docs/blob-candles-plan — DONE 5 Aug
2026: migration verified, both hosts flipped, sync + backtest proven).

## Why

Every out-of-sample test to date has failed or been unjudgeable for the same
structural reason: the dataset holds ~one market cycle (2016–2026), the
strategy's edge concentrates in convulsions, and the 2023–26 holdout is
almost convulsion-free (docs/regime-setups-plan/results.md). Extending to
~2000 adds the dot-com crash (2000–02) and the GFC (2008–09):

- Train/holdout splits can BOTH contain real bear markets — "held up
  out-of-sample" becomes a claim the data can actually referee.
- Walk-forward validation gets several meaningful windows instead of one.
- Survivorship honesty improves where it matters most: the dot-com
  graveyard is the harshest delisting cohort in modern history.

Storage is no longer a constraint (blob, pennies). The constraints that
remain are MEMORY, Tiingo data quality that far back, and job duration.

## Scope

- Listed universe: extend existing symbols' history back to 2000-01-01.
- Delisted universe: widen the DelistedBackfillService date filter from
  end-date >= 2016 to >= 2000; same screening rules, same SymbolLifecycle
  bookkeeping (BarsStored=false for screened-out).
- Benchmarks: SPY (exists since 1993) and VIX history back to 2000 for
  regime/Crisis detection. Sector ETFs that didn't exist yet (XLRE 2015,
  XLC 2018) — the RS calculator's SPY fallback already covers the gap.
- Dataset version bumps to v3 on completion: every pre-extension backtest
  becomes evidence-incomparable, as with v2.

## Design decisions

### D1 — Memory: per-request start year (chosen)

The backtester loads the whole store into RAM per job. ~4.8M bars today;
2000-onwards with the dot-com delisted cohort plausibly 12–15M — 2–3× the
working set on a host that has OOM'd before (hence the serialized consumer).

Chosen approach: `HistoricBacktestRequest` gains `DataFromYear` (nullable).
- Null = the CURRENT default window (2016+) — every existing run shape,
  cost and result stays identical. No silent change to anything.
- Set (e.g. 2000) = the loader reads only blobs' bars from that year on
  (decode-then-filter per symbol; blob layout unchanged).
- The Lab gets a "Data from" selector on the deep-check runs (validate /
  sweep / A/B) so long-window tests are a deliberate choice.
- Fingerprint: the effective from-year joins ConfigFingerprint ("dfy=")
  so a 2000-window result can never masquerade as a 2016-window one.

Rejected: raising host memory (costs money, still O(everything) forever);
slimming the bar struct (helps ~2×, invasive, and 26y of growth eats it).

### D2 — Data quality gates (pre-declared, not tuned later)

Tiingo's delisted coverage degrades with age. Per-symbol admission rules:
- Same liquidity screen as v2 (price/volume bands) applied over the
  symbol's own era, not today's levels.
- Reject symbols with > 10% missing trading days across their listed span
  (patchy corpses poison the engine's exit logic more than they inform it).
- Record every rejection in SymbolLifecycle (BarsStored=false + reason) so
  the exclusion set is auditable, exactly like v2.

### D3 — Split/adjustment sanity

Tiingo adjusted prices are used as-is (same as v2), but the backfill spot-
checks N=20 well-known survivors (AAPL, MSFT...) against their known split
history: a bar where adjClose deviates from close by an implausible factor
flags the symbol for exclusion rather than silently corrupting a decade.

### D4 — Job mechanics

Reuses the chunked-continuation pattern (proven twice: delisted backfill,
blob migration): supported-tickers CSV -> candidates -> chunks of ~200 with
fresh SB messages. Expected duration: several hours on the platform Power
key at 1 req/s (thousands of new symbols + ~1,500 backward extensions).
Runs are resumable; a host restart costs one chunk.

## What this does NOT change

- Live trading, research, signals: nothing reads deep history except
  backtests that opt in via DataFromYear.
- The 2016+ default window for existing run shapes (D1).
- The evidence discipline: the FIRST deep-window experiments must be
  pre-declared before results are seen (the obvious first three: baseline
  OR book on 2000–18 train / 2019–26 holdout; the same walk-forward; H2
  convulsion-only VS re-tested with convulsions in BOTH windows).

## Phases

- **P1 — loader + plumbing:** DataFromYear through request -> loader ->
  fingerprint; Lab "Data from" selector; tests. Ships before any new data
  exists (harmless with today's dataset).
- **P2 — the backfill:** sync window config 2000; delisted filter widened;
  quality gates D2/D3; VIX/SPY deep sync; chunked run; dataset v3 bump on
  completion.
- **P3 — the pre-declared deep experiments** (recorded in results.md win
  or lose, no mining — same contract as regime-setups).
