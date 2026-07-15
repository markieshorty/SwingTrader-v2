# Setup-driven entry/exit tactics inside the regime envelope

Status: **spec / not built.** Design doc for review. Grounds a two-layer risk
model: the market-regime risk books set the *envelope* (how much risk, when to
trade at all); the *setup type* that triggered each entry sets the *tactics*
(stop, target, hold) of that individual trade.

---

## 0. The motivating example: breakouts

Today Breakout setups are **hard-capped at Watch — never Buy** in live trading
(`ResearchPipeline.DetermineRecommendationAsync`). The reasoning was empirical:
over the Oct 2023 – Jul 2026 baseline, Breakout trades averaged **-1.20% (37%
win rate)** and were the single drag flipping the system negative; excluding
them turned -5.2% into +14.1%. The same on/off switch exists in the Strategy
Lab as an **"exclude breakouts" checkbox** and an `ExcludeBreakout` flag on
every backtest candidate.

But "exclude the whole setup" is a blunt instrument. That -1.20% was measured
under **one fixed exit policy** — the same flat stop / target / max-hold every
other setup runs. A breakout is a fundamentally different trade shape from an
oversold bounce: it needs room to extend and a different time horizon. It's
entirely plausible that **breakouts are unprofitable under mean-reversion exit
rules but profitable under breakout-appropriate ones** — a wider initial stop,
a trailing stop that arms sooner, a longer guide-hold that lets winners run.
We can't know until we can tune exits *per setup* and measure it.

So the plan for breakouts is:

1. **Stop hard-excluding them in live trading.** Remove the Watch cap; let a
   Breakout that clears the Buy threshold be a Buy like any other setup.
2. **Move the "exclude breakouts" checkbox** out of the A/B panel and into the
   setup-filter dropdown (see §6) — a step toward per-setup configuration
   rather than a single boolean.
3. **Make the exit tactics per-setup** (this spec), so the optimizer can search
   whether breakouts become viable with the right hold/stop/target — instead of
   permanently benching a whole class of signal on one dead-flat-exits result.

Breakouts are just the sharpest example. The general point: **the same signal
can be a good or bad trade depending on how you exit it**, and right now every
setup is exited identically.

---

## 1. The core idea: two layers

```
Regime envelope  (per-account, per regime book — BUILT)
    how much risk is on, and whether to trade at all
    ├─ locked capital %, position size %, max open positions
    ├─ auto-pause (Bear/Crisis)
    └─ daily-loss circuit breaker
        │
        ▼
Setup tactics    (per-setup — NEW)
    how THIS trade is entered and exited
    ├─ stop-loss %      (initial risk)
    ├─ target %         (profit objective)
    ├─ guide-hold days  (soft time horizon; runners extend — §3)
    └─ trailing shape   (activation / distance)
```

The **envelope** answers "given the market, how aggressive am I and how much do
I commit?" — already built as the four regime books (Bull / Neutral / Bear /
Crisis). The **tactics** answer "given the *pattern* I just bought, how do I
manage the trade?" — and today those are flat across every setup.

## 2. What's already built (the envelope)

`AccountRiskProfile`, one row per regime, provides the envelope: `LockedCapitalPct`,
`FlatPositionPct`, `MaxOpenPositions`, `AutopauseTrading`, `DailyLossCircuitBreakerPct`.
The live regime detector (`MarketRegimeService`) picks the active book each
Monitor cycle. That layer stands as-is.

It *also* currently carries the flat exit values — `StopLossPct`, `TargetPct`,
`MaxHoldDays`, `MinHoldDays`, `MomentumHealthThreshold`, `TrailingActivationPct`,
`TrailingDistancePct`. This spec moves the **per-trade tactical** ones (stop,
target, hold, trailing) down into the setup layer; the envelope keeps only the
exposure/pause/breaker dials.

## 3. What's new (setup tactics)

