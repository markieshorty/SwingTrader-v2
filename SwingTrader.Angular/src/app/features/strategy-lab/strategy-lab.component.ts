import { CommonModule } from '@angular/common';
import { Component, computed, inject, OnDestroy, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSliderModule } from '@angular/material/slider';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../../core/services/api.service';
import {
  AbResultDto,
  BacktestHistoryItemDto,
  BacktestResultDto,
  HistoricResultDto,
  LabAnalyseResponseDto,
  LabAnalyseSuggestionDto,
  LabDataStatusDto,
  LabSuggestionDto,
  LabTradingRulesDto,
  LabWeightsDto,
  StrategyLabResponseDto,
  MonteCarloResultDto,
  SetupAblationDto,
  RegimeComparisonDto,
  SetupTacticsDto,
  SetupTacticsRowDto,
  MarketRegimeName,
  StrategyWeightsDto,
  SweepResultDto,
  ValidateResultDto,
} from '../../core/models/dtos';
import { errorMessage } from '../../shared/utils/error-message.util';
import { ConfirmDialogComponent } from '../../shared/components/confirm-dialog/confirm-dialog.component';
import { BacktestHistoryDialogComponent } from './backtest-history-dialog/backtest-history-dialog.component';

interface WeightDial {
  key: keyof LabWeightsDto;
  label: string;
  hint: string;
}

interface DiffRow {
  label: string;
  oldVal: string;
  newVal: string;
  changed: boolean;
}

@Component({
  selector: 'app-strategy-lab',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatCardModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatSliderModule,
    MatSlideToggleModule,
    MatProgressSpinnerModule,
    MatProgressBarModule,
    MatTabsModule,
    MatTooltipModule,
    MatDialogModule,
    MatExpansionModule,
  ],
  templateUrl: './strategy-lab.component.html',
  styleUrl: './strategy-lab.component.scss',
})
export class StrategyLabComponent implements OnDestroy {
  private api = inject(ApiService);
  private snackbar = inject(MatSnackBar);
  private dialog = inject(MatDialog);

  // History tabs (A/B History / Optimizer History). Loaded lazily the first
  // time each tab is opened, and refreshed after an apply.
  abHistory = signal<BacktestHistoryItemDto[]>([]);
  sweepHistory = signal<BacktestHistoryItemDto[]>([]);
  abHistoryLoaded = signal(false);
  sweepHistoryLoaded = signal(false);

  // Tabs: 0 A/B Testing · 1 Optimizer · 2 A/B History · 3 Optimizer History.
  onLabTab(index: number): void {
    this.labTabIndex.set(index);
    // Tab order: 0 A/B, 1 Optimizer, 2 Regimes, 3 A/B History, 4 Optimizer History.
    if (index === 3) this.loadHistory('ab');
    if (index === 4) this.loadHistory('sweep');
  }

  loadHistory(mode: 'ab' | 'sweep', force = false): void {
    const loaded = mode === 'ab' ? this.abHistoryLoaded : this.sweepHistoryLoaded;
    if (loaded() && !force) return;
    this.api.getBacktestHistory(mode).subscribe({
      next: (items) => {
        (mode === 'ab' ? this.abHistory : this.sweepHistory).set(items);
        loaded.set(true);
      },
      error: () => loaded.set(true),
    });
  }

  openHistory(item: BacktestHistoryItemDto): void {
    this.dialog
      .open(BacktestHistoryDialogComponent, { width: '620px', maxWidth: '95vw', data: item })
      .afterClosed()
      .subscribe((applied) => {
        if (applied) this.loadHistory(item.mode, true);
      });
  }

  // The six gate weights (sentiment/fundamental drive the live Forward score,
  // not the backtestable gate, so they're tuned in Settings, not here).
  readonly dials: WeightDial[] = [
    { key: 'rsi', label: 'RSI', hint: 'Dip-buying signal — favours pullbacks recovering from oversold' },
    { key: 'macd', label: 'MACD', hint: 'Momentum direction — rewards rising, positive momentum' },
    { key: 'volume', label: 'Volume', hint: 'Volume confirmation — rewards above-average participation' },
    { key: 'setupQuality', label: 'Setup quality', hint: 'How favourable the detected chart pattern is' },
    { key: 'relativeStrength', label: 'Relative strength', hint: 'Performance vs the stock’s sector ETF' },
    { key: 'priceLevel', label: 'Price level', hint: 'Position relative to support/resistance memory' },
  ];

  // ── A/B Testing tab state ──────────────────────────────────────────────────
  dataSource = signal<'own' | 'historic'>('own');
  weights = signal<LabWeightsDto>({
    rsi: 0.23, macd: 0.12, volume: 0.28,
    setupQuality: 0.16, relativeStrength: 0.14, priceLevel: 0.07,
  });
  buyThreshold = signal(6.0);
  // The single "Exclude setups" multiselect - the one source of truth for
  // which setups a run skips, honoured by both own-data replay and historic.
  // Empty = exclude nothing (matches live, which excludes no setups). Replaced
  // the old breakout-only dropdown + the separate trading-rules multiselect.
  readonly allSetups = ['OversoldRecovery', 'Breakout', 'MomentumContinuation', 'VolumeSpike', 'TrendFollowing'];
  excludedSetups = signal<string[]>([]);

  // Historic A/B regime frame: which risk-book envelope BOTH columns replay
  // under. 'off' = a single run under Neutral (no baseline, halves the job);
  // any regime = compare your dials vs production under that book; 'mixed' =
  // switch book by the regime detected each day (live-like). Replaces the old
  // "compare against production" checkbox and the bear-autopause proxy toggle.
  regimeMode = signal<'off' | 'neutral' | 'bull' | 'bear' | 'crisis' | 'mixed'>('off');
  readonly regimeModeOptions = [
    { value: 'off', label: "Don't compare (dials only)" },
    { value: 'neutral', label: 'Neutral' },
    { value: 'bull', label: 'Bull' },
    { value: 'bear', label: 'Bear' },
    { value: 'crisis', label: 'Crisis' },
    { value: 'mixed', label: 'Mixed (regime-switch)' },
  ] as const;

  // Per-regime autopause TRIAL override (user column only): inherit the live
  // book, or force it on/off for this run without touching Settings. Lets you
  // trial e.g. "Bear not autopaused" against live.
  autopauseChoice = signal<Record<string, 'inherit' | 'on' | 'off'>>(
    { Bull: 'inherit', Neutral: 'inherit', Bear: 'inherit', Crisis: 'inherit' });
  // Which override rows to show: all four under Mixed, just the forced one else.
  autopauseRows = computed<string[]>(() => {
    switch (this.regimeMode()) {
      case 'mixed': return ['Bull', 'Neutral', 'Bear', 'Crisis'];
      case 'bull': return ['Bull'];
      case 'bear': return ['Bear'];
      case 'crisis': return ['Crisis'];
      case 'neutral': return ['Neutral'];
      default: return [];
    }
  });
  setAutopauseChoice(regime: string, choice: 'inherit' | 'on' | 'off'): void {
    this.autopauseChoice.update((m) => ({ ...m, [regime]: choice }));
  }
  private buildAutopauseOverrides(): Record<string, boolean> | null {
    const set = Object.entries(this.autopauseChoice()).filter(([, v]) => v !== 'inherit');
    return set.length ? Object.fromEntries(set.map(([r, v]) => [r, v === 'on'])) : null;
  }

  // Retained for the apply / validate / Monte-Carlo paths, which still reason
  // about the live bear autopause; the A/B form no longer exposes it as a dial
  // (the regime dropdown owns autopause now).
  autopauseBear = signal(true);
  private productionAutopauseBear = true;

