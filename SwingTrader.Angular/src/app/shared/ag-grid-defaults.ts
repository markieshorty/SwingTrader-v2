import { ColDef } from 'ag-grid-community';

// flex + minWidth make columns share the available width instead of AG
// Grid's default fixed 200px-per-column behaviour, which was overflowing
// its container and causing a page-wide horizontal scrollbar.
export const defaultColDef: ColDef = {
  sortable: true,
  filter: true,
  resizable: true,
  suppressMovable: false,
  flex: 1,
  // Lower than the previous 100 so the many-column trade grids can pack
  // tighter and fit without a horizontal scrollbar.
  minWidth: 80,
  // Long headers ("Share Price Entry", "Real Money Entry") wrap onto multiple
  // lines and the header row auto-grows to fit, so every heading stays fully
  // readable instead of being truncated with an ellipsis when columns narrow.
  wrapHeaderText: true,
  autoHeaderHeight: true,
  cellStyle: {
    color: 'var(--st-text)',
    backgroundColor: 'var(--st-card)',
  },
};

// Grid date cells: "29 Jul 2026" instead of the raw ISO timestamp. The raw
// value stays on the row for sorting (ISO strings order correctly) - only the
// display goes through this.
// Sort comparator for close-date columns: an open trade has no close date,
// and "still open" is the freshest state there is - treat null as newest so
// the default descending sort shows open trades first, then closed trades
// most-recent-first.
export function closedAtComparator(a: string | null | undefined, b: string | null | undefined): number {
  const ts = (v: string | null | undefined) => (v ? Date.parse(v) : Number.MAX_SAFE_INTEGER);
  return ts(a) - ts(b);
}

export function formatTradeDate(value: string | null | undefined): string {
  if (!value) return '-';
  const d = new Date(value);
  return isNaN(d.getTime())
    ? '-'
    : d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
}
