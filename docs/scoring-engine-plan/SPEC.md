# Scoring engine rebuild — build spec

12 Aug 2026. Companion to [README.md](README.md), which holds the decision log
and the evidence behind each choice. This document is what gets built.

**This is a hard cutover.** No feature flag, no shadow period, no A/B. The old
funnel is deleted, not disabled. Everything below assumes that, and the
validation gates in §8 exist because it is the only safety net.

---

## 1. What is being built

A setup-first technical scoring engine that replaces the two-stage funnel
(gate score → Claude forward score → combined /20).

```
                         OLD                                   NEW
  ┌──────────────────────────────────┐   ┌────────────────────────────────────┐
  │ 6 global weighted components     │   │ Detect setups (graded 0-1)         │
  │        ↓                         │   │        ↓                           │
  │ gate score, one weight vector    │   │ Per-setup factor model             │
  │        ↓                         │   │        ↓                           │
  │ Claude forward score (45/30/25)  │   │ Calibrate to expected right-tail   │
  │        ↓                         │   │        ↓                           │
  │ combined /20 decides Buy         │   │ Absolute bar + veto cascade        │
  └──────────────────────────────────┘   └────────────────────────────────────┘
```

**Objective function: right-tail capture.** The measured base rate for a +25%
move in 40 trading days is **7.37%**. A setup that cannot lift a signal
meaningfully above that is not earning its place, whatever it does to win rate
or expectancy. Every existing Lab metric optimises the opposite thing and must
be re-pointed.

### Decisions carried in

| | |
|---|---|
| Universe | Unchanged — S&P 1500 + Nasdaq-100 |
| Membership | Graded 0–1, not boolean; a name may belong to several setups |
| Dials | Per-account config rows, sweepable. **Account level only** — no per-regime conditioning in v1 |
| Freezing | Every signal records the dials and calibration version it was produced under |
| `Unknown` | Not scored, cannot be bought |
| `TrendFollowing` | Demoted from setup to context factor |
| `OversoldRecoveryLoose` | Returns as a dial (`recoveryLookbackBars: 0`), not an enum member |
| Forward score | Deleted |
| Trading rules | ATR multiples, not percentages |
| Lab | Rebuilt for the new shape, in this spec |

---

## 2. Phases

Ordered by dependency. P0 is a hard prerequisite for P2's calibration and P5's
sweeps, and — because this is a hard cutover — for any evidence that the new
engine is better than the old one.

| Phase | Deliverable | Blocking |
|---|---|---|
| **P0** | Replay / shadow book | Everything |
| **P1** | Detection rewrite — graded membership, per-setup dials | P2, P5 |
| **P2** | Per-setup scoring + calibration | P5, P6 |
| **P3** | Veto cascade | — |
| **P4** | ATR trading rules | P0 replay fidelity |
| **P5** | Lab rebuild | P1, P2 |
| **P6** | Funnel removal + fallout | P2 |

P3 and P4 are independent of each other and can land in either order. P6 must
land with or after P2 — the system cannot have both deciders live at once.

---

## 3. P0 — Replay / shadow book

Produces an outcome for every signal, not just the 27 that were filled.

### What exists

- `CounterfactualReplay.Run()` — gap-aware stop → target → trail → time-cap walk
  forward, 0.25%/side costs. Reusable as-is until P4 changes the dials it takes.
- `SetupDetector` — shared by the live pipeline, `HistoricBacktester` and the
  local tool, so detection over history already works.
- `BlobHistoricalCandleRepository` — bars for 2,671 symbols, 43% delisted.

### What to build

1. **`ShadowOutcome` table.** One row per (signal or synthetic signal) × dial-set
   version. Stores entry/exit dates and prices, exit reason, return, days held,
   **and the dial set + calibration version used**. Immutable once written.
2. **Backfill job** over the 2,499 existing scored signals.
3. **Synthetic population generator** — run detection over historical bars to
   produce signals that were never scored live. This is what gives each setup a
   population in the hundreds-to-thousands rather than the dozens.
