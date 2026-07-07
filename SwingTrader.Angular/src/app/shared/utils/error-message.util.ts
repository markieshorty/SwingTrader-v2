/**
 * Returns a user-facing error message for HTTP errors.
 * 403s always surface as a clear permissions message so Members know
 * why an action was blocked, rather than seeing a generic failure toast.
 */
export function errorMessage(err: any, fallback: string): string {
  if (err?.status === 403) return 'You don\'t have permission to do this — only the account Owner can.';
  return err?.error?.message ?? fallback;
}