  // ── Trading rules (experiment, historic mode only) ─────────────────────────
  // Overrides ride the request and apply to "Your dials" only; the production
  // baseline always replays with the live risk-profile rules. Numeric fields
  // are prefilled from the profile so an untouched Run simulates production.
  rulesMaxHoldDays = signal(10);
  rulesMaxOpenPositions = signal(3);
  rulesTrailingActivation = signal(5); // percent, converted to fraction on send
  rulesTrailingDistance = signal(3);
  // Prefilled from the live risk profile, like every other rule field - an
  // untouched Run simulates production's actual stop/target exactly.
  rulesStopLossPct = signal(5);
  rulesTargetPct = signal(8);
  rulesSimulateProbation = signal(true);
  rulesMinHoldDays = signal(3);
  rulesHealthThreshold = signal(0.5);
  // Position sizing. Flat = mirrors live sizing (X% of equity per trade).
  // Pool = a simulator-only alternative (no live equivalent): a capped
  // active-capital pool limits total deployment.
  rulesPositionFraction = signal(10);      // percent of equity per trade (flat, mirrors live)
  rulesLockedCapitalPct = signal(20);      // percent of account held in reserve (mirrors live)

  // ── Per-setup tactics editor (Phase 4) ─────────────────────────────────────
  // Prefilled from the account's live SetupTactics so an untouched grid mirrors
  // live. Editing a row overrides that setup's stop/target/guide-hold/trailing
  // for the "Your dials" candidate only; the baseline always replays live.
  setupTacticsRanges = signal<SetupTacticsDto['allowedRanges'] | null>(null);
  labTacticsDraft = signal<SetupTacticsRowDto[]>([]);
  private labTacticsBaseline: SetupTacticsRowDto[] = [];
  // Setups switched off for live trading - the default excluded set, so an
  // untouched A/B and the "reset to live/production" actions mirror the book
  // actually being traded.
  private liveExcludedSetups: string[] = [];
  tacticsTouched = computed(() =>
    JSON.stringify(this.labTacticsDraft()) !== JSON.stringify(this.labTacticsBaseline));

  updateTacticField(setup: string, key: keyof SetupTacticsRowDto, value: number): void {
    this.labTacticsDraft.update((rows) =>
      rows.map((r) => (r.setupType === setup ? { ...r, [key]: value } : r)));
  }

  // Fraction (0.07) -> whole-percent for display, rounded to kill float noise
  // (0.07 * 100 = 7.000000000000001). 2dp keeps the 0.5 step, drops trailing 0s.
  tacticPct(fraction: number): number {
    return Math.round(fraction * 10000) / 100;
  }

  // A setup that's excluded from the run can't take trades, so editing its
  // per-setup tactics is meaningless - the row's inputs disable.
  isSetupExcluded(setup: string): boolean {
    return this.excludedSetups().includes(setup);
  }
  private profileRules = {
    maxHoldDays: 10, maxOpenPositions: 3, trailingActivation: 5, trailingDistance: 3,
    minHoldDays: 3, healthThreshold: 0.5,
    stopLossPct: 5, targetPct: 8, positionFraction: 10, lockedCapitalPct: 20,
  };
  // Which regime book the Trading-rules panel is prefilled from. Defaults to
  // Neutral (what the backtest baseline replays under); the panel's dropdown
  // loads any book's envelope as a starting point for an experiment.
  rulesRegime = signal<MarketRegimeName>('Neutral');

  rulesTouched = computed(() =>
    this.excludedSetups().length > 0
    || this.rulesMaxHoldDays() !== this.profileRules.maxHoldDays
    || this.rulesMaxOpenPositions() !== this.profileRules.maxOpenPositions
    || this.rulesTrailingActivation() !== this.profileRules.trailingActivation
    || this.rulesTrailingDistance() !== this.profileRules.trailingDistance
    || this.rulesStopLossPct() !== this.profileRules.stopLossPct
    || this.rulesTargetPct() !== this.profileRules.targetPct
    || !this.rulesSimulateProbation()
    || this.rulesMinHoldDays() !== this.profileRules.minHoldDays
    || this.rulesHealthThreshold() !== this.profileRules.healthThreshold
    || this.rulesPositionFraction() !== this.profileRules.positionFraction
    || this.rulesLockedCapitalPct() !== this.profileRules.lockedCapitalPct
    || this.tacticsTouched());

  // The trading-rules override payload, or null when the panel is untouched
  // (so the engine uses the live risk profile exactly as before). Shared by
  // run() and validateRun() so the validated config is byte-identical to the
  // one the A/B ran.
  private buildRules(): LabTradingRulesDto | null {
    if (!this.rulesTouched()) return null;
    const pr = this.profileRules;
    // The uniform stop/target/hold/trailing fields are applied by the engine to
    // EVERY setup, so a field left at its default would silently clobber the
    // account's live PER-SETUP tactics. Send each only when the user actually
    // changed it from the live baseline; otherwise null, so the engine keeps the
    // per-setup tactics intact. (num = raw value, pct = fraction for %-fields.)
    const num = (v: number, base: number): number | null => (v === base ? null : v);
    const pct = (v: number, base: number): number | null => (v === base ? null : v / 100);
    return {
      excludedSetups: this.excludedSetups(),
      maxHoldDays: num(this.rulesMaxHoldDays(), pr.maxHoldDays),
      maxOpenPositions: num(this.rulesMaxOpenPositions(), pr.maxOpenPositions),
      trailingActivationPct: pct(this.rulesTrailingActivation(), pr.trailingActivation),
      trailingDistancePct: pct(this.rulesTrailingDistance(), pr.trailingDistance),
      stopLossPct: pct(this.rulesStopLossPct(), pr.stopLossPct),
      targetPct: pct(this.rulesTargetPct(), pr.targetPct),
      simulateProbation: this.rulesSimulateProbation(),
      minHoldDays: num(this.rulesMinHoldDays(), pr.minHoldDays),
      momentumHealthThreshold: num(this.rulesHealthThreshold(), pr.healthThreshold),
      positionFraction: this.rulesPositionFraction() / 100,
      lockedCapitalPct: num(this.rulesLockedCapitalPct(), pr.lockedCapitalPct) === null ? null : this.rulesLockedCapitalPct() / 100,
      activeCapitalPct: null,
      maxPositionPctOfActive: null,
      // Per-setup tactics only ride when the grid is edited; otherwise null so
      // the engine uses the account's live SetupTactics unchanged.
      setupTactics: this.tacticsTouched()
        ? this.labTacticsDraft().map((r) => ({
            setup: r.setupType,
            stopLossPct: r.stopLossPct,
            targetPct: r.targetPct,
            guideHoldDays: r.guideHoldDays,
            trailingActivationPct: r.trailingActivationPct,
            trailingDistancePct: r.trailingDistancePct,
          }))
        : null,
    };
  }

  // Snaps only the uniform rule fields to the loaded regime book's values -
  // used both by the full reset and by the panel's "prefill from regime" picker
  // (which must not disturb the excluded-setups selection or per-setup tactics).
  private applyRuleDefaults(): void {
    this.rulesMaxHoldDays.set(this.profileRules.maxHoldDays);
    this.rulesMaxOpenPositions.set(this.profileRules.maxOpenPositions);
    this.rulesTrailingActivation.set(this.profileRules.trailingActivation);
    this.rulesTrailingDistance.set(this.profileRules.trailingDistance);
    this.rulesStopLossPct.set(this.profileRules.stopLossPct);
    this.rulesTargetPct.set(this.profileRules.targetPct);
    this.rulesSimulateProbation.set(true);
    this.rulesMinHoldDays.set(this.profileRules.minHoldDays);
    this.rulesHealthThreshold.set(this.profileRules.healthThreshold);
    this.rulesPositionFraction.set(this.profileRules.positionFraction);
    this.rulesLockedCapitalPct.set(this.profileRules.lockedCapitalPct);
  }

