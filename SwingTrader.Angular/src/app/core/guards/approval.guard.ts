import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, forkJoin, map, of } from 'rxjs';
import { ApiService } from '../services/api.service';

// Runs before onboardingGuard on every main app route. Two independent
// gates can leave someone unapproved: the friends-and-family AdminApproved
// gate (every brand-new sign-up, Owner or Member) and the per-account
// Owner-approves-Member gate - UserRegistrationMiddleware enforces both for
// real (403s every /api/* call except approval-status and account/me), this
// guard just keeps unapproved users off pages that would otherwise render
// broken/empty behind those 403s.
//
// Not approved AND haven't confirmed a real email yet -> onboarding's email
// step first (the ONE thing a gated user can still do, since the email
// captured at first login can be a synthetic fallback - the superadmin
// needs a real address to recognize them by in the Unapproved admin tab).
// Not approved but email already confirmed -> nothing left to do but wait.
export const approvalGuard: CanActivateFn = () => {
  const api = inject(ApiService);
  const router = inject(Router);

  return forkJoin([api.getApprovalStatus(), api.getMe()]).pipe(
    map(([status, me]) =>
      status.isApproved ? true : router.createUrlTree([me.hasConfirmedEmail ? '/pending-approval' : '/onboarding']),
    ),
    // The backend is the real enforcement point regardless of what this
    // guard decides - if the status check itself fails (transient error),
    // don't trap an already-approved user behind it.
    catchError(() => of(true)),
  );
};

// Guards /pending-approval itself: once approved, sitting there is pointless.
export const approvalCompleteGuard: CanActivateFn = () => {
  const api = inject(ApiService);
  const router = inject(Router);

  return api.getApprovalStatus().pipe(
    map((status) => (status.isApproved ? router.createUrlTree(['/dashboard']) : true)),
    catchError(() => of(true)),
  );
};
