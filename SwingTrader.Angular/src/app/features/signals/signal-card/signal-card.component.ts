import { CommonModule } from '@angular/common';
import { Component, input, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { SignalDto } from '../../../core/models/dtos';
import { ConvictionBarComponent } from '../../../shared/components/conviction-bar/conviction-bar.component';

@Component({
  selector: 'app-signal-card',
  standalone: true,
  imports: [CommonModule, MatIconModule, ConvictionBarComponent],
  template: `
    <div class="signal-row" (click)="expanded.set(!expanded())">
      <span class="symbol">{{ signal().symbol }}</span>
      <span class="company">{{ signal().companyName }}</span>
      <span class="date">{{ signal().signalDate | date: 'dd/MM/yyyy' }}</span>
      <app-conviction-bar [signal]="signal()" />
      <span class="badge" [class]="signal().recommendation.toLowerCase()">{{
        signal().recommendation
      }}</span>
      <mat-icon>{{ expanded() ? 'expand_less' : 'expand_more' }}</mat-icon>
    </div>
    @if (expanded()) {
      <div class="detail">
        <p>{{ signal().fundamentalNarrative ?? 'No fundamental narrative available.' }}</p>
        <div class="scores">
          <span>RSI {{ signal().rsiScore?.toFixed(2) ?? 'n/a' }}</span>
          <span>MACD {{ signal().macdScore?.toFixed(2) ?? 'n/a' }}</span>
          <span>Volume {{ signal().volumeScore?.toFixed(2) ?? 'n/a' }}</span>
          <span>Sentiment {{ signal().sentimentComponentScore?.toFixed(2) ?? 'n/a' }}</span>
          <span>Setup {{ signal().setupQualityScore?.toFixed(2) ?? 'n/a' }}</span>
          <span>RelStrength {{ signal().relativeStrengthScore?.toFixed(2) ?? 'n/a' }}</span>
          <span>PriceLevel {{ signal().priceLevelScore?.toFixed(2) ?? 'n/a' }}</span>
          <span>Fundamental {{ signal().fundamentalMomentumScore?.toFixed(2) ?? 'n/a' }}</span>
        </div>
      </div>
    }
  `,
  styles: [
    `
      .signal-row {
        display: grid;
        grid-template-columns: 80px 1fr 90px 160px 90px 24px;
        align-items: center;
        gap: 12px;
        padding: 10px 12px;
        border-bottom: 1px solid var(--st-border);
        cursor: pointer;
      }
      .symbol {
        font-weight: 600;
      }
      .company {
        color: var(--st-muted);
        font-size: 13px;
      }
      .date {
        color: var(--st-muted);
        font-size: 12px;
        font-variant-numeric: tabular-nums;
      }
      .badge {
        font-size: 11px;
        padding: 2px 8px;
        border-radius: 999px;
        text-align: center;
      }
      .badge.buy {
        background: #14532d;
        color: var(--st-green);
      }
      .badge.watch {
        background: #713f12;
        color: var(--st-amber);
      }
      .badge.hold {
        background: #1e3a8a;
        color: var(--st-blue);
      }
      .badge.avoid,
      .badge.sell {
        background: #7f1d1d;
        color: var(--st-red);
      }
      .detail {
        padding: 12px;
        background: var(--st-card);
        border-bottom: 1px solid var(--st-border);
        font-size: 12px;
      }
      .scores {
        display: flex;
        flex-wrap: wrap;
        gap: 12px;
        color: var(--st-muted);
        margin-top: 8px;
      }
    `,
  ],
})
export class SignalCardComponent {
  signal = input.required<SignalDto>();
  expanded = signal(false);
}
