import { CommonModule } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { AgGridAngular } from 'ag-grid-angular';
import { ColDef } from 'ag-grid-community';
import { defaultColDef } from '../../../shared/ag-grid-defaults';
import { TradeDto } from '../../../core/models/dtos';

@Component({
  selector: 'app-trade-history',
  standalone: true,
  imports: [CommonModule, AgGridAngular],
  template: `
    <div class="summary-row">
      <span>Win rate: {{ winRate() | number: '1.0-1' }}%</span>
      <span>Avg return: {{ avgReturn() | number: '1.1-1' }}%</span>
      <span>Trades: {{ trades().length }}</span>
    </div>
    <ag-grid-angular
      class="ag-theme-alpine-dark"
      style="width: 100%; height: 400px;"
      [rowData]="trades()"
      [columnDefs]="columnDefs"
      [defaultColDef]="defaultColDef"
      [getRowClass]="rowClass"
    />
  `,
  styles: [
    `
      .summary-row {
        display: flex;
        gap: 24px;
        margin-bottom: 12px;
        font-size: 13px;
        color: var(--st-muted);
      }
    `,
  ],
})
export class TradeHistoryComponent {
  trades = input.required<TradeDto[]>();

  defaultColDef = defaultColDef;

  columnDefs: ColDef<TradeDto>[] = [
    {
      colId: 'symbol',
      headerName: 'Symbol',
      sortable: true,
      filter: true,
      // "Apple Inc (AAPL)" - full name then ticker; bare ticker for older
      // trades with no stored company name.
      valueGetter: (p) => (p.data?.companyName ? `${p.data.companyName} (${p.data.symbol})` : (p.data?.symbol ?? '')),
    },
    // Share price is the instrument's own per-share price (USD for US-listed
    // stocks) - Real Money is the actual £ that left/entered the account for
    // that leg, from T212's own reported fill (post FX-conversion/fees), not
    // derived from the share price. See MonitorService.ReconcileOrderFillsAsync.
    { field: 'entryPrice', headerName: 'Share Price Entry', valueFormatter: (p) => `$${p.value?.toFixed(2)}` },
    {
      field: 'exitPrice',
      headerName: 'Share Price Exit',
      valueFormatter: (p) => (p.value != null ? `$${p.value.toFixed(2)}` : '-'),
    },
    {
      field: 'entryValueGbp',
      headerName: 'Real Money Entry',
      valueFormatter: (p) => (p.value != null ? `£${p.value.toFixed(2)}` : '-'),
    },
    {
      field: 'exitValueGbp',
      headerName: 'Real Money Exit',
      valueFormatter: (p) => (p.value != null ? `£${p.value.toFixed(2)}` : '-'),
    },
    {
      field: 'feesGbp',
      headerName: 'Fees',
      valueFormatter: (p) => (p.value != null ? `£${p.value.toFixed(2)}` : '-'),
    },
    {
      field: 'realizedPnl',
      headerName: 'P&L',
      valueFormatter: (p) => (p.value != null ? `£${p.value.toFixed(2)}` : '-'),
    },
    {
      field: 'realizedPnlPercent',
      headerName: 'Return %',
      valueFormatter: (p) => (p.value != null ? `${p.value.toFixed(1)}%` : '-'),
    },
    { field: 'daysHeld', headerName: 'Hold Days' },
    { field: 'setupType', headerName: 'Setup Type' },
    { field: 'convictionScoreAtEntry', headerName: 'Conviction' },
    {
      field: 'forwardScoreAtEntry',
      headerName: 'Forward Score',
      valueFormatter: (p) => (p.value != null ? p.value.toFixed(2) : '-'),
    },
    {
      field: 'sizeMultiplier',
      headerName: 'Size ×',
      valueFormatter: (p) => (p.value != null ? `×${p.value.toFixed(2)}` : '-'),
    },
    {
      colId: 'contract',
      headerName: 'Contract',
      // The frozen-at-entry rules the trade ran under; '-' = pre-freeze trade
      // that used the live profile throughout.
      valueGetter: (p) => {
        const t = p.data;
        if (!t) return '';
        const parts: string[] = [];
        if (t.minHoldDaysAtEntry != null) parts.push(`probation ${t.minHoldDaysAtEntry}d`);
        if (t.maxHoldDaysAtEntry != null) parts.push(`guide ${t.maxHoldDaysAtEntry}d`);
        if (t.trailingActivationPctAtEntry != null && t.trailingDistancePctAtEntry != null)
          parts.push(`trail ${t.trailingDistancePctAtEntry}% after +${t.trailingActivationPctAtEntry}%`);
        return parts.length > 0 ? parts.join(' · ') : '-';
      },
    },
    { field: 'marketRegimeAtEntry', headerName: 'Regime' },
    { field: 'status', headerName: 'Outcome' },
  ];

  winRate = computed(() => {
    const t = this.trades();
    if (t.length === 0) return 0;
    const wins = t.filter((x) => (x.realizedPnl ?? 0) > 0).length;
    return (wins / t.length) * 100;
  });

  avgReturn = computed(() => {
    const t = this.trades();
    if (t.length === 0) return 0;
    const total = t.reduce((sum, x) => sum + (x.realizedPnlPercent ?? 0), 0);
    return total / t.length;
  });

  rowClass(params: { data?: TradeDto }): string {
    if (!params.data?.realizedPnl) return '';
    return params.data.realizedPnl >= 0 ? 'row-win' : 'row-loss';
  }
}
