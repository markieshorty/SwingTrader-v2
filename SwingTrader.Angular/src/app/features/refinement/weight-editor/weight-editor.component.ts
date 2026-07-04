import { CommonModule } from '@angular/common';
import { Component, computed, input } from '@angular/core';

@Component({
  selector: 'app-weight-editor',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="weights">
      @for (row of rows(); track row.name) {
        <div class="weight-row">
          <span class="label">{{ row.name }}</span>
          <div class="track">
            <div class="fill current" [style.width.%]="row.current * 100"></div>
            @if (row.suggested !== null) {
              <div
                class="marker"
                [class.up]="row.suggested > row.current"
                [class.down]="row.suggested < row.current"
                [style.left.%]="row.suggested * 100"
              ></div>
            }
          </div>
          <span class="value">{{ (row.current * 100) | number: '1.0-1' }}%</span>
          @if (row.suggested !== null) {
            <span class="suggested" [class.up]="row.suggested > row.current" [class.down]="row.suggested < row.current">
              → {{ (row.suggested * 100) | number: '1.0-1' }}%
            </span>
          }
        </div>
      }
    </div>
  `,
  styles: [
    `
      .weight-row {
        display: grid;
        grid-template-columns: 140px 1fr 60px 80px;
        align-items: center;
        gap: 8px;
        margin-bottom: 8px;
        font-size: 13px;
      }
      .label {
        color: var(--st-muted);
      }
      .track {
        position: relative;
        height: 10px;
        background: var(--st-border);
        border-radius: 5px;
      }
      .fill.current {
        position: absolute;
        left: 0;
        top: 0;
        bottom: 0;
        background: var(--st-blue);
        border-radius: 5px;
      }
      .marker {
        position: absolute;
        top: -3px;
        width: 3px;
        height: 16px;
        background: var(--st-text);
        transform: translateX(-50%);
      }
      .marker.up {
        background: var(--st-green);
      }
      .marker.down {
        background: var(--st-red);
      }
      .value {
        text-align: right;
      }
      .suggested.up {
        color: var(--st-green);
      }
      .suggested.down {
        color: var(--st-red);
      }
    `,
  ],
})
export class WeightEditorComponent {
  currentWeights = input.required<Record<string, number>>();
  suggestedWeights = input<Record<string, number> | null>(null);

  rows = computed(() => {
    const current = this.currentWeights();
    const suggested = this.suggestedWeights();
    return Object.entries(current).map(([name, value]) => ({
      name,
      current: value,
      suggested: suggested ? (suggested[name] ?? null) : null,
    }));
  });
}