A **setup tactics table** — for each `SetupType` (OversoldRecovery, Breakout,
MomentumContinuation, VolumeSpike, TrendFollowing), a tactics profile:

| Field | Meaning |
|---|---|
| `StopLossPct` | initial stop below entry |
| `TargetPct` | profit target above entry |
| `GuideHoldDays` | **soft** time horizon (see below) |
| `TrailingActivationPct` / `TrailingDistancePct` | trailing-stop shape |

Intuition for seeded defaults (starting points, to be tuned/optimized):

- **OversoldRecovery** — a snap-back. Tight stop, modest target, **short**
  guide-hold: it either bounces quickly or the thesis is wrong.
- **Breakout** — needs room. **Wider** stop, **larger** target, **longer**
  guide-hold, trailing that arms early to lock the run.
- **MomentumContinuation / TrendFollowing** — the runners. Long guide-hold,
  trailing-stop-led exits, big targets.
- **VolumeSpike** — event-driven; medium everything, quick to cut if the volume
  doesn't sustain.

## 4. The guide-hold + runner mechanic

Today the hold budget is: `MinHoldDays` (probation floor) → free run →
`MaxHoldDays` (**hard** `TimeExit` in `PositionMonitorService`). A position that
crosses `MaxHoldDays` without hitting stop or target is force-closed regardless
of how strongly it's still trending.

The new model makes the hold a **guide, not a guillotine**, gated by the
existing momentum-health signal:

```
MinHoldDays        GuideHoldDays                         HardHoldCeiling
(probation floor)  (soft checkpoint)                     (absolute cap)
     │                   │                                     │
     ▼                   ▼                                     ▼
  don't judge   ── run ──  momentum healthy? ── yes ── keep running ──►  force exit
  the thesis              │                              (re-check daily)
  before here            no
                          │
                          ▼
                     exit (thesis stalled)
```

- **Before `MinHoldDays`**: probation — never time-exit; give the thesis room
  (unchanged; `MomentumHealthService` already frozen-threshold at entry —
  "thesis as contract").
- **At `GuideHoldDays`** (was `MaxHoldDays`): instead of a hard `TimeExit`,
  consult `MomentumHealthService`. If the position is still **Confirmed**
  (RSI rising, volume sustaining, price above entry, outperforming sector) it's
  a **runner** — let it continue and re-check each cycle. If it's stalled, exit.
- **`HardHoldCeiling`**: a hard cap so nothing is held forever even if momentum
  keeps flickering healthy. **Derived, not stored per setup** — a single
  per-account multiplier (`HoldCeilingMultiple`, e.g. `2.5`) applied to each
  setup's own `GuideHoldDays`. A 4-day snap-back guide caps at ~10 days; a
  24-day trend guide caps at ~60 — per-setup ceilings for free, with one dial
  (see §9, decision 2).

This is exactly "runners get to continue based on momentum" — and it reuses the
momentum-health calculator that already backs the probation phase, so live and
backtest stay in lockstep (the historic backtester already simulates probation).

## 5. Regime × setup interaction

**Decision: orthogonal (A).** The regime is a pure **exposure envelope**
(size / count / pause); the setup owns **all** the exit tactics
(stop/target/hold/trailing). No cross-term — a Breakout is managed the same way
in Bull or Crisis; the regime only controls whether you take it and how big.

Rationale: one tactics profile *per setup* fits on all that setup's trades
across every regime — far more data per fit. The alternative, a `setups ×
regimes` matrix (5 × 4 = 20 profiles), splits history so thin that cells like
"Breakout in Crisis" would fit exit rules to a handful of trades — the exact
overfitting trap flagged for per-regime *weights*. And regime-awareness isn't
lost: the envelope already changes behaviour per regime (smaller/fewer/paused);
the matrix would ask the market to *also* rewrite each trade's exits, a second,
much hungrier hypothesis.

