# Regime-Conditional Setup Selection

Status: **SPEC — drafted 4 Aug 2026, not built. Lab-first: nothing goes live
without an out-of-sample verdict.**

## Motivation (the 4 Aug 2026 v2 evidence)

On the survivorship-free dataset, strict OversoldRecovery's market-adjusted
edge is **+1.33%/trade in the vol-rich training years** (2016–23: contains
the 2018 vol spike, 2020 crash, 2022 bear) and **+0.27%/trade in the calm
holdout** (2023–26). The strategy is a *convulsion harvester that idles in
smooth markets*. The obvious response — flip which setups trade based on the
market state — is well-motivated but walks straight into two graves dug the
same day:

- **Mixed regime books were dominated** (242.8% / Calmar 0.30 vs 952.5% /
  0.65 single-book): flipping *exposure* (autopause, sizing) starves the
  strategy exactly when its food appears. This plan flips *setup selection*,
  which is a different lever — but the same detection lag and timing risks
  apply.
- **Loose and VolumeSpike both looked additive on decade data and collapsed
  out-of-sample.** Any setup family introduced to "fill the calm periods" is
  a fresh candidate for the same trap.

And one structural warning that shapes the whole design: **the dataset holds
roughly one full market cycle.** A setups × regimes grid has ~n=1 episodes
per cell to learn from, and the holdout window is structurally thin on
high-vol days (that thinness is *why* the edge looks absent there). This
feature can only ever be tested honestly as a small number of pre-declared
hypotheses — never swept broadly, or the winner will be noise by
construction.

## Design

### Core: per-regime excluded setups

- `RegimeEnvelope` (engine) gains `IReadOnlyCollection<SetupType>?
  ExcludedSetups` — resolved per simulated day exactly like the existing
  envelope fields. Entry candidates already filter on an exclusion set; in
  Mixed mode that set becomes `baseExclusions ∪ envelope.ExcludedSetups`.
- `AccountRiskProfile` gains `DisabledSetupsCsv` (nullable string, per
  book). Null = no per-regime opinion (the account-level
  `SetupTactics.Enabled` toggles govern alone — today's behaviour exactly).
  Non-null = these setups are additionally untradeable while that regime's
  book is active.
- Live wiring (P2 only): `DetermineRecommendationAsync`'s disabled set
  becomes `accountDisabled ∪ activeBook.DisabledSetups`. Same demote-to-
  Watch semantics as the existing toggle; signals still detect/score, so
  shadow evidence accumulates for the regimes where a setup is off.
- `ConfigFingerprint`: regime-book hashing (already present for Mixed)
  extends to the per-book exclusion lists.

### Lab surface (P1 — this is where the feature lives until proven)

- `HistoricTradingRules` gains `RegimeExcludedSetups` (map: regime name →
  setup names) so a Lab run can test a conditional book WITHOUT any live
  config existing. The rules panel's Mixed section gets a per-regime setup
  multi-select mirroring the exposure forms.
- Regime-comparison mode grows one column per tested hypothesis so the
  Force-single-book vs Mixed vs conditional-Mixed comparison sits in one
  table.

### Pre-declared hypotheses (the ONLY ones to test initially)

Declared here, before any results are seen, to keep the experiment honest:

- **H1 "calm idle":** OR trades only in Bear/Crisis/recovery regimes; NOTHING
  trades in Bull/Neutral. Tests whether the calm-market +0.27% is worth its
  drawdown contribution at all.
- **H2 "convulsion-only VolumeSpike":** OR everywhere; VolumeSpike enabled
  ONLY in Bear/Crisis. Tests whether VS's collapsed edge was calm-specific
  (its train-window profits may have clustered in vol windows too — the
  decomposition data can pre-check this cheaply before even simulating).
- **H3 (control):** current flat book. Any conditional hypothesis must beat
  it on the HOLDOUT with the standard ≥50% retention bar, judged primarily
  on Calmar, not total return.

If H1/H2 fail holdout, the conclusion is recorded and the feature stays
Lab-only (config exists, all books null). No further hypothesis mining.

### Explicitly out of scope

- Claude-driven "market analysis" (narrative regime calls): the
  deterministic regime service is free, backtestable and already computed
  daily; an LLM regime signal is neither backtestable nor free. Revisit only
  if the deterministic version proves the concept and its lag is the
  demonstrated bottleneck.
- Per-regime WEIGHTS (different gate mixes per regime): parameter explosion
  with n=1 cycles per cell. Not testable honestly on this dataset.

## Phases

- **P1 — engine + Lab (no live effect):** RegimeEnvelope exclusions, rules
  override + panel UI, fingerprint coverage, regime-comparison columns, the
  three pre-declared hypothesis runs. Migration for `DisabledSetupsCsv`
  (shipped null everywhere).
- **P2 — live wiring (only if a hypothesis survives holdout):** pipeline
  disabled-set union, risk-book UI multi-select, activity-log note when a
  regime flip changes the tradeable book.

## Test plan

- Unit: envelope exclusion resolution per day (Mixed switches the set);
  fingerprint distinguishes conditional books; rules mapping.
- Lab: H1/H2/H3 on v2 with validate — results recorded in
  docs/regime-setups-plan/results.md win or lose.

## Sequencing note

Deliberately queued BEHIND the 4 Aug v2 weights sweep: the sweep may change
the weights/threshold that define what "conviction" means, which changes the
trade population every hypothesis above would be measured on.
