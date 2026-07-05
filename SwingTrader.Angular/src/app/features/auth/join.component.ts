import { Component, OnInit, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { environment } from '../../../environments/environment';

// Reached via an invite link (/join?invite={token}) shared directly by an
// Account Owner. Stashes the token client-side, then triggers the same
// Google sign-in flow as a normal login — inviteTokenInterceptor attaches
// it as the X-Invite-Token header on the first authenticated request,
// which UserRegistrationMiddleware reads to join the inviter's Account as
// a Member instead of creating a brand-new Account.
@Component({
  selector: 'app-join',
  standalone: true,
  template: `<p class="joining">Joining account...</p>`,
  styles: [
    `
      .joining {
        display: flex;
        align-items: center;
        justify-content: center;
        height: 100vh;
        color: var(--st-muted);
      }
    `,
  ],
})
export class JoinComponent implements OnInit {
  private route = inject(ActivatedRoute);
  private msal = inject(MsalService);

  ngOnInit(): void {
    const token = this.route.snapshot.queryParamMap.get('invite');
    if (token) {
      sessionStorage.setItem('pendingInviteToken', token);
    }
    this.msal.loginRedirect({
      scopes: ['openid', 'profile', environment.b2cScope],
    });
  }
}
