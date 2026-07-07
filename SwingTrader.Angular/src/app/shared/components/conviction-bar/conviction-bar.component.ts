import { CommonModule } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { MatTooltipModule } from '@angular/material/tooltip';
import { SignalDto } from '../../../core/models/dtos';

@Component({
  selector: 'app-conviction-bar',
  standalone: true,
  imports: [CommonModule, MatTooltipModule],
  template: `
    <div class="conviction" [matTooltip]="tooltipText()">
      <span class="score">{{ signal().convictionScore | number: '1.1-1' }}</span>
      <div class="bar">
        <div
          class="fill"
          [style.width.%]="(signal().convictionScore ?? 0) * 10"
          [class]="fillClass()"
        ></div>
      </div>
    </div>
  `,
  styles: [
    `
      .conviction {
        display: flex;
        align-items: center;
        gap: 8px;
      }
      .score {
        font-weight: 600;
        min-width: 28px;
      }
      .bar {
        flex: 1;
        height: 6px;
        border-radius: 3px;
        background: var(--st-border);
        overflow: hidden;
      }
      .fill {
        height: 100%;
        border-radius: 3px;
      }
      .fill.high {
        background: var(--st-green);
      }
      .fill.medium {
        background: var(--st-amber);
      }
      .fill.low {
        background: var(--st-red);
      }
    `,
  ],
})
export class ConvictionBarComponent {
  signal = input.required<SignalDto>();

  fillClass = computed(() => {
    switch (this.signal().recommendation) {
      case 'Buy': return 'high';
      case 'Watch': return 'medium';
      default: return 'low';
    }
  });

  tooltipText = computed(() => {
    const s = this.signal();
    return [
      `RSI: ${s.rsiScore?.toFixed(2) ?? 'n/a'}`,
      `MACD: ${s.macdScore?.toFixed(2) ?? 'n/a'}`,
      `Volume: ${s.volumeScore?.toFixed(2) ?? 'n/a'}`,
      `Sentiment: ${s.sentimentComponentScore?.toFixed(2) ?? 'n/a'}`,
      `Setup: ${s.setupQualityScore?.toFixed(2) ?? 'n/a'}`,
      `RelStrength: ${s.relativeStrengthScore?.toFixed(2) ?? 'n/a'}`,
      `PriceLevel: ${s.priceLevelScore?.toFixed(2) ?? 'n/a'}`,
      `Fundamental: ${s.fundamentalMomentumScore?.toFixed(2) ?? 'n/a'}`,
    ].join('\n');
  });
}
