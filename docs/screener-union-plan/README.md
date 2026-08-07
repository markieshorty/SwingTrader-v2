# Screener union plan — feeding each setup its own candidates

Spec agreed 7 Aug 2026. **Not built.** Discussion only until P1 reports.

## The problem

The screener narrows ~1,500 index constituents to 80 candidates for Claude
using **one factor**: today's absolute percentage move.

```
universe (S&P 500/400/600 + Nasdaq 100, live from Wikipedia)
  -> drop already-watchlisted and open-trade symbols
  -> Finnhub quote per symbol
  -> filter: price in [$15, $500]
  -> filter: |dayChange| in [1%, 15%]
  -> rank by |dayChange| x TopMoverOrderBoost
  -> walk the ranking applying a $10M 20-day dollar-volume floor
  -> keep the top MaxCandidatesForClaude (80)
```

Those 80 are then narrowed to ~25 by Claude, whose prompt uses sector only as
a spreading constraint ("no more than 5 from any sector") — never
comparatively.

Downstream, five detectors classify each name (first match wins):

| Setup | Trigger | Needs a big daily move? |
|---|---|---|
| OversoldRecovery | `RSI<35`, above lower band, above `close[-4]` | Sometimes |
| Breakout | above upper band, `VolumeRatio>1.5`, `MACD>0` | **Yes** — expansion |
| MomentumContinuation | `RSI 50-65`, `EMA9>EMA21`, `MACD>0`, `VolumeRatio>1.0` | **No** |
| VolumeSpike | `VolumeRatio>2.0` **and `dayChange>1.5%`** | **Yes, by definition** |
| TrendFollowing | `EMA9>EMA21`, `RSI>50`, above mid band | **No** |

**The coupling.** VolumeSpike's own trigger requires a >1.5% move, which is a
strict subset of the screen's `|dayChange| >= 1%` criterion — the screener
cannot help but feed it. Breakout demands band-expansion on 1.5x volume,
which also produces a large move. Meanwhile TrendFollowing and
MomentumContinuation impose no move requirement at all and can fire on a
+0.3% day, which the screen discards before Claude sees it.

So the screen is a **volatility-expansion filter**, aligned with two setups
and close to orthogonal to two others.

### What this does NOT explain

An earlier version of this argument claimed Breakout underperforms because
the screener starves it. **That is false and was corrected before speccing:**
Breakout requires expansion plus volume, so it is among the *best*-fed
setups. Its drag (backtests: excluding it gained ~14%) is a genuine problem
with the setup, not a supply artefact. Do not build this expecting to rescue
Breakout.

### A speculation, flagged as such

The screen ranks on **absolute** move, so it surfaces large *decliners* as
readily as gainers. Large decliners are precisely the `RSI<35` population
that feeds OversoldRecovery — the setup the conviction scorer rates highest
(quality 1.0), and the one whose top band inverted in every backtest (the
falling knives). Meanwhile TrendFollowing — rated lowest at 0.5, and the
setup closest to the momentum effect that has the strongest academic support
— is the most starved.

That is a coherent story for why the baseline strategy backtested
unprofitable. It is **not evidence**. P1 measures it rather than assuming it.

## Pre-declared hypotheses

Declared BEFORE the measurement exists, per house rule.

- **H-SC1**: setup representation among the screened 80 is materially skewed
  versus the same detectors run over the whole universe on the same day.
- **H-SC2**: specifically, TrendFollowing and MomentumContinuation are
  under-represented relative to their universe frequency, and VolumeSpike is
  over-represented.
- **H-SC3** (only testable after P2): feeding a starved setup candidates that
  match its trigger improves that setup's per-setup outcomes.

If H-SC1 fails — representation is roughly proportional — **P2 is not built.**
The whole design rests on a starvation that must be demonstrated first.

## P1 — measure the starvation. No behaviour change.

Fully reconstructable offline: every input is a daily candle, and the blob
store holds ~9.1M bars over 2,671 symbols back to 2000. No API calls, no
Claude tokens.

For each historical trading day in the sample:

1. Run `DetectSetup` over the whole universe → the *available* population per
   setup.
2. Reconstruct the screen from the same candles (price band, `|dayChange|`
   band, rank, dollar-volume floor, top 80) → the *selected* population.
