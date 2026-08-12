# Scoring engine rebuild — consolidated position

Working document, 12 Aug 2026. Captures what has been decided, what the data
says, and what is still open. **Not a spec** — the spec follows once the open
questions are answered.

---

## 1. The objective (decided)

> "The goal of the system is to make big gains with a relatively small capital
> base, so waiting for the right trade is the right call."

Two consequences that shape everything downstream:

- **Right-tail capture is the objective function**, not expectancy per trade or
  win rate. A 30% win rate with occasional +40% winners beats a 60% win rate
  grinding +2%. Every current Lab metric ranks those the wrong way round.
- **The success criterion is a base-rate lift.** Measured over the candle store:
  a +25% move in 40 trading days has a **7.37%** base rate (−25%: 4.28%). A
  setup that cannot lift a signal meaningfully above 7.37% is not earning its
  place, whatever it does to win rate.

Saying "nothing today" on most days is the intended behaviour, not a failure.

---

## 2. Decided

| # | Decision |
|---|---|
| D1 | **Scrap the global weight vector.** Setup is identified first; factors are weighted *within* each setup. |
| D2 | **Graded membership**, not boolean. Each setup returns 0–1; a name may belong to several. This removes the first-match-wins ordering problem rather than reshuffling it. |
| D3 | **Dials become per-account config rows**, like `SetupTactics` already is — so the Lab can sweep them. Today they are 13 hardcoded constants in `SetupDetector`. |
| D4 | **Frozen at signal time.** Every signal records the dials it was detected and scored under. Without this, the first sweep makes all prior history unreadable — the same defect as the Almost tab replaying with today's tactics. |
| D5 | **`TrendFollowing` is demoted from setup to context factor.** It has no trigger — it describes a state, so it re-fires daily (SNOW: 23 of 29 trading days). Trend strength stays available to every setup as a modulating variable; it stops competing as an entry. |
| D6 | **`OversoldRecoveryLoose` returns as a dial, not an enum member.** `recoveryLookbackBars: 4 → 0` *is* the loose variant. It stops carrying its own tactics, scorer entry and retired-enum baggage, and the Lab can sweep the question instead of us deciding it. |
| D7 | **A veto layer exists alongside weighted factors.** Vetoes are boolean and need no weights, so they are cheap to add and cannot quietly accumulate fitted parameters. |
| D8 | **Universe stays as-is** — S&P 1500 + Nasdaq-100. Q1 closed. |
| D9 | **The funnel score is ripped out.** No forward score, no combined /20, no `ForwardBuyThreshold`. The technical score ranks; **vetoes remove**. Consistent with the only measured signal: gate r = +0.294, forward r = −0.126, and the sum (+0.044) worse than either. |
| D10 | **No setup, no trade.** `Unknown` is not scored and cannot be bought. Q3 closed. |
| D11 | **Trading rules become ATR multiples**, not percentages — stop, target, trail activation and trail distance. Q6 closed. |

---

## 3. Why the current engine is wrong (measured, not asserted)

**The two main components are tuned for opposite regimes.**

```
ScoreRsi   peaks at 1.0 at RSI 35, decays to 0.25 by RSI 65, 0.0 above 75
ScoreMacd  histogram > 0 && rising -> 1.0
           histogram < 0 && rising -> 0.3   <-- the oversold turn
```

A textbook `OversoldRecovery` scores **1.0 on RSI and 0.3 on MACD**. A textbook
`Breakout` scores **1.0 on MACD and ≤0.25 on RSI**. One global weight vector can
trade one penalty against the other; it cannot make both correct.

Consistent with this: **Breakout carries the highest average gate score (7.40)
and the highest forward score (6.57), and backtested as the drag** (excluding it:
+14%). A setup that scores best and performs worst is what a mis-specified
composite looks like.

