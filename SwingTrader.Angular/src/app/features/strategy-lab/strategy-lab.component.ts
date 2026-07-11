import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
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
  BacktestResultDto,
  HistoricResultDto,
  LabAnalyseResponseDto,
  LabAnalyseSuggestionDto,
  LabDataStatusDto,
  LabSuggestionDto,
  LabWeightsDto,
  StrategyLabResponseDto,
  StrategyWeightsDto,
  SweepResultDto,
} from '../../core/models/dtos';
import { errorMessage } from '../../shared/utils/error-message.util';
import { ConfirmDialogComponent } from '../../shared/components/confirm-dialog/confirm-dialog.component';

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
    MatCheckboxModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatSliderModule,
    MatSlideToggleModule,
    MatProgressSpinnerModule,
    MatTabsModule,
    MatTooltipModule,
    MatDialogModule,
    MatExpansionModule,
  ],
  templateUrl: './strategy-lab.component.html',
  styleUrl: './strategy-lab.component.scss',
})
export class StrategyLabComponent {
  private api = inject(ApiService);
  private snackbar = inject(MatSnackBar);
  private dialog = inject(MatDialog);

  readonly dials: WeightDial[] = [
    { key: 'rsi', label: 'RSI', hint: 'Dip-buying signal — favours pullbacks recovering from oversold' },
    { key: 'macd', label: 'MACD', hint: 'Momentum direction — rewards rising, positive momentum' },
    { key: 'volume', label: 'Volume', hint: 'Volume confirmation — rewards above-average participation' },
    { key: 'sentiment', label: 'News sentiment', hint: "Claude's read of recent news for the stock" },
    { key: 'setupQuality', label: 'Setup quality', hint: 'How favourable the detected chart pattern is' },
    { key: 'relativeStrength', label: 'Relative strength', hint: 'Performance vs the stock’s sector ETF' },
    { key: 'priceLevel', label: 'Price level', hint: 'Position relative to support/resistance memory' },
    { key: 'fundamentalMomentum', label: 'Fundamental momentum', hint: 'Earnings/revenue trajectory score' },
  ];

  // Components the historic backtester cannot reconstruct from price/volume
  // bars alone — they score a fixed neutral 0.5 during a historic run. Their
  // weight therefore isn't ignored: it contributes weight×0.5 to every
  // conviction score, so shifting weight onto them compresses all scores
  // toward 5.0 and only changes selectivity vs the Buy threshold - pure
  // dilution, measuring nothing about the component itself, and the result
  // wouldn't transfer to production where these components have real data.
  // The UI locks these dials while historic mode is selected.
  private readonly noHistoricDataKeys: ReadonlySet<keyof LabWeightsDto> = new Set([
    'sentiment', 'fundamentalMomentum',
  ]);

  lacksHistoricData(key: keyof LabWeightsDto): boolean {
    return this.dataSource() === 'historic' && this.noHistoricDataKeys.has(key);
  }

  noHistoricDataLabels(): string {
    return this.dials.filter((d) => this.noHistoricDataKeys.has(d.key)).map((d) => d.label).join(', ');
  }

  // ── A/B Testing tab state ──────────────────────────────────────────────────
  dataSource = signal<'own' | 'historic'>('own');
  weights = signal<LabWeightsDto>({
    rsi: 0.17, macd: 0.09, volume: 0.21, sentiment: 0.16,
    setupQuality: 0.12, relativeStrength: 0.1, priceLevel: 0.05, fundamentalMomentum: 0.1,
  });
  buyThreshold = signal(6.0);
  excludeBreakout = signal(true);
  // Historic A/B is opt-in (doubles a multi-minute job); own-data A/B is free
  // and always on - the server evaluates production dials alongside yours.
  compareBaselineHistoric = signal(false);
  // Historic-only dial: skip new entries while SPY < its 200-day average
  // (approximates the live bear autopause). Initialised from the account's
  // live setting; unlike sentiment etc. this IS reconstructable from bars,
  // so flipping it is a legitimate experiment.
  autopauseBear = signal(true);
  private productionAutopauseBear = true;

