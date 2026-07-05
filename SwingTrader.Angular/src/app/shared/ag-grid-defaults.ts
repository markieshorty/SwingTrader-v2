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
  minWidth: 100,
  cellStyle: {
    color: 'var(--st-text)',
    backgroundColor: 'var(--st-card)',
  },
};