  resetRules(): void {
    this.excludedSetups.set([...this.liveExcludedSetups]);
    this.applyRuleDefaults();
    this.labTacticsDraft.set(this.labTacticsBaseline.map((r) => ({ ...r })));
  }

  // Loads a regime book's envelope into the Trading-rules panel as a starting
  // point (bug: position size wasn't prefilled at all; now it is). Only the
  // uniform rule fields change - excluded setups and per-setup tactics stay put.
  loadRegimeIntoRules(regime: MarketRegimeName): void {
    this.rulesRegime.set(regime);
    this.api.getRiskProfile(regime).subscribe({
      next: (p) => {
        this.profileRules = {
          maxHoldDays: p.maxHoldDays,
          maxOpenPositions: p.maxOpenPositions,
          trailingActivation: Math.round(p.trailingActivationPct * 100),
          trailingDistance: Math.round(p.trailingDistancePct * 100),
          minHoldDays: p.minHoldDays,
          healthThreshold: p.momentumHealthThreshold,
          stopLossPct: Math.round(p.stopLossPct * 100),
          targetPct: Math.round(p.targetPct * 100),
          positionFraction: Math.round(p.flatPositionPct * 100),
          lockedCapitalPct: Math.round(p.lockedCapitalPct * 100),
        };
        this.applyRuleDefaults();
      },
      error: () => {},
    });
  }

  running = signal(false);
  response = signal<StrategyLabResponseDto | null>(null);
  historicResult = signal<HistoricResultDto | null>(null);
  abResult = signal<AbResultDto | null>(null);
  historicStatus = signal<string | null>(null); // Queued/Running progress text

  // Out-of-sample validation of the current form dials+rules.
  validating = signal(false);
  validateStatus = signal<string | null>(null);
  validateResult = signal<ValidateResultDto | null>(null);

  // Monte Carlo robustness check (trade-order bootstrap) of the same config.
  monteCarloRunning = signal(false);
  monteCarloStatus = signal<string | null>(null);
  monteCarloResult = signal<MonteCarloResultDto | null>(null);

  // Snapshot of the dials actually submitted for the run in progress/last
  // completed - the "old" side of diff tables and the config sent to the
  // analysis endpoint. Can't just read the live `weights` signal: the user
  // may have loaded other dials into the form since the run finished.
  private ranWeights: LabWeightsDto | null = null;
  private ranThreshold = 0;
  private ranExcludeBreakout = false;
  private ranAutopauseBear = true;
  // Regime frame the last historic run used, shown in the A/B result caption so
  // "Production baseline" is unambiguous (which book was it replayed under).
  ranRegimeLabel = signal('');
  private lastHistoricRunId: number | null = null;
  // The completed optimizer run - lets "Apply winner" reproduce the FULL
  // winning config (weights + rule/tactic overrides + autopause) server-side,
  // instead of the weights-only live-apply path which silently drops the very
  // rule change that often won.
  private lastSweepRunId: number | null = null;
  // Fingerprint + shape of the last completed historic run, so the inline
  // "Apply your dials" can reproduce the FULL tested config (incl. rule/tactic
  // overrides) - but only when the form still matches what actually ran, and
  // only for an A/B run (a bare single historic run stores no applyable config).
  private lastHistoricConfigFingerprint: string | null = null;
  private lastHistoricWasAb = false;

  // ── Claude analysis (advisory only) ────────────────────────────────────────
  analysing = signal(false);
  analysis = signal<LabAnalyseResponseDto | null>(null);

  // Programmatic tab control (the "Test winner in A/B" shortcut jumps tabs).
  labTabIndex = signal(0);

  // ── Optimizer tab state ────────────────────────────────────────────────────
  optimizing = signal(false);
  sweepStatus = signal<string | null>(null);
  sweepResult = signal<SweepResultDto | null>(null);
  sweepProgress = signal<{ completed: number; total: number } | null>(null);
  // When the displayed result was restored from a PAST run (rather than one
  // just finished in this session), this holds its completion time so the
  // card can say how stale it is. Cleared when a fresh run starts.
  sweepResultCompletedAt = signal<string | null>(null);

  // ── Setup-contribution (leave-one-out ablation) state ──────────────────────
  ablationRunning = signal(false);
  ablationStatus = signal<string | null>(null);
  ablationResult = signal<SetupAblationDto | null>(null);
  ablationProgress = signal<{ completed: number; total: number } | null>(null);

  // ── Regime comparison state ────────────────────────────────────────────────
  regimeRunning = signal(false);
  regimeStatus = signal<string | null>(null);
  regimeResult = signal<RegimeComparisonDto | null>(null);
  regimeProgress = signal<{ completed: number; total: number } | null>(null);

  // ── Shared state ───────────────────────────────────────────────────────────
  dataStatus = signal<LabDataStatusDto | null>(null);
  dataStatusLoading = signal(true); // true until the first data-status fetch resolves
  syncing = signal(false);
  productionWeights = signal<StrategyWeightsDto | null>(null);
  isOwner = signal(false);
  // One poll handle per job kind: an A/B run and an optimizer sweep can be in
  // flight at the same time, and starting one must not kill the other's poll.
  private pollHandles = new Map<'ab' | 'sweep' | 'validate' | 'montecarlo' | 'ablation' | 'regime', ReturnType<typeof setInterval>>();

  weightSum = computed(() => {
    const w = this.weights();
    return Math.round(
      (w.rsi + w.macd + w.volume + w.setupQuality + w.relativeStrength + w.priceLevel) * 100,
    ) / 100;
  });
  sumOk = computed(() => Math.abs(this.weightSum() - 1.0) <= 0.01);

  constructor() {
    // Prefill the dials from live production weights so "Run" with nothing
    // touched simulates exactly what production does today.
    this.api.getStrategyWeights().subscribe({
      next: (w) => {
        this.productionWeights.set(w);
        this.resetToProduction(false);
      },
      error: () => {},
    });
    this.api.getAccountSettings().subscribe({
      next: (s) => this.isOwner.set(s.role === 'Owner'),
      error: () => {},
    });
    this.api.getLabDataStatus().subscribe({
      next: (d) => {
        this.dataStatus.set(d);
        this.dataStatusLoading.set(false);
      },
      error: () => this.dataStatusLoading.set(false),
    });
    // Trading-rule defaults mirror the Neutral baseline book (what the backtest
    // engine replays), so an untouched Run reproduces the production baseline.
    this.loadRegimeIntoRules('Neutral');
    // Per-setup tactics editor, prefilled from the account's live SetupTactics
    // so an untouched grid replays exactly what production does per setup.
    this.api.getSetupTactics().subscribe({
      next: (t) => {
        this.setupTacticsRanges.set(t.allowedRanges);
        this.labTacticsBaseline = t.setups.map((r) => ({ ...r }));
        this.labTacticsDraft.set(t.setups.map((r) => ({ ...r })));
        // Setups switched off for live trading start excluded, so an untouched
        // A/B mirrors the book actually traded (matches the production baseline).
        this.liveExcludedSetups = t.setups.filter((r) => !r.enabled).map((r) => r.setupType);
        this.excludedSetups.set([...this.liveExcludedSetups]);
      },
      error: () => {},
    });
    // The bear-autopause dial mirrors the Bear regime book (where the live
    // "pause entries in a bear" decision now lives).
    this.api.getRiskProfile('Bear').subscribe({
      next: (b) => {
        this.productionAutopauseBear = b.autopauseTrading;
        this.autopauseBear.set(b.autopauseTrading);
      },
      error: () => {},
    });
    // Optimizer runs are persisted on the run row forever, but the run id
    // only lived in this component's memory - a page refresh lost both a
    // finished sweep's output and, mid-run, the poll tracking one still
    // running server-side. Restore the former; REATTACH to the latter.
    this.api.getLatestBacktestRun('sweep').subscribe({
      next: (r) => {
        // A run started in THIS session takes precedence over history.
        if (this.optimizing()) return;
        if (r.status === 'Queued' || r.status === 'Running') {
          this.optimizing.set(true);
          this.startSweepPoll(r.id);
          return;
        }
        if (r.result && 'mode' in r.result && r.result.mode === 'sweep') {
          this.sweepResult.set(r.result);
          this.sweepResultCompletedAt.set(r.completedAt);
        }
      },
      error: () => {}, // 404 = never run one - nothing to restore
    });
  }

