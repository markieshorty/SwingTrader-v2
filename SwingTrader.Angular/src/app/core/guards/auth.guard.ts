import { inject } from '@angular/core';
import { CanActivateFn } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { environment } from '../../../environments/environment';

export const authGuard: CanActivateFn = (_route, state) => {
  const msal = inject(MsalService);

  if (msal.instance.getAllAccounts().length > 0) {
    return true;
  }

  // MSAL's redirectUri is fixed to the site root (see app.config.ts), so
  // after login completes the browser always lands back on "/" - without
  // this, SplashComponent's post-login logic sends every successful login
  // straight to /dashboard, silently discarding a deep link like
  // /trades?tab=approvals that brought the user here in the first place.
  sessionStorage.setItem('postLoginRedirect', state.url);

  msal.instance.loginRedirect({
    scopes: ['openid', 'profile', environment.b2cScope],
  });

  return false;
};
