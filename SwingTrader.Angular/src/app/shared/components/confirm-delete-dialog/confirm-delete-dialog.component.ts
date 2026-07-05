import { CommonModule } from '@angular/common';
import { Component, Inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';

export interface ConfirmDeleteDialogData {
  title: string;
  message: string;
  confirmWord: string;
}

// Requires typing an exact confirmation word (e.g. "DELETE") before the
// confirm button enables - a deliberately higher-friction gate than a
// native confirm() for actions that can't be undone, since native confirms
// get reflexively clicked through without reading them.
@Component({
  selector: 'app-confirm-delete-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p>{{ data.message }}</p>
      <p>
        Type <strong>{{ data.confirmWord }}</strong> below to confirm.
      </p>
      <mat-form-field appearance="outline" class="confirm-field">
        <mat-label>Confirmation</mat-label>
        <input matInput [(ngModel)]="typedValue" [placeholder]="data.confirmWord" autocomplete="off" />
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button (click)="dialogRef.close(false)">Cancel</button>
      <button mat-raised-button color="warn" [disabled]="typedValue !== data.confirmWord" (click)="dialogRef.close(true)">
        Confirm
      </button>
    </mat-dialog-actions>
  `,
  styles: [
    `
      .confirm-field {
        width: 100%;
      }
    `,
  ],
})
export class ConfirmDeleteDialogComponent {
  typedValue = '';

  constructor(
    public dialogRef: MatDialogRef<ConfirmDeleteDialogComponent, boolean>,
    @Inject(MAT_DIALOG_DATA) public data: ConfirmDeleteDialogData,
  ) {}
}
