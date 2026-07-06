import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MsalService } from '@azure/msal-angular';
import { environment } from '../../../environments/environment';

// Public landing page - the app's default route (and also reached via
// /join?invite=... links shared by an Account Owner, which drives the same
// component into "join" mode via the invite query param rather than a
// separate page).
//
// Reached by anyone, authenticated or not. An already-signed-in visitor is
// bounced straight to /dashboard - except an invite link, which instead
// shows a message that the invite doesn't apply (an account can only
// belong to one Account at a time), matching the old dedicated
// JoinComponent's behaviour.
//
// Register and Log In (and the invite flow's single Join button) all
// trigger the same MSAL redirect - CIAM's unified sign-up-or-sign-in flow
// decides which experience to show, there's no separate app-side
// registration step (UserRegistrationMiddleware creates the Account/AppUser
// on first authenticated request either way, reading the invite token back
// out of sessionStorage via inviteTokenInterceptor).
@Component({
  selector: 'app-splash',
  standalone: true,
  imports: [MatButtonModule],
  template: `
    <div class="splash-container">
      <div class="splash-content">
        <h1>SwingTrader</h1>

        @if (alreadySignedIn()) {
          <p class="blurb">
            You're already signed in under an existing account, so this invite link doesn't apply — an account can
            only belong to one Account at a time. Redirecting to your dashboard…
          </p>
        } @else if (inviteToken()) {
          <p class="blurb">You've been invited to join a SwingTrader account.</p>
          <div class="actions">
            <button mat-raised-button color="primary" (click)="joinAccount()">Join Account</button>
          </div>
        } @else {
          <p class="blurb">
            An autonomous swing trading system that screens the market, scores setups, and manages positions with
            disciplined, data-driven rules — so you don't have to watch every tick.
          </p>
          <div class="actions">
            <button mat-raised-button color="primary" (click)="authenticate()">Register</button>
            <button mat-stroked-button (click)="authenticate()">Log In</button>
          </div>
        }

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
  private route = inject(ActivatedRoute);

  inviteToken = signal<string | null>(null);
  alreadySignedIn = signal(false);

  ngOnInit(): void {
    const token = this.route.snapshot.queryParamMap.get('invite');
    this.inviteToken.set(token);

    if (this.msal.instance.getAllAccounts().length > 0) {
      if (token) {
        this.alreadySignedIn.set(true);
        setTimeout(() => this.router.navigateByUrl('/dashboard'), 3000);
      } else {
        this.router.navigateByUrl('/dashboard');
      }
    }
  }

  joinAccount(): void {
    const token = this.inviteToken();
    if (token) {
      sessionStorage.setItem('pendingInviteToken', token);
    }
    this.authenticate();
  }

  authenticate(): void {
    this.msal.loginRedirect({
      scopes: ['openid', 'profile', environment.b2cScope],
    });
  }
}
