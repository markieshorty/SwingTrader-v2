import { CommonModule } from '@angular/common';
import { Component, computed, input, signal } from '@angular/core';
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
        @if (signal().gateScore !== null) {
          <div class="scores funnel"
            title="Funnel shadow (Phase F1): what the two-stage design WOULD decide - gate (technical entry) and forward (sizing) scores. Not driving recommendations yet.">
            <span>Gate {{ signal().gateScore?.toFixed(1) }}{{ signal().wouldPassGate ? ' ✓' : '' }}</span>
            <span>Forward {{ signal().forwardScore?.toFixed(1) }}{{ signal().forwardScoreDegraded ? ' (degraded)' : '' }}</span>
            @if (sizeMultiplier() !== null) {
              <span class="size-mult" [class.up]="sizeMultiplier()! > 1.005" [class.down]="sizeMultiplier()! < 0.995"
                title="Prospective F2 size multiplier from the forward score and the active book's aggressiveness — execution recomputes this at buy time, then applies the cash/slot clamps.">
                Size {{ sizeMultiplier()!.toFixed(2) }}×
              </span>
            }
            @if (signal().wouldBeVetoed) { <span class="veto">would veto</span> }
          </div>
        }
      </div>
    }
  `,
  styles: [
    `
      .signal-row {
        display: grid;
        grid-template-columns: 80px 1fr 90px 160px 90px 24px;
      }

      .size-mult {
        font-variant-numeric: tabular-nums;
        border-radius: 10px;
        padding: 0 6px;
        background: rgba(127, 127, 127, 0.12);
        &.up { color: #2e7d32; }
        &.down { color: #c62828; }
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
      .scores.funnel {
        border-top: 1px dashed var(--st-border);
        padding-top: 6px;
        font-style: italic;
      }
      .veto {
        color: var(--st-red);
      }
    `,
  ],
})
export class SignalCardComponent {
  signal = input.required<SignalDto>();

  // The active book's F2 dials (today's board only; null on historic rows).
  sizing = input<{ funnelMode: boolean; aggressiveness: number; maxTilt: number } | null>(null);

  // Mirror of PositionSizingService.ComputeForwardMultiplier - a preview of
  // what execution would apply, before the cash/slot clamps.
  sizeMultiplier = computed<number | null>(() => {
    const dials = this.sizing();
    const s = this.signal();
    if (!dials?.funnelMode || dials.aggressiveness <= 0) return null;
    if (s.forwardScore === null || s.forwardScoreDegraded) return null;
    const tilt = Math.max(-1, Math.min(1, (s.forwardScore - 5) / 5));
    return 1 + dials.aggressiveness * dials.maxTilt * tilt;
  });
  expanded = signal(false);
}
