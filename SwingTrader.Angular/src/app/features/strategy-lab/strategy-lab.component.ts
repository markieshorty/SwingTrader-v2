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
  private lastHistoricRunId: number | null = null;

  // ── Claude analysis (advisory only) ────────────────────────────────────────
  analysing = signal(false);
  analysis = signal<LabAnalyseResponseDto | null>(null);

  // ── Optimizer tab state ────────────────────────────────────────────────────
  optimizing = signal(false);
  sweepStatus = signal<string | null>(null);
  sweepResult = signal<SweepResultDto | null>(null);

  // ── Shared state ───────────────────────────────────────────────────────────
  dataStatus = signal<LabDataStatusDto | null>(null);
  syncing = signal(false);
  productionWeights = signal<StrategyWeightsDto | null>(null);
  isOwner = signal(false);
  private pollTimer: ReturnType<typeof setInterval> | null = null;

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
      next: (d) => this.dataStatus.set(d),
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
        this.snackbar.open('Candle sync queued — the initial 3-year load takes ~15 minutes. Check back shortly.', 'Dismiss', { duration: 6000 });
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
    };
    this.ranWeights = request.weights;
    this.ranThreshold = request.buyThreshold;
    this.ranExcludeBreakout = request.excludeBreakout;

    if (historic) {
      this.api.runStrategyLabHistoric(request).subscribe({
        next: (r) => {
          this.lastHistoricRunId = r.backtestRunId;
          this.pollRun(r.backtestRunId, 'Running — replaying 3 years of market data through your dials…',
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
        r.backtestRunId,
        'Running — evaluating ~25 dial variations on the training window, then validating the best on held-out data. This takes a while…',
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
  // decides how to interpret the stored result.
  private pollRun(
    runId: number,
    runningText: string,
    status: ReturnType<typeof signal<string | null>>,
    onDone: (result: BacktestResultDto | null) => void,
  ): void {
    status.set('Queued — the job runs server-side…');
    if (this.pollTimer) clearInterval(this.pollTimer);
    this.pollTimer = setInterval(() => {
      this.api.getBacktestRun(runId).subscribe({
        next: (r) => {
          if (r.status === 'Running') status.set(runningText);
          if (r.status === 'Completed' || r.status === 'Failed') {
            if (this.pollTimer) clearInterval(this.pollTimer);
            this.pollTimer = null;
            status.set(null);
            if (r.status === 'Completed') onDone(r.result);
            else {
              this.snackbar.open(`Job failed: ${r.error}`, 'Dismiss', { duration: 8000 });
              onDone(null);
              this.running.set(false);
              this.optimizing.set(false);
            }
          }
        },
        error: () => {}, // transient poll failure - keep polling
      });
    }, 5000);
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
    return this.diffRows(
      sweep.baseline.weights, sweep.baseline.buyThreshold, sweep.baseline.excludeBreakout,
      sweep.winner.weights, sweep.winner.buyThreshold, sweep.winner.excludeBreakout);
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

  // ── Apply flows (always confirmed, owner-only) ─────────────────────────────

  applyToProduction(): void {
    const w = this.weights();
    this.applyConfig(w, this.buyThreshold(),
      `This sets your LIVE strategy weights and Buy threshold (${this.buyThreshold().toFixed(1)}) to the dials currently in the form. ` +
      'The next research run will score every signal with them. Are you sure?');
  }

  applySweepWinner(sweep: SweepResultDto): void {
    const extra = sweep.validation.heldUp
      ? ''
      : ' WARNING: this configuration did NOT hold up on held-out data — applying it is not recommended.';
    this.applyConfig(sweep.winner.weights, sweep.winner.buyThreshold,
      `This sets your LIVE strategy weights and Buy threshold (${sweep.winner.buyThreshold.toFixed(1)}) to the optimizer's ` +
      `winning configuration ("${sweep.winner.label}").${extra} Are you sure?`);
  }

  private applyConfig(w: LabWeightsDto, threshold: number, message: string): void {
    const prod = this.productionWeights();
    if (!prod) return;
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
          .updateStrategyWeights({
            ...prod,
            rsiWeight: w.rsi, macdWeight: w.macd, volumeWeight: w.volume, sentimentWeight: w.sentiment,
            setupQualityWeight: w.setupQuality, relativeStrengthWeight: w.relativeStrength,
            priceLevelWeight: w.priceLevel, fundamentalMomentumWeight: w.fundamentalMomentum,
            buyThreshold: threshold,
          })
          .subscribe({
            next: () => {
              this.snackbar.open('Applied — production now uses these dials. ✅', 'Dismiss', { duration: 4000 });
              this.api.getStrategyWeights().subscribe({
                next: (updated) => this.productionWeights.set(updated),
                error: () => {},
              });
            },
            error: (err) => this.snackbar.open(errorMessage(err, 'Failed to apply.'), 'Dismiss', { duration: 5000 }),
          });
      });
  }
}