  // Server-side jobs outlive the page, but setInterval doesn't get cleaned up
  // by Angular - without this, navigating away mid-run left every poll
  // ticking (and snackbaring) forever in the background.
  ngOnDestroy(): void {
    for (const handle of this.pollHandles.values()) clearInterval(handle);
    this.pollHandles.clear();
  }

  historicAvailable(): boolean {
    return (this.dataStatus()?.bars ?? 0) > 0;
  }

  syncData(): void {
    this.syncing.set(true);
    this.api.syncLabData().subscribe({
      next: () => {
        this.snackbar.open('Candle sync queued — the one-off backfill to the full 10-year window takes ~45 minutes; incremental runs after that are quick. Check back shortly.', 'Dismiss', { duration: 6000 });
        this.syncing.set(false);
      },
      error: (err) => {
        this.snackbar.open(errorMessage(err, 'Failed to queue sync.'), 'Dismiss', { duration: 5000 });
        this.syncing.set(false);
      },
    });
  }

  setWeight(key: keyof LabWeightsDto, value: number): void {
    this.weights.set({ ...this.weights(), [key]: value });
  }

  // Scale all six gate weights so they sum to exactly 1.0 - saves the user
  // hand-tuning dials to hit the sum-to-one requirement the Run button enforces.
  normaliseWeights(): void {
    const w = this.weights();
    const adjustable = this.dials;
    const lockedTotal = 0;
    const adjustableTotal = adjustable.reduce((s, d) => s + w[d.key], 0);
    const target = 1 - lockedTotal;
    if (adjustableTotal <= 0 || target <= 0) return;

    const scaled = { ...w };
    for (const d of adjustable) scaled[d.key] = Math.round((w[d.key] / adjustableTotal) * target * 100) / 100;
    // Rounding can leave the sum a hair off 1.00; drop any residue on the
    // largest adjustable weight so it lands exactly.
    const residue = 1 - this.dials.reduce((s, d) => s + scaled[d.key], 0);
    const largest = adjustable.reduce((a, b) => (scaled[a.key] >= scaled[b.key] ? a : b));
    scaled[largest.key] = Math.round((scaled[largest.key] + residue) * 100) / 100;
    this.weights.set(scaled);
  }

  // Snap the dial form back to the current production configuration.
  resetToProduction(notify = true): void {
    const w = this.productionWeights();
    if (!w) return;
    this.weights.set({
      rsi: w.rsiWeight, macd: w.macdWeight, volume: w.volumeWeight,
      setupQuality: w.setupQualityWeight, relativeStrength: w.relativeStrengthWeight,
      priceLevel: w.priceLevelWeight,
    });
    this.buyThreshold.set(w.buyThreshold);
    this.excludedSetups.set([...this.liveExcludedSetups]);
    this.autopauseBear.set(this.productionAutopauseBear);
    if (notify) this.snackbar.open('Dials reset to current production settings.', 'Dismiss', { duration: 3000 });
  }

  run(): void {
    if (!this.sumOk()) {
      this.snackbar.open(`Weights must sum to 1.0 (currently ${this.weightSum().toFixed(2)}).`, 'Dismiss', { duration: 4000 });
      return;
    }
    this.running.set(true);
    this.response.set(null);
    this.historicResult.set(null);
    this.abResult.set(null);
    this.analysis.set(null);
    this.historicStatus.set(null);

    const historic = this.dataSource() === 'historic';
    const request = {
      dataSource: this.dataSource(),
      weights: this.weights(),
      buyThreshold: this.buyThreshold(),
      excludedSetups: this.excludedSetups(),
      // Derived for back-compat: the historic candidate + diff still carry a
      // breakout bool; the multiselect is the source of truth.
      excludeBreakout: this.excludedSetups().includes('Breakout'),
      // Own-data comparison is a free in-memory replay - always on. Historic
      // compares against production only when a regime is picked in the dropdown
      // ("Don't compare" = a single run under the Neutral book).
      compareBaseline: historic ? this.regimeMode() !== 'off' : true,
      autopauseDuringBear: this.autopauseBear(),
      // Trading-rule overrides only ride historic runs, and only when
      // something was actually changed - an untouched panel sends nothing so
      // the engine uses the live risk profile exactly as before.
      rules: historic ? this.buildRules() : null,
      // Regime frame both columns replay under ('off' = single Neutral run);
      // per-regime autopause overrides ride the user column only.
      regimeMode: historic ? (this.regimeMode() === 'off' ? 'neutral' : this.regimeMode()) : null,
      autopauseOverrides: historic ? this.buildAutopauseOverrides() : null,
    };
    this.ranWeights = request.weights;
    this.ranThreshold = request.buyThreshold;
    this.ranExcludeBreakout = request.excludeBreakout;
    this.ranAutopauseBear = request.autopauseDuringBear;
    this.ranRegimeLabel.set(historic
      ? (this.regimeModeOptions.find((o) => o.value === this.regimeMode())?.label ?? '')
      : '');

    if (historic) {
      const fingerprint = this.configFingerprint();
      const wasAb = request.compareBaseline;
      this.api.runStrategyLabHistoric(request).subscribe({
        next: (r) => {
          this.lastHistoricRunId = r.backtestRunId;
          this.lastHistoricConfigFingerprint = fingerprint;
          this.lastHistoricWasAb = wasAb;
          this.pollRun('ab', r.backtestRunId, 'Running — replaying the historic market data through your dials…',
            this.historicStatus, (result) => {
              if (result && 'mode' in result && result.mode === 'ab') this.abResult.set(result);
              else this.historicResult.set(result as HistoricResultDto);
              this.running.set(false);
            });
        },
        error: (err) => {
          this.snackbar.open(errorMessage(err, 'Failed to queue the backtest.'), 'Dismiss', { duration: 5000 });
          this.running.set(false);
        },
      });
      return;
    }

    this.api.runStrategyLab(request).subscribe({
      next: (r) => {
        this.response.set(r);
        this.running.set(false);
      },
      error: (err) => {
        this.snackbar.open(errorMessage(err, 'Simulation failed.'), 'Dismiss', { duration: 5000 });
        this.running.set(false);
      },
    });
  }

  // Out-of-sample validation of the CURRENT form dials+rules: the optimizer's
  // train/holdout split + hold-up verdict, on demand. Hand-tuned configs are
  // in-sample by construction (the user iterated against the full window) -
  // this is the gate between "looks great" and "believe it".
  validateRun(): void {
    if (!this.sumOk()) {
      this.snackbar.open(`Weights must sum to 1.0 (currently ${this.weightSum().toFixed(2)}).`, 'Dismiss', { duration: 4000 });
      return;
    }
    this.validating.set(true);
    this.validateResult.set(null);
    const request = {
      dataSource: 'historic' as const,
      weights: this.weights(),
      buyThreshold: this.buyThreshold(),
      excludedSetups: this.excludedSetups(),
      // Derived for back-compat: the historic candidate + diff still carry a
      // breakout bool; the multiselect is the source of truth.
      excludeBreakout: this.excludedSetups().includes('Breakout'),
      autopauseDuringBear: this.autopauseBear(),
      rules: this.buildRules(),
    };
    this.api.validateStrategyLab(request).subscribe({
      next: (r) => this.pollRun('validate', r.backtestRunId,
        'Validating — tuning window (~70%) and held-out remainder run separately, plus the production baseline on the held-out window…',
        this.validateStatus, (result) => {
          if (result && 'mode' in result && result.mode === 'validate') this.validateResult.set(result);
          else if (result) this.snackbar.open('Unexpected validation result shape.', 'Dismiss', { duration: 5000 });
          this.validating.set(false);
        }),
      error: (err) => {
        this.snackbar.open(errorMessage(err, 'Failed to queue the validation.'), 'Dismiss', { duration: 5000 });
        this.validating.set(false);
      },
    });
  }

