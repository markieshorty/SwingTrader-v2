# Strategy Lab: Weight Optimization — Plan

Three features, all building on the existing Strategy Lab (own-data + historic modes,
suggestion search, apply-to-production flow). Written up for pickup once available —
not started.

## Standing requirement: clarity over raw output

Applies to all three features below, not just the newly written parts. This came out
of real confusion while using the existing Lab: the historic result's `n=` counts and
percentages meant nothing without a footnote explaining they're raw returns, not
market-adjusted (see the bucket-table fix already shipped — [`strategy-lab.component.html`](../SwingTrader.Angular/src/app/features/strategy-lab/strategy-lab.component.html)
— as the reference pattern for "how much explanation is enough").

Any new number, stat, or result surfaced by these features needs a nearby,
plain-English explanation of what it means — not buried in a tooltip only, and not
assumed obvious. Concretely, for anything new built from this plan:

- A stat with no unit or context (a bare count, a bare percentage) needs a label that
  says what's being counted/averaged, not just the number.
- Anywhere a metric could be misread (raw vs. market-adjusted, per-trade vs.
  cumulative, "better" meaning different things for different metrics) needs a short
  explanatory line near the number, not left to the reader to infer from a
  half-remembered convention.
- A one-line summary sentence near the top of a result (like the historic result
  card's existing summary paragraph) is the right place for "what does this mean"
  in plain terms — put a similar sentence anywhere a new result type is introduced.
- When Claude is generating text (Features 1 and 3), the same bar applies to its
  output: plain language, no jargon that isn't explained, and it should say what a
  number means in the same breath as stating it, not assume the reader already knows.

Treat "is this understandable to someone who didn't build the backtester" as a
review gate before any of these features ship, the same way build/test passing is.

- **Own-data mode** (`StrategyLabService.RunOwnDataAsync`): in-memory replay against
  the user's own closed trades. Cheap — sub-second.
- **Historic mode** (`BacktestConsumerFunction` + `HistoricBacktester`): full engine
  replay over ~1M daily bars across the S&P 1500, run async via `backtest-jobs`
  Service Bus queue, polled from the UI. Takes a few minutes per run.
- **Suggestions** (`StrategyLabService.Search`, own-data mode only): local nudge
  search — ±5% on one weight at a time (renormalized), a few Buy-threshold shifts,
  and the Breakout toggle. Top 3 candidates that beat the baseline by >0.05%. Not
  exhaustive, not available in historic mode, never auto-applies.
- **Diff table** (just shipped): each suggestion shows old vs. new weight per dial in
  an expandable panel.
- **Apply flow**: always a manual, confirmed action (`applyToProduction()`) — owner
  only, confirm dialog, pushes weights + Buy threshold to `AccountRiskProfile`/prod
  weights. This should not change for either feature below — auto-optimization must
  still land on a human-reviewed "Apply" click.

## Feature 1: Full weight-sweep optimizer

**Goal:** instead of "nudge one weight ±5%, keep top 3," search more broadly for a
better weight combination and surface the best one as an "optimum" the user reviews
and applies.

**Why not a brute-force grid:** 8 weights summing to 1.0 makes a real grid search
combinatorially infeasible, especially for historic mode where each evaluation costs
minutes. A smarter search is needed:

- **Own-data mode** (cheap, in-memory): can afford a genuinely large search —
  e.g. iterated coordinate ascent (repeat the existing nudge-search to convergence,
  not just one pass) or ~500–2000 random weight vectors on the simplex (Dirichlet
  sampling so they sum to 1) evaluated in memory, keep the best by a chosen metric
  (expectancy, profit factor, or a blended score — needs a decision, see "Open
  questions").
- **Historic mode** (expensive, ~1M bars per run): must run as a background job like
  today's single backtest, but evaluating dozens/hundreds of candidates serially
  would take hours. Options, in order of effort:
  1. Cap it — evaluate a small fixed set of candidates (e.g. 20–30 promising ones,
     maybe seeded from the own-data optimizer's result) rather than a true sweep.
  2. Parallelize across multiple Function invocations / Service Bus messages, one
     candidate per message, then aggregate — bigger infra change (fan-out/fan-in),
     probably a phase-2 item.
  3. Accept a much longer async job (tens of minutes) with clear UI progress and no
     parallelism, as a v1.

**Recommended v1 scope:** own-data mode gets the real optimizer (cheap, safe to be
ambitious). Historic mode gets a capped sweep (~20–30 candidates) reusing the existing
async job pattern — same "Queued → Running → Completed" UI, just evaluating multiple
weight sets per job instead of one, returning the winner (plus maybe top 3) instead of
a single result.

**UI:** a new "Find optimum weights" action alongside "Run Simulation" (not
replacing it — a user should still be able to test one specific config manually).
Result: the winning weight set shown via the diff table pattern already built
(old = whatever's currently in the dials or production, new = optimizer's pick),
plus its simulated stats, plus the existing owner-gated Apply button. No
auto-apply — same guardrail as today's suggestions.

**Open question to resolve before building:** what should the optimizer maximize?
Raw expectancy could favor a high-return/high-variance corner; profit factor could
favor low trade-count corners that got lucky. Likely need a floor (e.g. `minKept`
trade count, same guard the existing `Search()` uses) plus probably optimize on
expectancy with profit factor and max-drawdown as tie-breakers/guardrails rather than
a single scalar. Worth deciding explicitly rather than picking one silently.

**Human-readable explanation of the result (not just numbers):** the mechanical
suggestions today just show a description string like "Increase Volume weight to 24%
(others rebalanced)" plus the diff table — accurate, but it doesn't say *why* that
direction won or what it means. Once the optimizer has picked a winner (from
potentially dozens/hundreds of evaluated candidates), it should also produce a plain
paragraph: which weights moved and in which direction, and what in the underlying
data justifies it — e.g. "Volume weight increased from 21% to 28%, mostly reallocated
from Sentiment (16% → 9%). Across the candidates tried, higher-volume-weighted
configs consistently reduced StopLoss-exit frequency without giving up much win rate,
suggesting volume confirmation is doing more useful filtering here than sentiment is."

This is naturally a second job for Claude (same mechanism as Feature 3's analysis,
reused rather than duplicated): after the sweep/optimizer finishes, feed it the
winning config, the baseline it beat, and — ideally — a summary of the candidates
tried (not just the winner) so it can describe *why* this direction won relative to
the alternatives, not just describe the winning numbers in prose. Keep the same
guardrail as Feature 3: this text is explanatory, not a new source of truth — the
actual "should I apply this" decision still rests on the simulated stats and the
human's judgment, not on how convincing the paragraph reads. Worth being deliberate
that the writeup doesn't overstate confidence beyond what the backtest sample size
actually supports (e.g. call out low trade counts in a bucket if that's driving the
result, rather than writing confidently past it).

## Feature 2: Manual A/B — new dials vs. current dials

**Goal:** run the dials currently in the form *and* a baseline (production weights,
or whatever was last applied) side by side, so the user sees both results at once
instead of running one, remembering the numbers, then running the other.

**Own-data mode:** cheap — trivial to fire both evaluations from one "Run" click and
render two result cards (or a single card with two columns / a delta row per stat:
expectancy, profit factor, win rate, total return, drawdown). No async complexity.

**Historic mode:** doubles the cost of an already multi-minute job. Recommend this be
**opt-in**, not automatic — e.g. a checkbox "Also compare against production" next to
Run Simulation, only shown/enabled in historic mode. When checked, queue two
`BacktestRun` rows (or extend `BacktestJobMessage` to carry a second weight set) and
render both once both complete.

**UI:** results panel gets a "vs. production" comparison strip when a baseline is
available — reuse the stat-grid layout already in the historic result card, just
duplicated per side with a delta column (or arrow + color) showing which one's
better for each metric.

## Feature 3: Claude-assisted "what to try next" (analysis only, no auto-apply)

**Goal:** after any run (own-data or historic), send the result to Claude — headline
stats, the by-setup/conviction/exit breakdowns, and the weights/threshold/breakout
config that produced it — and ask for a short written analysis plus a *suggested next
config to try*. Purely advisory: no run, no apply, no simulation triggered by Claude
itself. The user reads the suggestion, then manually loads those dials (reusing the
existing `tryDials()` mechanism the mechanical suggestions already use) and clicks Run
Simulation themselves if they want to test it.

**Why this is worth it alongside Feature 1's mechanical optimizer:** the nudge-search
in `Search()` is mechanical — it only tries fixed ±5% single-dial deltas and keeps
whatever clears a small bar. It can't reason about *why* a config underperformed. A
model looking at the full breakdown could notice things a fixed search can't, e.g.
"StopLoss exits account for 47% of trades and average -5.5%, more than double the
frequency of Target exits, which suggests tightening entry-quality weights rather than
this bucket being unavoidable" or "the 7-conviction bucket is inversely predictive
here too, consistent with prior data — the model's own confidence isn't reliable
above 6." Cross-signal reasoning like that is exactly what a fixed nudge-search
misses.

**Important framing:** a Claude suggestion is a hypothesis, not a verified result —
unlike the mechanical suggestions (which only appear because they already
back-tested better), this hasn't been simulated. It must flow through the normal
Run Simulation step before anyone treats the numbers as real. Should be presented
as "worth trying" language, not a result.

**Implementation shape:** a small new endpoint (e.g.
`POST /strategy-lab/analyse`) that takes the just-completed result (own-data
`LabResultDto` or historic `HistoricResultDto`) plus the weights/threshold that
produced it, builds a prompt summarizing the stats + bucket breakdowns, calls Claude,
and returns free text (analysis paragraph) plus optionally a structured suggested
`LabWeightsDto`/threshold/breakout the UI can feed straight into `tryDials()`. Cheap
to build — no new job/queue infra, just one Claude call per "Analyse this run" click
(user-triggered, not automatic on every run, to control cost). This same
endpoint/prompt-building code is reused by Feature 1's "explain the sweep result"
writeup below — build this one first and Feature 1 just calls it with a richer
payload (winner + baseline + candidate summary) instead of a single run's result.

## Suggested build order

1. Feature 3, own-data mode first (cheapest — one Claude call, no job/queue infra,
   no optimizer math to get right; also the fastest way to validate whether the
   analysis is actually useful before investing in Feature 1's optimizer).
2. Own-data mode optimizer (Feature 1, cheap, no infra changes — biggest mechanical
   bang for effort).
3. Own-data mode manual A/B (Feature 2, also cheap, reuses existing evaluate path).
4. Feature 3 extended to historic mode (same endpoint, just fed `HistoricResultDto`
   instead — trivial once the own-data version exists).
5. Historic mode capped sweep (Feature 1 extension) — reuse `BacktestConsumerFunction`
   pattern, extend `HistoricBacktestRequest`/`BacktestJobMessage` to carry multiple
   candidate weight sets, return winner + top-N.
6. Historic mode opt-in A/B (Feature 2 extension) — smallest incremental change once
   #5's multi-candidate job plumbing exists (an A/B is just a sweep of 2).

Steps 5–6 share a lot of the "run N weight sets as one background job" plumbing, so
sequencing the historic-mode optimizer before historic A/B avoids building the same
multi-candidate job infrastructure twice. Feature 3 is deliberately first because it's
the cheapest signal on whether this whole direction (more automated dial-tuning
assistance) is worth the later, pricier investments.