`ScoreSetupQuality` is setup-awareness in its weakest form — a constant per type.
An intercept, not a model. And `ScoreVolume(volumeRatio)` is **direction-blind**:
a high-volume selloff *raises* conviction on a dip.

**Signals per distinct symbol** — a usable test of whether a setup detects an
event or describes a state:

| Setup | Signals | Symbols | Per symbol |
|---|---|---|---|
| Unknown | 1,407 | 246 | 5.7 |
| TrendFollowing | 843 | 214 | 3.9 |
| OversoldRecoveryLoose | 50 | 22 | 2.3 |
| OversoldRecovery | 28 | 15 | 1.9 |
| MomentumContinuation | 113 | 68 | 1.7 |
| Breakout | 74 | 49 | 1.5 |
| **VolumeSpike** | 20 | 20 | **1.0** |

Near 1.0 = a real event. Near 4 = a screen. `TrendFollowing` + `Unknown` are
**89% of account 440's signals**.

---

## 4. What the live evidence can and cannot support

**27 distinct closed signals.** Correlation with realised return:

| Predictor | r |
|---|---|
| Gate score | **+0.294** |
| Forward score | −0.126 |
| Combined /20 | +0.044 |

At n=27 significance needs \|r\| ≈ 0.38. **Nothing here is measurable yet.** Two
observations survive that caveat:

- The gate is the only predictor pointing the right way, and it is currently
  demoted to a pass/fail filter while the forward score decides Buys.
- Summing them is *worse than either* — the /20 dilutes the gate with noise.

**The loose setup, on real fills:** 14 trades, 5 winners (36%), **−2.56%** mean.
Worst of any setup. All 14 already had gate > 6, so "loose + gate > 6" is not a
filter — it is the trades already taken. It also carries the **lowest** average
forward score of any setup (5.10), and only 6 of 103 signals ever cleared
forward 7.

Its retirement rested on a survivorship-free replay: **+1.64% on survivors vs
−0.19% over 409 trades on the full universe.** Bringing it back needs a
mechanism that beats that number, not a hunch.

**Parameter budget.** ~5 trigger dials × 5 setups ≈ 25 parameters sounds
hopeless against 27 trades — but the space **factorises**: OversoldRecovery's
dials only affect OversoldRecovery's population, and detection is pure candle
arithmetic, so each setup's historical population runs to hundreds or thousands.
Free to compute, holdout-validatable with existing Lab machinery. This is the
part that can be validated properly *before* it goes live.

---

## 5. Factor inventory, ranked by testability

The organising principle: **classify every factor by cost × backtestability**,
and let that set priority — not how interesting it is.

### Free, candle-only, backtestable — the core

- **Sector-relative move** — did the name fall alone, or with its sector?
  Uses the existing `SectorEtfMap` (11 GICS sectors). Best free proxy for
  "news vs noise", and the highest-value single addition identified.
- **Signed volume / accumulation-distribution** — fixes the direction-blindness.
- **Close position in the daily range** — buyers stepping in vs sellers in control.
- **ATR-normalised dip depth** instead of raw RSI thresholds.
- **Gap character** — gapped and filled (liquidity) vs gapped and held (repricing).
- **Name-level volatility regime** — is this move unusual *for this stock*.

### Free-ish, non-candle, backtestable

- **Filing proximity** (EDGAR, free, fully historical) — the testable form of the
  news veto.
- **Filing deltas / FD3 distress** — already exists; should become a first-class
  factor rather than a bolt-on.
- **Earnings proximity** — a large confound under every mean-reversion setup.
  *Open: do we have historical earnings dates?*

### Paid, now backtestable (revised)

- **News at the dip.** Verified 12 Aug: the Tiingo key **has** news entitlement,
  and historical date-ranged queries work. This reverses an earlier claim that a
  sentiment veto could not be backtested.

---

## 6. News: what the probe actually showed

Endpoint verified working, including history.

