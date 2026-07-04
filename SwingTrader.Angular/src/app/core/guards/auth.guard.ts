import { inject } from '@angular/core';
import { CanActivateFn } from '@angular/router';
import { AuthService } from '../services/auth.service';

// Inactive until Phase 10c: AuthService.isAuthenticated is always true, so
// this guard is currently a no-op. Once 10c wires up MSAL, this starts
// redirecting unauthenticated users without any guard code changes.
export const authGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  return auth.isAuthenticated();
};
