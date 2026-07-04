import { Component, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

@Component({
  selector: 'app-error-card',
  standalone: true,
  imports: [MatIconModule],
  template: `
    <div class="error-card">
      <mat-icon>error_outline</mat-icon>
      <span>{{ message() }}</span>
    </div>
  `,
  styles: [
    `
      .error-card {
        display: flex;
        align-items: center;
        gap: 8px;
        padding: 12px 16px;
        margin: 8px 16px;
        background: #7f1d1d;
        color: #fecaca;
        border-radius: 8px;
        font-size: 14px;
      }
    `,
  ],
})
export class ErrorCardComponent {
  message = input.required<string>();
}
