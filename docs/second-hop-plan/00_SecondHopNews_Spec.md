# Second-Hop News Score — Spec

**Status: Spec agreed with Mark, 13 Jul 2026 (build not started)**

**Objective:** score news about **economically linked** companies — suppliers,
customers, competitors, shared-supply-chain names — for each watchlist symbol.
The market reprices the company a story is *about* within seconds; it reprices
the second hop (the supplier of the company that guided up) over **days to
weeks**, because almost nobody systematically reads one inferential hop out.
Claude already knows the economic graph; this spec turns that knowledge into a
persisted, auditable input.

## Why (evidence base)

1. **The lag is documented.** Cohen & Frazzini, "Economic Links and Predictable
   Returns" (Journal of Finance 2008): returns of economically linked firms
   predict a stock's returns with a multi-week lag — investor attention simply
   doesn't propagate along the customer-supplier graph at repricing speed. The
   effect lives at exactly our swing horizon (days to weeks), unlike the
   filing-delta signal.
2. **The graph is the moat, and we get it for free.** No retail feed sells the
   supplier/customer/competitor map. Claude can produce and maintain it from
   general knowledge plus the news it already reads — a differentiated input
   with zero data spend.
3. **It rides existing plumbing.** News fetching, Claude sentiment scoring, the
   sentiment archive, the Forward blend, shadow-first evaluation — all already
   built. The marginal cost is one bounded daily platform job plus one
   relevance pass per researched symbol.

## Components

### 1. The economic graph — `EconomicLink`, Claude-built, cached, visible

Per watchlist symbol, a platform-level cached list of links:

