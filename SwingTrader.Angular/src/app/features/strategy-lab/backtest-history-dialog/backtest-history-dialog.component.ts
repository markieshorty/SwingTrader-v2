import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiService } from '../../../core/services/api.service';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import {
  AbResultDto,
  BacktestHistoryItemDto,
  SweepResultDto,
} from '../../../core/models/dtos';
import { errorMessage } from '../../../shared/utils/error-message.util';

// Opened from an Optimizer-History or A/B-History row. Two tabs:
//   Summary      - the run's headline stats, weights, tested rules + apply.
//   Full results - the run's stored ResultJson rendered at the same level of
//                  detail as the live results panels (head-to-head columns and
//                  per-setup/conviction/exit buckets for A/B; winner vs
//                  baseline, holdout validation verdict, buckets and the top
//                  candidates for a sweep) - without leaving the dialog.
@Component({
  selector: 'app-backtest-history-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule,
    MatProgressSpinnerModule, MatTabsModule, MatTooltipModule],
  template: `
    <h2 mat-dialog-title>
      {{ item.mode === 'ab' ? 'A/B run' : 'Optimizer run' }} — {{ item.completedAt | date: 'medium' }}
    </h2>
    <mat-dialog-content>
      <mat-tab-group>
        <mat-tab label="Summary">
          <p class="label">{{ item.label }}</p>

          @if (item.stats; as st) {
            <div class="stat-grid">
              <div><span class="k">Trades</span><span class="v">{{ st.trades }}</span></div>
              <div><span class="k">Win rate</span><span class="v">{{ st.winRatePct | percent: '1.0-1' }}</span></div>
              <div><span class="k">Total return</span><span class="v">{{ st.totalReturnPct | number: '1.1-1' }}%</span></div>
              <div><span class="k">Max drawdown</span><span class="v">{{ st.maxDrawdownPct | number: '1.1-1' }}%</span></div>
              <div><span class="k">Profit factor</span><span class="v">{{ st.profitFactor | number: '1.2-2' }}</span></div>
              <div><span class="k">Expectancy</span><span class="v">{{ st.expectancyPct | number: '1.2-2' }}%/trade</span></div>
            </div>
          }

          @if (item.weights; as w) {
            <h4>Weights <span class="thr">· Buy threshold {{ item.buyThreshold | number: '1.1-1' }}</span></h4>
            <div class="weight-grid">
              <div><span class="k">RSI</span><span class="v">{{ w.rsi | percent: '1.0-0' }}</span></div>
              <div><span class="k">MACD</span><span class="v">{{ w.macd | percent: '1.0-0' }}</span></div>
              <div><span class="k">Volume</span><span class="v">{{ w.volume | percent: '1.0-0' }}</span></div>
              <div><span class="k">Setup</span><span class="v">{{ w.setupQuality | percent: '1.0-0' }}</span></div>
              <div><span class="k">Rel. strength</span><span class="v">{{ w.relativeStrength | percent: '1.0-0' }}</span></div>
              <div><span class="k">Price level</span><span class="v">{{ w.priceLevel | percent: '1.0-0' }}</span></div>
            </div>
          }

          @if (item.rules; as r) {
            <h4>Risk settings tested</h4>
            <div class="rules">
              @if (r.maxHoldDays != null) { <span class="rule">Max hold {{ r.maxHoldDays }}d</span> }
              @if (r.minHoldDays != null) { <span class="rule">Probation {{ r.minHoldDays }}d</span> }
              @if (r.maxOpenPositions != null) { <span class="rule">Max positions {{ r.maxOpenPositions }}</span> }
              @if (r.stopLossPct != null) { <span class="rule">Stop {{ r.stopLossPct | percent: '1.0-1' }}</span> }
              @if (r.targetPct != null) { <span class="rule">Target {{ r.targetPct | percent: '1.0-1' }}</span> }
              @if (r.trailingActivationPct != null) { <span class="rule">Trail arms +{{ r.trailingActivationPct | percent: '1.0-1' }}</span> }
              @if (r.trailingDistancePct != null) { <span class="rule">Trail dist {{ r.trailingDistancePct | percent: '1.0-1' }}</span> }
              @if (r.momentumHealthThreshold != null) { <span class="rule">Health floor {{ r.momentumHealthThreshold }}</span> }
              @if (r.maxPositionPctOfActive != null) { <span class="rule">Pos size {{ r.maxPositionPctOfActive | percent: '1.0-0' }}</span> }
            </div>
          }

          @if (item.canApply) {
            <div class="apply-choices">
              @if (item.mode === 'ab' || item.hasRiskOverrides) {
                <mat-checkbox [(ngModel)]="applyWeights">Apply weights</mat-checkbox>
                <mat-checkbox [(ngModel)]="applyRisk" [disabled]="!item.hasRiskOverrides"
                  matTooltip="Writes this run's rule overrides to live: profile-level caps to Risk Management, any stop/target/guide-hold/trailing tactics and setup exclusions to the Setups tab, and a bear-autopause change to the Bear regime book.">
                  Apply risk settings{{ item.hasRiskOverrides ? '' : ' (none in this run)' }}
                </mat-checkbox>
              } @else {
                <p class="muted">This run tuned weights only — applying replaces your live strategy weights.</p>
              }
            </div>
          } @else {
            <p class="muted">This run has no applyable configuration.</p>
          }
        </mat-tab>

        <mat-tab label="Full results">
          @if (fullLoading()) {
            <div class="full-loading"><mat-spinner diameter="28"></mat-spinner></div>
          } @else if (fullAb(); as ab) {
            <div class="ab-grid">
              @for (c of ab.candidates; track c.label) {
                <div class="ab-column">
                  <h4>{{ c.label }}</h4>
                  <div class="ab-stat"><span>Trades placed</span><span class="num">{{ c.result.trades }}</span></div>
                  <div class="ab-stat"><span>Expectancy / trade</span>
                    <span class="num" [class.good]="c.result.expectancyPct > 0" [class.bad]="c.result.expectancyPct < 0">{{ c.result.expectancyPct | number: '1.2-2' }}%</span></div>
                  <div class="ab-stat"><span>Win rate</span><span class="num">{{ c.result.winRate | percent: '1.0-1' }}</span></div>
                  <div class="ab-stat"><span>Profit factor</span>
                    <span class="num" [class.good]="c.result.profitFactor > 1" [class.bad]="c.result.profitFactor < 1">{{ c.result.profitFactor | number: '1.2-2' }}</span></div>
                  <div class="ab-stat"><span>Total return</span>
                    <span class="num" [class.good]="c.result.totalReturnPct > 0" [class.bad]="c.result.totalReturnPct < 0">{{ c.result.totalReturnPct | number: '1.1-1' }}%</span></div>
                  <div class="ab-stat"><span>Max drawdown</span><span class="num">{{ c.result.maxDrawdownPct | number: '1.1-1' }}%</span></div>
                  <div class="ab-stat"><span>SPY buy &amp; hold</span><span class="num">{{ c.result.spyReturnPct | number: '1.1-1' }}%</span></div>
                </div>
              }
            </div>
            @for (c of ab.candidates; track c.label) {
              <h4 class="bucket-title">{{ c.label }} — by setup / conviction / exit</h4>
              <ng-container *ngTemplateOutlet="buckets; context: { $implicit: c.result }"></ng-container>
            }
          } @else if (fullSweep(); as sw) {
            <div class="ab-grid">
              <div class="ab-column">
                <h4>Production baseline <span class="muted small">(train window)</span></h4>
                <ng-container *ngTemplateOutlet="sweepStats; context: { $implicit: sw.baseline }"></ng-container>
              </div>
              <div class="ab-column">
                <h4>Winner — {{ sw.winner.label }} <span class="muted small">(train window)</span></h4>
                <ng-container *ngTemplateOutlet="sweepStats; context: { $implicit: sw.winner }"></ng-container>
              </div>
            </div>

            <h4 class="bucket-title" [class.good]="sw.validation.heldUp" [class.bad]="!sw.validation.heldUp">
              {{ sw.validation.heldUp ? '✓ Held up out-of-sample' : '⚠️ Did not hold up out-of-sample' }}
            </h4>
            <p class="muted small">{{ sw.validation.verdict }}</p>
            <div class="stat-grid">
              <div><span class="k">Winner, holdout</span><span class="v">{{ sw.validation.holdoutAdjustedExpectancyPct | number: '1.2-2' }}%/trade</span></div>
              <div><span class="k">Baseline, holdout</span><span class="v">{{ sw.validation.baselineHoldoutAdjustedExpectancyPct | number: '1.2-2' }}%/trade</span></div>
              <div><span class="k">Winner, train</span><span class="v">{{ sw.validation.trainAdjustedExpectancyPct | number: '1.2-2' }}%/trade</span></div>
            </div>

            <h4 class="bucket-title">Winner on the held-out window — by setup / conviction / exit</h4>
            <ng-container *ngTemplateOutlet="buckets; context: { $implicit: sw.validation.holdout }"></ng-container>

            @if (sw.explanation) {
              <h4 class="bucket-title">Analysis</h4>
              <p class="muted small explanation">{{ sw.explanation }}</p>
            }

            <h4 class="bucket-title">Top candidates (by robust score)</h4>
            <div class="cand-table">
              <div class="cand-row head muted small">
                <span>Candidate</span><span>trades</span><span>win%</span><span>robust</span><span>adj. expectancy</span>
              </div>
              @for (c of topCandidates(); track c.label) {
                <div class="cand-row" [class.winner-row]="c.label === sw.winner.label">
                  <span>{{ c.label }}</span>
                  <span class="num">{{ c.trades }}</span>
                  <span class="num">{{ c.winRate | percent: '1.0-0' }}</span>
                  <span class="num">{{ c.robustScorePct | number: '1.2-2' }}%</span>
                  <span class="num">{{ c.adjustedExpectancyPct | number: '1.2-2' }}%</span>
                </div>
              }
            </div>
          } @else {
            <p class="muted">This run has no stored result to show.</p>
          }
        </mat-tab>
      </mat-tab-group>

      <ng-template #sweepStats let-c>
        <div class="ab-stat"><span>Trades placed</span><span class="num">{{ c.trades }}</span></div>
        <div class="ab-stat"><span matTooltip="Market-adjusted: each trade's return minus SPY's move over the same days">Adj. expectancy</span>
          <span class="num" [class.good]="c.adjustedExpectancyPct > 0" [class.bad]="c.adjustedExpectancyPct < 0">{{ c.adjustedExpectancyPct | number: '1.2-2' }}%</span></div>
        <div class="ab-stat"><span matTooltip="Worse train-window half, discounted — the score candidates were ranked on">Robust score</span>
          <span class="num">{{ c.robustScorePct | number: '1.2-2' }}%</span></div>
        <div class="ab-stat"><span>Win rate</span><span class="num">{{ c.winRate | percent: '1.0-1' }}</span></div>
        <div class="ab-stat"><span>Profit factor</span>
          <span class="num" [class.good]="c.profitFactor > 1" [class.bad]="c.profitFactor < 1">{{ c.profitFactor | number: '1.2-2' }}</span></div>
        <div class="ab-stat"><span>Total return</span>
          <span class="num" [class.good]="c.totalReturnPct > 0" [class.bad]="c.totalReturnPct < 0">{{ c.totalReturnPct | number: '1.1-1' }}%</span></div>
        <div class="ab-stat"><span>Max drawdown</span><span class="num">{{ c.maxDrawdownPct | number: '1.1-1' }}%</span></div>
      </ng-template>

      <ng-template #buckets let-h>
        <div class="bucket-tables">
          @for (bucket of [
            { title: 'By setup', rows: h.bySetup },
            { title: 'By conviction', rows: h.byConviction },
            { title: 'By exit', rows: h.byExitReason },
          ]; track bucket.title) {
            <div class="bucket">
              <h5>{{ bucket.title }}</h5>
              <div class="bucket-row bucket-head muted small">
                <span></span><span>trades</span><span>win%</span><span>hold</span><span>expectancy</span>
              </div>
              @for (row of bucket.rows; track row.key) {
                <div class="bucket-row">
                  <span>{{ row.key }}</span>
                  <span class="num muted small">{{ row.count }}</span>
                  <span class="num muted small">{{ row.winRate | percent: '1.0-0' }}</span>
                  <span class="num muted small">{{ row.avgHoldDays | number: '1.0-1' }}d</span>
                  <span class="num" [class.good]="row.avgReturnPct > 0" [class.bad]="row.avgReturnPct < 0">{{ row.avgReturnPct | number: '1.2-2' }}%</span>
                </div>
              }
            </div>
          }
        </div>
      </ng-template>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Close</button>
      <button mat-raised-button color="primary"
        [disabled]="!item.canApply || applying() || (checkboxesShown && !applyWeights() && !applyRisk())"
        (click)="apply()">
        Apply to live
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .label { font-weight: 600; margin: 12px 0 8px; }
    .thr { font-weight: 400; color: var(--st-muted); font-size: 13px; }
    .stat-grid, .weight-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 6px 20px; margin: 8px 0; }
    .weight-grid { grid-template-columns: repeat(4, 1fr); }
    .stat-grid .k, .weight-grid .k { color: var(--st-muted); font-size: 12px; margin-right: 6px; }
    .stat-grid .v, .weight-grid .v { font-weight: 600; }
    .rules { display: flex; flex-wrap: wrap; gap: 6px; }
    .rule { font-size: 12px; border-radius: 10px; padding: 2px 8px; background: rgba(128,128,128,0.15); }
    .apply-choices { display: flex; flex-direction: column; gap: 4px; margin-top: 12px; }
    .muted { color: var(--st-muted); font-size: 13px; }
    .small { font-size: 12px; }
    .full-loading { display: flex; justify-content: center; padding: 32px; }
    .ab-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; margin: 12px 0; }
    .ab-column { border: 1px solid var(--st-border, rgba(128,128,128,0.25)); border-radius: 8px; padding: 10px 14px;
      h4 { margin: 0 0 8px; font-size: 14px; } }
    .ab-stat { display: flex; justify-content: space-between; font-size: 13px; padding: 2px 0;
      .num { font-weight: 600; } }
    .good { color: var(--st-green, #2e9b57); }
    .bad { color: var(--st-amber); }
    .bucket-title { margin: 16px 0 6px; font-size: 13px; }
    .bucket-tables { display: grid; grid-template-columns: repeat(3, 1fr); gap: 12px; }
    .bucket h5 { margin: 0 0 4px; font-size: 12px; color: var(--st-muted); text-transform: uppercase; letter-spacing: 0.04em; }
    .bucket-row { display: grid; grid-template-columns: 1.4fr 0.6fr 0.6fr 0.6fr 1fr; gap: 4px; font-size: 12px; padding: 2px 0;
      .num { text-align: right; } }
    .bucket-head span { color: var(--st-muted); }
    .cand-table { font-size: 12px; }
    .cand-row { display: grid; grid-template-columns: 2fr 0.6fr 0.6fr 1fr 1fr; gap: 6px; padding: 3px 0; border-top: 1px solid rgba(128,128,128,0.12);
      .num { text-align: right; }
      &.head { border-top: none; }
      &.winner-row { color: var(--st-green, #2e9b57); font-weight: 600; } }
    .explanation { white-space: pre-line; }
  `],
})
export class BacktestHistoryDialogComponent {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snackbar = inject(MatSnackBar);
  ref = inject(MatDialogRef<BacktestHistoryDialogComponent, boolean>);

