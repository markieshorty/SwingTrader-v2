import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../../core/services/api.service';
import { AccountMemberDto } from '../../core/models/dtos';

// Members/invite UI implemented ahead of the rest of Settings (Phase 10e)
// since it's the only piece of this page Phase 10c's spec actually
// requires. Owner-only enforcement happens server-side (403 on
// createInvite/removeMember for non-Owners) rather than duplicating role
// checks client-side.
@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, MatCardModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatIconModule],
  templateUrl: './settings.component.html',
  styleUrl: './settings.component.scss',
})
export class SettingsComponent {
  private api = inject(ApiService);
  private snackbar = inject(MatSnackBar);

  members = signal<AccountMemberDto[]>([]);
  inviteEmail = '';
  inviteLink = signal<string | null>(null);

  constructor() {
    this.loadMembers();
  }

  private loadMembers(): void {
    this.api.getMembers().subscribe({
      next: (members) => this.members.set(members),
      error: () => this.members.set([]),
    });
  }

  createInvite(): void {
    if (!this.inviteEmail.trim()) return;

    this.api.createInvite(this.inviteEmail.trim(), window.location.origin).subscribe({
      next: (result) => {
        this.inviteLink.set(result.inviteUrl);
        this.inviteEmail = '';
      },
      error: (err) => {
        const message = err.status === 403
          ? 'Only the account Owner can invite members.'
          : 'Failed to create invite.';
        this.snackbar.open(message, 'Dismiss', { duration: 4000 });
      },
    });
  }

  removeMember(userId: string): void {
    this.api.removeMember(userId).subscribe({
      next: () => this.loadMembers(),
      error: (err) => {
        const message = err.status === 403
          ? 'Only the account Owner can remove members.'
          : 'Failed to remove member.';
        this.snackbar.open(message, 'Dismiss', { duration: 4000 });
      },
    });
  }

  copyInviteLink(): void {
    const link = this.inviteLink();
    if (link) {
      navigator.clipboard.writeText(link);
      this.snackbar.open('Invite link copied', 'Dismiss', { duration: 2000 });
    }
  }
}
