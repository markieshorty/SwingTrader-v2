# Selective buy plan — one position, chosen by the forward score

Spec agreed 7 Aug 2026. **Not built.**

Supersedes the sleeves architecture for this account. Cadentic's job is
narrowed back to one thing: **choose a speculative stock, buy it, sell it.**
Index exposure is held directly in Trading 212 and is none of this software's
business.

## Why

Today's work established, in order:

- The swing book lost to SPY at every survivable configuration. Best tuned
  result was +1.79%/trade with an **83% max drawdown** and a **31.8% chance of
  beating SPY** — Monte Carlo over 2,000 reshuffles of its own trades.
- The gate score is **anti-predictive**: conviction band 8 returned -9.52%
  while band 4 was the best performer. It works as a quality filter, not as a
  ranker. (Consistent with the long-known 8+ inversion.)
- The unconditional 40-trading-day forward return across 4.7M (symbol, day)
  windows is **+3.01%**, positive 56.1% of the time. Any trade must beat that,
  not zero.
- Probation and tight stops were amputating that drift: turning probation off
  moved OversoldRecovery's hold from 18.3 to 38 days.

The SPY-core sleeve was an attempt to fix the benchmark problem in software.
It was scope creep: the owner already holds ETFs at the broker, so Cadentic
was duplicating an allocation that exists anyway, and paying for it in
reconciliation drift, band rebalancing and funding-sequence complexity.

**What survives the deletion is the measurement, not the mechanism.** A trade
still has to beat ~+2.3% of drift over a 30-day hold plus ~0.4-0.6% of
round-trip friction, so the honest hurdle is about **+2.8%**, and expectancy
should be read market-adjusted. Cadentic no longer needs to *hold* the index
to be judged against it.

Risk sizing also leaves the software: with Cadentic funded as an explicit
speculative slice, an 83% drawdown of that slice is a decision made once,
deliberately, outside the app - rather than a surprise percentage of
everything.

## The design

```
watchlist -> gate (quality filter, pass/fail)
          -> forward score >= ForwardVetoFloor   <-- the ONLY selector
          -> one position, whole allocation
          -> stop / target / trailing / time exit
          -> flat, wait for the next one
```

`MaxOpenPositions = 1`. No sleeve, no cap, no core. Between trades the
account sits in cash, which is fine: at roughly one qualifying signal a month
and ~49-day holds the sleeve would be empty only ~24% of the time anyway, and
idle-cash drag on a deliberately-sized speculative slice is not a problem
worth engineering around.

### The threshold is the existing dial, not a new one

`ForwardVetoFloor` already demotes a gate-passing Buy to Watch when the
forward score sits below it. Selecting on "forward >= N" is that mechanism
with the number raised - no new setting, no new code path, and it inherits
the Trials-page trial and the veto-floor sweep that already exist for tuning
it.

**Measured 13 Jul - 7 Aug 2026, 2,103 scored signals:**

| Bar | Signals | Also passing gate |
|---|---|---|
| forward >= 7 | 216 | 73 |
| forward >= 8 | 4 | **3** |
| forward >= 9 | 0 | - |

All three of the >=8 gate-passing signals were `OversoldRecoveryLoose`, the
setup **retired 4 Aug** and scored 0.0 so it can never Buy. So **>=8 yields
zero tradeable signals** and >=9 yields none at all. The usable range is
around 6.5-7.5, and the distribution is steep there - a whole-number dial has
no useful settings, so the sweep must run in tenths.

### All setups stay on

OversoldRecovery is **29 of 2,103 signals (1.4%)** - the 4-bar recovery
confirmation makes it rare by construction. Restricting to it yields under
one signal per account per month *before* any forward bar, which is a drought
rather than a strategy. And the hypothesis under test has changed: it is now
"does a high forward score predict outperformance", so constraining the setup
confounds the measurement.

The forward bar also largely solves the `Unknown` problem on its own - only
**5 of 850** Unknown signals reach >=7 with a passing gate, and Unknown has
the lowest average forward score of any group (5.44).

## Pre-declared hypotheses

Declared before the data exists, per house rule.

- **H-SEL1**: gate-passing signals with forward score >= the floor beat the
  market-adjusted hurdle (drift + friction, ~+2.8% over a 30-day hold).
  Judged on market-adjusted expectancy, target n >= 30 closed trades.
- **H-SEL2**: failing the veto CLOSED (no forward score = no trade) does not
  materially reduce the qualifying signal count. Falsified if the tradeable
  rate drops by more than half.
- **H-SEL3**: with one position and a patient hold, disabling probation
  improves outcomes. Narrow and directional: the Lab showed probation is net
  POSITIVE globally (-0.30% with, -0.91% without) because it converts -11.4%
  stop-outs into -2.7% early exits - but it is net NEGATIVE for
  OversoldRecovery specifically (-0.52% -> +1.47%, hold 18.3d -> 38d). This
  design is all patient entries, so the OversoldRecovery direction is the one
  that should dominate. If it does not, probation goes back on.

