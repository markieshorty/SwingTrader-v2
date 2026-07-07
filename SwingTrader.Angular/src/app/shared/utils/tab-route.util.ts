import { ActivatedRoute, Router } from '@angular/router';
import { Title } from '@angular/platform-browser';

// Shared helper for mat-tab-group pages that want ?tab=name deep-linking -
// reads the initial tab from the query param on load, and keeps the URL in
// sync (via replaceUrl, so tab-switching doesn't spam browser history) as
// the user changes tabs.
export function readTabIndexFromRoute(route: ActivatedRoute, tabNames: readonly string[]): number {
  const tabParam = route.snapshot.queryParamMap.get('tab');
  const index = tabParam ? tabNames.indexOf(tabParam) : -1;
  return index >= 0 ? index : 0;
}

// pageTitle is a fixed string (e.g. "Trades"), re-applied after the
// query-param-only navigation settles - without this, the browser tab
// title ends up reflecting the raw URL (e.g. "Trades?tab=approvals")
// since this in-place navigation doesn't cross a route boundary with its
// own static route title.
export function writeTabIndexToRoute(
  router: Router,
  route: ActivatedRoute,
  tabNames: readonly string[],
  index: number,
  titleService?: Title,
  pageTitle?: string,
): void {
  router
    .navigate([], {
      relativeTo: route,
      queryParams: { tab: tabNames[index] },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    })
    .then(() => {
      if (titleService && pageTitle) titleService.setTitle(pageTitle);
    });
}