  // Monte Carlo robustness check: one full-window run of the CURRENT form
  // config, then thousands of bootstrap reshuffles of its own trade log.
  // Answers a different question from Validate: not "does the edge exist
  // outside the tuning window?" but "how much of the headline number is the
  // lucky ORDER of trades, and what drawdown should I actually budget for?"
  monteCarloRun(): void {
    if (!this.sumOk()) {
      this.snackbar.open(`Weights must sum to 1.0 (currently ${this.weightSum().toFixed(2)}).`, 'Dismiss', { duration: 4000 });
      return;
    }
    this.monteCarloRunning.set(true);
    this.monteCarloResult.set(null);
    const request = {
      dataSource: 'historic' as const,
      weights: this.weights(),
      buyThreshold: this.buyThreshold(),
      excludedSetups: this.excludedSetups(),
      // Derived for back-compat: the historic candidate + diff still carry a
      // breakout bool; the multiselect is the source of truth.
      excludeBreakout: this.excludedSetups().includes('Breakout'),
      autopauseDuringBear: this.autopauseBear(),
      rules: this.buildRules(),
    };
    this.api.monteCarloStrategyLab(request).subscribe({
      next: (r) => this.pollRun('montecarlo', r.backtestRunId,
        'Running — one full-window simulation, then 2,000 reshuffled orderings of its trades…',
        this.monteCarloStatus, (result) => {
          if (result && 'mode' in result && result.mode === 'montecarlo') this.monteCarloResult.set(result);
          else if (result) this.snackbar.open('Unexpected Monte Carlo result shape.', 'Dismiss', { duration: 5000 });
          this.monteCarloRunning.set(false);
        }),
      error: (err) => {
        this.snackbar.open(errorMessage(err, 'Failed to queue the Monte Carlo run.'), 'Dismiss', { duration: 5000 });
        this.monteCarloRunning.set(false);
      },
    });
  }

  // ── Optimizer tab ──────────────────────────────────────────────────────────

  // "Search for optimal trading rules": adds exit/probation/position rule
  // candidates to the sweep alongside the weight search.
  searchRules = signal(false);

  runOptimizer(): void {
    this.optimizing.set(true);
    this.sweepResult.set(null);
    this.sweepProgress.set(null);
    this.sweepResultCompletedAt.set(null); // fresh run - the "from a past run" caption no longer applies
    this.api.runStrategyLabOptimize(this.searchRules()).subscribe({
      next: (r) => this.startSweepPoll(r.backtestRunId),
      error: (err) => {
        this.snackbar.open(errorMessage(err, 'Failed to queue the optimizer.'), 'Dismiss', { duration: 5000 });
        this.optimizing.set(false);
        this.sweepProgress.set(null);
      },
    });
  }

  runSetupContribution(): void {
    this.ablationRunning.set(true);
    this.ablationResult.set(null);
    this.ablationProgress.set(null);
    this.api.runSetupContribution().subscribe({
      next: (r) => this.startAblationPoll(r.backtestRunId),
      error: (err) => {
        this.snackbar.open(errorMessage(err, 'Failed to queue the setup-contribution run.'), 'Dismiss', { duration: 5000 });
        this.ablationRunning.set(false);
      },
    });
  }

  private startAblationPoll(runId: number): void {
    this.pollRun(
      'ablation',
      runId,
      'Running — replaying the strategy with each setup removed in turn, on the training and held-out windows…',
      this.ablationStatus,
      (result) => {
        if (result && 'mode' in result && result.mode === 'ablation') this.ablationResult.set(result);
        this.ablationRunning.set(false);
        this.ablationProgress.set(null);
      });
  }

  runRegimeComparison(): void {
    this.regimeRunning.set(true);
    this.regimeResult.set(null);
    this.regimeProgress.set(null);
    this.api.runRegimeComparison().subscribe({
      next: (r) => this.startRegimePoll(r.backtestRunId),
      error: (err) => {
        this.snackbar.open(errorMessage(err, 'Failed to queue the regime comparison.'), 'Dismiss', { duration: 5000 });
        this.regimeRunning.set(false);
      },
    });
  }

  private startRegimePoll(runId: number): void {
    this.pollRun(
      'regime',
      runId,
      'Running — replaying your strategy under each regime book forced across the full period, plus the per-day Mixed run…',
      this.regimeStatus,
      (result) => {
        if (result && 'mode' in result && result.mode === 'regime') this.regimeResult.set(result);
        this.regimeRunning.set(false);
        this.regimeProgress.set(null);
      });
  }

  // Highest total return across the compared modes - drives the "best" highlight
  // so the one-vs-mix verdict is obvious at a glance.
  bestRegimeReturn = computed(() => {
    const rows = this.regimeResult()?.rows ?? [];
    return rows.length ? Math.max(...rows.map((r) => r.totalReturnPct)) : null;
  });

  // Shared by a freshly-queued sweep and the tab-load reattach to one that
  // was already running server-side when the page was opened/refreshed.
  private startSweepPoll(runId: number): void {
    this.lastSweepRunId = runId;
    this.pollRun(
      'sweep',
      runId,
      'Running — evaluating ~1,200 dial variations (deterministic sweep + ML-guided search) on the training window, then validating the best on held-out data. Expect roughly an hour…',
      this.sweepStatus,
      (result) => {
        if (result && 'mode' in result && result.mode === 'sweep') this.sweepResult.set(result);
        // Only warn on a genuinely unexpected COMPLETED payload - a null
        // here means the job failed, and pollRun already surfaced the real
        // "Job failed: <error>" message; showing a generic shape warning
        // would just clobber it (which is exactly what hid a SQL timeout).
        else if (result) this.snackbar.open('Unexpected optimizer result shape.', 'Dismiss', { duration: 5000 });
        this.optimizing.set(false);
        this.sweepProgress.set(null);
      });
  }

  // Server-side jobs are polled until the run row completes; the caller
  // decides how to interpret the stored result. Restarting a job of the same
  // kind replaces its own poll; the other kind's poll is left running.
  private pollRun(
    kind: 'ab' | 'sweep' | 'validate' | 'montecarlo' | 'ablation' | 'regime',
    runId: number,
    runningText: string,
    status: ReturnType<typeof signal<string | null>>,
    onDone: (result: BacktestResultDto | null) => void,
  ): void {
    status.set('Queued — the job runs server-side…');
    const existing = this.pollHandles.get(kind);
    if (existing) clearInterval(existing);
    const timer = setInterval(() => {
      this.api.getBacktestRun(runId).subscribe({
        next: (r) => {
          if (r.status === 'Running') status.set(runningText);
          if (kind === 'sweep' && r.totalCandidates != null && r.completedCandidates != null) {
            this.sweepProgress.set({ completed: r.completedCandidates, total: r.totalCandidates });
          }
          if (kind === 'ablation' && r.totalCandidates != null && r.completedCandidates != null) {
            this.ablationProgress.set({ completed: r.completedCandidates, total: r.totalCandidates });
          }
          if (kind === 'regime' && r.totalCandidates != null && r.completedCandidates != null) {
            this.regimeProgress.set({ completed: r.completedCandidates, total: r.totalCandidates });
          }
          if (r.status === 'Completed' || r.status === 'Failed') {
            clearInterval(timer);
            this.pollHandles.delete(kind);
            status.set(null);
            if (r.status === 'Completed') onDone(r.result);
            else {
              this.snackbar.open(`Job failed: ${r.error}`, 'Dismiss', { duration: 8000 });
              onDone(null);
              if (kind === 'ab') this.running.set(false);
              else if (kind === 'validate') this.validating.set(false);
              else if (kind === 'montecarlo') this.monteCarloRunning.set(false);
              else if (kind === 'ablation') { this.ablationRunning.set(false); this.ablationProgress.set(null); }
              else if (kind === 'regime') { this.regimeRunning.set(false); this.regimeProgress.set(null); }
              else this.optimizing.set(false);
              if (kind === 'sweep') this.sweepProgress.set(null);
            }
          }
        },
        error: () => {}, // transient poll failure - keep polling
      });
    }, 5000);
    this.pollHandles.set(kind, timer);
  }

