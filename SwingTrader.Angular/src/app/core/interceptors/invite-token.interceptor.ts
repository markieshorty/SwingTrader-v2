import { HttpInterceptorFn } from '@angular/common/http';

// Attaches a pending invite token (stashed by join.component.ts before
// redirecting to Google sign-in) as X-Invite-Token on outgoing API calls,
// so UserRegistrationMiddleware can pick it up on first login. Cleared
// after being sent once — the middleware only consults it during brand
// new AppUser creation, so there's no need to keep resending it forever.
export const inviteTokenInterceptor: HttpInterceptorFn = (req, next) => {
  const token = sessionStorage.getItem('pendingInviteToken');
  if (!token || !req.url.includes('/api/')) {
    return next(req);
  }

  sessionStorage.removeItem('pendingInviteToken');
  return next(req.clone({ setHeaders: { 'X-Invite-Token': token } }));
};
