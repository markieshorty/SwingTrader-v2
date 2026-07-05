import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { ApiService } from '../services/api.service';

// Runs before onboardingGuard on every main app route. A Member who joined
// via an invite link starts unapproved and can't do anything until the
// Owner approves them - UserRegistrationMiddleware enforces this for real
// (403s every /api/* call except this status check), this guard just keeps
// them off pages that would otherwise render broken/empty behind those
// 403s and sends them to an explicit "waiting for approval" screen instead.
export const approvalGuard: CanActivateFn = () => {
  const api = inject(ApiService);
  const router = inject(Router);

  return api.getApprovalStatus().pipe(
    map((status) => (status.isApproved ? true : router.createUrlTree(['/pending-approval']))),
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
