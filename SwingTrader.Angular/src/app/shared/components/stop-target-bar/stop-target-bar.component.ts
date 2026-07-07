import { CommonModule } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { PositionDto } from '../../../core/models/dtos';

@Component({
  selector: 'app-stop-target-bar',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="bar-container">
      <div class="labels">
        <span class="stop">£{{ position().stopLoss | number: '1.2-2' }}</span>
        <span
          class="current"
          [class.near-stop]="position().isNearStop"
          [class.near-target]="position().isNearTarget"
        >
          £{{ position().currentPrice | number: '1.2-2' }}
        </span>
        <span class="target">£{{ position().target | number: '1.2-2' }}</span>
      </div>
      <div class="track">
        <div class="danger-zone" [style.width.%]="dangerWidth()"></div>
        <div class="safe-zone" [style.width.%]="safeWidth()"></div>
        @if (trailingStopPosition(); as pos) {
          <div class="trailing-marker" [style.left.%]="pos" title="Trailing stop"></div>
        }
        <div class="current-marker" [style.left.%]="currentPosition()"></div>
      </div>
      @if (position().trailingStopPrice) {
        <p class="trailing-label">Trailing stop: £{{ position().trailingStopPrice | number: '1.2-2' }}</p>
      }
    </div>
  `,
  styles: [
    `
      .bar-container {
        width: 100%;
      }
      .labels {
        display: flex;
        justify-content: space-between;
        font-size: 12px;
        margin-bottom: 4px;
        color: var(--st-muted);
      }
      .current {
        font-weight: 600;
        color: var(--st-text);
      }
      .current.near-stop {
        color: var(--st-red);
      }
      .current.near-target {
        color: var(--st-green);
      }
      .track {
        position: relative;
        height: 8px;
        border-radius: 4px;
        background: var(--st-border);
        overflow: visible;
      }
      .danger-zone {
        position: absolute;
        left: 0;
        top: 0;
        bottom: 0;
        background: var(--st-red);
        opacity: 0.4;
        border-radius: 4px 0 0 4px;
      }
      .safe-zone {
        position: absolute;
        right: 0;
        top: 0;
        bottom: 0;
        background: var(--st-green);
        opacity: 0.4;
        border-radius: 0 4px 4px 0;
      }
      .current-marker {
        position: absolute;
        top: -3px;
        width: 3px;
        height: 14px;
        background: var(--st-text);
        border-radius: 2px;
        transform: translateX(-50%);
      }
      .trailing-marker {
        position: absolute;
        top: -3px;
        width: 3px;
        height: 14px;
        background: var(--st-amber);
        border-radius: 2px;
        transform: translateX(-50%);
      }
      .trailing-label {
        font-size: 11px;
        color: var(--st-amber);
        margin: 4px 0 0;
      }
    `,
  ],
})
export class StopTargetBarComponent {
  position = input.required<PositionDto>();

  dangerWidth = computed(() => {
    const p = this.position();
    const total = p.target - p.stopLoss;
    if (total === 0) return 0;
    const danger = p.entryPrice - p.stopLoss;
    return Math.max(0, Math.min(100, (danger / total) * 100));
  });

  safeWidth = computed(() => 100 - this.dangerWidth());

  currentPosition = computed(() => {
    const p = this.position();
    const total = p.target - p.stopLoss;
    if (total === 0) return 0;
    const current = p.currentPrice - p.stopLoss;
    return Math.max(0, Math.min(100, (current / total) * 100));
  });

  // Once armed, the trailing stop supersedes the static StopLoss as the real
  // downside trigger - shown as its own marker so an exit like "TrailingStopHit
  // at £134.37" doesn't look disconnected from a bar that only ever plotted
  // the original stop/target range.
  trailingStopPosition = computed(() => {
    const p = this.position();
    if (!p.trailingStopPrice) return null;
    const total = p.target - p.stopLoss;
    if (total === 0) return null;
    const pos = (p.trailingStopPrice - p.stopLoss) / total * 100;
    return Math.max(0, Math.min(100, pos));
  });
}
