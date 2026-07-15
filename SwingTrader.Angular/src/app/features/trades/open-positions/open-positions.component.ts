import { CommonModule } from '@angular/common';
import { Component, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { StopTargetBarComponent } from '../../../shared/components/stop-target-bar/stop-target-bar.component';
import { CurrencyGbpPipe } from '../../../shared/pipes/currency-gbp.pipe';
import { PercentSignedPipe } from '../../../shared/pipes/percent-signed.pipe';
import { PositionDto } from '../../../core/models/dtos';
import { ApiService } from '../../../core/services/api.service';
import { DashboardDataService } from '../../../core/services/dashboard-data.service';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { errorMessage } from '../../../shared/utils/error-message.util';

@Component({
  selector: 'app-open-positions',
  standalone: true,
  imports: [CommonModule, MatButtonModule, MatCardModule, MatIconModule, MatTooltipModule, StopTargetBarComponent, CurrencyGbpPipe, PercentSignedPipe],
  template: `
    @if (positions().length === 0) {
      <p class="muted">No open positions.</p>
    }
    <div class="position-grid">
      @for (position of positions(); track position.id) {
        <mat-card class="position-card">
          <div class="position-header">
            <span class="symbol" [title]="position.companyName || position.symbol">{{ position.symbol }}</span>
            <span
              class="pnl"
              [class.positive]="position.unrealisedPnl >= 0"
              [class.negative]="position.unrealisedPnl < 0"
            >
              {{ position.unrealisedPnl | currencyGbp }} ({{ position.unrealisedPnlPercent | percentSigned }})
            </span>
            <button mat-icon-button class="close-early" [disabled]="closing() === position.id"
              (click)="closeEarly(position)"
              matTooltip="Close early — places a market sell in Trading212 now">
              <mat-icon>sell</mat-icon>
            </button>
          </div>
          <span class="phase-badge" [class]="'phase-' + position.phase.toLowerCase()">{{ phaseLabel(position) }}</span>
          <app-stop-target-bar [position]="position" />
          @if (position.momentumHealthVerdict) {
            <p class="detail-line momentum-line">
              Momentum: {{ position.momentumHealthScore | number: '1.2-2' }} — {{ position.momentumHealthReasoning }}
            </p>
          }
          <div class="detail-grid">
            <span>Entry {{ position.entryDate | date: 'mediumDate' }}</span>
            <span>{{ heldLabel(position) }}</span>
            <span>Setup: {{ position.setupType }}</span>
            <span>Conviction: {{ position.convictionScoreAtEntry ?? 'n/a' }}</span>
            <span>Regime: {{ position.marketRegimeAtEntry ?? 'n/a' }}</span>
          </div>
          <!-- The contract: rules frozen at buy time (thesis-as-contract).
               Null fields = trade pre-dates the freeze; those run under the
               live profile, so show nothing rather than guess. -->
          @if (contractLabel(position); as contract) {
            <p class="detail-line contract-line" title="Rules frozen at entry — later settings changes don't affect this position">
              {{ contract }}
            </p>
          }
          @if (position.forwardScoreAtEntry !== null || position.sizeMultiplier !== null) {
            <p class="detail-line">
              @if (position.forwardScoreAtEntry !== null) {
                Forward score at entry: {{ position.forwardScoreAtEntry | number: '1.2-2' }}
              }
              @if (position.sizeMultiplier !== null) {
                · Size ×{{ position.sizeMultiplier | number: '1.2-2' }}
              }
            </p>
          }
        </mat-card>
      }
    </div>
  `,
  styles: [
    `
      .position-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
        gap: 12px;
      }
      .position-card {
        padding: 12px;
      }
      .position-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 4px;
        margin-bottom: 8px;
      }
      .mark-closed {
        width: 32px;
        height: 32px;
        line-height: 32px;
        opacity: 0.55;
      }
      .mark-closed:hover {
        opacity: 1;
      }
      .symbol {
        font-weight: 600;
      }
      .pnl.positive {
        color: var(--st-green);
      }
      .pnl.negative {
        color: var(--st-red);
      }
      .detail-line {
        font-size: 12px;
        color: var(--st-muted);
        margin: 4px 0;
      }
      .detail-grid {
        display: flex;
        flex-direction: column;
        gap: 2px;
        font-size: 12px;
        color: var(--st-muted);
        margin-top: 8px;
      }
      .muted {
        color: var(--st-muted);
      }
      .phase-badge {
        display: inline-block;
        font-size: 11px;
        font-weight: 600;
        padding: 2px 8px;
        border-radius: 10px;
        margin-bottom: 6px;
      }
      .phase-probation {
        background: color-mix(in srgb, var(--st-amber) 20%, transparent);
        color: var(--st-amber);
      }
      .phase-confirmed {
        background: color-mix(in srgb, var(--st-green) 20%, transparent);
        color: var(--st-green);
      }
      .phase-exiting {
        background: color-mix(in srgb, var(--st-red) 20%, transparent);
        color: var(--st-red);
      }
      .momentum-line {
        font-style: italic;
      }
      .contract-line {
        margin-top: 6px;
      }
    `,
  ],
})
export class OpenPositionsComponent {
  positions = input.required<PositionDto[]>();

  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snackbar = inject(MatSnackBar);
  private data = inject(DashboardDataService);

  closing = signal<number | null>(null);

  // "Close early" - confirm, then place a REAL market sell in Trading212
  // via the same exit path the monitor's rule-driven exits use. The P&L
  // shown is an estimate; the monitor reconciles the real fill afterwards.
  closeEarly(position: PositionDto): void {
    const ref = this.dialog.open<ConfirmDialogComponent, ConfirmDialogData, boolean>(ConfirmDialogComponent, {
      width: '440px',
      data: {
        title: `Close ${position.symbol} early?`,
        message:
          `This places a REAL market sell order in Trading212 for ` +
          `${position.quantity} share(s) of ${position.symbol} right now.\n\n` +
          `Estimated P&L at the current price: £${position.unrealisedPnl.toFixed(2)} ` +
          `(${position.unrealisedPnlPercent.toFixed(1)}%). The exact figure depends on the fill.`,
        cancelLabel: 'Cancel',
        confirmLabel: 'Sell at market',
        confirmColor: 'warn',
      },
    });

    ref.afterClosed().subscribe((confirmed) => {
      if (!confirmed) return;
      this.closing.set(position.id);
      this.api.closePositionEarly(position.id).subscribe({
        next: (r) => {
          this.closing.set(null);
          this.snackbar.open(
            `${r.symbol} sold at market — est. P&L £${(r.realizedPnl ?? 0).toFixed(2)} (final figure after fill reconciliation)`,
            'Dismiss', { duration: 5000 });
          this.data.refresh();
        },
        error: (err) => {
          this.closing.set(null);
          this.snackbar.open(errorMessage(err, 'Sell order failed — the position is unchanged.'), 'Dismiss', { duration: 5000 });
        },
      });
    });
  }

  heldLabel(position: PositionDto): string {
    if (position.daysHeld >= 1) return `${position.daysHeld} ${position.daysHeld === 1 ? 'day' : 'days'} held`;
    const hours = Math.floor((Date.now() - new Date(position.entryDate).getTime()) / 3_600_000);
    return `${hours} hour${hours === 1 ? '' : 's'} held`;
  }

  contractLabel(position: PositionDto): string | null {
    const parts: string[] = [];
    if (position.minHoldDaysAtEntry !== null) parts.push(`probation ${position.minHoldDaysAtEntry}d`);
    if (position.maxHoldDaysAtEntry !== null) parts.push(`guide hold ${position.maxHoldDaysAtEntry}d`);
    if (position.momentumHealthThresholdAtEntry !== null) parts.push(`health floor ${position.momentumHealthThresholdAtEntry}`);
    if (position.trailingActivationPctAtEntry !== null && position.trailingDistancePctAtEntry !== null)
      parts.push(`trail ${position.trailingDistancePctAtEntry}% after +${position.trailingActivationPctAtEntry}%`);
    return parts.length > 0 ? `Contract: ${parts.join(' · ')}` : null;
  }

  phaseLabel(position: PositionDto): string {
    switch (position.phase) {
      case 'Confirmed':
        return `Day ${position.daysHeld} · Confirmed`;
      case 'Exiting':
        return `Day ${position.daysHeld} · Momentum exit in progress`;
      default:
        return `Day ${position.daysHeld} · Probation`;
    }
  }
}