  // ── Trading rules (experiment, historic mode only) ─────────────────────────
  // Overrides ride the request and apply to "Your dials" only; the production
  // baseline always replays with the live risk-profile rules. Numeric fields
  // are prefilled from the profile so an untouched Run simulates production.
  readonly excludableSetups = ['OversoldRecovery', 'MomentumContinuation', 'VolumeSpike', 'TrendFollowing', 'Unknown'];
  rulesExtraExcludedSetups = signal<string[]>([]);
  rulesMaxHoldDays = signal(10);
  rulesMaxOpenPositions = signal(3);
  rulesTrailingActivation = signal(5); // percent, converted to fraction on send
  rulesTrailingDistance = signal(3);
  // Blank = production's EntryLevelCalculator table (setup-based stops,
  // conviction-based targets); a value = flat override for the run.
  rulesStopLossPct = signal<number | null>(null);
  rulesTargetPct = signal<number | null>(null);
  rulesSimulateProbation = signal(true);
  rulesMinHoldDays = signal(3);
  rulesHealthThreshold = signal(0.5);
  // Position sizing. Flat = the engine's long-standing model (X% of equity
  // per trade). Pool = mirrors live PositionSizingService: a tier-sized
  // active-capital pool caps total deployment (Tier 1 = 10% of the account).
  rulesSizingMode = signal<'flat' | 'pool'>('flat');
  rulesPositionFraction = signal(10);      // percent of equity per trade (flat mode)
  rulesActiveCapitalPct = signal(10);      // percent of equity in the pool (pool mode)
  rulesMaxPositionPctOfActive = signal(33); // percent of pool per position (pool mode)
  private profileRules = {
    maxHoldDays: 10, maxOpenPositions: 3, trailingActivation: 5, trailingDistance: 3,
    minHoldDays: 3, healthThreshold: 0.5, maxPositionPctOfActive: 0.33,
  };

  rulesTouched = computed(() =>
    this.rulesExtraExcludedSetups().length > 0
    || this.rulesMaxHoldDays() !== this.profileRules.maxHoldDays
    || this.rulesMaxOpenPositions() !== this.profileRules.maxOpenPositions
    || this.rulesTrailingActivation() !== this.profileRules.trailingActivation
    || this.rulesTrailingDistance() !== this.profileRules.trailingDistance
    || this.rulesStopLossPct() !== null
    || this.rulesTargetPct() !== null
    || !this.rulesSimulateProbation()
    || this.rulesMinHoldDays() !== this.profileRules.minHoldDays
    || this.rulesHealthThreshold() !== this.profileRules.healthThreshold
    || this.rulesSizingMode() !== 'flat'
    || this.rulesPositionFraction() !== 10);

  resetRules(): void {
    this.rulesExtraExcludedSetups.set([]);
    this.rulesMaxHoldDays.set(this.profileRules.maxHoldDays);
    this.rulesMaxOpenPositions.set(this.profileRules.maxOpenPositions);
    this.rulesTrailingActivation.set(this.profileRules.trailingActivation);
    this.rulesTrailingDistance.set(this.profileRules.trailingDistance);
    this.rulesStopLossPct.set(null);
    this.rulesTargetPct.set(null);
    this.rulesSimulateProbation.set(true);
    this.rulesMinHoldDays.set(this.profileRules.minHoldDays);
    this.rulesHealthThreshold.set(this.profileRules.healthThreshold);
    this.rulesSizingMode.set('flat');
    this.rulesPositionFraction.set(10);
    this.rulesActiveCapitalPct.set(10);
    this.rulesMaxPositionPctOfActive.set(Math.round(this.profileRules.maxPositionPctOfActive * 100));
  }

  running = signal(false);
  response = signal<StrategyLabResponseDto | null>(null);
  historicResult = signal<HistoricResultDto | null>(null);
  abResult = signal<AbResultDto | null>(null);
  historicStatus = signal<string | null>(null); // Queued/Running progress text

  // Snapshot of the dials actually submitted for the run in progress/last
  // completed - the "old" side of diff tables and the config sent to the
  // analysis endpoint. Can't just read the live `weights` signal: the user
  // may have loaded other dials into the form since the run finished.
  private ranWeights: LabWeightsDto | null = null;
  private ranThreshold = 0;
  private ranExcludeBreakout = false;
  private ranAutopauseBear = true;
  private lastHistoricRunId: number | null = null;

  // ── Claude analysis (advisory only) ────────────────────────────────────────
  analysing = signal(false);
  analysis = signal<LabAnalyseResponseDto | null>(null);

  // Programmatic tab control (the "Test winner in A/B" shortcut jumps tabs).
  labTabIndex = signal(0);

  // ── Optimizer tab state ────────────────────────────────────────────────────
  optimizing = signal(false);
  sweepStatus = signal<string | null>(null);
  sweepResult = signal<SweepResultDto | null>(null);

