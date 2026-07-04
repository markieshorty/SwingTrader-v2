import { CommonModule } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { FeatureCardDto } from '../../../core/models/dtos';

@Component({
  selector: 'app-feature-card',
  standalone: true,
  imports: [CommonModule, MatCardModule],
  template: `
    <mat-card class="feature-card">
      <div class="header">
        <span class="light" [class]="statusClass()"></span>
        <span class="name">{{ feature().featureName }}</span>
        <span class="risk" [class]="feature().riskLevel.toLowerCase()">{{ feature().riskLevel }} risk</span>
      </div>
      <ul class="criteria">
        @for (c of feature().criteria; track c.label) {
          <li [class.met]="c.met">{{ c.met ? '✅' : '❌' }} {{ c.label }}</li>
        }
      </ul>
      <p class="assessment">{{ feature().assessment }}</p>
      @if (feature().estimatedReadyDateRange) {
        <p class="eta">ETA: {{ feature().estimatedReadyDateRange }}</p>
      }
      <p class="action">{{ feature().actionHint }}</p>
    </mat-card>
  `,
  styles: [
    `
      .feature-card {
        padding: 16px;
      }
      .header {
        display: flex;
        align-items: center;
        gap: 8px;
        margin-bottom: 8px;
      }
      .light {
        width: 10px;
        height: 10px;
        border-radius: 50%;
        background: var(--st-muted);
      }
      .light.ready {
        background: var(--st-green);
      }
      .light.approaching {
        background: var(--st-amber);
      }
      .light.notready {
        background: var(--st-red);
      }
      .light.alreadyenabled {
        background: var(--st-blue);
      }
      .name {
        flex: 1;
        font-weight: 600;
      }
      .risk {
        font-size: 11px;
        color: var(--st-muted);
      }
      .criteria {
        list-style: none;
        padding: 0;
        margin: 8px 0;
        font-size: 13px;
      }
      .criteria li {
        color: var(--st-muted);
        padding: 2px 0;
      }
      .criteria li.met {
        color: var(--st-text);
      }
      .assessment {
        font-size: 13px;
      }
      .eta,
      .action {
        font-size: 12px;
        color: var(--st-muted);
      }
    `,
  ],
})
export class FeatureCardComponent {
  feature = input.required<FeatureCardDto>();
  statusClass = computed(() => this.feature().status.toLowerCase());
}
