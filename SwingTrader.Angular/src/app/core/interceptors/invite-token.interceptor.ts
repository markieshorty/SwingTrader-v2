import { HttpInterceptorFn } from '@angular/common/http';
import { finalize } from 'rxjs';

// Attaches a pending invite token (stashed by join.component.ts before
// redirecting to Google sign-in) as X-Invite-Token on outgoing API calls,
// so UserRegistrationMiddleware can pick it up on first login.
//
// The SPA fires several /api/* requests in parallel on first load (the
// dashboard's forkJoin, the onboarding guard's key check, etc.), and on the
// server UserRegistrationMiddleware's per-userId lock lets whichever one of
// those requests happens to win the race actually create the AppUser/Account
// - reading X-Invite-Token off THAT request's own headers, not necessarily
// the specific request this interceptor first attached it to. Removing the
// token from sessionStorage as soon as it was read (the previous behaviour)
// meant only the very first request constructed carried the header; if a
// different, header-less request won the race, the invite was silently
// dropped and the person got a brand-new orphan Account instead of joining
// the inviter's - confirmed in production against a real invite. Keeping
// the token in sessionStorage until a request actually completes ensures
// every request fired during that initial burst carries the header, so
// whichever one wins the race still has it.
export const inviteTokenInterceptor: HttpInterceptorFn = (req, next) => {
  const token = sessionStorage.getItem('pendingInviteToken');
  if (!token || !req.url.includes('/api/')) {
    return next(req);
  }

  return next(req.clone({ setHeaders: { 'X-Invite-Token': token } })).pipe(
    finalize(() => sessionStorage.removeItem('pendingInviteToken')),
  );
};
