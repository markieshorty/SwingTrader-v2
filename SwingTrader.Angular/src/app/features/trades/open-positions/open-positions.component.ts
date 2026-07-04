import { CommonModule } from '@angular/common';
import { Component, input } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { StopTargetBarComponent } from '../../../shared/components/stop-target-bar/stop-target-bar.component';
import { CurrencyGbpPipe } from '../../../shared/pipes/currency-gbp.pipe';
import { PercentSignedPipe } from '../../../shared/pipes/percent-signed.pipe';
import { PositionDto } from '../../../core/models/dtos';

@Component({
  selector: 'app-open-positions',
  standalone: true,
  imports: [CommonModule, MatCardModule, StopTargetBarComponent, CurrencyGbpPipe, PercentSignedPipe],
  template: `
    @if (positions().length === 0) {
      <p class="muted">No open positions.</p>
    }
    <div class="position-grid">
      @for (position of positions(); track position.id) {
        <mat-card class="position-card">
          <div class="position-header">
            <span class="symbol">{{ position.symbol }}</span>
            <span
              class="pnl"
              [class.positive]="position.unrealisedPnl >= 0"
              [class.negative]="position.unrealisedPnl < 0"
            >
              {{ position.unrealisedPnl | currencyGbp }} ({{ position.unrealisedPnlPercent | percentSigned }})
            </span>
          </div>
          <app-stop-target-bar [position]="position" />
          @if (position.trailingStopPrice) {
            <p class="detail-line">Trailing stop: £{{ position.trailingStopPrice | number: '1.2-2' }}</p>
          }
          <div class="detail-grid">
            <span>Entry {{ position.entryDate | date: 'mediumDate' }}</span>
            <span>{{ position.daysHeld }} days held</span>
            <span>Setup: {{ position.setupType }}</span>
            <span>Conviction: {{ position.convictionScoreAtEntry ?? 'n/a' }}</span>
            <span>Regime: {{ position.marketRegimeAtEntry ?? 'n/a' }}</span>
          </div>
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
        justify-content: space-between;
        margin-bottom: 8px;
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
    `,
  ],
})
export class OpenPositionsComponent {
  positions = input.required<PositionDto[]>();
}