- `Symbol` (the watchlist target), `LinkedName` + `LinkedTicker` (nullable —
  private companies allowed but unscoreable), `Relation`
  (Supplier | Customer | Competitor | SharedChain), `TransmissionNote` (which
  direction good news flows, e.g. "TSMC capacity constraint is NEGATIVE for
  NVDA but POSITIVE for competitor foundries"), `Strength` 0..1, `Rationale`,
  `BuiltAt`, `Model`.

Built by a monthly Claude call per symbol (company profile + "list the 5-10
most economically significant links, with transmission direction and a
one-line rationale each"); refreshed on watchlist change or 30-day expiry.
**Links are surfaced in the UI** (watchlist symbol detail) — hallucinated links
must be cheap for a human to spot and delete; an admin-editable kill switch per
link (`Suppressed` flag) beats silent trust.

### 2. Bellwether coverage — widening the news the platform already scores

Second-hop events mostly originate at large names (NVDA, TSMC, AAPL, ASML...)
that may not be on any watchlist. A fixed, config-listed **bellwether set**
(~40 symbols: index heavyweights + sector leaders) gets the existing
fetch-news-and-Claude-score treatment once per day, platform-level, results
written into the existing `SentimentDailyScore` archive (which the momentum
blend already reads — widening the archive is a side benefit). Cost bound:
~40 news fetches + at most 40 Claude scoring calls/day platform-wide, cached
by (symbol, day), skipped when a symbol has no news.

### 3. Propagation — the per-symbol relevance pass

At research time, for each watchlist symbol:

1. Collect the last `LookbackDays` (default **5**) of scored events for its
   linked tickers from the archive (bellwethers + other watchlist symbols),
   keeping only |score| ≥ `MinSourceMagnitude` (default **0.3**) — quiet days
   propagate nothing and cost nothing.
2. One Claude call with the target's profile, the link records (relation +
   transmission note), and the candidate events: for each event, does it
   transmit to the target, in which direction, at what strength — **excluding
   any event that is directly about the target itself** (its own news is
   already stage-2's job; double-counting is the main contamination risk).
3. Combine into `SecondHopScore` -1..+1 with per-event decay (half-life
   `SecondHop:HalfLifeTradingDays`, default **5** — the documented propagation
   window) plus `SecondHopSummary` ("TSMC guided up 20% on AI capacity;
   AMAT supplies TSMC's expansion — bullish, strength 0.6").

No links, no qualifying events, or the pass fails → **null** (degraded
semantics identical to the other forward inputs: neutral contribution, never a
veto).

## Integration — Forward side only, never the Gate

```
forward01 = ForwardSentimentWeight   * sentimentComponent
          + ForwardFundamentalWeight * fundamentalMomentum
          + ForwardSecondHopWeight   * secondHopComponent    // NEW, default 0
```

`secondHopComponent = 0.5 + SecondHopScore/2`. `ForwardSecondHopWeight` default
**0** — the pipeline ships inert and earns its weight from shadow evidence.
Unlike the filing delta, this signal's natural horizon **matches the swing
book**, so if the shadow correlation holds it should eventually deserve a real
share of the blend.

## Storage & plumbing

- **`EconomicLink`** table (platform-level) as above; **`BellwetherConfig`**
  is config, not DB (`SecondHop:Bellwethers` symbol list).
- **BellwetherSync job** — daily platform job (pre-research, ~7:00 ET; queue
  `bellwether-jobs`), fetch + score + archive; same idempotency pattern as the
  other platform jobs. Uses platform Finnhub/Claude keys.
- **Graph refresh** rides the existing weekly Watchlist job (per-symbol links
  rebuilt when `BuiltAt` > 30 days or the symbol is new).
- **Signal fields:** `SecondHopScore` (decimal?), `SecondHopSummary` (string?)
  on StockSignal — the shadow record. The relevance pass happens inside the
  research pipeline after stage-2 fetches (funnel-enabled runs: gate-passers
  only, same cost-saving contract as sentiment).

## Phases (each observable before the next touches money)

| Phase | Behaviour change | Gate to advance |
|---|---|---|
| **SH1 Graph + Shadow** | None. Links built + visible in UI; bellwether sync live; SecondHopScore persisted on signals; daily report line (`Second hop: N symbols scored, strongest: AMAT +0.6 via TSMC guidance`). | ≥6 weeks of shadow AND sign-correlation with 5d/20d market-adjusted forward returns reviewed; hallucinated-link rate eyeballed and acceptably low. |
| **SH2 Blend** | `ForwardSecondHopWeight` raised from 0; flows into sizing/veto via existing Forward machinery. | Scorecard shows the blend improving Forward-vs-outcome correlation. |
| **SH3 Tune** | Half-life, magnitude floor, bellwether list, link-refresh cadence tuned from evidence; consider event-triggered graph refresh (an 8-K naming a new major customer). | Quarterly review, same as the veto floor. |

## Risks / mitigations

- **Hallucinated or stale links** — the defining failure mode. Mitigate:
  every link carries a human-readable rationale, is visible in the UI, and is
  individually suppressible; SH1's report line makes each strong score's
  provenance one glance to audit; monthly refresh bounds staleness.
- **Double-counting** a story that also appears in the target's own news:
  the relevance prompt excludes events directly about the target, and the
  dedupe is auditable via SecondHopSummary naming its source event.
- **Crowding at hop one**: an NVDA beat moving the whole semis sector is
  partly captured by the existing RelativeStrength component. Mitigate: the
  relevance prompt asks for *specific* transmission (named supply/customer
  relationships), not sector sympathy — sector beta is explicitly not the
  claim being scored.
- **Token creep**: bounded by the bellwether cap, the magnitude floor, the
  (symbol, day) cache, gate-passer-only execution under the funnel, and one
  batched relevance call per symbol per day.
- **Untickered links** (private suppliers): stored for context, skipped for
  scoring — never a failure.

## Decisions locked (13 Jul 2026)

1. Graph is Claude-built but human-auditable: rationale required, UI-visible,
   per-link suppression. No silent trust.
2. Second-hop events come from the archive (watchlist + bellwethers) — no
   per-link ad-hoc news fetching in v1.
3. Events directly about the target are excluded — this score measures the
   second hop only.
4. Forward-side integration behind `ForwardSecondHopWeight` default 0; shadow
   first; degraded/null never vetoes. The Gate score is untouched.
5. Short decay (half-life 5 trading days) — this signal is for the swing book.
