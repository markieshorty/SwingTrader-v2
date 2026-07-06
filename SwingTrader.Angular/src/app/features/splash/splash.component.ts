import { Component, inject, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MsalService } from '@azure/msal-angular';
import { environment } from '../../../environments/environment';

// Public landing page - the app's default route. Reached by anyone,
// authenticated or not; an already-signed-in visitor is bounced straight
// to /dashboard so this only ever shows to first-time/logged-out visitors.
// Log In and Register both trigger the same MSAL redirect - CIAM's unified
// sign-up-or-sign-in flow decides which experience to show, there's no
// separate app-side registration step (UserRegistrationMiddleware creates
// the Account/AppUser on first authenticated request either way).
@Component({
  selector: 'app-splash',
  standalone: true,
  imports: [MatButtonModule],
  template: `
    <div class="splash-container">
      <div class="splash-content">
        <h1>SwingTrader</h1>
        <p class="blurb">
          An autonomous swing trading system that screens the market, scores setups, and manages positions with
          disciplined, data-driven rules — so you don't have to watch every tick.
        </p>
        <div class="actions">
          <button mat-raised-button color="primary" (click)="authenticate()">Register</button>
          <button mat-stroked-button (click)="authenticate()">Log In</button>
        </div>
        <p class="disclaimer">Trading involves risk. This system is not financial advice.</p>
      </div>
    </div>
  `,
  styles: [
    `
      .splash-container {
        display: flex;
        align-items: center;
        justify-content: center;
        height: 100vh;
        background: var(--st-navy);
        padding: 24px;
      }
      .splash-content {
        text-align: center;
        max-width: 480px;
      }
      h1 {
        font-size: 40px;
        margin-bottom: 8px;
        color: var(--st-text, white);
      }
      .blurb {
        color: var(--st-muted);
        font-size: 15px;
        line-height: 1.5;
        margin-bottom: 32px;
      }
      .actions {
        display: flex;
        justify-content: center;
        gap: 16px;
        margin-bottom: 24px;
      }
      .disclaimer {
        font-size: 11px;
        color: var(--st-muted);
      }
    `,
  ],
})
export class SplashComponent implements OnInit {
  private msal = inject(MsalService);
  private router = inject(Router);

  ngOnInit(): void {
    if (this.msal.instance.getAllAccounts().length > 0) {
      this.router.navigateByUrl('/dashboard');
    }
  }

  authenticate(): void {
    this.msal.loginRedirect({
      scopes: ['openid', 'profile', environment.b2cScope],
    });
  }
}
