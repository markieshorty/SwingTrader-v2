# Regime-Conditional Setup Selection — Results

Run 4 Aug 2026 (evening), on the survivorship-free v2 dataset, immediately
after the v2 weights sweep completed. Judged against the pre-declared bar in
README.md: beat the flat-book control on the HOLDOUT with ≥50% retention,
primarily on Calmar. **Per the spec, no further hypotheses were mined after
these results.**

## H1 "calm idle" — FAILED (as a variant run)

As-run config differed from the pure spec: ALL setups off in Bull/Neutral,
**OR + VS** tradeable in Bear (spec said OR only) — i.e. H1 combined with
H2's convulsion-only VS. Read as "trade convulsions only, idle in calm":

| | Calm-idle variant | Flat book (control) |
|---|---|---|
| Trades | 257 | ~404 |
| Expectancy/trade | 1.45% | ~0.89% |
| Total return | 335.9% | ~357.8% |
| Max drawdown | **51.8%** | ~52% |

**Verdict: the premise is dead.** Removing the calm-regime trades gave up
return (~22pts) while the drawdown did NOT improve — the drawdown lives in
the convulsion windows themselves, which this config still trades. Calmar
gets worse, not better. Corollary: the calm-market OR trickle (+0.27%/trade
holdout) is pulling its weight and stays.

## H2 "convulsion-only VolumeSpike" — DID NOT HOLD UP

Config: OR everywhere (live book), VS re-enabled in-sim and excluded in
Bull/Neutral → VS trades only Bear/Crisis. In-sample A/B looked strong:
**515% / Calmar 0.41** vs the flat book's ~358% / ~0.35.

Validate:

| Window | H2 | Production |
|---|---|---|
| Tuning (market-adj expectancy) | 0.91%/trade (299 trades) | — |
| Held-out | **0.18%/trade** (123 trades) | -0.19%/trade |

Retention 20% — far below the ≥50% bar. **Not applied.**

**Honest reading — untestable rather than refuted.** The 2023–26 holdout
contains almost no Bear/Crisis days, so VS (H2's entire active ingredient)
could barely fire there; H2's holdout number is essentially the OR book's
own holdout showing. The train-window advantage came from regimes the
holdout cannot replay — exactly the n≈1-cycles-per-cell trap the README
pre-declared. A config whose edge cannot be demonstrated out-of-sample does
not go live, however plausible the story.

Footnote: H2 was positive (+0.18%) where production was negative (-0.19%)
on the same held-out window — OR-book behaviour plus noise on 123 trades,
not a VS effect. Interesting, not actionable.

## Live status (5 Aug 2026)

H2 is deployed live-forward as an **explicitly interim** book — Mark: "I
dont have a coherent strategy yet that validates out of sample so its just
an interim." Config: account toggles OR+VS; VS excluded in Bull/Neutral
(Default override disabled so regimes govern); Bear trades OR+VS at normal
exposure; Crisis autopauses at 10%/1/90%-locked. No out-of-sample claim is
made for it — the demo book's forward trades through the next real vol
regime are the judge, and the deep-history dataset (docs/deep-history-plan)
is the path to a properly validated successor.

## Disposition

- **P2 (live wiring) WAS built** later the same evening at Mark's request
  ("better than nothing") despite the failed hypotheses: pipeline demotes
  Buys for regime-excluded setups, Risk Management gains a per-book
  "Setups off while this book governs" multi-select, and a regime flip that
  changes the tradeable book writes an activity-feed note. It ships INERT —
  every live book's `DisabledSetupsCsv` is null, and the settings help text
  points here before anyone fills one in.
- The flat OR-only book stands as production.
- The hypothesis that can settle H2 — does VS earn its keep in a real vol
  regime — now belongs to FORWARD evidence: shadow signals through the next
  genuine Bear/Crisis episode. Revisit only then; no re-sweeping of
  setup×regime combinations on this dataset.
