import { Injectable, signal } from '@angular/core';
import { UserDto } from '../models/dtos';

// Phase 10c will implement MSAL here. For now: always authenticated.
// Guards use this service — when 10c wires it up, no guard changes needed.
@Injectable({ providedIn: 'root' })
export class AuthService {
  isAuthenticated = signal<boolean>(true);
  currentUser = signal<UserDto | null>(null);

  login(): void {
    // Phase 10c: redirect to Google OAuth via MSAL
    console.log('Auth not yet implemented');
  }

  logout(): void {
    // Phase 10c: MSAL logout
    console.log('Auth not yet implemented');
  }
}
