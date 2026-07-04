import { Component, computed, input } from '@angular/core';

@Component({
  selector: 'app-sparkline',
  standalone: true,
  template: `
    <svg [attr.viewBox]="viewBox()" preserveAspectRatio="none" class="sparkline">
      <polyline [attr.points]="points()" [class.positive]="isPositive()" [class.negative]="!isPositive()" />
    </svg>
  `,
  styles: [
    `
      .sparkline {
        width: 100%;
        height: 32px;
        display: block;
      }
      polyline {
        fill: none;
        stroke-width: 2;
        vector-effect: non-scaling-stroke;
      }
      polyline.positive {
        stroke: var(--st-green);
      }
      polyline.negative {
        stroke: var(--st-red);
      }
    `,
  ],
})
export class SparklineComponent {
  values = input<number[]>([]);
  width = 100;
  height = 32;

  viewBox = computed(() => `0 0 ${this.width} ${this.height}`);

  isPositive = computed(() => {
    const v = this.values();
    if (v.length < 2) return true;
    return v[v.length - 1] >= v[0];
  });

  points = computed(() => {
    const v = this.values();
    if (v.length === 0) return '';
    const min = Math.min(...v);
    const max = Math.max(...v);
    const range = max - min || 1;
    const step = this.width / Math.max(1, v.length - 1);
    return v
      .map((val, i) => {
        const x = i * step;
        const y = this.height - ((val - min) / range) * this.height;
        return `${x},${y}`;
      })
      .join(' ');
  });
}
