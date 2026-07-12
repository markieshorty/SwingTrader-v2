# Phase F2 — The flip (gate drives entry, forward drives size)

**Goal:** the Gate score becomes the recommendation authority; the sizing
multiplier goes live (inert at the default dial); stage-2 work is skipped for
symbols that can't trade, landing the cost saving. Entered only after F1's
divergence review.

## Behaviour changes

1. **Recommendation** = f(GateScore): Buy at `>= BuyThreshold`, Watch at
   `>= MinConvictionForWatch`, existing RSI>75/Breakout-cap/open-position rules
   unchanged. The legacy 8-blend stops driving anything.
2. **`ConvictionScore` field = GateScore from this phase onward.** One field,
   one meaning, all downstream machinery (probation display, reports,
   refinement buckets, UI) keeps working; a report footnote marks the
   changeover date since band statistics shift meaning at that boundary.
3. **Stage-2 skip:** sentiment + fundamental fetching/scoring runs ONLY when
   `GateScore >= MinConvictionForWatch` (Watch and Buy candidates need forward
   scores; sub-Watch symbols get `ForwardScore = null`, sentiment fields null —
   which the null-means-unavailable persistence convention already handles).
   This is the ~80% Claude-token saving. The sentiment ARCHIVE consequently
   only accrues for gate-passers — accepted: those are the symbols whose
   history matters, and the momentum blend degrades gracefully on thin history
   by design.
4. **Sizing multiplier live** in `ExecutionService`:
   - `AccountRiskProfile.SizingAggressiveness` (0..1, default 0, Settings
     slider next to the other risk dials; CapitalRules gains the 0..1 range
     constants).
   - Multiplier formula per the overview; applied to the budget-derived size
     BEFORE the existing caps/dust-floor clamps, which stay supreme.
   - `Trade.SizeMultiplier` + `Trade.ForwardScoreAtEntry` persisted (migration)
     so the scorecard can evaluate the dial.
   - Default 0 ⇒ F2 deploy changes sizes by exactly nothing until Mark raises
     the dial deliberately.

## Work items

- `ResearchPipeline`: reorder — indicators → gate (+earnings adj) → early-out
  persistence for sub-Watch symbols → stage-2 (sentiment incl. momentum +
  catalyst, fundamentals) → forward score → recommendation.
- `ExecutionService`: multiplier application + persistence; guard: degraded
  Forward score ⇒ multiplier 1.
- `AccountRiskProfile` + migration + Settings API/UI slider + Guide tab note.
- `Trade` migration (2 columns), DTOs.
- Refinement: `ComponentCorrelationService` gains ForwardScore-vs-outcome and
  SizeMultiplier-vs-outcome correlations computed over the gated population.
- Angular signals/trades pages: Gate is the headline number; Forward shown
  with a size-tilt chip on Buys.
- Tests: recommendation matrix over gate/forward combinations; skip-path
  (sub-Watch symbol makes zero sentiment/fundamental calls — assert via
  substitute clients); multiplier math incl. clamps, degraded guard,
  aggressiveness 0 no-op regression; migrations.

## Rollback

Config flag `Research:FunnelEnabled` (default true in F2). Off = F1 behaviour
(legacy conviction drives, stage-2 for all). One setting, no deploy.

## Exit criteria (gate to F3)

- ≥4 weeks of demo trades under gate-driven entry with dial raised to a
  deliberate test value (suggest 0.5) for at least half that period.
- Scorecard review: ForwardScore-vs-outcome correlation direction is sane;
  SizeMultiplier didn't concentrate losses; trade count within expected range
  of F1's gate-would-Buy rate.
