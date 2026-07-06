import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { ApiService } from '../services/api.service';
import { ApiKeyProvider, KeyStatusesDto } from '../models/dtos';

// Keys required before the account can actually run research/execution.
// Claude isn't required - it has a shared fallback key (see
// UserKeyService.GetKeyAsync), so accounts never need their own.
const REQUIRED_PROVIDERS: ApiKeyProvider[] = ['Finnhub', 'Tiingo'];

// Trading212 issues separate credentials per environment, and the account's
// TradingMode toggle only allows switching to whichever environment already
// has a saved pair (enforced server-side in PUT /account/trading-config) -
// so onboarding only needs *one* complete pair, demo or live, not both, and
// not specifically the one matching the account's current TradingMode.
function hasAnyTrading212Pair(statuses: KeyStatusesDto): boolean {
  const hasDemo = statuses.Trading212DemoKey !== 'NotSet' && statuses.Trading212DemoSecret !== 'NotSet';
  const hasLive = statuses.Trading212LiveKey !== 'NotSet' && statuses.Trading212LiveSecret !== 'NotSet';
  return hasDemo || hasLive;
}

export function isOnboardingComplete(statuses: KeyStatusesDto): boolean {
  return REQUIRED_PROVIDERS.every((p) => statuses[p] !== 'NotSet') && hasAnyTrading212Pair(statuses);
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
