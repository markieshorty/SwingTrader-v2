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
            <span class="symbol" [title]="position.companyName || position.symbol">{{ position.symbol }}</span>
            <span
              class="pnl"
              [class.positive]="position.unrealisedPnl >= 0"
              [class.negative]="position.unrealisedPnl < 0"
            >
              {{ position.unrealisedPnl | currencyGbp }} ({{ position.unrealisedPnlPercent | percentSigned }})
            </span>
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

  heldLabel(position: PositionDto): string {
    if (position.daysHeld >= 1) return `${position.daysHeld} ${position.daysHeld === 1 ? 'day' : 'days'} held`;
    const hours = Math.floor((Date.now() - new Date(position.entryDate).getTime()) / 3_600_000);
    return `${hours} hour${hours === 1 ? '' : 's'} held`;
  }

  contractLabel(position: PositionDto): string | null {
    const parts: string[] = [];
    if (position.minHoldDaysAtEntry !== null) parts.push(`probation ${position.minHoldDaysAtEntry}d`);
    if (position.maxHoldDaysAtEntry !== null) parts.push(`max hold ${position.maxHoldDaysAtEntry}d`);
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
