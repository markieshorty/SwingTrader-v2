import { Injectable, computed, inject, signal } from '@angular/core';
import { MsalService } from '@azure/msal-angular';
import { ApiService } from './api.service';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private msal = inject(MsalService);
  private api = inject(ApiService);

  isAuthenticated = computed(() => this.msal.instance.getAllAccounts().length > 0);

  currentUser = computed(() => {
    const accounts = this.msal.instance.getAllAccounts();
    if (accounts.length === 0) return null;
    return {
      name: accounts[0].name ?? accounts[0].username,
      email: accounts[0].username,
      userId: accounts[0].localAccountId,
    };
  });

  isAdmin = signal<boolean>(false);

  constructor() {
    if (this.isAuthenticated()) {
      this.api.getAdminMe().subscribe({
        next: () => this.isAdmin.set(true),
        error: () => this.isAdmin.set(false),
      });
    }
  }

  logout(): void {
    // postLogoutRedirectUri is also set globally in app.config.ts's
    // msalConfig, but passed explicitly here too since MSAL doesn't
    // otherwise fall back to redirectUri for this - without it, logout
    // lands on a generic Microsoft/CIAM page instead of back on the splash.
    this.msal.logoutRedirect({ postLogoutRedirectUri: window.location.origin });
  }
}