**Use `crawlDate`, never `publishedDate`.** Tiingo backdates `publishedDate`
when it onboards a source or buys an archive; replaying on it would let articles
that did not exist at decision time veto a trade. `crawlDate` is Tiingo-recorded
and cannot be backdated.

**Article count is a market-cap proxy, not a signal.** Over the 14 real loose
trades, `corr(articles, return) = +0.514` — the *wrong* sign for the hypothesis,
driven by two heavily-covered leverage points (ORCL, VRT), below significance at
n=14. Do not build on volume.

**The content points at sector, not company.** Six of the nine losers are
semiconductor or EV names whose defining headline is the *sector* selling off —
VSH's −18.11% week led with "Why the SOXS Semiconductor Bear ETF Is Surging as
Chip Stocks Sell Off." The one clean company-specific case is **ORCL's S&P
downgrade (−3.02%)** — genuine material bad news, and it lost.

**Practical notes for any implementation:** filter content farms (`gurufocus`,
`simplywall.st`, `kalkinemedia`, `zacks`, `marketbeat` — 15 of VSH's 16
articles); Tiingo tags passing mentions, so "an article mentions this ticker"
over-fires on large caps and is silent on small ones.

### Costs (Haiku 4.5 at $1/$5 per MTok; Batch API −50%)

| Job | Cost |
|---|---|
| Full historical classification, all 2,499 scored signals | ~**$9** one-off (~$4.50 batched) |
| Daily classification, 100-name watchlist | ~**£6/month** (~£3 batched) |

The token cost is not the blocker. It never was.

---

## 7. Open questions — needed before a spec

Q1 (universe), Q2 (gate vs forward), Q3 (`Unknown`) and Q6 (ATR) are closed —
see D8–D11. Remaining:

| # | Question | Why it changes the design |
|---|---|---|
| Q4 | **Veto v1 ordering.** Filings are free and historical; news is now also backtestable but paid. Proposed as a cascade — see §9. | Decides what gates the loose setup's return, and the running cost. |
| Q5 | **Cross-setup comparability** — see §10. With one slot and no funnel score, the technical score is the sole ranker, so this is now load-bearing rather than a detail. | The most likely way a setup-first design fails quietly. |
| Q7 | **Is a sector-wide dip a better or worse reversion candidate?** Instinct says a name that fell only because its sector fell carries no company-specific information and should revert. **The measured evidence says the opposite** — six of nine loose losers were semis/EV names in a sector drawdown, and they kept falling. Must be answered by replay, not assumed, because it sets the *sign* of the sector factor. | Gets the sector factor backwards if assumed. |
| Q8 | **Dip-start definition and scan window** — see §9. | Bounds the news cost and decides what "explains the dip" even means. |

---

## 9. The veto system (replacing the funnel)

**Proposed cascade — cheapest and most-informative first.** Each stage can veto;
only survivors reach the next, so the paid stage runs on a minority of signals.

| Stage | Cost | Backtestable | Question |
|---|---|---|---|
| 1. Sector-relative | free | yes, now | Did the whole sector move? (sign per Q7) |
| 2. Filing proximity (EDGAR) | free | yes, now | Is there an 8-K / material filing in the window? |
| 3. Filing delta / FD3 distress | free | yes, now | Fundamental deterioration? *(already exists)* |
| 4. Earnings proximity | free* | *needs source check* | Is the dip an earnings reaction? |
| 5. News classification | paid | yes (crawlDate) | Does a specific event explain the drop? |

**Stage 5 asks a causal question, not a sentiment one.** "Is the tone negative?"
is the wrong prompt — content farms produce endless mildly-negative filler. The
question is *"did something happen that justifies a lower price?"*

### Dip-start definition (Q8)

The scan window runs from **dip start** to **signal time**, both dials:

- `dipStartMode` — local high within N bars / first bar of the down-run /
  ATR-drawdown threshold crossing
- `maxLookbackBars` — hard cap, because a slow decline is otherwise unbounded in
  both articles and tokens
