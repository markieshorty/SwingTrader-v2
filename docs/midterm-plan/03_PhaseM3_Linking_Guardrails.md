# Phase M3 — Position Detection, Thesis Linking, No-Overlap Guardrails

**Status: Planned**

**Objective:** connect manual reality to the thesis book. The app detects manual
T212 buys via the existing portfolio sync, prompts the user to link them to an
accepted thesis, and enforces — in both directions — that a symbol is never held
by the swing bot and a mid-term thesis at once.

## ⚠ VERIFY at build time

- ⚠ What the T212 portfolio endpoint returns for manually-opened positions
  (ticker format, average price, quantity, opened timestamp?) — capture a real
  payload with a manual demo buy before writing the matcher. The BrokerTicker
  disambiguation lessons (HAL vs HAL1a_EQ) apply here.
- ⚠ Whether Monitor's pending-reconciliation logic could mistake a manual
  mid-term buy for one of ITS positions — read that path before touching it.

## Design

### 1. Detection + linking

- The existing Monitor cycle already fetches T212 positions. New step: positions
  that match **no** swing Trade row and **no** linked thesis are surfaced as
  "unrecognised positions" on the Mid-Term page.
- If an unrecognised position's symbol matches an accepted (Proposed) thesis:
  banner "Looks like you bought {symbol} — link it to your thesis?" One click
  sets Status=Active, `LinkedAt`, `EntryPrice` (T212 average price — the real
  fill, not the proposal price; the scorecard keeps both so slippage-from-
  proposal is itself visible).
- Unrecognised positions matching no thesis are just listed (informational —
  the user is allowed to own things the app doesn't manage; nothing nags).
- Selling detection: a linked position that disappears from T212 closes the
  thesis (`ClosedAt`, `ExitPrice` = last seen price, CloseReason=ManualClose) —
  reconciliation again, not self-reporting.

### 2. No-overlap guardrails (both directions, hard)

- **Swing must not buy thesis symbols:** ExecutionService's eligibility filter
  (where closed-today and pending symbols are already excluded) also excludes
  symbols with an Active or accepted-Proposed thesis. Logged + activity event
  ("skipped — held as mid-term thesis"), counted as skipped.
- **Mid-term must not propose swing-held symbols:** the M2 selection prompt
  input excludes symbols with open swing Trades AND the post-parse validation
  drops any pick that slipped through (belt and braces, code-enforced).
- **Race window:** the link check and the swing execution read the same tables
  in the same DB — the guardrail queries run inside each job at decision time,
  not from a cache.

## Test plan

- Matcher: T212 position ↔ thesis symbol matching incl. broker-ticker
  disambiguation fixtures; unmatched positions listed not linked.
- Lifecycle: link sets Active with T212 entry price; disappearance closes with
  ManualClose; scorecard uses proposal anchors AND real entry.
- Guardrails: swing execution skips thesis symbols (unit: eligibility filter);
  selection post-parse drops swing-held symbols; both paths log.
- Monitor regression: swing pending-reconciliation untouched by unrecognised
  manual positions (fixture with a manual position present).

## Acceptance criteria

- Demo: manually buy an accepted pick at T212 → within one Monitor cycle the
  link banner appears; link → Active with the real fill price.
- With an Active thesis on symbol X, a swing Buy signal for X is skipped with a
  visible activity-log entry; the next monthly selection never proposes a
  swing-held symbol.

## Rollback

Feature-flagged with M2's `MidTerm:Enabled`; guardrails are no-ops when no
theses exist.
