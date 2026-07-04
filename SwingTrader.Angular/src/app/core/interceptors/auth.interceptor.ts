import { HttpInterceptorFn } from '@angular/common/http';

// Phase 10c: inject the MSAL access token here. For now: pass through
// unchanged. When 10c activates, this interceptor adds
// Authorization: Bearer {token} to all API requests.
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  return next(req);
};
