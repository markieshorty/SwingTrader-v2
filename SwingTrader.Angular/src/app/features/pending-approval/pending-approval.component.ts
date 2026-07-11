import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { AuthService } from '../../core/services/auth.service';
import { ApiService } from '../../core/services/api.service';

@Component({
  selector: 'app-pending-approval',
  standalone: true,
  imports: [MatButtonModule, MatCardModule, MatIconModule],
  templateUrl: './pending-approval.component.html',
  styleUrl: './pending-approval.component.scss',
})
export class PendingApprovalComponent {
  auth = inject(AuthService);
  private api = inject(ApiService);

  // Two independent gates can land someone here - the friends-and-family
  // superadmin gate (adminApproved) and the per-account Owner-approves-
  // Member gate. Default to the stricter friends-and-family message since
  // it's the one nobody but the app owner can grant, and it's also the gate
  // a brand-new self-registered Owner is actually stuck behind.
  adminApproved = signal(false);

  constructor() {
    this.api.getApprovalStatus().subscribe({
      next: (status) => this.adminApproved.set(status.adminApproved),
    });
  }
}
