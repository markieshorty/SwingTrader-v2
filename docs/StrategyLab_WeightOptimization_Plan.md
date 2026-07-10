# Strategy Lab: Weight Optimization — Plan

Three features, all building on the existing Strategy Lab (own-data + historic modes,
suggestion search, apply-to-production flow). Written up for later pickup — not
started.

## UI structure: Strategy Lab becomes two tabs

The Lab page splits into two tabs (same pattern as the Watchlists page's
Configuration / Stock Universe tabs):

1. **Optimizer** — the full-sweep idea (Feature 1). "Find me better weights": run the
   sweep, review the winner (diff table + stats + plain-English explanation), apply
   if convinced.
2. **A/B Testing** — the manual-experiment idea (Feature 2, with Feature 3's Claude
   analysis attached). "Test my idea": fiddle the dials, run them head-to-head
   against the current production config, read the comparison, optionally ask Claude
   what to try next.

Shared elements (data-source dropdown, dial form, data-status line) live once and are
used by both tabs. Today's single-page layout is essentially the A/B tab minus the
side-by-side comparison, so the split is mostly moving existing pieces, not
rebuilding them.

## Standing requirement: clarity over raw output

Applies to everything below. This came out of real confusion while using the existing
Lab: the historic result's `n=` counts and percentages meant nothing without a
footnote explaining they're raw returns, not market-adjusted (see the bucket-table
fix already shipped in
[`strategy-lab.component.html`](../SwingTrader.Angular/src/app/features/strategy-lab/strategy-lab.component.html)
as the reference pattern for "how much explanation is enough").

Any new number, stat, or result surfaced by these features needs a nearby,
plain-English explanation of what it means — not buried in a tooltip only, and not
assumed obvious:

- A stat with no unit or context (a bare count, a bare percentage) needs a label that
  says what's being counted/averaged, not just the number.
- Anywhere a metric could be misread (raw vs. market-adjusted, per-trade vs.
  cumulative, "better" meaning different things for different metrics) needs a short
  explanatory line near the number.
