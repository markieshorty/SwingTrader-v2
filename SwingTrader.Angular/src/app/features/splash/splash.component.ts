import { Component, inject, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
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
  imports: [MatButtonModule, MatProgressSpinnerModule],
  template: `
    @if (checkingAuth()) {
      <div class="splash-container">
        <mat-spinner diameter="40"></mat-spinner>
      </div>
    } @else {
    <div class="splash-container">
      <div class="splash-content">
        <h1>Cadentic</h1>

        @if (alreadySignedIn()) {
          <p class="blurb">
            You're already signed in under an existing account, so this invite link doesn't apply — an account can
            only belong to one Account at a time. Redirecting to your dashboard…
          </p>
        } @else if (inviteToken()) {
          <p class="blurb">You've been invited to join a Cadentic account.</p>
          <div class="actions">
            <button mat-raised-button color="primary" (click)="joinAccount()">Join Account</button>
          </div>
        } @else {
          <p class="blurb">
            A private workspace that automates the routine of following markets — focused watchlists,
            daily research and a morning brief, on a steady cadence.
          </p>
          <div class="actions">
            <button mat-raised-button color="primary" (click)="authenticate()">Register</button>
            <button mat-stroked-button (click)="authenticate()">Log In</button>
          </div>
        }

        <p class="disclaimer">Private, invite-only software. Not financial advice. Investing involves risk.</p>
      </div>
    </div>
    }
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
  // Starts true so an already-authenticated visitor sees a blank spinner
  // instead of a flash of the full marketing page (register/log in buttons)
  // before ngOnInit's async navigateByUrl takes effect. Only flips to false
  // for the branches that actually need to show splash content.
  checkingAuth = signal(true);

  ngOnInit(): void {
    const token = this.route.snapshot.queryParamMap.get('invite');
    this.inviteToken.set(token);

    if (this.msal.instance.getAllAccounts().length === 0) {
      this.checkingAuth.set(false);
      return;
    }

    // MSAL restores the exact page (including ?invite=...) you were on
    // before loginRedirect() sent you to Google/CIAM - so a brand-new visitor
    // who just clicked "Join Account" lands right back here, now
    // authenticated, with the invite param still in the URL. Without this
    // marker that looks identical to someone who already had a session
    // clicking a fresh invite link, which incorrectly showed "this invite
    // doesn't apply" on every successful first-time join.
    const returningFromJoin = sessionStorage.getItem('inviteJoinReturn') === '1';
    if (returningFromJoin) {
      sessionStorage.removeItem('inviteJoinReturn');
      this.router.navigateByUrl('/dashboard');
      return;
    }

    if (token) {
      this.alreadySignedIn.set(true);
      this.checkingAuth.set(false);
      setTimeout(() => this.router.navigateByUrl('/dashboard'), 3000);
    } else {
      // authGuard stashes the originally-requested URL before sending an
      // unauthenticated visitor to login (MSAL's redirectUri always lands
      // back on "/", losing any deep link like /trades?tab=approvals
      // otherwise) - restore it here instead of always going to /dashboard.
      const redirect = sessionStorage.getItem('postLoginRedirect');
      sessionStorage.removeItem('postLoginRedirect');
      this.router.navigateByUrl(redirect || '/dashboard');
    }
  }

  joinAccount(): void {
    const token = this.inviteToken();
    if (token) {
      sessionStorage.setItem('pendingInviteToken', token);
    }
    sessionStorage.setItem('inviteJoinReturn', '1');
    this.authenticate();
  }

  authenticate(): void {
    this.msal.loginRedirect({
      scopes: ['openid', 'profile', environment.b2cScope],
    });
  }
}
