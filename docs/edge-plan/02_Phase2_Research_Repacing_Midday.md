# Phase 2 — Research Repacing + Fresh-Signal Execution

**Status: Built 10 Jul 2026 — awaiting user verification (515 tests green).**
Build notes: Readiness moved 7:00 → 8:45 ET (the phase doc missed that it must
stay after Report, which moved to 8:30). Midday rescore rides the same
research-jobs queue via `ResearchJobMessage.JobType = "ResearchMidday"` (the
job-log dedup key; legacy queued messages default to "Research").
`RateLimiting:TiingoMaxPerHour` and `Tiingo:SyncDelayMs` must be set in the
Function App's configuration (deliberately NOT in infra/** — pushing infra
resets the live API image); until set, both fall back to free-tier pacing.

**Objective:** stop trading on 5-hour-old scores. The Tiingo pacing is still set for
the FREE tier (50 req/hour), which forces Research to run at 4:00 ET and Execution to
buy at 9:20+ ET off numbers computed before dawn. With Power (10,000 req/hour) the
whole constraint disappears: research later, on fresher data, and optionally rescore
mid-morning so buys reflect the market that actually opened.

## Verified assumptions (citations)

- Tiingo limiter hardcoded free-tier: `new RateLimiter(50, TimeSpan.FromHours(1))` —
  `SwingTrader.Functions/Program.cs:80`. One shared singleton paces ALL Tiingo calls
  in the Functions host (research candles, RS ETF fetches, regime SPY fetch).
- Scheduler windows (`SchedulerFunction.cs`): Research weekdays 4:00–4:05 ET (comment
  explicitly cites the 50/hr cap and "~90 minutes for 75 symbols"); Report 6:30–6:35;
  Execution window 9:20–15:55 (re-runs enabled by job-log dedup + approval deletes);
  Watchlist Sundays 20:00.
- Research is single-symbol-serial (`MaxConcurrentSymbols` forced to 1 for DbContext
  safety — `ResearchConsumerFunction.cs` comment ~line 37-46), so wall-clock time is
  dominated by rate-limiter sleeps, not CPU.
- `WasExecuted` is redelivery/rescore-safe: `PersistSignalAsync` deliberately leaves
  it untouched on rescore, and `SignalRepository.UpsertAsync` never copies it — the
  WDAY double-buy bug is already fixed (`ResearchPipeline.cs` ~491, repo ~55). This
  is what makes a midday rescore safe to even consider.
- Report consumes signals at 6:30 and builds the approval email; approval covers the
  whole trading day (`SchedulerFunction.cs` comment, approval flow).
- Candle sync paces itself separately with a flat 350 ms delay (`CandleSyncService`),
  independent of the limiter — 350 ms ≈ 2.9 req/s ≈ 10,300/hr. ⚠ near the Power
  ceiling; see VERIFY.

## ⚠ VERIFY at build time

- ⚠ The Power limits are per API token. The platform candle-sync key and the user's
  personal key are the SAME Tiingo account/token for this deployment (memory says the
  user's Power key doubles as `Tiingo--PlatformApiKey`). If so, budget them together:
  candle sync (weekly, ~minutes) + research (daily) + backtest loads share 10k/hr.
  Confirm which keys are configured before setting limits.
- ⚠ Finnhub is now the binding constraint (50/min limiter, `Program.cs:81`). Research
  per symbol makes Finnhub calls (quote/news/earnings) too — repacing Tiingo doesn't
  remove the Finnhub sleeps. Measure a real run's wall-clock after the change; the
  scheduler window below assumes ~15–25 min for ~90 symbols, verify with logs.
- ⚠ Report at 6:30 must still run AFTER research completes. If Research moves later,
  Report and the approval email timing move with it — confirm the user is OK with a
  later approval email (it currently lands ~6:35 ET / 11:35 UK).
- ⚠ Claude limiter (45/min) also paces sentiment + fundamentals per symbol — include
  in the wall-clock estimate.

## Design

1. **Config-driven pacing.** Replace the hardcoded `50/hour` with configuration
   (`RateLimiting:TiingoMaxPerHour`, default e.g. **3,600/hr = 1 req/s** — a
   deliberately conservative 36% of the Power cap, leaving headroom for candle sync,
   Lab backtests and the regime checks; NOT 10k). Keep the free-tier value as the
   fallback default in code so a missing config never bursts a free key. Same for
   candle sync delay (`Tiingo:SyncDelayMs`, default 400).
2. **Reschedule Research** from 4:00 → **7:30–7:35 ET** (pre-market data much
   fresher; earnings/news from the morning included) and **Report** from 6:30 →
   **8:30–8:35 ET**. Keep Execution at 9:20+. Gap of ~55 min between research finish
   (⚠ verify wall-clock) and report is the safety margin. All times remain in the
   scheduler's ET logic.
3. **Midday rescore (flagged, off by default).** New scheduler slot ~**12:30–12:35
   ET**, weekdays, enqueues a second Research job (`Research` job type with a
   distinct job-log key suffix so dedup doesn't block it). Effects, all already-safe:
   signals upsert in place for the same `SignalDate`; `WasExecuted` survives;
   Execution's window (9:20–15:55) means afternoon re-runs (triggered by position
   closes freeing capital, or late approvals) buy from the REFRESHED scores.
   Config: `Research:MiddayRescoreEnabled` (default false), plus a Settings toggle
   (Trading tab) if wanted later — v1 is config-only.
4. **No approval re-ask.** Approval semantics unchanged: the day's approval covers
   rescored signals too (it already covers same-day re-buys). Call this out in the
   Guide page text.

## Test plan

- `RateLimiter` config plumbing: given config value X, limiter spacing =
  period/X (extend existing RateLimiterTests; note the existing flaky timing test —
  don't add more timing-sensitive asserts, test the computed `_minDelayMs` instead;
  expose it internal-for-testing).
- Scheduler: window boundary tests for the new times (mirror existing
  `SchedulerFunction` tests if present; if none exist, add InWindow tests for
  7:30/8:30/12:30 slots including the dedup-key distinction for the midday job).
- Midday rescore job-log key: same-day second enqueue is allowed for the midday
  variant, still deduped within itself.
- Regression: `PersistSignalAsync` preserves `WasExecuted=true` through a rescore
  (test exists — extend to assert conviction/recommendation DO update).

## Acceptance criteria

- With Power config set: research job wall-clock measured and logged; completes
  ≥30 min before Report slot.
- Free-tier fallback proven: with no config override, limiter behaves exactly as
  today (50/hr).
- Midday rescore (enabled in Demo): signals visibly update intraday (UpdatedAt),
  `WasExecuted` flags survive, no duplicate buys across the day.

## Rollback

All behaviour behind config; setting `TiingoMaxPerHour=50` and disabling the midday
slot restores today's behaviour without a deploy.
