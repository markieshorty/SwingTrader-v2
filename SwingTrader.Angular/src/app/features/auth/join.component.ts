import { CommonModule } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { environment } from '../../../environments/environment';

// Reached via an invite link (/join?invite={token}) shared directly by an
// Account Owner. Stashes the token client-side, then triggers the same
// Google sign-in flow as a normal login — inviteTokenInterceptor attaches
// it as the X-Invite-Token header on the first authenticated request,
// which UserRegistrationMiddleware reads to join the inviter's Account as
// a Member instead of creating a brand-new Account.
//
// UserRegistrationMiddleware only ever consults the invite token while
// creating a brand-new AppUser - it does nothing for someone who already
// has one. Calling loginRedirect() unconditionally here (regardless of
// whether the browser already had a valid MSAL session) meant an
// already-signed-in person clicking their own invite link would redirect
// straight back through B2C and land back on the SPA, immediately
// re-running this same ngOnInit - a fast reload loop that fires many
// parallel first-load API calls each cycle, which is what created 442
// orphan Account rows in production before UserRegistrationMiddleware's
// registration lock (separate fix) existed to stop each cycle's requests
// from racing each other.
@Component({
  selector: 'app-join',
  standalone: true,
  imports: [CommonModule],
  template: `
    @if (alreadySignedIn()) {
      <p class="joining">
        You're already signed in under an existing account, so this invite link doesn't apply — an account can
        only belong to one Account at a time. Redirecting to your dashboard…
      </p>
    } @else {
      <p class="joining">Joining account...</p>
    }
  `,
  styles: [
    `
      .joining {
        display: flex;
        align-items: center;
        justify-content: center;
        height: 100vh;
        padding: 0 48px;
        text-align: center;
        color: var(--st-muted);
      }
    `,
  ],
})
export class JoinComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private msal = inject(MsalService);

  alreadySignedIn = signal(false);

  ngOnInit(): void {
    if (this.msal.instance.getAllAccounts().length > 0) {
      this.alreadySignedIn.set(true);
      setTimeout(() => this.router.navigateByUrl('/dashboard'), 3000);
      return;
    }

    const token = this.route.snapshot.queryParamMap.get('invite');
    if (token) {
      sessionStorage.setItem('pendingInviteToken', token);
    }
    this.msal.loginRedirect({
      scopes: ['openid', 'profile', environment.b2cScope],
    });
  }
}