4. **Nightly stamping** for new signals as bars arrive.

### Requirements

- **Frozen dials.** A replay records the dials it ran under. Re-running with
  different dials creates new rows; it never overwrites.
- **No lookahead.** Entry is the first bar strictly after the signal date.
  Any non-candle factor must be filtered on its own as-of timestamp
  (`crawlDate` for news — see §6).
- **Delisting-aware.** The candle store is 43% delisted and that is a feature —
  a replay that silently drops delisted names reproduces exactly the survivorship
  artefact that retired the loose setup (+1.64% on survivors vs −0.19% over 409
  trades on the full universe).

### Cost

Zero tokens. Candle arithmetic over blob-stored bars.

---

## 4. P1 — Detection

### Current state

13 hardcoded constants in `SetupDetector.Detect()`, first-match-wins ordering,
boolean membership:

```
OversoldRecovery      RSI < 35 · price > lowerBand · price > close[-4]
Breakout              price > upperBand · volRatio > 1.5 · macdHist > 0
MomentumContinuation  RSI 50–65 · EMA9 > EMA21 · macdHist > 0 · volRatio > 1.0
VolumeSpike           volRatio > 2.0 · 1-day change > 1.5%
TrendFollowing        EMA9 > EMA21 · RSI > 50 · price > midBand   [DELETE]
```

### Target state

Each setup becomes a declarative unit returning **membership ∈ [0,1]** plus a
quality vector. No ordering, no first-match-wins. A name may hold membership in
several setups simultaneously.

### Per-setup dials

**OversoldRecovery** (absorbs the loose variant)

| Dial | Default | Notes |
|---|---|---|
| `rsiCeiling` | 35 | |
| `rsiFloor` | 25 | **New.** The falling-knife guard exists in `ScoreRsi` today but *not* in detection |
| `lowerBandDistance` | 0 | currently a bare `>` |
| `recoveryLookbackBars` | 4 | **`0` = the retired loose variant** |
| `recoveryMinPct` | 0.0 | currently a bare `>`, zero magnitude required |
| `dipDepthAtr` | — | new, ATR-normalised |

**Breakout**

| Dial | Default | Notes |
|---|---|---|
| `upperBandDistance` | 0 | currently a bare `>` |
| `volumeFloor` | 1.5 | |
| `macdFloor` | 0 | |
| `priorRangeLookback` | — | **New and material.** There is currently no notion of *what it broke out of*; a break from a six-week coil and from three flat days are identical today. Plausibly why Breakout scores highest and backtests worst |
| `consolidationTightness` | — | new |

**MomentumContinuation**

| Dial | Default |
|---|---|
| `rsiFloor` / `rsiCeiling` | 50 / 65 |
| `emaFast` / `emaSlow` | 9 / 21 (the periods are themselves dials) |
| `macdFloor` | 0 |
| `volumeFloor` | 1.0 |

**VolumeSpike**

| Dial | Default |
|---|---|
| `volumeFloor` | 2.0 |
| `changeFloor` | 1.5% |
| `volumeAvgWindow` | — (currently implicit in `VolumeRatio`) |

**TrendFollowing — deleted as a setup.** Becomes a context factor (§5) available
to every setup. Rationale: it has no trigger, so it re-fires daily — SNOW hit it
on 23 of 29 trading days, and it averages 3.9 signals per symbol against
VolumeSpike's 1.0. It describes a state, not an event.

### Universe filters

Price and liquidity band ($15–500) becomes explicit config above detection rather
than implied.

### Consequence to expect

`TrendFollowing` + `Unknown` are **89% of account 440's signals**. With
TrendFollowing gone and `Unknown` unbuyable, roughly nine in ten scored names
will produce no candidate. That is the intended behaviour, not a regression.

---

## 5. P2 — Scoring and calibration

### The defect being fixed

`ScoreRsi` peaks at RSI 35 and decays to 0.25 by RSI 65. `ScoreMacd` scores
`histogram < 0 && rising` — the oversold turn — at **0.3**. One is tuned for mean
reversion, the other for momentum, and a single global weight vector can trade
one penalty against the other but cannot make both correct. `ScoreVolume` takes
only a magnitude and is direction-blind, so a high-volume selloff *raises*
conviction on a dip.