**Watch item, not a hypothesis**: `Breakout` has the highest average forward
score (6.51) and supplies 33 of the 73 gate-passing >=7 signals - more than
everything else combined - while backtests repeatedly identified it as the
drag (excluding it gained ~14%). Claude's forward model and the historical
simulation disagree about the same setup, and this design lets Breakout
dominate selective buys. One of them is wrong; the forward stamps should say
which.

## P1 - config only, no code

Immediately available, nothing to deploy:

- `ForwardVetoFloor` -> the chosen bar (start ~7.0, swept in tenths)
- `MaxOpenPositions` -> 1
- `SizingAggressiveness` -> 0 (the F2 tilt has nothing to tilt)

## P2 - two small code changes

**Fail the veto CLOSED.** Today:

```csharp
public static bool ShouldVeto(decimal? forwardScore, bool degraded, decimal vetoFloor) =>
    forwardScore is { } f && !degraded && f < vetoFloor;
```

A missing or degraded forward score never vetoes. That was right when the
forward score was a safety net over the gate - a data outage must not stop
trading. It is exactly backwards once the forward score is the ONLY selector:
a signal with no forward score should be the last thing bought, not waved
through the single filter. Note stage 2 is skipped entirely for sub-Watch
gates, so null is common rather than exceptional.

**Give probation a live off switch.** There is none. `SimulateProbation` is a
backtest flag with no production equivalent, and `MomentumHealthThreshold` is
clamped to 0.20-0.60 while `Exit` fires on `score < threshold` over a score
bounded in [0,1] - so no legal setting disables it. The Lab can therefore
model a configuration production cannot run, which is the same Lab/live
divergence as the `Unknown` exclusion. Add `ProbationEnabled` to the risk
profile mirroring `SimulateProbation`; it gates `RunnerStalled` too, since
they share the verdict path.

Under one position this matters more than it did: a probation exit at day 7
does not merely close a trade, it forfeits the opportunity waited a month for.

## P3 - remove the sleeve architecture

Delete `SpyCoreService`, `AccountAllocation` (SpyCorePct / FactorTiltPct /
CoreTicker / SwingPct), the `SleeveType` enum and every sleeve-scoped path
through sizing, monitoring, reporting and reconciliation, plus the SPY tab.
`LockedCapitalPct` goes with them - the sleeve cap and the locked reserve were
one ceiling expressed twice, and with `MaxOpenPositions = 1` the position size
IS the ceiling.

**The one thing that must be replaced, not just deleted.** Reconciliation
currently suppresses the owner's ETF holdings via:

```csharp
if (allocation.SpyCorePct > 0
    && pos.Ticker.StartsWith(allocation.CoreTicker, StringComparison.OrdinalIgnoreCase))
    return;
```

That early-return is the ONLY reason held ETFs are not adopted as swing
positions or flagged as drift every cycle. Remove the sleeve and the
suppression dies with it. Replace with an **ignored-tickers list on the
account** - one field, one check, no allocation percentages. It does the only
job the sleeve architecture was actually performing here.

## P4 - deferred cleanup

`SizingMode`, `SizingStyle`, `RiskPerTradePct`, `FlatPositionPct` as a
concept. All become inert under P1 and none block the design. Note
`SizingStyle` also gates ATR-anchored stop/target placement, not just
position sizing - those must be separated before either is removed.

## Out of scope

- **Sell-to-fund / VUAG core / sleeve caps.** Considered and dropped 7 Aug:
  the index is held at the broker, so the software modelling it was
  duplication.
- **A promotion ladder (10 -> 20 -> 30%).** Moot once sizing leaves the app:
  scaling up means funding the account with more, which is a decision made
  outside it.
- **Setup filtering.** All setups on, per the counts above.
- **Re-optimising the gate weights.** The gate is a pass/fail quality filter
  here; its internal weighting stops mattering once it no longer ranks.

## Cost

No Claude tokens. Nothing here changes what is scored - only what is bought.
Trade frequency FALLS (roughly one a month at the >=7 bar), so per-signal
research spend is unchanged and execution spend drops.

## What this is not

Not evidence that the strategy works. Today's best backtest was fitted on one
window, its "out-of-sample" validation compared the same fixed dials across
two periods rather than re-fitting (the endpoint's own comment: *hand-tuned
configs are in-sample by construction*), and Monte Carlo put the chance of
beating SPY at 31.8%. This plan makes the failure mode ACCEPTABLE - a small,
deliberately-sized slice, one position at a time, patient exits - rather than
making the edge real. Whether an edge exists is what H-SEL1 is for, and it
will take a year or more of forward data to answer.
