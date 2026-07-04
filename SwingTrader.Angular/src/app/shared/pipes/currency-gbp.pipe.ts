import { Pipe, PipeTransform } from '@angular/core';

@Pipe({ name: 'currencyGbp', standalone: true })
export class CurrencyGbpPipe implements PipeTransform {
  transform(value: number | null | undefined): string {
    if (value === null || value === undefined || Number.isNaN(value)) {
      return '£0.00';
    }
    const sign = value < 0 ? '-' : '';
    return `${sign}£${Math.abs(value).toLocaleString('en-GB', {
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    })}`;
  }
}
