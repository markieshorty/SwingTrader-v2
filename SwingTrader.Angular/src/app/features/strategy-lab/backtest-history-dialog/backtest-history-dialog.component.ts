import { CommonModule } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../../core/services/api.service';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { BacktestHistoryItemDto } from '../../../core/models/dtos';
import { errorMessage } from '../../../shared/utils/error-message.util';

// Opened from an Optimizer-History or A/B-History row: shows the run's stats,
// weights, and (for A/B) the risk-rule overrides it tested, with an apply
// action. A/B offers the two-checkbox weights/risk choice; the optimizer only
// tunes weights, so it applies weights only. Applying always confirms first.
@Component({
  selector: 'app-backtest-history-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule],
  template: `
    <h2 mat-dialog-title>
      {{ item.mode === 'ab' ? 'A/B run' : 'Optimizer run' }} — {{ item.completedAt | date: 'medium' }}
    </h2>
    <mat-dialog-content>
      <p class="label">{{ item.label }}</p>

      @if (item.stats; as st) {
        <div class="stat-grid">
          <div><span class="k">Trades</span><span class="v">{{ st.trades }}</span></div>
          <div><span class="k">Win rate</span><span class="v">{{ st.winRatePct | number: '1.0-1' }}%</span></div>
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
              matTooltip="Writes this run's rule overrides to live: profile-level caps to Risk Management, and any stop/target/guide-hold/trailing tactics to the matching setups on the Setups tab.">
              Apply risk settings{{ item.hasRiskOverrides ? '' : ' (none in this run)' }}
            </mat-checkbox>
          } @else {
            <p class="muted">This run tuned weights only — applying replaces your live strategy weights.</p>
          }
        </div>
      } @else {
        <p class="muted">This run has no applyable configuration.</p>
      }
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
    .label { font-weight: 600; margin: 0 0 8px; }
    .thr { font-weight: 400; color: var(--st-muted); font-size: 13px; }
    .stat-grid, .weight-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 6px 20px; margin: 8px 0; }
    .weight-grid { grid-template-columns: repeat(4, 1fr); }
    .stat-grid .k, .weight-grid .k { color: var(--st-muted); font-size: 12px; margin-right: 6px; }
    .stat-grid .v, .weight-grid .v { font-weight: 600; }
    .rules { display: flex; flex-wrap: wrap; gap: 6px; }
    .rule { font-size: 12px; border-radius: 10px; padding: 2px 8px; background: rgba(128,128,128,0.15); }
    .apply-choices { display: flex; flex-direction: column; gap: 4px; margin-top: 12px; }
    .muted { color: var(--st-muted); font-size: 13px; }
  `],
})
export class BacktestHistoryDialogComponent {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snackbar = inject(MatSnackBar);
  private ref = inject(MatDialogRef<BacktestHistoryDialogComponent, boolean>);

  item = inject<BacktestHistoryItemDto>(MAT_DIALOG_DATA);
  applying = signal(false);
  // Checkboxes appear whenever a choice exists: always for A/B, and for a
  // sweep whose winner carried rule overrides ("search for optimal trading
  // rules"). A weights-only sweep just applies weights.
  applyWeights = signal(true);
  applyRisk = signal(false);

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
