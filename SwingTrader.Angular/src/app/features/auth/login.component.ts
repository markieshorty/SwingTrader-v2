import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MsalService } from '@azure/msal-angular';
import { environment } from '../../../environments/environment';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [MatButtonModule, MatCardModule],
  template: `
    <div class="login-container">
      <mat-card class="login-card">
        <h1>SwingTrader</h1>
        <p>Autonomous swing trading system</p>
        <button mat-raised-button color="primary" (click)="login()">Sign in with Google</button>
        <p class="disclaimer">Trading involves risk. This system is not financial advice.</p>
      </mat-card>
    </div>
  `,
  styles: [
    `
      .login-container {
        display: flex;
        align-items: center;
        justify-content: center;
        height: 100vh;
        background: var(--st-navy);
      }
      .login-card {
        padding: 32px;
        text-align: center;
        max-width: 360px;
      }
      .disclaimer {
        font-size: 11px;
        color: var(--st-muted);
        margin-top: 16px;
      }
    `,
  ],
})
export class LoginComponent {
  private msal = inject(MsalService);

  login(): void {
    // If the page was reached via a /join?invite=... link, join.component.ts
    // has already stashed the token in sessionStorage before redirecting
    // here — nothing extra to do, UserRegistrationMiddleware reads it back
    // out via the X-Invite-Token header on the first authenticated request.
    this.msal.loginRedirect({
      scopes: ['openid', 'profile', environment.b2cScope],
    });
  }
}
