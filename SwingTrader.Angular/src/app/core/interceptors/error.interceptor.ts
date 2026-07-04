import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';

export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  return next(req).pipe(
    catchError((error: HttpErrorResponse) => {
      if (error.status === 0) {
        console.error('API unavailable');
      } else if (error.status === 401) {
        // Phase 10c: redirect to login
        console.log('Unauthorised');
      } else if (error.status === 500) {
        console.error('Server error', error);
      }
      return throwError(() => error);
    }),
  );
};
