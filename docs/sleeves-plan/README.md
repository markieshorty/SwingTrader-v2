# Capital Sleeves: SPY Core / Factor Tilt / Swing

Status: **SPEC — drafted 5 Aug 2026, not built.**

## Motivation

Two days of honest measurement (docs/regime-setups-plan/results.md,
docs/deep-history-plan) established: the swing strategy's edge is real but
small (strict OR +0.27%/trade OOS; +0.53%/trade over 26y) and it cannot
carry an account through real cycles alone (26y replay: 8.5% total, 95.4%
max DD, vs SPY 766%). Meanwhile capital stands idle on no-signal days, and
every pound is exposed to a single unvalidated strategy.

The response is composition, not more optimization: each account splits its
capital into independently-managed **sleeves** with different edge
mechanisms, different clocks, and separately-measured P&L:

- **SPY core** — market beta. The boring 766%. Buy-and-hold + band
  rebalancing.
- **Factor tilt** — monthly momentum+quality rotation. The only approach in
  this space with overwhelming existing out-of-sample evidence (decades,
  many markets). Realistic goal: a few % over SPY, market-like drawdowns.
- **Swing** — the existing system, unchanged, sized against its slice.
  Continues as the live laboratory for the OR edge + interim regime book.

Not investment advice; Mark's own allocation choices, defaults conservative.

## Design principles

1. **Sleeves are watertight.** A sleeve trades only its slice. A swing
   drawdown can never eat the SPY core; the factor sleeve can't lever
   against swing cash. Enforced at sizing time from sleeve-level equity,
   not account equity.
2. **Per-sleeve P&L is first-class.** Every trade is stamped with its
   sleeve; the dashboard answers "which sleeve is earning" per account.
   This is the evidence layer that makes the composition self-correcting.
3. **Same discipline as everything else.** The factor sleeve backtests on
   the deep dataset and must look sane on 2000+ before live. Allocation
   changes are validated, audited settings changes. No sleeve ships
   enabled; defaults preserve today's behaviour exactly (Swing 100%).
4. **Cheap.** SPY core: ~zero marginal cost. Factor sleeve: no Claude in
   the loop (pure price/fundamental ranks); a dozen orders/month. The only
   metered spend stays the swing sleeve's existing research.

## Data model

`AccountAllocation` (one per account):
- `SpyCorePct` + `FactorTiltPct` + `SwingPct` = 100 (validated). Default
  0 / 0 / 100 — ships inert.
- `ParkIdleSwingCash` (bool, default false): swing-sleeve cash not
  reserved by open/pending positions is parked in SPY and sold down
  automatically when an entry needs it (fixes standing-on-cash days
  without changing swing behaviour).
- `FactorTopN` (default 15), `FactorRebalanceDay` (default first trading
  day of month).

Trades/Positions gain `Sleeve` (enum: Swing default, SpyCore, Factor) —
nullable-backfilled as Swing for history.

## Phases

### P1 — Allocation model + SPY core + idle-cash parking

- AccountAllocation settings page (the "pie": three sliders that must sum
  to 100, same validation pattern as risk books) + API + migration.
- Sleeve-aware sizing: swing position sizing reads sleeve equity
  (account equity x SwingPct) everywhere account equity is read today.
- SPY core manager (new monitor-cycle step, platform-simple): target
  value = equity x SpyCorePct; trade only when drift > 5% of sleeve
  (band rebalancing - a few orders/year). Uses existing T212 execution
  path; orders stamped Sleeve=SpyCore.
- Idle-cash parking: swing sleeve's unreserved cash -> SPY when
  ParkIdleSwingCash; parked value is NOT swing equity for sizing (no
  double-count); sold down before an entry that needs the cash (the 5%
  market-order reserve discipline applies on the sell-then-buy chain).
- Dashboard: per-sleeve value + P&L split on the portfolio page.
- Tests: allocation validation, sleeve-equity sizing, drift-band maths,
  parking reserve/release.

### P2 — Factor sleeve (Lab first, like everything else)

- **P2a — backtest mode.** New Lab run mode "factor": monthly-rebalance
  simulation over the historic store (deep window supported): rank by
  6-12mo return skipping the most recent month, quality screen (dollar-
  volume + price floor + positive momentum consistency; fundamentals
  optional later), hold top N, replace only what exits the top third
  (turnover control). Costs: engine path is FAR simpler than the swing
  engine (no intraday exits, no stops) - one pass over monthly ranks.
  Judged the standard way: full-window + train/holdout vs SPY, deep
  window included. Pre-declared bar: beats SPY on the holdout with
  market-like max DD, or it doesn't ship.
- **P2b — live wiring** (only if P2a passes): monthly scheduler job
  (first trading day, after research), rank -> diff -> orders through the
  execution path, stamped Sleeve=Factor; activity entry lists the
  rebalance ("out: X, Y; in: Z"). Factor sleeve pauses with the account's
  execution pause like everything else.

### P3 — Swing selectivity (dials + evidence, no new machinery)

- Read the forward scorecard at ~100 scored trades. Current live table
  (5 Aug, 27 trades) is directionally interesting (7+ band: 2 trades,
  both won, +9.07% avg) but far too small to act on.
- First lever: SizingAggressiveness (F2 size tilt) meaningfully on, with
  a modest ForwardVetoFloor (4-5) to cut only the clearly-bad tail -
  selectivity by SIZE keeps every gate-passer feeding the scorecard.
- Escalate to a strict floor (6.5-7) ONLY if the grown scorecard shows
  monotonic discrimination. Decision recorded in results.md either way.

## Explicitly out of scope

- Cross-sleeve margin/netting; options/short anything.
- Claude scoring inside the factor sleeve.
- Per-user sleeve strategies beyond the three (the pie is allocation, not
  a plugin system).
- Sharing sleeves via strategy-share (later; snapshot gains the pie then).

## Sequencing

P1 first (small, immediately useful, zero strategy risk). P2a next - the
factor backtest verdict on the deep dataset decides P2b. P3 rides the
scorecard clock, not the build clock.