- Articles filtered on **`crawlDate <= signal timestamp`** (never `publishedDate`)
- Content farms excluded before classification (`gurufocus`, `simplywall.st`,
  `kalkinemedia`, `zacks`, `marketbeat`)

### Cost consequence

Ripping out the funnel **removes** the per-gate-passer-per-day forward scoring,
which is the bulk of current Claude spend. The news veto adds back far less,
because it fires per *event* and only on cascade survivors. **Net spend should
fall.**

### Config to retire with the funnel

`ForwardBuyThreshold`, forward component weights, forward veto floor,
`ConvictionCeiling`, and the funnel-related `BlockReasons` become dead. Needs the
same settings audit the sleeve removal got.

---

## 10. Cross-setup comparability (Q5)

With one slot, no funnel score, and per-setup factor models, raw scores are not
comparable — **whichever setup's model is most generous wins the slot every
day**, regardless of merit. Four options:

| Option | Mechanism | Verdict |
|---|---|---|
| **A. Historical percentile** | Map each raw score to its own setup's score distribution. "92nd-percentile Breakout" vs "78th-percentile OversoldRecovery". | Free, needs no outcome data, removes the grossest bias. But it *equalises setups that are not equal* — a top-decile instance of a bad setup outranks a good instance of a good one. **Interim only.** |
| **B. Calibrate to outcome probability** | Map each setup's score to P(+25% in 40 days), estimated from that setup's own historical replay. Probabilities are natively comparable. | Correct in principle, and available — detection is free and historical, so the replay supplies the calibration set. Needs holdout to avoid fitting noise. |
| **C. Calibrate to expected right-tail value** | As B, but P(event) × magnitude. A setup with a lower hit rate and bigger winners correctly wins the slot. | **Matches the stated objective most directly.** Preferred target. |
| **D. Absolute bar per setup, no ranking** | Each setup gets a threshold calibrated to the same forward probability; anything clearing it is equivalent; ties broken arbitrarily or on liquidity. | Crude, but avoids false precision — and see the opportunity-cost point below. |

### The point that makes ranking less important than it looks

With one position and a ~30-day hold, the book is full roughly three-quarters of
the time. **The real comparison is not "which signal is best today" but "is this
signal good enough to spend the slot for a month."** Taking a mediocre signal
today forfeits the option on a better one next week.

That argues for an **absolute calibrated bar as the primary gate**, with ranking
used only to break ties among signals that clear it — i.e. **C for the bar, and
C again for the tie-break**, with D's discipline about not over-trusting small
score differences.

### Constraints on any calibration

- **It is a function of the dials.** Change a trigger dial and the setup's
  population changes, so the calibration must be recomputed as part of the swept
  config — not fitted once and left.
- **Freeze and version it** alongside the dials (D4), or the same
  unreadable-history problem returns by another route.
- **Graded membership interacts**: a name may belong to two setups; score under
  both and take the higher calibrated value.

---

## 8. Sequencing implied by the evidence

1. **Replay first.** 2,499 scored signals exist; 27 have outcomes. Everything
   gate-side is free to backfill, and nothing below can be evaluated without it.
2. **Fix the scoring shapes** — per-setup scoring functions with sign-correct
   direction. Domain logic, **zero new fitted parameters**.
3. **Add the free factors** — sector-relative first.
4. **Then, and only then, per-setup fitted weights** — there is nothing to fit
   them on yet.

Every angle measured on 12 Aug pointed the same way: **the losers were sector
moves, not company news.** Sector-relative is free, instant, needs no API, and
can be backfilled across all 2,499 signals this week.

### Cost of the rebuild, stated plainly

This resets the forward-evidence clock. Six weeks of live evidence on the current
configuration becomes non-comparable, having explicitly stopped building on
6 Aug to let it accumulate. That is an argument for replay-based validation
before the switch, not against the rebuild.
