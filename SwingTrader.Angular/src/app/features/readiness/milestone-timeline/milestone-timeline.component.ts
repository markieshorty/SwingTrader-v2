import { CommonModule } from '@angular/common';
import { Component, input } from '@angular/core';
import { MilestoneDto } from '../../../core/models/dtos';

@Component({
  selector: 'app-milestone-timeline',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="timeline">
      @for (milestone of milestones(); track milestone.label) {
        <div class="milestone" [class.completed]="milestone.completed">
          <div class="dot"></div>
          <div class="label">{{ milestone.label }}</div>
          @if (milestone.estimatedDateRange) {
            <div class="date">{{ milestone.estimatedDateRange }}</div>
          }
        </div>
      }
    </div>
  `,
  styles: [
    `
      .timeline {
        display: flex;
        overflow-x: auto;
        gap: 24px;
        padding: 16px 0;
      }
      .milestone {
        text-align: center;
        min-width: 120px;
      }
      .dot {
        width: 12px;
        height: 12px;
        border-radius: 50%;
        background: var(--st-border);
        margin: 0 auto 8px;
      }
      .milestone.completed .dot {
        background: var(--st-green);
      }
      .label {
        font-size: 12px;
        color: var(--st-text);
      }
      .date {
        font-size: 11px;
        color: var(--st-muted);
      }
    `,
  ],
})
export class MilestoneTimelineComponent {
  milestones = input.required<MilestoneDto[]>();
}
