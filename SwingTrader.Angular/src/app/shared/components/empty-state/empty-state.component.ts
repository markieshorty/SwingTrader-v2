import { Component, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

// Quiet, intentional-looking empty states: a muted icon over a one-liner,
// replacing bare "No X." paragraphs so empty panels read as calm rather
// than broken.
@Component({
  selector: 'app-empty-state',
  standalone: true,
  imports: [MatIconModule],
  template: `
    <div class="empty">
      <mat-icon>{{ icon() }}</mat-icon>
      <span>{{ message() }}</span>
    </div>
  `,
  styles: [
    `
      .empty {
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 6px;
        padding: 22px 12px;
        color: var(--st-muted);
        opacity: 0.85;
      }
      mat-icon {
        font-size: 28px;
        width: 28px;
        height: 28px;
        opacity: 0.6;
      }
      span {
        font-size: 13px;
      }
    `,
  ],
})
export class EmptyStateComponent {
  icon = input<string>('inbox');
  message = input.required<string>();
}