  item = inject<BacktestHistoryItemDto>(MAT_DIALOG_DATA);
  applying = signal(false);
  // Checkboxes appear whenever a choice exists: always for A/B, and for a
  // sweep whose winner carried rule overrides ("search for optimal trading
  // rules"). A weights-only sweep just applies weights.
  applyWeights = signal(true);
  applyRisk = signal(false);

  // Full-results tab data: the run's stored ResultJson, fetched once on open.
  fullLoading = signal(true);
  fullAb = signal<AbResultDto | null>(null);
  fullSweep = signal<SweepResultDto | null>(null);

  topCandidates = computed(() => {
    const sw = this.fullSweep();
    if (!sw) return [];
    return [...sw.candidates]
      .filter((c) => c.metConstraints)
      .sort((a, b) => b.robustScorePct - a.robustScorePct)
      .slice(0, 10);
  });

  constructor() {
    this.api.getBacktestRun(this.item.id).subscribe({
      next: (r) => {
        if (r.result && 'mode' in r.result) {
          if (this.item.mode === 'ab' && r.result.mode === 'ab') this.fullAb.set(r.result);
          else if (this.item.mode === 'sweep' && r.result.mode === 'sweep') this.fullSweep.set(r.result);
        }
        this.fullLoading.set(false);
      },
      error: () => this.fullLoading.set(false),
    });
  }

