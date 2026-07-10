import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSliderModule } from '@angular/material/slider';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../../core/services/api.service';
import {
  HistoricResultDto,
  LabDataStatusDto,
  LabSuggestionDto,
  LabWeightsDto,
  StrategyLabResponseDto,
  StrategyWeightsDto,
} from '../../core/models/dtos';
import { errorMessage } from '../../shared/utils/error-message.util';
import { ConfirmDialogComponent } from '../../shared/components/confirm-dialog/confirm-dialog.component';

interface WeightDial {
  key: keyof LabWeightsDto;
  label: string;
  hint: string;
}

@Component({
  selector: 'app-strategy-lab',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    MatCardModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatSelectModule,
    MatSliderModule,
    MatSlideToggleModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
    MatDialogModule,
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

  dataSource = signal<'own' | 'historic'>('own');
  weights = signal<LabWeightsDto>({
    rsi: 0.17, macd: 0.09, volume: 0.21, sentiment: 0.16,
    setupQuality: 0.12, relativeStrength: 0.1, priceLevel: 0.05, fundamentalMomentum: 0.1,
  });
  buyThreshold = signal(6.0);
  excludeBreakout = signal(true);

  running = signal(false);
  response = signal<StrategyLabResponseDto | null>(null);
  historicResult = signal<HistoricResultDto | null>(null);
  historicStatus = signal<string | null>(null); // Queued/Running progress text
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
        this.weights.set({
          rsi: w.rsiWeight, macd: w.macdWeight, volume: w.volumeWeight, sentiment: w.sentimentWeight,
          setupQuality: w.setupQualityWeight, relativeStrength: w.relativeStrengthWeight,
          priceLevel: w.priceLevelWeight, fundamentalMomentum: w.fundamentalMomentumWeight,
        });
        this.buyThreshold.set(w.buyThreshold);
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

  run(): void {
    if (!this.sumOk()) {
      this.snackbar.open(`Weights must sum to 1.0 (currently ${this.weightSum().toFixed(2)}).`, 'Dismiss', { duration: 4000 });
      return;
    }
    this.running.set(true);
    this.response.set(null);
    this.historicResult.set(null);
    this.historicStatus.set(null);

    const request = {
      dataSource: this.dataSource(),
      weights: this.weights(),
      buyThreshold: this.buyThreshold(),
      excludeBreakout: this.excludeBreakout(),
    };

    if (this.dataSource() === 'historic') {
      this.api.runStrategyLabHistoric(request).subscribe({
        next: (r) => this.pollHistoric(r.backtestRunId),
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

  // Historic runs execute server-side as a queued job (full engine over ~1M
  // bars takes a few minutes) - poll until it completes.
  private pollHistoric(runId: number): void {
    this.historicStatus.set('Queued — the simulation runs server-side and takes a few minutes…');
    if (this.pollTimer) clearInterval(this.pollTimer);
    this.pollTimer = setInterval(() => {
      this.api.getBacktestRun(runId).subscribe({
        next: (r) => {
          if (r.status === 'Running') this.historicStatus.set('Running — replaying 3 years of market data through your dials…');
          if (r.status === 'Completed' || r.status === 'Failed') {
            if (this.pollTimer) clearInterval(this.pollTimer);
            this.pollTimer = null;
            this.running.set(false);
            this.historicStatus.set(null);
            if (r.status === 'Completed') this.historicResult.set(r.result);
            else this.snackbar.open(`Backtest failed: ${r.error}`, 'Dismiss', { duration: 8000 });
          }
        },
        error: () => {}, // transient poll failure - keep polling
      });
    }, 5000);
  }

  // Load a suggestion's dials into the form (doesn't touch production).
  tryDials(s: LabSuggestionDto): void {
    this.weights.set({ ...s.weights });
    this.buyThreshold.set(s.buyThreshold);
    this.excludeBreakout.set(s.excludeBreakout);
    this.snackbar.open('Dials loaded — hit Run Simulation to see the full result.', 'Dismiss', { duration: 3000 });
  }

  applyToProduction(): void {
    const prod = this.productionWeights();
    if (!prod) return;
    const w = this.weights();
    this.dialog
      .open(ConfirmDialogComponent, {
        data: {
          title: 'Apply to production',
          message:
            `This sets your LIVE strategy weights and Buy threshold (${this.buyThreshold().toFixed(1)}) to the dials currently in the form. ` +
            'The next research run will score every signal with them. Are you sure?',
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
            buyThreshold: this.buyThreshold(),
          })
          .subscribe({
            next: () => this.snackbar.open('Applied — production now uses these dials. ✅', 'Dismiss', { duration: 4000 }),
            error: (err) => this.snackbar.open(errorMessage(err, 'Failed to apply.'), 'Dismiss', { duration: 5000 }),
          });
      });
  }
}