  // ── Claude analysis ────────────────────────────────────────────────────────

  analyseRun(): void {
    if (!this.ranWeights) return;
    const own = this.response()?.result ?? null;
    this.analysing.set(true);
    this.analysis.set(null);
    this.api.analyseStrategyLabRun({
      dataSource: this.dataSource(),
      weights: this.ranWeights,
      buyThreshold: this.ranThreshold,
      excludeBreakout: this.ranExcludeBreakout,
      ownResult: this.dataSource() === 'own' && own
        ? {
            totalClosedTrades: own.totalClosedTrades, tradesKept: own.tradesKept,
            droppedWinners: own.droppedWinners, droppedLosers: own.droppedLosers,
            actualAvgReturnPct: own.actualAvgReturnPct, simAvgReturnPct: own.simAvgReturnPct,
            actualWinRate: own.actualWinRate, simWinRate: own.simWinRate,
          }
        : null,
      backtestRunId: this.dataSource() === 'historic' ? this.lastHistoricRunId : null,
      autopauseDuringBear: this.ranAutopauseBear,
    }).subscribe({
      next: (r) => {
        this.analysis.set(r);
        this.analysing.set(false);
      },
      error: (err) => {
        this.snackbar.open(errorMessage(err, 'Analysis failed.'), 'Dismiss', { duration: 5000 });
        this.analysing.set(false);
      },
    });
  }

  tryAnalysisSuggestion(s: LabAnalyseSuggestionDto): void {
    this.weights.set({ ...s.weights });
    this.buyThreshold.set(s.buyThreshold);
    // Leave the excluded-setups selection as-is: suggestions vary the scoring
    // dials, not which setups the run skips, so overwriting it here would
    // silently drop a non-breakout exclusion the user had chosen.
    this.snackbar.open('Suggested dials loaded — hit Run Simulation to test the hypothesis.', 'Dismiss', { duration: 4000 });
  }

  // ── Diff tables ────────────────────────────────────────────────────────────

  private diffRows(
    oldW: LabWeightsDto, oldT: number, oldB: boolean,
    newW: LabWeightsDto, newT: number, newB: boolean,
  ): DiffRow[] {
    const rows: DiffRow[] = this.dials.map((d) => ({
      label: d.label,
      oldVal: (oldW[d.key] * 100).toFixed(0) + '%',
      newVal: (newW[d.key] * 100).toFixed(0) + '%',
      changed: Math.abs(oldW[d.key] - newW[d.key]) >= 0.005,
    }));
    rows.push({ label: 'Buy threshold', oldVal: oldT.toFixed(1), newVal: newT.toFixed(1), changed: oldT !== newT });
    rows.push({ label: 'Exclude Breakout', oldVal: oldB ? 'Yes' : 'No', newVal: newB ? 'Yes' : 'No', changed: oldB !== newB });
    return rows;
  }

  suggestionDiffRows(s: LabSuggestionDto): DiffRow[] {
    if (!this.ranWeights) return [];
    return this.diffRows(this.ranWeights, this.ranThreshold, this.ranExcludeBreakout, s.weights, s.buyThreshold, s.excludeBreakout);
  }

  sweepDiffRows(sweep: SweepResultDto): DiffRow[] {
    // Weights + buy threshold.
    const rows: DiffRow[] = this.dials.map((d) => ({
      label: d.label,
      oldVal: (sweep.baseline.weights[d.key] * 100).toFixed(0) + '%',
      newVal: (sweep.winner.weights[d.key] * 100).toFixed(0) + '%',
      changed: Math.abs(sweep.baseline.weights[d.key] - sweep.winner.weights[d.key]) >= 0.005,
    }));
    rows.push({
      label: 'Buy threshold',
      oldVal: sweep.baseline.buyThreshold.toFixed(1),
      newVal: sweep.winner.buyThreshold.toFixed(1),
      changed: sweep.baseline.buyThreshold !== sweep.winner.buyThreshold,
    });
    // Full excluded-setups list, not just the legacy breakout bool - a winner
    // that drops VolumeSpike must show it or the diff lies about what won.
    const oldEx = this.excludedLabel(sweep.baseline.excludedSetups, sweep.baseline.excludeBreakout);
    const newEx = this.excludedLabel(sweep.winner.excludedSetups, sweep.winner.excludeBreakout);
    rows.push({ label: 'Excluded setups', oldVal: oldEx, newVal: newEx, changed: oldEx !== newEx });
    rows.push({
      label: 'Bear autopause',
      oldVal: sweep.baseline.autopauseDuringBear ? 'On' : 'Off',
      newVal: sweep.winner.autopauseDuringBear ? 'On' : 'Off',
      changed: sweep.baseline.autopauseDuringBear !== sweep.winner.autopauseDuringBear,
    });
    // The winning lever is often a rule/tactic change, not a weight - surface
    // every override the winner carries so "what won" is never invisible.
    rows.push(...this.ruleDiffRows(sweep.winner.rules));
    return rows;
  }

  private excludedLabel(excluded: string[] | undefined, breakoutFallback: boolean): string {
    const list = excluded ?? (breakoutFallback ? ['Breakout'] : []);
    return list.length ? list.join(', ') : 'none';
  }

  // Renders the winner's trading-rule + per-setup-tactic overrides as diff rows.
  // The baseline replays with the live risk profile (Rules == null), so the old
  // side reads "live" - honest that we're showing the delta from production.
  private ruleDiffRows(rules: LabTradingRulesDto | null | undefined): DiffRow[] {
    const rows: DiffRow[] = [];
    if (!rules) return rows;
    const pct = (v: number) => (v * 100).toFixed(0) + '%';
    const add = (label: string, newVal: string) => rows.push({ label, oldVal: 'live', newVal, changed: true });
    if (rules.maxHoldDays != null) add('Guide hold (all setups)', `${rules.maxHoldDays}d`);
    if (rules.stopLossPct != null) add('Stop (all setups)', pct(rules.stopLossPct));
    if (rules.targetPct != null) add('Target (all setups)', pct(rules.targetPct));
    if (rules.trailingActivationPct != null) add('Trail arm (all setups)', pct(rules.trailingActivationPct));
    if (rules.trailingDistancePct != null) add('Trail distance (all setups)', pct(rules.trailingDistancePct));
    if (rules.maxOpenPositions != null) add('Max open positions', `${rules.maxOpenPositions}`);
    if (rules.minHoldDays != null) add('Probation day', `${rules.minHoldDays}d`);
    if (rules.momentumHealthThreshold != null) add('Health floor', rules.momentumHealthThreshold.toFixed(2));
    for (const t of rules.setupTactics ?? []) {
      add(`${t.setup} tactics`, `stop ${pct(t.stopLossPct)} · target ${pct(t.targetPct)} · hold ${t.guideHoldDays}d`);
    }
    return rows;
  }

