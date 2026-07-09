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
