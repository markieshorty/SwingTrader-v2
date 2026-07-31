# On-Demand Research — slot-aware stage-2 scoring

Status: **SPEC — agreed 31 Jul 2026, not yet built**

## Problem

Research runs in full for every account every morning, regardless of whether the
account can act on the output. A 2-slot account is full most of the time when the
strategy is working, so on most days the expensive half of research (news
fetches, fundamentals pulls, Claude sentiment) produces conviction-grade scores
nobody can buy with. Conversely, when a slot opens mid-day (stop-loss, target,
manual sell), the same-day execution re-run buys from scores computed at 7:30 ET
— hours stale by then.

## What must NOT change

- **Stage one (the funnel gate) stays daily for every account.** Gate scores,
  `WouldPassGate`, cross-sectional percentiles and the shadow/forward-score
  evidence are the datasets the mid-Aug review and funnel scorecard depend on;
  they must stay gap-free. Stage one is deterministic and near-free.
- **The sentiment archive keeps its cross-account reuse** (30 Jul): one Claude
  call per symbol per day platform-wide. This spec layers on top of it.
- **Weekly Watchlist, Monitor, Report scheduling are untouched** (Report content
  changes slightly — see below).

## Design

### 1. Slot-aware stage-2 skip (morning run)

`ResearchPipeline` already skips stage two per-symbol when
`gateScore < weights.WatchThreshold` (ResearchPipeline.cs ~line 147). Add an
account-level input: **free slots** = `MaxOpenPositions` (active regime book) −
(open + pending trades in the account's current mode).

- `freeSlots <= 0` → stage two is skipped for **all** symbols that day.
  `NewsSummary` records why: `"Skipped — portfolio full (slot-aware skip)"` so
  the Intelligence page and signal detail stay honest.
- Recommendation classification still runs on gate-only inputs; symbols that
  would have been Buys classify as Watch (a Buy without stage-2 conviction
  must never execute). `WasStageTwoSkipped`-style state is implicit in the
  null component scores — no schema change needed.
- Autopaused accounts (regime book or circuit-breaker/manual pause) also count
  as 0 free slots: entries can't happen, so conviction scores are equally
  unactionable.

### 2. Stage-2 top-up when a slot opens

When capital frees up mid-day, re-run research as a **top-up** before execution:

- Extend `PositionExitService.ReenqueueExecutionIfDoneForTodayAsync` (used by
  every exit path and the app's Close-early) to also re-arm research: if
  today's `Research` job-log row is Completed AND today's signals carry no
  stage-2 scores (i.e. the morning run was slot-skipped), delete the
  **`ResearchMidday`** job-log row (job type already exists — scheduler
  self-heal + `ResearchConsumerFunction` already accept it) so the next
  scheduler tick enqueues a rescore. Execution's own re-enqueue then naturally
  runs AFTER the rescore because `TryEnqueueAsync` fires Execution only when
  its window is open and signals exist — see sequencing below.
- The top-up run scores **only the day's gate-passers** (signals with
  `WouldPassGate = true` or gate ≥ WatchThreshold), not the whole watchlist —
  it reuses the morning's stage-one outputs and just adds sentiment/
  fundamentals + re-classification. Target wall-clock: 2–4 min on the platform
  Tiingo key.
- Archive reuse still applies inside the top-up: symbols another account
  scored that morning cost zero Claude calls.

### 3. Sequencing (top-up before buy)

The freed-capital hook currently deletes today's Execution job-log row
immediately. Under this spec, when a top-up is needed it must **first** re-arm
`ResearchMidday` and only delete the Execution row once the rescore completes.
Simplest correct mechanism: `ResearchConsumerFunction`, on finishing a
`ResearchMidday` run that produced at least one Buy, performs the
delete-Execution-job-log step itself (same guard rules as the existing hook).
No new queues, no new job types, no timing races: scheduler tick →
ResearchMidday → (on completion) → scheduler tick → Execution. Worst-case lag
from exit to re-entry: ~12 min (two 5-min ticks + rescore).

If the morning run was NOT slot-skipped (signals already carry stage-2), the
hook behaves exactly as today — straight to Execution with the morning scores.

### 4. Approval-gated accounts

An account with `ApprovalRequired` that gets a mid-day top-up would need a
mid-day approval. Phase 1 punts: for approval accounts the top-up still
rescoring but Execution keeps its existing behaviour (no approval → no buy;
the owner can approve from the app if they're around — the approval email
should send on top-up completion just like the morning Report). No same-day
guarantee for approval accounts; next morning's run covers them.

### 5. Report

The morning Report for a slot-skipped account should say so plainly ("Portfolio
full — conviction scoring deferred until a position closes") instead of showing
empty sentiment columns. Report content only; scheduling unchanged.

## Config

`Research:SlotAwareStageTwo` (bool, default **false** at ship). Flip per the
standard pattern once a few top-up cycles have been observed in Demo. Off =
today's behaviour exactly.

## Cost impact (state before shipping, per the standing rule)

Recurring saving only, no new bursts. Currently stage two runs for every
gate-passer daily (~5–15 symbols/account/day; Claude cost already deduped
platform-wide by the archive). With all four accounts typically full, expect
**most weekday stage-2 work to disappear**: remaining Claude spend ≈ one
sentiment call per NEW symbol per day on days when at least one account has a
slot, plus top-up calls after exits (bounded by gate-passers, usually already
archived that day). Non-Claude savings: the news/fundamentals HTTP volume for
full accounts. Rough order: another 60–80% off the post-30-Jul research spend
on full-portfolio days.

## Edge cases

- **Slot opens outside the Execution window** (after 15:55 ET / overnight):
  top-up is pointless — guard the re-arm with the same window check the
  scheduler uses; next morning's run handles it (and that morning the account
  HAS a free slot, so stage two runs normally).
- **Multiple exits same day**: `ResearchMidday` job-log dedup gives at most one
  top-up per day; a second exit after a completed top-up re-arms Execution
  only (scores are fresh enough).
- **Race: exit during the morning Research run**: the morning run's slot count
  is read at start; an exit mid-run leaves stage two skipped but the freed-
  capital hook fires afterwards and schedules the top-up. Correct by
  construction.
- **Sentiment momentum continuity**: full-skip days write no archive rows for
  symbols no other account scored. Accepted: momentum already tolerates gaps
  (`SentimentMomentumMinHistory`), and the cross-account overlap keeps popular
  symbols continuous.
- **BrokerRejectedAt / insufficient-funds interplay**: unchanged — top-up only
  refreshes scores; execution's selection and rejection handling are as shipped
  30 Jul.

## Phases

1. **P1 — slot-aware skip** (flag off → on in Demo): pipeline slot input,
   skip + honest summaries, Report wording. No top-up yet; freed capital uses
   morning scores if present, else no same-day re-entry.
2. **P2 — top-up chain**: exit-hook re-arm of ResearchMidday, gate-passer-only
   rescore, completion-triggered Execution re-arm, approval-account email.
3. **P3 (optional, later)** — drop the 7:30 stage-2 entirely even for accounts
   with slots and make ALL conviction scoring just-in-time before the 9:31
   window (research at 9:00 on live prices). Only worth it once P1/P2 have
   proven the chain; changes the daily rhythm, so decide separately.

## Test plan

- Unit: slot-count calculation (open+pending, per-mode, regime-book
  MaxOpenPositions); pipeline skip flag; ResearchMidday completion re-arm
  guards (window, approval, already-fresh signals).
- Demo rehearsal: force-fill an account, verify morning skip summaries; close a
  position early in-app, watch ResearchMidday → Execution chain place a buy
  with fresh scores; verify approval account sends the approval email instead
  of buying.