3. Record, per setup: available count, selected count, and the ratio.

Output: one representation table per setup, plus its drift over time.
Deliverable is a number, not a feature.

**Caveat that must be honoured**: the blob store covers roughly today's
liquid universe, so a historical reconstruction inherits survivorship bias —
the same bias that killed the factor sleeve. Representation *ratios* are less
exposed than returns would be (both numerator and denominator are drawn from
the same biased set), but the delisted backfill (docs/survivorship-plan)
would be needed before any *return* claim is made from this reconstruction.

## P2 — union of per-setup screens (only if P1 justifies it)

Replace one blended ranking with several narrow ones. A weighted composite
was considered and **rejected**: blending rewards names that are moderately
interesting on every factor over names that are exactly right for one setup,
which is the same compromise the current single ranking already makes.

```
hard filters (unchanged, applied first):
  universe -> drop already-watchlisted/open -> price band
  -> dollar-volume floor -> earnings-window exclusion

then, per setup, rank the survivors by a proxy for that setup's trigger
and take the top K:

  OversoldRecovery      rank by: how far RSI is below 35, given the 4-bar
                                 recovery already holds
  Breakout              rank by: distance above the upper band x volume ratio
  MomentumContinuation  rank by: MACD histogram, gated to RSI 50-65 and
                                 EMA9>EMA21
  VolumeSpike           rank by: volume ratio (NOT by price move — the move
                                 is already in the detector)
  TrendFollowing        rank by: EMA spread and distance above the mid band

union -> dedupe -> cap at MaxCandidatesForClaude
```

Each candidate is stamped with **which screen surfaced it** (several may),
so per-setup outcomes become attributable — without that stamp P2 cannot be
judged and must not ship.

**Two design rules.**

*Constraints are not weights.* Earnings proximity, the price band and the
liquidity floor are hard filters. A stock reporting tomorrow is structurally
unbuyable and must be absent, not merely ranked lower. Blending a binary
constraint into a continuous score lets a strong name buy its way past a
hard rule.

*Move the earnings check into the screener.* It currently blocks Buys
downstream in the research pipeline, which means per-symbol Claude spend on
names that are unbuyable that week. Pure waste removal, no hypothesis
attached — worth doing regardless of the rest of this plan.

**Parameter count**: 5 (one K per setup) against 6 for a weighted blend, and
each K is independently testable against its own setup's trades rather than
only in combination. This is the top of the funnel — everything downstream
inherits whatever it selects — so it is the single worst place in the system
to overfit.

## Validation before any live flip

The last two things fitted this way (factor sleeve, regime setups) both died
at validation. Same discipline applies:

- Walk-forward, out-of-sample. No in-sample-only claim.
- A **control**: the current single-factor screen, run over the identical
  period and universe. "Better than nothing" is not the bar; better than
  what already runs is.
- Per-setup attribution, so any improvement is traceable to the setups P1
  said were starved rather than to noise elsewhere.
- **Falsification**: if a starved setup is properly fed and still
  underperforms, the setup lacks edge and should be retired rather than
  re-tuned. That is the outcome this plan is most likely to produce, and it
  is a useful one.

## P3 — live flip

Behind a config flag, default off, shadow-stamped first: compute the union
selection, record what it *would* have chosen alongside the live selection,
and compare before anything changes. Same pattern as the cross-sectional
percentile (docs/funnel-plan) and the funnel itself.

## Out of scope

- Weighted/blended composite scoring (rejected above).
- Changing the detectors themselves. This plan changes what reaches them,
  nothing else — otherwise the comparison has two moving parts.
- Sector-relative and peer-bucket ranking. Deferred by the cross-sectional
  ranking plan pending a verdict on the inert percentile already shipped;
  adding a second comparative metric before the first reports repeats the
  pattern this project is trying to break.
- `MinPrice = $15`. Worth noting the swing book therefore cannot trade the
  sub-$1 micro-caps the filing-events feed monitors — two halves of the
  system pointed at non-overlapping populations — but that is a separate
  decision.

## Cost

Zero Claude tokens. P1 is local candle arithmetic over the blob store; P2
adds no API calls (the screen's per-symbol quote fetch is unchanged, and the
new factors come from candles already held). The only cost is runtime on the
backtest reconstruction.