  // "Stronger run" in the A/B card = higher expectancy per trade. Ties (or a
  // single candidate) highlight nothing.
  abWinnerLabel(ab: AbResultDto): string | null {
    if (ab.candidates.length < 2) return null;
    const sorted = [...ab.candidates].sort((a, b) => b.result.expectancyPct - a.result.expectancyPct);
    return sorted[0].result.expectancyPct === sorted[1].result.expectancyPct ? null : sorted[0].label;
  }

  // Ranked on the robust score - the same key the backend picks the winner
  // on - so the winner actually appears at (or near) the top of the list.
  // Sorting on the headline expectancy here used to put lucky small-sample
  // configs above the winner and made the selection look arbitrary.
  sweepCandidatesRanked(sw: SweepResultDto) {
    return [...sw.candidates].sort((a, b) => b.robustScorePct - a.robustScorePct);
  }

  // Load a suggestion's dials into the form (doesn't touch production).
  tryDials(s: LabSuggestionDto): void {
    this.weights.set({ ...s.weights });
    this.buyThreshold.set(s.buyThreshold);
    // Own-data suggestions vary only the scoring dials; leave the excluded-
    // setups selection untouched so a non-breakout exclusion isn't silently
    // dropped when loading one.
    this.snackbar.open('Dials loaded — hit Run Simulation to see the full result.', 'Dismiss', { duration: 3000 });
  }

  // Shortcut from the optimizer results: load the winner's full configuration
  // into the A/B tab (historic mode, compare-against-production pre-ticked)
  // for a head-to-head over the FULL window with setup/exit breakdowns - the
  // human sanity-check step before Apply. Deliberately does NOT start the
  // run: the user still presses Run.
  testWinnerInAb(sweep: SweepResultDto): void {
    const w = sweep.winner;
    // Start from live-default rules, then layer the winner's overrides on top so
    // the reproduced run matches the winner EXACTLY. Without this the form's
    // (default) rules silently replace the winner's, and A/B / Validate / Monte
    // Carlo would test a different config than the one that won.
    this.resetRules();
    this.weights.set({ ...w.weights });
    this.buyThreshold.set(w.buyThreshold);
    this.excludedSetups.set(w.excludedSetups ?? (w.excludeBreakout ? ['Breakout'] : []));
    this.autopauseBear.set(w.autopauseDuringBear);

    const r = w.rules;
    if (r) {
      // Uniform rule fields (percent fields are stored as fractions, shown ×100).
      if (r.maxHoldDays != null) this.rulesMaxHoldDays.set(r.maxHoldDays);
      if (r.maxOpenPositions != null) this.rulesMaxOpenPositions.set(r.maxOpenPositions);
      if (r.stopLossPct != null) this.rulesStopLossPct.set(r.stopLossPct * 100);
      if (r.targetPct != null) this.rulesTargetPct.set(r.targetPct * 100);
      if (r.trailingActivationPct != null) this.rulesTrailingActivation.set(r.trailingActivationPct * 100);
      if (r.trailingDistancePct != null) this.rulesTrailingDistance.set(r.trailingDistancePct * 100);
      if (r.minHoldDays != null) this.rulesMinHoldDays.set(r.minHoldDays);
      if (r.momentumHealthThreshold != null) this.rulesHealthThreshold.set(r.momentumHealthThreshold);
      // Per-setup tactics: overlay each override onto the matching editor row.
      if (r.setupTactics?.length) {
        this.labTacticsDraft.update((rows) =>
          rows.map((row) => {
            const ov = r.setupTactics!.find((o) => o.setup === row.setupType);
            return ov
              ? { ...row, stopLossPct: ov.stopLossPct, targetPct: ov.targetPct, guideHoldDays: ov.guideHoldDays,
                  trailingActivationPct: ov.trailingActivationPct, trailingDistancePct: ov.trailingDistancePct }
              : row;
          }));
      }
    }

    this.dataSource.set('historic');
    this.regimeMode.set('neutral'); // compare vs production under the Neutral book
    this.labTabIndex.set(0);
    this.snackbar.open(
      `Winner's full config ("${w.label}") loaded — including its rule/setup overrides. Hit Run Simulation for the head-to-head vs production.`,
      'Dismiss', { duration: 6000 });
  }

  // ── Apply flows (always confirmed, owner-only) ─────────────────────────────

  // Canonical snapshot of the config that defines a run's live-applyable
  // outcome. Captured when a historic run is launched, re-computed at apply
  // time: an exact match means the form still describes what actually ran, so
  // the run's stored full config (rules + tactics) is safe to apply.
  private configFingerprint(): string {
    return JSON.stringify({
      weights: this.weights(),
      buyThreshold: this.buyThreshold(),
      excludedSetups: this.excludedSetups(),
      autopause: this.autopauseBear(),
      rules: this.buildRules(),
    });
  }

  applyToProduction(): void {
    const historic = this.dataSource() === 'historic';
    // When the run carried rule/tactic overrides, apply the FULL tested config
    // through the validated run - the same path the optimizer winner uses - so
    // "Apply your dials" reproduces live EXACTLY what beat production, instead
    // of silently dropping the tactics with only a warning.
    if (historic && this.rulesTouched()) {
      const matches = this.lastHistoricRunId != null
        && this.lastHistoricWasAb
        && this.lastHistoricConfigFingerprint === this.configFingerprint();
      if (matches) {
        this.applyHistoricRunFull(this.lastHistoricRunId!);
        return;
      }
      // Rules are touched but no completed A/B run reproduces the current form
      // (never ran, ran single, or the form changed since). Refuse to apply a
      // weights-only subset that wouldn't match what's on screen.
      this.snackbar.open(
        'Your dials include rule/tactic overrides. Run an A/B (compare against production) with these exact dials first, then Apply reproduces the whole config live.',
        'Dismiss', { duration: 8000 });
      return;
    }
    // Weights-only apply: own-data (rules can't be tested on a replay) or a
    // historic run with no rule overrides.
    const w = this.weights();
    const autopause = historic ? this.autopauseBear() : undefined;
    const autopauseNote = autopause !== undefined && autopause !== this.productionAutopauseBear
      ? ` This also turns the live bear-market autopause ${autopause ? 'ON' : 'OFF'}.`
      : '';
    this.applyConfig(w, this.buyThreshold(),
      `This sets your LIVE strategy weights and Buy threshold (${this.buyThreshold().toFixed(1)}) to the dials currently in the form.` +
      `${autopauseNote} The next research run will score every signal with them. Are you sure?`,
      autopause, this.buildManualEvidence());
  }

