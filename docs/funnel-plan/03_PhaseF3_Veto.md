# Phase F3 — The veto (forward floor blocks clearly-negative Buys)

**Goal:** enable the asymmetric stage-2 veto: a gate-passing Buy whose Forward
score sits below the floor is demoted to Watch with the reason recorded.
Entered only after F2's scorecard shows the Forward score correlates sanely
with outcomes — a veto driven by a signal that hasn't earned it is just noise
with authority.

## Behaviour change (single)

In `DetermineRecommendationAsync` (post-gate, Buy candidates only):

```
if (forwardScore is not null && !degraded && forwardScore < profile.ForwardVetoFloor)
    recommendation = Watch;  reasoning += " Forward veto: score X.X below floor Y.Y."
```

- Floor moves from config to `AccountRiskProfile.ForwardVetoFloor`
  (default 2.5, Settings slider 0–5) — it's a per-account risk posture, like
  the other dials.
- Degraded or null Forward scores NEVER veto (a data outage must not block
  trading — fail-open, consistent with every other service in the pipeline).
- Vetoed signals persist `WouldBeVetoed = true` AND `Recommendation = Watch`
  with the reason, so the scorecard can measure what the vetoes would have
  returned (the counterfactual is the whole justification for keeping them
  visible as Watch rather than discarding).

## Work items

- Recommendation change + reasoning append (`ResearchPipeline`).
- `AccountRiskProfile.ForwardVetoFloor` migration + Settings slider + Guide.
- Report: vetoed-today list with forward-score breakdown; scorecard section
  tracking vetoed-signal counterfactual returns vs executed Buys.
- Tests: veto matrix (below/at/above floor; degraded; null; non-Buy gate
  outcomes unaffected); counterfactual fields persisted.

## Tuning loop (steady state)

Quarterly (or after every ~50 vetoes), review:

- **Veto quality:** average counterfactual return of vetoed signals vs
  executed Buys. Vetoes earning their keep = vetoed set underperforms. If the
  vetoed set OUTPERFORMS, lower the floor (or zero it) — the forward signal is
  inverted or noise at the low end.
- **Aggressiveness:** SizeMultiplier-vs-outcome from the F2 scorecard decides
  whether to raise the dial toward 1.0, hold, or cut back.
- **Forward blend:** ForwardSentimentWeight/ForwardFundamentalWeight adjusted
  only on correlation evidence, never intuition — same discipline as the
  strategy weights.

## End state

The funnel is the system: backtestable gate optimized honestly in the Lab,
forward information sizing and vetoing within hard risk rails, every unproven
signal on a measured leash, and a scorecard that can retire any of it the
moment the evidence says so.
