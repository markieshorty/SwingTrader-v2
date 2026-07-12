# Phase F1 — Shadow scoring (no behaviour change)

**Goal:** compute and persist the Gate and Forward scores on every signal while
live behaviour stays byte-identical to today, and surface a divergence view so
F2's flip is a reviewed decision, not a leap.

## Invariants (the definition of "no behaviour change")

- Legacy `ConvictionScore` (8-component blend + earnings + catalyst
  adjustments) is computed exactly as today and still drives Recommendation,
  execution eligibility, probation and reporting.
- Stage-2 inputs (sentiment, fundamentals) are still fetched for EVERY symbol —
  the F2 cost-saving skip is deliberately deferred so nothing about legacy
  scores shifts.
- The one accepted wrinkle: the catalyst adjustment currently lands on the
  final conviction. In F1 it is applied to BOTH numbers (legacy conviction, as
  today, and the shadow Forward score, as specced) so each stream is internally
  consistent.

## Work items

### 1. Score computation (`SwingTrader.Agents/Research`)

- New pure static `FunnelScores` (mirrors `ConvictionScorer` conventions):
  - `GateScore(weights, componentScores, rs, priceLevel)` — calls
    `ConvictionScorer.Calculate` with `sentimentScore: 0.5m,
    fundamentalMomentumScore: 0.5m`. One-liner by design; exists so the
    definition has a name, a doc comment and tests.
  - `ForwardScore(sentimentComponent01, fundamental01, sentimentWeight,
    fundamentalWeight)` → `(decimal Score, bool Degraded)`; null inputs
    contribute 0.5 and set Degraded.
- `ResearchPipeline.RunAsync`:
  - Compute `gateScore` (+ earnings adjustment applied to it, mirroring the
    legacy path) and `forwardScore` (+ catalyst adjustment applied to it).
  - Legacy conviction path untouched.

### 2. Schema (`SwingTrader.Core` + migration)

`StockSignal` gains (all nullable so historical rows stay valid):

```
decimal? GateScore            // stage-1 score, earnings-adjusted
decimal? ForwardScore         // stage-2 score, catalyst-adjusted, 0..10
bool     ForwardScoreDegraded // a component was unavailable
bool     WouldPassGate        // GateScore >= BuyThreshold at signal time
bool     WouldBeVetoed        // ForwardScore < ForwardVetoFloor (using F3's default)
```

`WouldPassGate`/`WouldBeVetoed` are snapshotted booleans (not derived at read
time) so later threshold changes don't rewrite history. One EF migration.

### 3. Config

`ResearchConfig`: `ForwardSentimentWeight` (0.6), `ForwardFundamentalWeight`
(0.4), `ForwardVetoFloor` (2.5) — floor unused for behaviour in F1 but needed
to snapshot `WouldBeVetoed` honestly.

### 4. Divergence visibility

- Signals API/DTO + Angular signals page: show Gate and Forward beside legacy
  conviction (small muted columns; tooltip explaining shadow status).
- Daily report gains a one-line funnel-shadow summary: "N signals; legacy Buy
  M; gate-would-Buy K; divergent D (list)".
- This is the F1 exit evidence: divergence reviewed over ≥2 weeks of signals
  before F2 is scheduled.

### 5. Tests

- `FunnelScoresTests`: gate equals ConvictionScorer-with-neutral-pair
  (property-style across random weights/components); forward blend math,
  degraded flags, catalyst interaction bounds.
- Pipeline-level: persisted signal carries both scores; legacy conviction
  unchanged for identical inputs (regression pin).
- Migration compiles; DTO round-trips.

## Exit criteria (gate to F2)

- ≥2 weeks of daily signals with both scores populated.
- Divergence report reviewed: the set {legacy Buy} vs {gate Buy} differences
  are understood and acceptable (expected sources: sentiment/fundamental
  weight in legacy blend, catalyst placement).
- No degradation in research-run duration/cost beyond noise (F1 adds no
  external calls).
