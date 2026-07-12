# Two-Stage Conviction Funnel — Overview

**Status: Spec agreed with Mark, 12 Jul 2026 (build not started)**

**Objective:** replace the single 8-component conviction blend with a two-stage
funnel: a **Gate score** built only from the six backtestable components decides
*whether* a candidate is tradeable; a **Forward score** built from the two
forward-looking components (plus catalyst detection) decides *how much* to size
it — with a veto floor for clearly negative reads. A new **sizing
aggressiveness** dial controls how strongly the Forward score differentiates
position sizes.

## Why (evidence base)

1. **Backtest/production fidelity.** Today's backtests pin Sentiment and
   FundamentalMomentum at a fake-neutral 0.5 inside an 8-weight blend, so the
   Strategy Lab optimizes a machine that doesn't exist in production. Under the
   funnel, stage 1 *is* what the backtester models — every sweep and holdout
   validation tests the true production gate. This retroactively makes the
   entire Lab honest.
2. **Three optimizer runs (5y and 10y windows, robust-score ranking) proved the
   six price-derived components are fully squeezed** — no reweighting of them
   beats production out-of-sample. The remaining edge must come from the
   forward-looking inputs, which are unbacktestable and therefore should not
   gate entry with unproven authority; they should *scale* it, bounded.
3. **Selection vs sizing is the statistically correct split.** Technical
   structure (validated, backtestable) decides entry; information edge
   (unvalidated, forward-looking) decides size. Unproven signals control money
   proportional to confidence, never binary decisions.
4. **Cleaner refinement statistics.** Every stage-2 candidate passed the same
   technical bar, so Forward-score-vs-outcome correlations on that homogenized
   population attribute far more cleanly than today's everything-blended score.
5. **Cost.** Sentiment (Claude) + fundamentals (Finnhub) run for every
   researched symbol today (~100/day). Under the funnel they run only for gate
   survivors (~10–20/day): an ~80% cut in sentiment tokens, realized in Phase F2.

## The scores

### Gate score (stage 1) — 0..10, deterministic, backtestable

**Definition:** exactly the number `HistoricBacktester` computes today —
`ConvictionScorer.Calculate` over the live StrategyWeights with Sentiment and
FundamentalMomentum pinned at neutral 0.5. *Deliberately NOT a renormalized
6-weight blend*: pinning the dead pair keeps the formula bit-identical to the
backtest engine and to every historical sweep result, so existing thresholds,
optimizer output and conviction-band analyses carry over without translation.

- Inputs: RSI, MACD, Volume, SetupQuality, RelativeStrength, PriceLevel
  component scores (unchanged).
- The **post-earnings beat/miss adjustment stays on the Gate score** — it is
  derived from EPS history (deterministic, in principle backtestable later).
- Buy/Watch thresholds keep their current fields and semantics
  (`StrategyWeights.BuyThreshold`, `ResearchConfig.MinConvictionForWatch`)
  applied to the Gate score.
- Existing entry rules unchanged: RSI>75 → Avoid, Breakout capped at Watch,
  Hold while a position is open.

### Forward score (stage 2) — 0..10, computed only for gate-passers (from F2)

A weighted blend of the two forward components, rescaled to 0..10:

```
forward01 = ForwardSentimentWeight * sentimentComponent      // 0..1, incl. momentum tilt
          + ForwardFundamentalWeight * fundamentalMomentum   // 0..1, incl. MSPR/accel/velocity
ForwardScore = 10 * forward01, then catalyst adjustment applied (±, bounded)
```

- Defaults: `ForwardSentimentWeight = 0.6`, `ForwardFundamentalWeight = 0.4`
  (sentiment is fresher; fundamentals lag by construction). Config, not DB.
- **The catalyst adjustment moves here** from the final-conviction slot: a
  detected forward catalyst adjusts the Forward score (same ±0.5-point bound,
  same defensive rejections). Rationale: a catalyst is forward-looking
  information — it belongs in the score whose job is forward-looking
  information, and it keeps the Gate score clean for backtest fidelity.