  // Full-config apply of a completed A/B run: weights + rule/tactic overrides +
  // autopause, through the same audited /backtest/{runId}/apply path as the
  // optimizer winner and the history dialog.
  private applyHistoricRunFull(runId: number): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        data: {
          title: 'Apply to production',
          message:
            'This sets your LIVE strategy weights and Buy threshold AND applies the rule/per-setup-tactic ' +
            'overrides you tested (and the bear-autopause setting if it differs) — the whole configuration this ' +
            'A/B run evaluated. Are you sure?',
          cancelLabel: 'Cancel',
          confirmLabel: 'Apply to production',
          confirmColor: 'warn',
        },
        width: '460px',
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (!confirmed) return;
        this.api.applyBacktestRun(runId, true, true).subscribe({
          next: () => {
            this.snackbar.open('Applied — production now uses these dials and their rule/tactic overrides. Recorded in the refinement history. ✅', 'Dismiss', { duration: 4000 });
            this.api.getStrategyWeights().subscribe({ next: (u) => this.productionWeights.set(u), error: () => {} });
            this.productionAutopauseBear = this.autopauseBear();
          },
          error: (err) => this.snackbar.open(errorMessage(err, 'Failed to apply.'), 'Dismiss', { duration: 5000 }),
        });
      });
  }

  applySweepWinner(sweep: SweepResultDto): void {
    if (this.lastSweepRunId == null) {
      this.snackbar.open('This optimizer run is no longer loaded — re-run the sweep before applying.', 'Dismiss', { duration: 5000 });
      return;
    }
    const w = sweep.winner;
    // Apply the winner through the stored run so live reproduces it EXACTLY:
    // weights + rule/tactic overrides + autopause, not just the weights.
    const autopauseChanged = w.autopauseDuringBear !== sweep.baseline.autopauseDuringBear;
    const hasRiskOverrides = !!w.rules || autopauseChanged;
    const extra = sweep.validation.heldUp
      ? ''
      : ' WARNING: this configuration did NOT hold up on held-out data — applying it is not recommended.';
    const riskNote = hasRiskOverrides
      ? ' This also applies its risk-rule and per-setup tactic overrides' +
        (autopauseChanged ? ` and turns the live bear-market autopause ${w.autopauseDuringBear ? 'ON' : 'OFF'}.` : '.')
      : '';
    this.dialog
      .open(ConfirmDialogComponent, {
        data: {
          title: 'Apply winner to production',
          message:
            `This sets your LIVE strategy weights and Buy threshold (${w.buyThreshold.toFixed(1)}) to the optimizer's ` +
            `winning configuration ("${w.label}").${riskNote}${extra} Are you sure?`,
          cancelLabel: 'Cancel',
          confirmLabel: 'Apply to production',
          confirmColor: 'warn',
        },
        width: '460px',
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (!confirmed) return;
        this.api.applyBacktestRun(this.lastSweepRunId!, true, hasRiskOverrides).subscribe({
          next: () => {
            this.snackbar.open(
              hasRiskOverrides
                ? 'Applied — production now uses the winner’s weights AND its risk/tactic overrides. Recorded in the refinement history. ✅'
                : 'Applied — production now uses the winner’s weights. Recorded in the refinement history. ✅',
              'Dismiss', { duration: 4000 });
            this.api.getStrategyWeights().subscribe({ next: (u) => this.productionWeights.set(u), error: () => {} });
            if (autopauseChanged) this.productionAutopauseBear = w.autopauseDuringBear;
          },
          error: (err) => this.snackbar.open(errorMessage(err, 'Failed to apply the winner.'), 'Dismiss', { duration: 5000 }),
        });
      });
  }

  // Evidence description for a form-dials apply, based on whatever run the
  // user most recently completed - the refinement page's audit trail should
  // say WHY the weights changed, not just that they did.
  private buildManualEvidence(): { summary: string; tradeCount: number; winRate: number; confidence: 0 | 1 | 2 } {
    const ab = this.abResult();
    if (ab && ab.candidates.length > 0) {
      const yours = ab.candidates[0];
      const prod = ab.candidates.length > 1 ? ab.candidates[1] : null;
      return {
        summary:
          `Strategy Lab historic A/B: "${yours.label}" expectancy ${yours.result.expectancyPct.toFixed(2)}%/trade over ` +
          `${yours.result.trades} trades` +
          (prod ? ` vs production ${prod.result.expectancyPct.toFixed(2)}%/trade over ${prod.result.trades} trades` : '') +
          ' on the same historic window.',
        tradeCount: yours.result.trades,
        winRate: yours.result.winRate,
        confidence: 1,
      };
    }
    const h = this.historicResult();
    if (h) {
      return {
        summary:
          `Strategy Lab historic run: ${h.trades} trades, expectancy ${h.expectancyPct.toFixed(2)}%/trade, ` +
          `profit factor ${h.profitFactor.toFixed(2)}, total return ${h.totalReturnPct.toFixed(1)}% ` +
          `(SPY ${h.spyReturnPct.toFixed(1)}%).`,
        tradeCount: h.trades,
        winRate: h.winRate,
        confidence: 1,
      };
    }
    const own = this.response();
    if (own) {
      return {
        summary:
          `Strategy Lab own-data replay: dials keep ${own.result.tradesKept}/${own.result.totalClosedTrades} of your closed ` +
          `trades, avg market-adjusted return ${own.result.simAvgReturnPct.toFixed(2)}%/trade ` +
          `(actual: ${own.result.actualAvgReturnPct.toFixed(2)}%).`,
        tradeCount: own.result.tradesKept,
        winRate: own.result.simWinRate,
        confidence: 1,
      };
    }
    return {
      summary: 'Applied manually from the Strategy Lab without a completed run to reference.',
      tradeCount: 0,
      winRate: 0,
      confidence: 0,
    };
  }

  // Syncs the live bear-autopause setting when an applied config carries a
  // different value than the account currently has - "Apply" must mean the
  // whole tested configuration, not just the weights.
  private syncAutopauseSetting(autopause: boolean | undefined): void {
    if (autopause === undefined || autopause === this.productionAutopauseBear) return;
    // The tested autopause maps to the Bear regime book.
    this.api.getRiskProfile('Bear').subscribe({
      next: (p) => {
        this.api.updateRiskProfile({
          regime: 'Bear',
          lockedCapitalPct: p.lockedCapitalPct,
          maxOpenPositions: p.maxOpenPositions,
          dailyLossCircuitBreakerPct: p.dailyLossCircuitBreakerPct,
          maxHoldDays: p.maxHoldDays,
          trailingActivationPct: p.trailingActivationPct,
          trailingDistancePct: p.trailingDistancePct,
          earningsGateDays: p.earningsGateDays,
          minHoldDays: p.minHoldDays,
          momentumHealthThreshold: p.momentumHealthThreshold,
          autopauseTrading: autopause,
          stopLossPct: p.stopLossPct,
          targetPct: p.targetPct,
          sizingMode: p.sizingMode,
          flatPositionPct: p.flatPositionPct,
          sizingAggressiveness: p.sizingAggressiveness,
          forwardVetoFloor: p.forwardVetoFloor,
        }).subscribe({
          next: () => {
            this.productionAutopauseBear = autopause;
            this.snackbar.open(`Live bear-market autopause turned ${autopause ? 'ON' : 'OFF'}.`, 'Dismiss', { duration: 4000 });
          },
          error: (err) => this.snackbar.open(
            errorMessage(err, 'Weights applied, but updating the bear-autopause setting failed — change it in Settings › Trading.'),
            'Dismiss', { duration: 8000 }),
        });
      },
      error: () => this.snackbar.open(
        'Weights applied, but updating the bear-autopause setting failed — change it in Settings › Trading.',
        'Dismiss', { duration: 8000 }),
    });
  }

  // Applies through POST /strategy-lab/apply rather than the raw weights PUT:
  // one click for the user, but the endpoint records an immediately-applied
  // RefinementSuggestion (origin Strategy Lab, carrying the run's evidence),
  // so the refinement page holds the audit trail of every weight change.
  private applyConfig(
    w: LabWeightsDto, threshold: number, message: string, autopause: boolean | undefined,
    evidence: { summary: string; tradeCount: number; winRate: number; confidence: 0 | 1 | 2 },
  ): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        data: {
          title: 'Apply to production',
          message,
          cancelLabel: 'Cancel',
          confirmLabel: 'Apply to production',
          confirmColor: 'warn',
        },
        width: '460px',
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (!confirmed) return;
        this.api
          .applyLabConfig({
            weights: w,
            buyThreshold: threshold,
            evidenceSummary: evidence.summary,
            tradeCount: evidence.tradeCount,
            winRate: evidence.winRate,
            confidence: evidence.confidence,
          })
          .subscribe({
            next: () => {
              this.snackbar.open('Applied — production now uses these dials. Recorded in the refinement history. ✅', 'Dismiss', { duration: 4000 });
              this.api.getStrategyWeights().subscribe({
                next: (updated) => this.productionWeights.set(updated),
                error: () => {},
              });
              this.syncAutopauseSetting(autopause);
            },
            error: (err) => this.snackbar.open(errorMessage(err, 'Failed to apply.'), 'Dismiss', { duration: 5000 }),
          });
      });
  }
}
