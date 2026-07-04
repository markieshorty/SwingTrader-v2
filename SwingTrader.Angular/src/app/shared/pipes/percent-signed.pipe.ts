import { Pipe, PipeTransform } from '@angular/core';

@Pipe({ name: 'percentSigned', standalone: true })
export class PercentSignedPipe implements PipeTransform {
  transform(value: number | null | undefined, decimals = 1): string {
    if (value === null || value === undefined || Number.isNaN(value)) {
      return 'n/a';
    }
    const sign = value > 0 ? '+' : '';
    return `${sign}${value.toFixed(decimals)}%`;
  }
}