  // ── Shared state ───────────────────────────────────────────────────────────
  dataStatus = signal<LabDataStatusDto | null>(null);
  dataStatusLoading = signal(true); // true until the first data-status fetch resolves
  syncing = signal(false);
  productionWeights = signal<StrategyWeightsDto | null>(null);
  isOwner = signal(false);
  // One poll handle per job kind: an A/B run and an optimizer sweep can be in
  // flight at the same time, and starting one must not kill the other's poll.
  private pollHandles = new Map<'ab' | 'sweep', ReturnType<typeof setInterval>>();

  weightSum = computed(() => {
    const w = this.weights();
    return Math.round(
      (w.rsi + w.macd + w.volume + w.sentiment + w.setupQuality + w.relativeStrength + w.priceLevel + w.fundamentalMomentum) * 100,
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
    // Prefill the bear-autopause dial from the account's live setting so an
    // untouched Run simulates what production does today.
    this.api.getRiskProfile().subscribe({
      next: (p) => {
        this.productionAutopauseBear = p.autopauseDuringBear;
        this.autopauseBear.set(p.autopauseDuringBear);
        // Trading-rules defaults mirror the live profile (percent for display).
        this.profileRules = {
          maxHoldDays: p.maxHoldDays,
          maxOpenPositions: p.maxOpenPositions,
          trailingActivation: Math.round(p.trailingActivationPct * 100),
          trailingDistance: Math.round(p.trailingDistancePct * 100),
          minHoldDays: p.minHoldDays,
          healthThreshold: p.momentumHealthThreshold,
          maxPositionPctOfActive: p.maxPositionPctOfActive,
        };
        this.resetRules();
      },
      error: () => {},
    });
  }

  historicAvailable(): boolean {
    return (this.dataStatus()?.bars ?? 0) > 0;
  }

  syncData(): void {
    this.syncing.set(true);
    this.api.syncLabData().subscribe({
      next: () => {
        this.snackbar.open('Candle sync queued — a full 5-year load takes ~25 minutes. Check back shortly.', 'Dismiss', { duration: 6000 });
        this.syncing.set(false);
      },
      error: (err) => {
        this.snackbar.open(errorMessage(err, 'Failed to queue sync.'), 'Dismiss', { duration: 5000 });
        this.syncing.set(false);
      },
    });
  }

  setWeight(key: keyof LabWeightsDto, value: number): void {
    if (this.lacksHistoricData(key)) return; // locked in historic mode
    this.weights.set({ ...this.weights(), [key]: value });
  }

  // Scale weights so they sum to exactly 1.0 - saves the user hand-tuning
  // dials to hit the sum-to-one requirement the Run button enforces. In
  // historic mode the locked no-data dials keep their values; only the
  // adjustable dials are scaled to fill the remainder.
  normaliseWeights(): void {
    const w = this.weights();
    const adjustable = this.dials.filter((d) => !this.lacksHistoricData(d.key));
    const lockedTotal = this.dials.filter((d) => this.lacksHistoricData(d.key)).reduce((s, d) => s + w[d.key], 0);
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
      rsi: w.rsiWeight, macd: w.macdWeight, volume: w.volumeWeight, sentiment: w.sentimentWeight,
      setupQuality: w.setupQualityWeight, relativeStrength: w.relativeStrengthWeight,
      priceLevel: w.priceLevelWeight, fundamentalMomentum: w.fundamentalMomentumWeight,
    });
    this.buyThreshold.set(w.buyThreshold);
    this.excludeBreakout.set(true);
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
      excludeBreakout: this.excludeBreakout(),
      // Own-data comparison is a free in-memory replay - always on. Historic
      // doubles a multi-minute job, so it follows the checkbox.
      compareBaseline: historic ? this.compareBaselineHistoric() : true,
      autopauseDuringBear: this.autopauseBear(),
      // Trading-rule overrides only ride historic runs, and only when
      // something was actually changed - an untouched panel sends nothing so
      // the engine uses the live risk profile exactly as before.
      rules: historic && this.rulesTouched()
        ? {
            excludedSetups: [
              ...(this.excludeBreakout() ? ['Breakout'] : []),
              ...this.rulesExtraExcludedSetups(),
            ],
            maxHoldDays: this.rulesMaxHoldDays(),
            maxOpenPositions: this.rulesMaxOpenPositions(),
            trailingActivationPct: this.rulesTrailingActivation() / 100,
            trailingDistancePct: this.rulesTrailingDistance() / 100,
            stopLossPct: this.rulesStopLossPct() !== null ? this.rulesStopLossPct()! / 100 : null,
            targetPct: this.rulesTargetPct() !== null ? this.rulesTargetPct()! / 100 : null,
            simulateProbation: this.rulesSimulateProbation(),
            minHoldDays: this.rulesMinHoldDays(),
            momentumHealthThreshold: this.rulesHealthThreshold(),
            positionFraction: this.rulesSizingMode() === 'flat' ? this.rulesPositionFraction() / 100 : null,
            activeCapitalPct: this.rulesSizingMode() === 'pool' ? this.rulesActiveCapitalPct() / 100 : null,
            maxPositionPctOfActive: this.rulesSizingMode() === 'pool' ? this.rulesMaxPositionPctOfActive() / 100 : null,
          }
        : null,
    };
    this.ranWeights = request.weights;
    this.ranThreshold = request.buyThreshold;
    this.ranExcludeBreakout = request.excludeBreakout;
    this.ranAutopauseBear = request.autopauseDuringBear;

