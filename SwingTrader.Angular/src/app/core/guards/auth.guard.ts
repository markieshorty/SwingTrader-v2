import { inject } from '@angular/core';
import { CanActivateFn } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { environment } from '../../../environments/environment';

export const authGuard: CanActivateFn = () => {
  const msal = inject(MsalService);

  if (msal.instance.getAllAccounts().length > 0) {
    return true;
  }

  msal.instance.loginRedirect({
    scopes: ['openid', 'profile', environment.b2cScope],
  });

  return false;
};
