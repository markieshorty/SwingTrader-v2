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
    this.msal.logoutRedirect();
  }
}
