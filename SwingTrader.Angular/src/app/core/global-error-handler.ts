import { ErrorHandler, Injectable, Injector, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';

@Injectable()
export class GlobalErrorHandler implements ErrorHandler {
  private injector = inject(Injector);

  handleError(error: any): void {
    console.error(error);

    // Snackbar is resolved lazily via Injector to avoid a circular DI
    // dependency between ErrorHandler and Angular Material's overlay stack.
    const snackbar = this.injector.get(MatSnackBar);
    const message = error?.error?.message ?? error?.message ?? 'An unexpected error occurred';

    snackbar.open(message, 'Dismiss', {
      duration: 5000,
      panelClass: 'error-snackbar',
    });
  }
}