- A one-line plain-terms summary sentence near the top of a result (like the historic
  result card's existing summary paragraph) — put a similar sentence anywhere a new
  result type is introduced.
- Claude-generated text is held to the same bar: plain language, no unexplained
  jargon, say what a number means in the same breath as stating it. And **honest**
  language — see the explanation constraints under Feature 1.

Treat "is this understandable to someone who didn't build the backtester" as a
review gate before any of these features ship, the same way build/test passing is.

## Standing requirement: out-of-sample validation (anti-overfitting)

The single biggest risk in this whole plan. An optimizer that evaluates dozens to
thousands of candidate weight sets against one fixed dataset **will** find a config
that looks great on that history by chance — 8 free parameters against a few hundred
trades guarantees it. Without a guard, Feature 1 is a machine for manufacturing
convincing-looking bad ideas that feed straight into an Apply button.

Hard requirement for any optimizer/sweep result (not a nice-to-have):

- **Split the data.** Optimize on the earlier portion of the window (e.g. Oct 2023 –
  mid 2025) and validate the winning config on the held-out remainder it never saw.
- **Show both numbers in the UI**, clearly labelled ("found on", "held up on") with a
  plain-English line explaining why the second number is the one to trust.
- **Suppress or prominently flag** a winner whose held-out performance collapses
  relative to its in-sample performance. A config that only wins on the data it was
  tuned on is noise, and the UI must say so rather than present it as a discovery.
- Same idea, smaller scale, for the mechanical suggestion search that already exists
  if it's ever made more aggressive.

## Context: what exists today

- **Own-data mode** (`StrategyLabService.RunOwnDataAsync`): in-memory replay against
  the user's own closed trades. Cheap — sub-second.
- **Historic mode** (`BacktestConsumerFunction` + `HistoricBacktester`): full engine
  replay over ~1M daily bars across the S&P 1500, run async via `backtest-jobs`
  Service Bus queue, polled from the UI. Takes a few minutes per run.
- **Suggestions** (`StrategyLabService.Search`, own-data mode only): local nudge
  search — ±5% on one weight at a time (renormalized), a few Buy-threshold shifts,
  and the Breakout toggle. Top 3 candidates that beat the baseline by >0.05%. Not
  exhaustive, not available in historic mode, never auto-applies.
- **Diff table** (shipped): each suggestion shows old vs. new weight per dial in an
  expandable panel.
- **Apply flow**: always a manual, confirmed action (`applyToProduction()`) — owner
  only, confirm dialog, pushes weights + Buy threshold to production. This must not
  change for any feature below — optimization output always lands on a
  human-reviewed "Apply" click.

## Feature 1: Weight-sweep optimizer (the "Optimizer" tab)

**Goal:** search more broadly than the current one-dial nudge for a better weight
combination, and surface the best *validated* one for review and manual apply.

**Historic mode is the real optimizer.** Each historic evaluation generates its
trades fresh from the full engine (screener → scoring → entries → exits), so a config
is judged on what it would actually have *done*, not on how it re-filters a fixed
pile. This is the honest evaluation and the one worth investing in.

**Own-data mode is deliberately kept timid.** Two structural problems cap how
ambitious it can be:

- *Censored data:* own-data mode can only re-filter trades that were actually taken —
  and they were taken because the **production** weights scored them above threshold.
  Configs that would have selected different trades can't be evaluated at all, so an
  own-data "optimum" is really "the best cherry-pick of this specific pile."
- *Sample size:* an aggressive search (hundreds+ of candidates on the simplex) over a
  few hundred censored outcomes is the overfitting scenario above in its worst form.
  The existing nudge search is tolerable *because* it's timid.

So: own-data mode gets at most an iterated version of the existing nudge search
(repeat to convergence instead of one pass), clearly labelled as "small refinements
to how your history would have been filtered" — not presented as an optimizer. The
Optimizer tab's headline feature runs on historic data.

**Why not a brute-force grid:** 8 weights summing to 1.0 makes a real grid
combinatorially infeasible, and each historic evaluation costs minutes. v1 shape:

- **Capped candidate set** (~20–30 configs) evaluated in one background job — reuse
  the existing `BacktestConsumerFunction` async pattern, same "Queued → Running →
  Completed" UI, just N evaluations per job instead of one, returning the winner
  (plus top 3) instead of a single result. Candidates are seeded from **production
  weights plus structured perturbations** (single-dial shifts, a few two-dial trades,
  a few Dirichlet-sampled points for diversity). Do **not** seed from an own-data
  optimizer result — that would pipe the overfit-prone path into the honest one.
- Fan-out across parallel Function invocations (one candidate per Service Bus
  message, aggregate at the end) is the phase-2 upgrade if 20–30 serial evaluations
  proves too slow in practice.

**Objective metric — decided, not deferred:** maximize **market-adjusted expectancy
per trade** (raw expectancy minus SPY's return over the same holding periods, so a
config isn't rewarded merely for being long during a bull market), subject to two
hard constraints: a **minimum trade count** (same spirit as the existing `minKept`
guard — a config that wins on 12 trades is noise) and a **max-drawdown ceiling**
(reject candidates whose drawdown exceeds, say, 1.25× the baseline's). Profit factor
is reported but not optimized. If implementation finds market-adjusted expectancy
impractical to compute in the historic engine, raw expectancy with the same
constraints is the fallback — but flag that choice visibly rather than making it
silently.

**Validation:** per the standing requirement — optimize on the earlier split,
validate the winner out-of-sample, show both, suppress/flag winners that don't hold
up.

**UI (Optimizer tab):** a "Find optimum weights" action. Result: the winning weight
set shown via the existing diff table pattern (old = production, new = optimizer's
pick), in-sample and held-out stats side by side, the plain-English explanation
below, and the existing owner-gated Apply button. No auto-apply.

**Human-readable explanation of the result:** once the sweep picks a winner, produce
a plain paragraph via Claude (same endpoint as Feature 3, richer payload: winner +
baseline + a summary of all candidates tried + both validation numbers). Constraints
on that text, beyond the general clarity bar:

- Describe **what** changed and **what was observed** across candidates. Any "why"
  is conjecture and must be worded as such ("this pattern is consistent with…",
  "one possible reading…") — with ~25 configs that each differ in several weights at
  once after renormalization, the sweep cannot establish *why* a direction won, and
  the writeup must not manufacture a causal story that happens to sound convincing.
- Call out fragility explicitly: low trade counts driving a bucket, a winner whose
  edge concentrated in a few trades, in-sample vs. held-out gaps.
- The text is explanatory, not a new source of truth — the apply decision rests on
  the validated stats and the human, not on how persuasive the paragraph reads.

## Feature 2: Manual A/B — new dials vs. current dials (the "A/B Testing" tab)

**Goal:** run the dials currently in the form *and* the production baseline together,
so the user sees both results side by side instead of running one, remembering the
numbers, then running the other.

**Own-data mode:** cheap — fire both evaluations from one "Run" click, render a
side-by-side stat comparison (expectancy, profit factor, win rate, total return,
drawdown) with a delta indicator per metric and a one-line plain-English verdict
("Your dials beat production on expectancy but with a deeper worst drawdown").

**Historic mode:** doubles the cost of an already multi-minute job, so **opt-in** —
a checkbox "Also compare against production" shown in historic mode. When checked,
the job evaluates both configs (extend `BacktestJobMessage` to carry the second
weight set — an A/B is just a sweep of 2, so this shares Feature 1's multi-candidate
plumbing).

**Snapshot the baseline at queue time.** Production weights can change while a
multi-minute job runs (or between queue and read). Store the baseline weight values
on the `BacktestRun` row when the job is queued and label the comparison with what
was actually evaluated — never re-read "current production" at render time.

**UI (A/B Testing tab):** essentially today's Lab layout plus the comparison strip —
dial form, Run, side-by-side results, the existing mechanical suggestions, and
Feature 3's "Analyse this run" action.

## Feature 3: Claude-assisted "what to try next" (analysis only, no auto-apply)

**Goal:** after any run, send the result to Claude — headline stats, the
by-setup/conviction/exit breakdowns, and the config that produced it — and get back a
short written analysis plus a *suggested next config to try*. Purely advisory: no
run, no apply, nothing triggered by Claude itself. The user reads the suggestion,
loads the dials via the existing `tryDials()` mechanism, and clicks Run Simulation
themselves.

**Why this is worth having alongside the mechanical optimizer:** the nudge-search is
mechanical — fixed ±5% single-dial deltas. It can't reason about *why* a config
underperformed. A model reading the full breakdown can cross-reference signals a
fixed search can't: e.g. "StopLoss exits are 47% of trades at −5.5% average, double
the frequency of Target exits — worth testing higher entry-quality weighting" or
"the 7-conviction bucket underperforms the 6s again, consistent with earlier
backtests — worth testing a lower Buy threshold rather than trusting high conviction."

**Important framing:** a Claude suggestion is a hypothesis, not a verified result —
unlike the mechanical suggestions (which only appear because they already back-tested
better), it hasn't been simulated. Present it in "worth trying" language, and it must
flow through a normal Run Simulation before its numbers are treated as real. The same
honesty constraints as Feature 1's explanation apply: observations stated as
observations, causal readings marked as conjecture, fragility (small samples) called
out.

**Implementation shape:** one new endpoint (e.g. `POST /strategy-lab/analyse`) that
takes the just-completed result (own-data `LabResultDto` or historic
`HistoricResultDto`) plus the config that produced it, builds a prompt summarizing
stats + bucket breakdowns, calls Claude, and returns analysis text plus an optional
structured suggested config the UI feeds into `tryDials()`. User-triggered per click
(not automatic on every run) to control cost. No new job/queue infra. Feature 1's
sweep explanation reuses this endpoint with a richer payload — build this first and
Feature 1 calls it with winner + baseline + candidate summary instead of a single
run's result.

## Suggested build order

1. **Feature 3, own-data mode** — cheapest (one Claude call, no infra), and the
   fastest way to validate whether AI-assisted dial analysis is useful at all before
   the pricier investments.
2. **Two-tab UI restructure + own-data A/B** (Feature 2) — moving existing pieces
   into tabs plus a second in-memory evaluation per run; no async complexity. Gives
   the A/B Testing tab its full shape early.
3. **Feature 3 extended to historic mode** — same endpoint, fed `HistoricResultDto`;
   trivial once the own-data version exists.
4. **Historic capped sweep with validation split** (Feature 1 proper) — the
   multi-candidate background job, objective metric + constraints, out-of-sample
   validation, diff table + explanation on the Optimizer tab.
5. **Historic opt-in A/B** (Feature 2 extension) — an A/B is a sweep of 2; smallest
   incremental change once #4's multi-candidate plumbing exists.
6. **Own-data iterated nudge** (Feature 1's deliberately-timid sibling) — lowest
   value, last; may reasonably be cut if the historic optimizer proves out.

Feature 3 stays first as the cheapest signal on whether this whole direction is worth
it. The old plan's "own-data optimizer first" ordering is dropped on purpose: the
own-data path is structurally the weakest evaluation (censored, small sample), so it
shouldn't be the flagship — the historic sweep is.
