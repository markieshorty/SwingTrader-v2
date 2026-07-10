# Phase 3 — Intraday Entry Confirmation (IEX)

**Status: Built 10 Jul 2026 — awaiting user verification (528 tests green).**
Build notes / resolved ⚠ checks:
- Real IEX calls (2026-07-10, Power key): full 78-bar session for AAPL AND the
  small-cap HALO; unknown tickers return `{"detail":"Not found."}` (object, not
  array) → deserialization throws → Unavailable → fail open. Intra-session
  latency could not be observed (market was closed) — that check moves to the
  Demo acceptance run.
- **Design amendment (volume baseline):** the spec compared IEX session volume
  against the consolidated 20-day average from daily candles. IEX volume is
  only ~2–4% of consolidated (AAPL: ~1.5M IEX vs ~50M consolidated), so that
  gate would have rejected every entry. The baseline is now SAME-SOURCE: one
  60-minute IEX call over 30 days, summed per day, averaged over the last 20
  trading days (the endpoint rejects resampleFreq=1day). No candle-repo
  dependency.
- Full-path ExecutionService integration (rejected ⇒ no Pending row) is
  covered by code ordering + the pure-gate tests, not an integration test —
  the placement path always sleeps 15s+ before its first broker call, the
  same reason existing execution tests stop at the eligibility steps.

**Objective:** stop buying setups that died between scoring and fill. Signals are
scored pre-market; orders place from 9:20 ET. A stock can gap far past its setup, or
open dead-on-arrival volume-wise, and today the system buys anyway. IEX 5-minute bars
(included in Power) give Execution a moment-of-purchase sanity gate.

## Verified assumptions (citations)

- Execution already refreshes a Finnhub quote immediately before placing and
  re-derives stop/target from it (`ExecutionService.cs` ~260-276) — so there IS a
  last-second price check, but it only re-anchors levels; it never *rejects* an
  entry. The gate slots in right there.
- Intent-first placement (`ExecutionService` ~278-316): the Pending row + WasExecuted
  claim happen BEFORE the broker call. The gate must run BEFORE the intent is
  persisted (a rejected entry should leave no Pending row and not claim the signal —
  or explicitly release the claim; decide: simplest correct order is
  gate → then intent → then broker).
- `ITiingoClient` is Refit; adding `[Get("/iex/{ticker}/prices")]` with
  `resampleFreq=5min&columns=open,high,low,close,volume` matches Tiingo's documented
  IEX endpoint shape (OHLC default; volume only when explicitly requested —
  tiingo.com/documentation/iex).
- The Functions host already creates a per-account Tiingo client for Execution's
  sibling consumers via `IUserHttpClientFactory` — Execution consumer can do the same.

## ⚠ VERIFY at build time

- ⚠ IEX intraday freshness on Power during market hours (docs don't state latency for
  the REST endpoint). Fetch a real 5-min series at ~9:25 ET and confirm the latest
  bar is from the current session and no more than ~10 min stale. If it's
  end-of-day-only on this plan, this phase's design is void — stop and re-plan
  (fallback: Finnhub quote-only heuristics, weaker).
- ⚠ Whether IEX bars exist for ALL our tradable symbols (IEX-listed volume can be
  thin for small caps in the S&P 600 tail). Missing/empty series must fail open.
- ⚠ Exact response shape (field casing, date format incl. timezone) — write the DTO
  from a captured real payload, not from memory.

## Design

1. **New client method**: `GetIexIntradayAsync(ticker, resampleFreq="5min",
   startDate=today)` on `ITiingoClient` + DTO from the ⚠-verified payload.
2. **`EntryConfirmationService`** (Agents, pure logic + thin fetch wrapper), returns
   `Confirmed | Rejected(reason) | Unavailable`:
   - **Gap gate**: if today's open (or latest price) is more than
     `MaxGapUpPct` (default 4%) above the signal's scored `CurrentPrice`, reject —
     the setup priced in; we'd be chasing. (Gap DOWN handling: if price is below the
     freshly-derived stop level, reject — we'd be buying an instant stop-out.)
   - **Volume gate** (only after ≥30 min of session): cumulative session volume <
     `MinSessionVolumeRatio` (default 0.15) × 20-day average daily volume → reject
     as dead-on-arrival. Before 9:50 ET skip this gate (too little data).
   - **Unavailable** (fetch error, empty series, symbol not on IEX) → **fail open**:
     log, count, proceed to buy exactly as today. A data outage must never halt
     trading.
3. **Wire into ExecutionService** immediately after ticker resolution and BEFORE the
   intent-first persist. Rejected entries: log activity event
   ("Entry skipped — {reason}"), increment the run's `skipped` counter, signal NOT
   claimed (still eligible if a later same-day re-run finds conditions normalised —
   deliberate: gaps fade).
4. **Config** (`ExecutionConfig`): `IntradayConfirmationEnabled` (default **false**
   initially — enable in Demo first), `MaxGapUpPct`, `MinSessionVolumeRatio`,
   `VolumeGateEarliestEt` (09:50). All surfaced in the daily report line when a skip
   occurs, per the clarity rule.
5. **20-day avg volume source**: reuse the candle repo (already fetched for
   research) — no extra API calls.

## Test plan

- `EntryConfirmationServiceTests` (pure logic, fixture bars):
  - gap-up exactly at threshold (boundary), above (reject), below (pass)
  - price below derived stop → reject
  - volume gate: before 9:50 ET skipped; after, below-ratio rejects, at-ratio passes
  - empty/missing series → Unavailable
  - Unavailable ⇒ ExecutionService still places (integration-style test with the
    service substituted)
- `ExecutionService` tests: rejected entry ⇒ no Pending row, `WasExecuted` stays
  false, skip counted; confirmed entry ⇒ identical flow to today (regression).
- DTO deserialisation test against the captured real payload (checked into fixtures).

## Acceptance criteria

- Demo mode, flag on: activity log shows confirmations/skips with reasons; a
  deliberately gapped fixture (unit) and at least one real observed skip or pass
  logged in Demo before enabling anywhere else.
- Flag off: byte-for-byte today's behaviour (regression suite green).

## Rollback

Config flag off = feature fully dormant. No schema changes.
