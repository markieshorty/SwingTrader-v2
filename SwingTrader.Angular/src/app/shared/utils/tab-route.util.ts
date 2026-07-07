import { ActivatedRoute, Router } from '@angular/router';

// Shared helper for mat-tab-group pages that want ?tab=name deep-linking -
// reads the initial tab from the query param on load, and keeps the URL in
// sync (via replaceUrl, so tab-switching doesn't spam browser history) as
// the user changes tabs.
export function readTabIndexFromRoute(route: ActivatedRoute, tabNames: readonly string[]): number {
  const tabParam = route.snapshot.queryParamMap.get('tab');
  const index = tabParam ? tabNames.indexOf(tabParam) : -1;
  return index >= 0 ? index : 0;
}

export function writeTabIndexToRoute(
  router: Router,
  route: ActivatedRoute,
  tabNames: readonly string[],
  index: number,
): void {
  router.navigate([], {
    relativeTo: route,
    queryParams: { tab: tabNames[index] },
    queryParamsHandling: 'merge',
    replaceUrl: true,
  });
}
