import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { ApiService } from '../services/api.service';
import { ApiKeyProvider, KeyStatusesDto } from '../models/dtos';

// Keys required before the account can actually run research/execution.
// Email creds are optional (notifications only), so they don't block entry.
const REQUIRED_PROVIDERS: ApiKeyProvider[] = ['Finnhub', 'Tiingo', 'Trading212Key', 'Trading212Secret', 'Claude'];

export function isOnboardingComplete(statuses: KeyStatusesDto): boolean {
  return REQUIRED_PROVIDERS.every((p) => statuses[p] !== 'NotSet');
}

// Runs after authGuard on every main app route. A brand-new account has no
// keys saved yet (UserRegistrationMiddleware only seeds the watchlist/
// weights, not credentials), so this redirects straight to the wizard on
// first login and on every subsequent login until setup is finished.
export const onboardingGuard: CanActivateFn = () => {
  const api = inject(ApiService);
  const router = inject(Router);

  return api.getKeyStatuses().pipe(
    map((statuses) => (isOnboardingComplete(statuses) ? true : router.createUrlTree(['/onboarding']))),
    // If the status check fails (e.g. transient API error), don't trap the
    // user on the wizard - let them through and the page-level API calls
    // will surface the real error.
    catchError(() => of(true)),
  );
};

// Guards the wizard route itself: once setup is already complete, sitting on
// /onboarding is pointless - bounce to the dashboard instead.
export const onboardingCompleteGuard: CanActivateFn = () => {
  const api = inject(ApiService);
  const router = inject(Router);

  return api.getKeyStatuses().pipe(
    map((statuses) => (isOnboardingComplete(statuses) ? router.createUrlTree(['/dashboard']) : true)),
    catchError(() => of(true)),
  );
};