- A **null** component (sentiment fetch failed, fundamentals unavailable)
  contributes neutral 0.5 and sets `ForwardScoreDegraded = true` on the signal
  — a degraded Forward score can size but never veto.

### Veto (stage 2 gate) — asymmetric floor, Phase F3 only

`ForwardScore < ForwardVetoFloor` (default **2.5**/10) blocks the Buy
(recommendation becomes Watch, reason recorded). The floor is deliberately low:
the veto exists to catch *clearly negative* reads (terrible news, bearish
catalyst, insider cluster-selling all at once), not to demand positive
confirmation — a mediocre Forward score sizes small instead of blocking, so
trade count (and the learning loop) is preserved. Degraded Forward scores never
veto.

## Sizing

Position sizing gains a multiplier applied to the existing budget-derived size:

```
tilt        = (ForwardScore - 5) / 5              // -1 .. +1
multiplier  = 1 + SizingAggressiveness * MaxSizingTilt * tilt
size        = clamp(baseSize * multiplier, existing caps)
```

- `SizingAggressiveness`: **0..1 dial on AccountRiskProfile** (Settings slider).
  Default **0 = multiplier is always 1** — flipping the funnel on changes
  nothing until the dial is raised. This is the Phase F2 safety property.
- `MaxSizingTilt`: config constant **0.5** ⇒ at full aggressiveness sizes span
  0.5x–1.5x of base.
- **Existing risk rails stay supreme**: tier pool budgets, per-position % caps,
  the £50 dust floor, and available cash all clamp *after* the multiplier. The
  dial shapes distribution within the risk budget, never expands it.
- The multiplier used is persisted on the Trade (`SizeMultiplier`) and the
  signal, so the scorecard can later ask "did aggressive sizing earn its keep?"

## What is NOT changing

- StrategyWeights storage/UI (8 weights, regime rows) — the dead pair simply
  stops pretending to matter in live scoring, matching the backtest.
- The Strategy Lab engine, optimizer, guardrails, OOS validation - untouched;
  their fidelity *improves* by definition of the Gate score.
- Probation/monitoring, earnings gate, execution approval flow, mid-term plan.
- The 8-component `ConvictionScore` field is retired *gradually* (see phases) —
  during shadow it keeps its exact current meaning.

## Phases (each observable before the next touches money)

| Phase | Behaviour change | Doc |
|---|---|---|
| **F1 Shadow** | None. Gate + Forward computed and persisted on every signal alongside legacy conviction; divergence report. | `01_PhaseF1_Shadow.md` |
| **F2 Flip** | Recommendation driven by Gate score; sizing multiplier live (dial default 0); stage-2 skipped for sub-Watch gate scores (cost saving lands). | `02_PhaseF2_Flip.md` |
| **F3 Veto** | Forward veto floor active; aggressiveness tuned from scorecard evidence. | `03_PhaseF3_Veto.md` |

## Risks / mitigations

- **Divergence surprises** (symbols that pass legacy Buy but fail the gate, or
  vice versa): exactly what F1's shadow report exists to quantify before F2.
- **Trade starvation from double-gating**: mitigated by the asymmetric veto
  (floor 2.5, not a midpoint bar) and sizing-not-blocking as the default lever.
- **Unproven forward signals controlling size**: bounded by aggressiveness=0
  default, MaxSizingTilt cap, and hard risk rails; validated via the
  refinement correlations and the SizeMultiplier scorecard before the dial
  is raised in earnest.
- **Claude-cost regression in F1**: F1 deliberately keeps stage-2 for all
  symbols (identical legacy behaviour); the saving arrives with F2's skip.

## Decisions locked (12 Jul 2026)

1. Gate score = backtester formula (dead pair pinned 0.5), NOT renormalized.
2. Forward split 60/40 sentiment/fundamental; catalyst adjusts Forward score.
3. Earnings beat/miss adjustment stays on the Gate score.
4. Veto is an asymmetric floor (default 2.5/10); degraded scores never veto.
5. Sizing dial on AccountRiskProfile, default 0; MaxSizingTilt 0.5.
6. Rollout strictly F1 → F2 → F3, each gated on reviewing the previous phase's
   evidence.