### Scoring shapes — no new fitted parameters

Per-setup scoring functions with sign-correct direction, hand-specified from
domain logic:

| Factor | OversoldRecovery | Breakout |
|---|---|---|
| RSI | low is good | elevated is confirmation |
| MACD | **negative-and-rising is the ideal state** | positive-and-rising |
| Volume | heavy is a warning (distribution) | heavy is confirmation |
| Trend context | dip inside an uptrend ≠ dip inside a downtrend | — |

This is definitional, not learnable. It is the majority of the available benefit
and costs nothing in parameter budget.

### Free factors to add

| Factor | Why |
|---|---|
| **Sector-relative move** | Best free proxy for "news vs noise". Uses the existing `SectorEtfMap` (11 GICS sectors). **Sign unresolved — see §10.** |
| Signed volume / accumulation-distribution | Fixes direction-blindness |
| Close position in daily range | Buyers stepping in vs sellers in control |
| ATR-normalised dip depth | Replaces raw RSI thresholds |
| Gap character | Gapped-and-filled (liquidity) vs gapped-and-held (repricing) |
| Name-level volatility regime | Is this move unusual *for this stock* |
| Trend context | Former `TrendFollowing`, demoted |

### Calibration — cross-setup comparability

Raw per-setup scores are **not comparable**. Without calibration, whichever
setup's model is most generous wins the single slot every day regardless of merit.

**Target: expected right-tail value.** Map each setup's raw score to
`P(+25% in 40 days) × magnitude`, estimated from that setup's own P0 replay
population, with holdout validation.

**Interim: percentile within setup**, if the replay population is too thin for a
stable mapping at launch. Percentile removes the grossest bias but *equalises
setups that are not equal* — a top-decile instance of a bad setup outranks a good
instance of a good one. Interim only, and it must be labelled as such in the UI.

**The absolute bar matters more than the ranking.** With one position and a
~30-day hold the book is full roughly three-quarters of the time, so the real
question is not "which signal is best today" but "is this good enough to spend
the slot for a month". Design accordingly:

- Primary gate: an **absolute calibrated bar**.
- Ranking: tie-break only, among signals that clear it.
- Do not act on small calibrated differences — they are not meaningful.

**Constraints:**

- The calibration is **a function of the dials**. Change a trigger dial and the
  population changes, so calibration must be recomputed *inside* the sweep, never
  fitted once and reused.
- **Freeze and version it** alongside the dials, or history becomes unreadable by
  a second route.
- **Graded membership**: score under every setup the name belongs to; take the
  highest calibrated value.

---

## 6. P3 — Veto cascade

Replaces the funnel entirely. Vetoes are **boolean and unweighted**, so they
cannot quietly accumulate fitted parameters. Ordered cheapest-and-most-
informative first; only survivors reach the next stage, so the paid stage runs on
a minority.

| # | Stage | Cost | Backtestable | Question |
|---|---|---|---|---|
| 1 | Sector-relative | free | yes | Did the whole sector move? |
| 2 | Filing proximity (EDGAR 8-K) | free | yes | Material filing in the window? |
| 3 | Filing delta / FD3 distress | free | yes | Fundamental deterioration? *(exists)* |
| 4 | Earnings proximity | free | yes, after backfill | Is the dip an earnings reaction? |
| 5 | News classification | paid | yes | Does a specific event explain the drop? |

### Scan window

Runs from **dip start** to **signal time**. Both ends are dials:

- `dipStartMode` — local high within N bars / first bar of the down-run /
  ATR-drawdown threshold crossing
- `maxLookbackBars` — hard cap. A slow decline is otherwise unbounded in both
  articles and tokens.

### News stage requirements

- **Filter on `crawlDate`, never `publishedDate`.** Tiingo backdates
  `publishedDate` when it onboards a source or buys an archive; replaying on it
  would let articles that did not exist at decision time veto a trade.
  `crawlDate` is Tiingo-recorded and cannot be backdated.
