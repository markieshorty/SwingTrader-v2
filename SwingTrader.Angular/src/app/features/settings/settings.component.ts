import { Component } from '@angular/core';
import { MatCardModule } from '@angular/material/card';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [MatCardModule],
  template: `
    <mat-card class="panel">
      <p>Settings will be available in a future update. API keys and account configuration will be managed here.</p>
    </mat-card>
  `,
  styles: [
    `
      .panel {
        padding: 16px;
      }
    `,
  ],
})
export class SettingsComponent {}