  get checkboxesShown(): boolean {
    return this.item.mode === 'ab' || this.item.hasRiskOverrides;
  }

  private effectiveWeights = computed(() => (this.checkboxesShown ? this.applyWeights() : true));
  private effectiveRisk = computed(() => (this.checkboxesShown ? this.applyRisk() : false));

  apply(): void {
    const parts: string[] = [];
    if (this.effectiveWeights()) parts.push('strategy weights');
    if (this.effectiveRisk()) parts.push('risk-management settings');

    const confirm = this.dialog.open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
      width: '460px',
      data: {
        title: 'Apply to live settings?',
        message:
          `This replaces your live ${parts.join(' and ')} with the values from this ` +
          `${this.item.mode === 'ab' ? 'A/B' : 'optimizer'} run. It takes effect on the next research run.`,
        cancelLabel: 'Cancel',
        confirmLabel: 'Apply to live',
        confirmColor: 'warn',
      },
    });

    confirm.afterClosed().subscribe((confirmed) => {
      if (!confirmed) return;
      this.applying.set(true);
      this.api.applyBacktestRun(this.item.id, this.effectiveWeights(), this.effectiveRisk()).subscribe({
        next: (r) => {
          const done = [r.appliedWeights ? 'weights' : null, r.appliedRisk ? 'risk settings' : null].filter(Boolean).join(' + ');
          this.snackbar.open(`Applied ${done} to live settings`, 'Dismiss', { duration: 4500 });
          this.ref.close(true);
        },
        error: (err) => {
          this.applying.set(false);
          this.snackbar.open(errorMessage(err, 'Failed to apply.'), 'Dismiss', { duration: 4500 });
        },
      });
    });
  }
}
