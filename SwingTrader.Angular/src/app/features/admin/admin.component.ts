import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Title } from '@angular/platform-browser';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { ApiService } from '../../core/services/api.service';
import { AdminActionLogDto, AdminJobFailureDto, AdminStatsDto, AdminUserSummaryDto, SentimentArchiveStatsDto } from '../../core/models/dtos';
import { readTabIndexFromRoute, writeTabIndexToRoute } from '../../shared/utils/tab-route.util';
import { ConfirmDialogComponent } from '../../shared/components/confirm-dialog/confirm-dialog.component';
import { MonitoringComponent } from './monitoring/monitoring.component';

const TAB_NAMES = ['overview', 'users', 'jobs', 'logs', 'health'] as const;

@Component({
  selector: 'app-admin',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, MatCardModule, MatButtonModule, MatIconModule, MatTabsModule, MonitoringComponent],
  templateUrl: './admin.component.html',
  styleUrl: './admin.component.scss',
})
export class AdminComponent {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snackbar = inject(MatSnackBar);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private titleService = inject(Title);

  stats = signal<AdminStatsDto | null>(null);
  sentimentArchive = signal<SentimentArchiveStatsDto | null>(null);
  users = signal<AdminUserSummaryDto[]>([]);
  jobFailures = signal<AdminJobFailureDto[]>([]);
  logs = signal<AdminActionLogDto[]>([]);

  suspendReasonInput: Record<string, string> = {};
  expandedUserId = signal<string | null>(null);

  selectedTabIndex = signal(0);

  constructor() {
    this.loadStats();
    this.loadUsers();
    this.loadJobFailures();
    this.loadLogs();
    this.selectedTabIndex.set(readTabIndexFromRoute(this.route, TAB_NAMES));
  }

  onTabChange(index: number): void {
    this.selectedTabIndex.set(index);
    writeTabIndexToRoute(this.router, this.route, TAB_NAMES, index, this.titleService, 'Admin');
  }

  private loadStats(): void {
    this.api.getAdminStats().subscribe({ next: (stats) => this.stats.set(stats) });
    this.api.getSentimentArchiveStats().subscribe({ next: (s) => this.sentimentArchive.set(s) });
  }

  private loadUsers(): void {
    this.api.getAdminUsers().subscribe({ next: (users) => this.users.set(users) });
  }

  private loadJobFailures(): void {
    this.api.getAdminJobFailures().subscribe({ next: (failures) => this.jobFailures.set(failures) });
  }

  private loadLogs(): void {
    this.api.getAdminLogs().subscribe({ next: (logs) => this.logs.set(logs) });
  }

  toggleExpanded(userId: string): void {
    this.expandedUserId.set(this.expandedUserId() === userId ? null : userId);
  }

  suspend(user: AdminUserSummaryDto): void {
    const reason = this.suspendReasonInput[user.userId] || undefined;
    this.api.suspendUser(user.userId, reason).subscribe({
      next: () => {
        this.snackbar.open(`${user.displayName} suspended`, 'Dismiss', { duration: 3000 });
        this.loadUsers();
        this.loadLogs();
      },
      error: () => this.snackbar.open('Failed to suspend user.', 'Dismiss', { duration: 4000 }),
    });
  }

  unsuspend(user: AdminUserSummaryDto): void {
    this.api.unsuspendUser(user.userId).subscribe({
      next: () => {
        this.snackbar.open(`${user.displayName} unsuspended`, 'Dismiss', { duration: 3000 });
        this.loadUsers();
        this.loadLogs();
      },
    });
  }

  resetOnboarding(user: AdminUserSummaryDto): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        data: {
          title: 'Reset onboarding',
          message: `Reset onboarding for ${user.displayName}? They will be sent back through the setup wizard.`,
          cancelLabel: 'Cancel',
          confirmLabel: 'Reset',
        },
        width: '420px',
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (!confirmed) return;
        this.api.resetUserOnboarding(user.userId).subscribe({
          next: () => {
            this.snackbar.open(`${user.displayName}'s onboarding reset`, 'Dismiss', { duration: 3000 });
            this.loadUsers();
            this.loadLogs();
          },
        });
      });
  }

  forceDemo(user: AdminUserSummaryDto): void {
    this.api.forceUserDemo(user.userId).subscribe({
      next: () => {
        this.snackbar.open(`${user.displayName} forced to Demo mode`, 'Dismiss', { duration: 3000 });
        this.loadUsers();
        this.loadLogs();
      },
    });
  }

  deleteUser(user: AdminUserSummaryDto): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        data: {
          title: 'Delete user',
          message: `Delete ${user.displayName} (${user.email})? This deactivates their account.`,
          cancelLabel: 'Cancel',
          confirmLabel: 'Delete',
          confirmColor: 'warn',
        },
        width: '420px',
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (!confirmed) return;
        this.api.deleteAdminUser(user.userId).subscribe({
          next: () => {
            this.snackbar.open(`${user.displayName} deleted`, 'Dismiss', { duration: 3000 });
            this.loadUsers();
            this.loadLogs();
          },
        });
      });
  }

  retryJob(failure: AdminJobFailureDto): void {
    this.api.retryAdminJob(failure.jobLogId).subscribe({
      next: () => {
        this.snackbar.open(`Retrying ${failure.jobType} for account ${failure.accountId}`, 'Dismiss', { duration: 3000 });
        this.loadJobFailures();
        this.loadLogs();
      },
      error: () => this.snackbar.open('Failed to retry job.', 'Dismiss', { duration: 4000 }),
    });
  }

  deleteJobFailure(failure: AdminJobFailureDto): void {
    this.dialog
      .open(ConfirmDialogComponent, {
        data: {
          title: 'Dismiss job failure',
          message: `Dismiss this ${failure.jobType} failure for account ${failure.accountId}? It will no longer appear in the failures list.`,
          cancelLabel: 'Cancel',
          confirmLabel: 'Dismiss',
        },
        width: '420px',
      })
      .afterClosed()
      .subscribe((confirmed) => {
        if (!confirmed) return;
        this.api.deleteAdminJobFailure(failure.jobLogId).subscribe({
          next: () => {
            this.snackbar.open(`Dismissed ${failure.jobType} failure for account ${failure.accountId}`, 'Dismiss', { duration: 3000 });
            this.loadJobFailures();
            this.loadLogs();
          },
          error: () => this.snackbar.open('Failed to delete job.', 'Dismiss', { duration: 4000 }),
        });
      });
  }
}