    if (historic) {
      this.api.runStrategyLabHistoric(request).subscribe({
        next: (r) => {
          this.lastHistoricRunId = r.backtestRunId;
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

  // ── Optimizer tab ──────────────────────────────────────────────────────────

  runOptimizer(): void {
    this.optimizing.set(true);
    this.sweepResult.set(null);
    this.api.runStrategyLabOptimize().subscribe({
      next: (r) => this.pollRun(
        'sweep',
        r.backtestRunId,
        'Running — evaluating ~200 dial variations on the training window, then validating the best on held-out data. Expect 10–20 minutes…',
        this.sweepStatus,
        (result) => {
          if (result && 'mode' in result && result.mode === 'sweep') this.sweepResult.set(result);
          else this.snackbar.open('Unexpected optimizer result shape.', 'Dismiss', { duration: 5000 });
          this.optimizing.set(false);
        }),
      error: (err) => {
        this.snackbar.open(errorMessage(err, 'Failed to queue the optimizer.'), 'Dismiss', { duration: 5000 });
        this.optimizing.set(false);
      },
    });
  }

  // Server-side jobs are polled until the run row completes; the caller
  // decides how to interpret the stored result. Restarting a job of the same
  // kind replaces its own poll; the other kind's poll is left running.
  private pollRun(
    kind: 'ab' | 'sweep',
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
          if (r.status === 'Completed' || r.status === 'Failed') {
            clearInterval(timer);
            this.pollHandles.delete(kind);
            status.set(null);
            if (r.status === 'Completed') onDone(r.result);
            else {
              this.snackbar.open(`Job failed: ${r.error}`, 'Dismiss', { duration: 8000 });
              onDone(null);
              if (kind === 'ab') this.running.set(false);
              else this.optimizing.set(false);
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
    this.excludeBreakout.set(s.excludeBreakout);
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
    const rows = this.diffRows(
      sweep.baseline.weights, sweep.baseline.buyThreshold, sweep.baseline.excludeBreakout,
      sweep.winner.weights, sweep.winner.buyThreshold, sweep.winner.excludeBreakout);
    rows.push({
      label: 'Bear autopause',
      oldVal: sweep.baseline.autopauseDuringBear ? 'On' : 'Off',
      newVal: sweep.winner.autopauseDuringBear ? 'On' : 'Off',
      changed: sweep.baseline.autopauseDuringBear !== sweep.winner.autopauseDuringBear,
    });
    return rows;
  }

  // "Stronger run" in the A/B card = higher expectancy per trade. Ties (or a
  // single candidate) highlight nothing.
  abWinnerLabel(ab: AbResultDto): string | null {
    if (ab.candidates.length < 2) return null;
    const sorted = [...ab.candidates].sort((a, b) => b.result.expectancyPct - a.result.expectancyPct);
    return sorted[0].result.expectancyPct === sorted[1].result.expectancyPct ? null : sorted[0].label;
  }

  sweepCandidatesRanked(sw: SweepResultDto) {
    return [...sw.candidates].sort((a, b) => b.adjustedExpectancyPct - a.adjustedExpectancyPct);
  }

  // Load a suggestion's dials into the form (doesn't touch production).
  tryDials(s: LabSuggestionDto): void {
    this.weights.set({ ...s.weights });
    this.buyThreshold.set(s.buyThreshold);
    this.excludeBreakout.set(s.excludeBreakout);
    this.snackbar.open('Dials loaded — hit Run Simulation to see the full result.', 'Dismiss', { duration: 3000 });
  }

  // Shortcut from the optimizer results: load the winner's full configuration
  // into the A/B tab (historic mode, compare-against-production pre-ticked)
  // for a head-to-head over the FULL window with setup/exit breakdowns - the
  // human sanity-check step before Apply. Deliberately does NOT start the
  // run: the user still presses Run.
  testWinnerInAb(sweep: SweepResultDto): void {
    this.weights.set({ ...sweep.winner.weights });
    this.buyThreshold.set(sweep.winner.buyThreshold);
    this.excludeBreakout.set(sweep.winner.excludeBreakout);
    this.autopauseBear.set(sweep.winner.autopauseDuringBear);
    this.dataSource.set('historic');
    this.compareBaselineHistoric.set(true);
    this.labTabIndex.set(0);
    this.snackbar.open(
      `Winner's dials ("${sweep.winner.label}") loaded — hit Run Simulation for the full-window head-to-head vs production.`,
      'Dismiss', { duration: 6000 });
  }

  // ── Apply flows (always confirmed, owner-only) ─────────────────────────────

  applyToProduction(): void {
    const w = this.weights();
    const autopause = this.dataSource() === 'historic' ? this.autopauseBear() : undefined;
    const autopauseNote = autopause !== undefined && autopause !== this.productionAutopauseBear
      ? ` This also turns the live bear-market autopause ${autopause ? 'ON' : 'OFF'}.`
      : '';
    // Rule overrides are experiment-only and are NOT applied - without this
    // warning, a run that beat production because of a rule change looks like
    // it "didn't save" when the applied weights don't reproduce it.
    const rulesNote = this.dataSource() === 'historic' && this.rulesTouched()
      ? ' NOTE: your run used Trading-rules overrides (setups/holds/positions/trailing) which are NOT applied here — ' +
        'to keep those, change them on the Settings › Trading page.'
      : '';
    this.applyConfig(w, this.buyThreshold(),
      `This sets your LIVE strategy weights and Buy threshold (${this.buyThreshold().toFixed(1)}) to the dials currently in the form.` +
      `${autopauseNote} The next research run will score every signal with them.${rulesNote} Are you sure?`,
      autopause, this.buildManualEvidence());
  }

  applySweepWinner(sweep: SweepResultDto): void {
    const extra = sweep.validation.heldUp
      ? ''
      : ' WARNING: this configuration did NOT hold up on held-out data — applying it is not recommended.';
    const autopause = sweep.winner.autopauseDuringBear;
    const autopauseNote = autopause !== this.productionAutopauseBear
      ? ` This also turns the live bear-market autopause ${autopause ? 'ON' : 'OFF'}.`
      : '';
    this.applyConfig(sweep.winner.weights, sweep.winner.buyThreshold,
      `This sets your LIVE strategy weights and Buy threshold (${sweep.winner.buyThreshold.toFixed(1)}) to the optimizer's ` +
      `winning configuration ("${sweep.winner.label}").${autopauseNote}${extra} Are you sure?`,
      autopause,
      {
        summary:
          `Optimizer sweep winner "${sweep.winner.label}": market-adjusted expectancy ` +
          `${sweep.validation.trainAdjustedExpectancyPct.toFixed(2)}%/trade on the tuning window, ` +
          `${sweep.validation.holdoutAdjustedExpectancyPct.toFixed(2)}% on held-out data ` +
          `(production baseline: ${sweep.validation.baselineHoldoutAdjustedExpectancyPct.toFixed(2)}% on the same held-out window) — ` +
          `${sweep.validation.heldUp ? 'HELD UP out-of-sample' : 'did NOT hold up out-of-sample; applied against recommendation'}.`,
        tradeCount: sweep.winner.trades,
        winRate: sweep.winner.winRate,
        confidence: sweep.validation.heldUp ? 2 : 0,
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
    this.api.getRiskProfile().subscribe({
      next: (p) => {
        this.api.updateRiskProfile({
          lockedCapitalPct: p.lockedCapitalPct,
          maxPositionPctOfActive: p.maxPositionPctOfActive,
          maxOpenPositions: p.maxOpenPositions,
          dailyLossCircuitBreakerPct: p.dailyLossCircuitBreakerPct,
          tier1UnlockMinTrades: p.tier1UnlockMinTrades,
          tier1UnlockMinWinRate: p.tier1UnlockMinWinRate,
          tier2UnlockMinTrades: p.tier2UnlockMinTrades,
          tier2UnlockMinWinRate: p.tier2UnlockMinWinRate,
          maxHoldDays: p.maxHoldDays,
          trailingActivationPct: p.trailingActivationPct,
          trailingDistancePct: p.trailingDistancePct,
          earningsGateDays: p.earningsGateDays,
          minHoldDays: p.minHoldDays,
          momentumHealthThreshold: p.momentumHealthThreshold,
          targetWatchlistSize: p.targetWatchlistSize,
          autopauseDuringBear: autopause,
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