- **(B) Regime modifiers — deferred.** A later, evidence-gated phase could layer
  a regime *multiplier* on top of the orthogonal tactics (e.g. Bear ×0.7 on
  every guide-hold, tighter trails in Crisis) — a handful of scalars, not a full
  matrix. Only if the orthogonal model demonstrably leaves money on the table.

## 6. Backtestability & the Strategy Lab

Unlike the forward score, **setups are fully reconstructable historically** —
the backtester already detects setup type and simulates probation. So the setup
tactics are **backtestable and optimizable**:

- The A/B panel's **"exclude breakouts" checkbox becomes a "setup filter"
  dropdown** (All setups / Exclude breakouts / one setup at a time), so you can
  isolate and study a single setup's behaviour under different exits.
- The **optimizer** gains a setup-tactics search space: for each setup, sweep
  stop / target / guide-hold and find the exit policy that maximizes that
  setup's own expectancy — the honest way to answer "are breakouts salvageable?"
- Per-setup expectancy tables on the results surface make the answer visible.

## 7. Data model (sketch)

- New `SetupTactics` rows: `(AccountId, SetupType, StopLossPct, TargetPct,
  GuideHoldDays, TrailingActivationPct, TrailingDistancePct)`. Seeded with the
  §3 defaults; editable in Settings (a small grid, one row per setup). **All
  four exit levers — stop, target, guide-hold, trailing — live here** (§5, §9
  decision 3), so one entry-time snapshot fully defines a trade's management.
- `AccountRiskProfile` sheds `StopLossPct`, `TargetPct`, `MaxHoldDays`,
  `MinHoldDays`, `TrailingActivationPct`, `TrailingDistancePct` (they move to
  `SetupTactics`). It keeps the **envelope** dials (locked capital, position
  size, max positions, auto-pause, circuit breaker) + `MinHoldDays` (probation
  floor, regime-level) + `MomentumHealthThreshold` + a single
  `HoldCeilingMultiple` (the per-account ×-of-guide-hold cap, §4).
- `Trade` already snapshots entry rules (`FrozenEntryRules`, thesis-as-contract);
  extend the snapshot to carry the setup's tactics so a mid-trade config edit
  never changes an open position's exits.
- Entry-level calculation (currently flat `StopLossPct`/`TargetPct`) reads the
  triggering setup's tactics instead.

## 8. Phasing

1. **Un-cap breakouts + setup filter dropdown.** Remove the live Watch cap;
   replace the A/B checkbox with the setup-filter dropdown. (Small, immediate.)
2. **Setup tactics table.** Introduce `SetupTactics`, seed defaults, wire entry
   levels + stop/target to the triggering setup. Orthogonal to regime (§5A).
3. **Guide-hold + runner extension.** Reframe the hard `MaxHoldDays` time-exit
   as a momentum-gated guide-hold with a hard ceiling (§4).
4. **Lab/optimizer integration.** Per-setup expectancy surfaces + a setup-tactics
   search space in the optimizer (§6).
5. *(Later, evidence-gated)* Regime × setup modifiers (§5B).

## 9. Decisions

Resolved (folded into §4–§7 above):

1. **Global/orthogonal tactics.** Setup exit rules are regime-independent; the
   regime is a pure exposure envelope. No `setups × regimes` matrix. (§5)
2. **Hold ceiling derived, not stored.** One per-account `HoldCeilingMultiple`
   × each setup's `GuideHoldDays` — per-setup ceilings with a single dial. (§4)
3. **Trailing lives with the setup tactics.** All four exit levers (stop,
   target, guide-hold, trailing) sit in one `SetupTactics` profile. (§5, §7)

Still open:

- **Migration of the current flat values** — seed each setup's stop/target/hold
  from today's flat `AccountRiskProfile` values so behaviour is continuous on
  day one, then let them diverge via tuning. (Recommended; confirm before build.)
- **Which regime book seeds the SetupTactics defaults** — the flat values differ
  per regime book now, so decide whether new setup defaults come from the
  Neutral book (baseline) or a blend. (Minor; Neutral is the obvious pick.)
