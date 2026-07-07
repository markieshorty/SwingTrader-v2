import { CommonModule } from '@angular/common';
import { Component, Inject } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';

export interface ConfirmApproveDialogData {
  tradeDate: string;
}

@Component({
  selector: 'app-confirm-approve-dialog',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>Approve today's trades?</h2>
    <mat-dialog-content>
      <p>
        This approves all of the Buy signals from {{ data.tradeDate | date: 'EEEE d MMM yyyy' }} for execution. The
        Execution agent will place orders for them at its next scheduled run.
      </p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button (click)="dialogRef.close(false)">Cancel</button>
      <button mat-raised-button color="primary" (click)="dialogRef.close(true)">Approve</button>
    </mat-dialog-actions>
  `,
})
export class ConfirmApproveDialogComponent {
  constructor(
    public dialogRef: MatDialogRef<ConfirmApproveDialogComponent, boolean>,
    @Inject(MAT_DIALOG_DATA) public data: ConfirmApproveDialogData,
  ) {}
}
