import { Component, computed, input } from '@angular/core';
import { RegimeDto } from '../../../core/models/dtos';

@Component({
  selector: 'app-regime-badge',
  standalone: true,
  template: `
    @if (regime()) {
      <span class="regime-badge" [class]="regimeClass()">
        {{ regime()!.regime }}
        @if (regime()!.regime === 'Crisis') {
          <span class="pulse-dot"></span>
        }
      </span>
    }
  `,
  styles: [
    `
      .regime-badge {
        padding: 4px 10px;
        border-radius: 999px;
        font-size: 12px;
        font-weight: 500;
        letter-spacing: 0.05em;
      }
      .bull {
        background: #14532d;
        color: #22c55e;
      }
      .neutral {
        background: #713f12;
        color: #f59e0b;
      }
      .bear {
        background: #7f1d1d;
        color: #ef4444;
      }
      .crisis {
        background: #7f1d1d;
        color: #ef4444;
        animation: pulse 1s infinite;
      }
      @keyframes pulse {
        0%,
        100% {
          opacity: 1;
        }
        50% {
          opacity: 0.5;
        }
      }
    `,
  ],
})
export class RegimeBadgeComponent {
  regime = input<RegimeDto | null>(null);
  regimeClass = computed(() => this.regime()?.regime.toLowerCase() ?? '');
}
