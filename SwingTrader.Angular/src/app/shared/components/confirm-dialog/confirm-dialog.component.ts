import { CommonModule } from '@angular/common';
import { Component, Inject } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';

export interface ConfirmDialogData {
  title: string;
  message: string;
  // Button that resolves false (the safe default).
  cancelLabel: string;
  // Button that resolves true (the action).
  confirmLabel: string;
  // 'warn' renders the confirm button red for destructive/risky actions.
  confirmColor?: 'primary' | 'warn';
}

// Plain yes/no confirm with fully custom labels and confirm-button colour -
// for lower-friction confirmations than ConfirmDeleteDialogComponent's
// type-the-word gate, where the wording of the buttons themselves carries
// the warning (e.g. "I don't care, change mode").
@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p style="white-space: pre-line">{{ data.message }}</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button (click)="dialogRef.close(false)">{{ data.cancelLabel }}</button>
      <button mat-raised-button [color]="data.confirmColor ?? 'primary'" (click)="dialogRef.close(true)">
        {{ data.confirmLabel }}
      </button>
    </mat-dialog-actions>
  `,
})
export class ConfirmDialogComponent {
  constructor(
    public dialogRef: MatDialogRef<ConfirmDialogComponent, boolean>,
    @Inject(MAT_DIALOG_DATA) public data: ConfirmDialogData,
  ) {}
}