- **Exclude content farms before classification** — `gurufocus`, `simplywall.st`,
  `kalkinemedia`, `zacks`, `marketbeat`. They were 15 of VSH's 16 articles in the
  probe; classifying them is paying tokens to read noise.
- **Ticker tagging is loose.** Tiingo tags passing mentions — a probe for AAPL
  returned an article about Lumentum. "An article mentions this ticker"
  over-fires on large caps and is silent on small ones.
- **The prompt asks a causal question, not a sentiment one**: *"did something
  happen that justifies a lower price?"* Not *"is the tone negative?"* — content
  farms produce endless mildly-negative filler.
- **Article count is not a signal.** It is a market-cap proxy: `corr(articles,
  return) = +0.514` over the 14 real loose trades — the wrong sign, driven by two
  heavily-covered leverage points, below significance at n=14.

### Earnings data prerequisite

Tiingo fundamentals is entitled but returns only the **fiscal period end**, not
the announcement date — not usable for proximity.

The source is **EDGAR 8-K Item 2.02** ("Results of Operations and Financial
Condition"), free and historically complete. `FilingEventScanService` already
parses item codes; 2.02 is simply not in `RoutedItemCodes`.

Two free work items:

1. **Backfill `Filings` from EDGAR's submissions API.** The current store holds
   1,935 filings over 441 symbols, 10-Q/10-K only, and **has holes** — a
   quarterly filer can never be more than ~45 days from its nearest 10-Q, yet 7
   of 14 loose trades measured 69–134 days. Any proximity test on the current
   store is unreadable.
2. **Add 2.02 capture** so announcement dates accumulate. Detection only — costs
   no tokens.

---

## 7. P4 — ATR trading rules

Percentages mean different things across names: a fixed 15% stop is ~1 ATR on a
volatile small cap and ~5 ATR on a mega cap. The loose setup ran a **1.5% trail
distance against a 1.62% mean absolute daily move** — a trail tighter than a
typical day's wiggle, exiting on noise essentially at random.

### Changes

| Component | Change |
|---|---|
| `SetupTactics` | `StopLossPct` / `TargetPct` / `TrailingActivationPct` / `TrailingDistancePct` → ATR multiples. Migration must convert or mode-flag existing rows across all 31 books |
| `Trade` | **New field: entry ATR**, stamped at fill. Without it the stop cannot be reconstructed after the fact |
| `CounterfactualReplay` | Accept multiples |
| `HistoricBacktester` | Accept multiples — **if this is missed, every backtest silently keeps using percentages** |
| `MonitorService` | Trailing-stop maintenance in ATR terms |
| `ExecutionService` | Stop/target placement at fill |
| Lab dials | Ranges expressed in multiples |

`TargetMode = AtrScaled` already exists — partial machinery, not a complete path.

### Re-open once P4 lands

The 25% target cap was measured as near-irrelevant (13 of 1,034 backtest trades
reached it) under the *old* engine. Under a right-tail objective with ATR-scaled
targets it may bind. Re-measure; do not carry the old conclusion forward.

---

## 8. Validation gates — the only safety net

Hard cutover means no A/B and no rollback short of a git revert plus a migration
reversal. Every gate below must pass **on replay** before the flip.

| Gate | Requirement |
|---|---|
| **G1 — Base rate lift** | Each enabled setup's calibrated top bucket must beat the 7.37% unconditional +25%/40d base rate by a stated margin, on holdout |
| **G2 — Holdout** | Dial sweeps validated on a held-out window the optimiser never saw. Existing Lab machinery (robust scoring, train/holdout split) carries over |
| **G3 — Survivorship** | Replay population includes delisted symbols. A run that silently excludes them is void — this is exactly what produced the loose setup's false +1.64% |
| **G4 — No lookahead** | Every non-candle factor filtered on its own as-of timestamp. News on `crawlDate`. Filings on filing date |
| **G5 — Reproducibility** | Re-running a stored replay with its recorded dial set and calibration version reproduces it exactly |
| **G6 — Sector sign** | §10 resolved empirically before the sector veto is enabled |
| **G7 — Old-engine comparison** | New engine replayed against the old engine's configuration over the same window, and beats it on the right-tail objective |

G7 is the one that replaces the A/B a flag would have given.

---

## 9. Fallout inventory

Explicitly in scope. A hard cutover means all of this must land in the same
change or the system does not build.

### 9.1 Deleted outright

| Item | Location |
|---|---|
| Forward scoring pipeline | `FunnelScores`, forward legs of `ResearchPipeline` |
| Combined /20 | everywhere |
| `ForwardBuyThreshold`, forward component weights, forward veto floor | `AccountRiskProfile`, settings, DTOs |
| `ConvictionCeiling` | risk profile + settings |
| `FunnelEnabled` | config |
| Funnel `BlockReasons` — `ForwardVeto`, `ConvictionCeiling` | `BlockReasons` constants |
| `SetupType.TrendFollowing`, `SetupType.OversoldRecoveryLoose` | `Enums.cs` — retire as enum members; loose becomes a dial |
| `ScoreRsi` / `ScoreMacd` / `ScoreVolume` / `ScoreSetupQuality` global forms | `ConvictionScorer` |

### 9.2 Rebuilt

| Component | Notes |
|---|---|
| `SetupDetector` | Graded membership, config-driven dials |
| `ConvictionScorer` | Per-setup factor models |
| `ResearchPipeline` | Stage-2 logic gone; veto cascade in; ranking by calibrated score |
| **Strategy Lab** | Sweep targets become per-setup dials + calibration. `SweepOptimizer`, `MlSweepOptimizer`, `CmaEs` search spaces, `BacktestConfigFactory`, `LabAnalysisPrompts`, `BacktestApplyExtractor`, `StrategyLabService`, `StrategyLabEndpoints`, `StrategyLabContracts` |
| `HistoricBacktester` | New engine + ATR rules |
| `CounterfactualReplay` | ATR rules |
| `AlmostTradesService` | Currently reads `ForwardBuyThreshold`; also **replays with today's tactics against signals scored under older ones** — fix as part of the freezing work |
| `ForwardScorecardService` | Repurpose or delete |
| `RefinementService` / `ApplyRefinementService` | Suggests global weight changes; must target per-setup dials |
| `StrategyShareService` | Snapshot shape changes. Note `ConfigFingerprint` was ripped out on 11 Aug — re-check current state before assuming |
| `SetupScreens` | Union screener keyed on setup types that are changing |

### 9.3 Angular

| Surface | Change |
|---|---|
| Signal cards | Forward chip (added 11 Aug) — remove; 7-column grid reverts |
| Dashboard | `fwd-chip` — remove |
| Today's Signals | Forward score column and threshold labelling |
| Intelligence page | Exists **as the funnel flip-evidence surface** — repurpose or delete wholesale |
| Scorecard | Forward panel, blocked-Buy panel |
| Trade History → Almost tab | Threshold references |
| Settings | Every funnel dial; new per-setup dial editors; ATR multiple inputs |
| Strategy Lab | Largest single surface — sweep config, diff tables, candidate lists, apply dialogs |
| `dtos.ts` | `SetupType` union, all funnel-bearing DTOs |

### 9.4 Data

| Change | Notes |
|---|---|
| `ShadowOutcome` table | New |
| Per-setup dial config table | New, per account |
| Calibration table + version | New |
| `Trade.EntryAtr` | New |
| `SetupTactics` | Percent → ATR multiples, 31 rows |
| `StockSignal` | `ForwardScore` and combined-score semantics; membership + calibrated score + dial-set version stamped |
| **Historic signals, trades and backtest runs** | **Discard.** No retention, no marking, no back-compat. Decided 12 Aug: the old engine was deemed flawed, so its history has no evidential value |

**Destructive migrations are permitted.** This materially simplifies the change:

- `StockSignals` funnel columns can be dropped rather than nullable-and-ignored.
- Stored `BacktestRun` result/request JSON can be **wiped**, not migrated. The
  legacy-name readers added on 12 Aug (`BacktestApplyExtractor.GateThreshold()`
  and the Lab's `gateOf()` fallback to `buyThreshold`) exist only to read
  pre-rename runs — **delete them as part of P5/P6** rather than extending them
  to a third schema.
- No dual-read paths, no version-tolerant deserialisation, no shims.

**This does not cancel the freezing requirement (D4).** Freezing exists so the
*new* system's own history stays readable the first time dials are swept — it is
forward-looking, not an audit trail for the old engine. Without it the second
sweep makes the first sweep's results uninterpretable, which is the defect that
already bit the Almost tab.

**Migration warnings from prior incidents:** `HasData` / `HasDefaultValue`
constants need EF migrations or startup is fatal; a scaffolded `decimal(18,2)`
will round penny stocks — use `(18,6)`; verify scaffolded `defaultValue` on new
bool columns rather than trusting it.

### 9.5 Tests

~961 currently pass. Expect large-scale breakage in `SetupDetectorTests`,
`SetupTacticsRepositoryTests`, `SweepOptimizerTests`, `MlSweepOptimizerTests`,
`StrategyLabServiceTests`, `BacktestApplyExtractorTests`,
`HistoricBacktesterRulesTests`, `ScreenerUnionBacktestTests`,
`ConfigFingerprintTests`, `FunnelScoresTests`. Rewrite, do not delete — the
assertions encode behaviour worth preserving in the new shape.

### 9.6 Operational

- Deploy order: migrations → Functions → API. Wait ~5 min after Functions
  deploys before queue sends.
- Never `deploy-infra` as part of this — it resets the API to its bootstrap
  placeholder.
- Research and Execution are once-daily and claimed via `JobLogEntries`'
  unique `(AccountId, JobType, JobDate)` index. A mid-day cutover leaves that
  day half-processed under two different engines; **flip outside market hours**.

---

## 10. Open — resolve during P0, not by assumption

**Q7: Is a sector-wide dip a better or worse reversion candidate?**

Intuition says a name that fell only because its sector fell carries no
company-specific information and should revert. **The measured evidence says the
opposite** — six of nine loose losers were semiconductor or EV names in a sector
drawdown, and they kept falling. VSH's −18.11% week led with *"Why the SOXS
Semiconductor Bear ETF Is Surging as Chip Stocks Sell Off."*

This sets the **sign** of the highest-value free factor in the design. Getting it
backwards vetoes exactly the wrong signals. It must come out of replay
(gate G6), not from either instinct.

---

## 11. Costs

| Item | Cost |
|---|---|
| P0 replay, P1 detection, P2 scoring, P4 ATR | **Zero tokens** — candle arithmetic |
| EDGAR backfill + 2.02 capture | **Free** |
| Historical news classification, all 2,499 signals | ~**$9** one-off (~$4.50 batched, Haiku 4.5) |
| Ongoing news veto | Per event, on cascade survivors only |

**Net running cost should fall.** Deleting the funnel removes per-gate-passer
per-day forward scoring, which is the bulk of current Claude spend. The news veto
fires far less often.

---

## 12. Accepted risks

1. **The evidence clock resets.** Six weeks and 27 settled signals are discarded
   on flip, deliberately (§9.4). G7 substitutes replay evidence for forward
   evidence. Live evidence restarts at zero, at roughly one settled trade a week.
2. **Rollback is effectively one-way.** Git revert plus migration reversal, and
   with destructive migrations there is no data to revert *to*. Flip outside
   market hours with a DB backup taken first — the backup is the only rollback
   path that exists.
3. **Calibration may be thin at launch** for low-frequency setups
   (OversoldRecovery: 28 live signals). Percentile fallback is specified, and the
   synthetic replay population is the mitigation.
4. **Detection becomes load-bearing.** With no-setup-no-trade, a detection bug
   silently stops all trading rather than degrading it. Needs an explicit
   "signals detected today" health metric and alerting.
