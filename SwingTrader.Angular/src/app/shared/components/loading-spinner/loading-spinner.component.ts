import { Component, input } from '@angular/core';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

@Component({
  selector: 'app-loading-spinner',
  standalone: true,
  imports: [MatProgressSpinnerModule],
  template: `
    <div class="spinner-wrap">
      <mat-spinner [diameter]="diameter()"></mat-spinner>
    </div>
  `,
  styles: [
    `
      .spinner-wrap {
        display: flex;
        justify-content: center;
        align-items: center;
        padding: 24px;
      }
    `,
  ],
})
export class LoadingSpinnerComponent {
  diameter = input<number>(40);
}
