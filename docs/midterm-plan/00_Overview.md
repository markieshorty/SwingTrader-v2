# Mid-Term Trading — Overview

**Status: Planned (spec agreed 12 Jul 2026)**

**Objective:** a second, human-executed strategy alongside the swing bot: 1–12 month
holds, thesis-driven, structural-growth focused. The app *recommends* — picks with
written theses, then weekly verdicts (Hold / Add / Trim / Sell-matured / Cut-thesis-
broken) — and the user executes manually at Trading 212. The app never places
mid-term orders.

## Why (evidence base)

The 5-year backtest re-baseline (Jul 2026) showed the 10-day swing timeframe has no
indicator edge net of costs (+10.5% vs SPY's +84% total return; best of 200 dial
candidates still negative market-adjusted). Mid-term holds change the economics:
costs amortize over 20%+ moves, the competition is thesis quality (Claude's actual
strength) not execution latency, and the sentiment archive/fundamental data now
being collected are exactly mid-term inputs. Mid-term picks cannot be backtested —
**the forward scorecard IS the evidence**, which is why it ships in Phase M1, not
last.

## Decisions (locked with Mark, 12 Jul 2026)

1. **Capital allocation:** available cash is split by percentage between mid-term
   and swing (new settings dials). The swing bot's sizing budget is capped by its
   share; the mid-term page shows its share as the budget guide for manual buys.
2. **Cadence:** monthly pick refresh, weekly reviews.
3. **No overlap:** a symbol can never be held by both strategies at once —
   enforced both directions (guardrail, not convention).
4. **Structural growth only.** Hype/attention-velocity detection is explicitly
   out of scope for v1 (revisit once the review loop is proven).

## Principles (inherited from the edge plan, plus new ones)

- Verify before build; tests ship with the change; no lookahead; fail open on
  data outages.
- **Thesis as contract:** every pick stores a structured thesis at selection time
  (expected value, horizon, invalidation conditions). Reviews evaluate against the
  contract, never re-litigate from scratch.
- **Reconcile, don't self-report:** manual T212 positions are detected via the
  existing portfolio sync and linked to theses; the user confirms links, they
  don't type in trades.
- **Scorecard from day one:** every pick is benchmarked against SPY from its
  recommendation date, visible on the page. First cycle runs as a paper shadow
  book before real money follows.
- **Clarity rule:** every verdict and pick carries plain-English reasoning a
  non-quant can follow (same rule as the Strategy Lab).

## Phases

| Phase | Doc | Delivers |
|---|---|---|
| M1 | 01 | Capital-split settings, entities + scorecard skeleton, mid-term screener (1500 → 80) |
| M2 | 02 | Claude selection (1–8 picks, structured theses), picks page, shadow book |
| M3 | 03 | T212 position detection + thesis linking, no-overlap guardrails both directions |
| M4 | 04 | Weekly review agent (5 verdicts vs the thesis contract), email digest, scorecard v2 |

Each phase is independently shippable and stops for user verification, same as the
edge plan.

## Explicitly out of scope (v1)

- Automated mid-term order placement (the app recommends; the human executes).
- Hype/attention-velocity signals.
- Options, non-US listings, crypto.
- Backtesting mid-term picks (impossible honestly; the scorecard replaces it).
