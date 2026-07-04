import { CurrencyGbpPipe } from './currency-gbp.pipe';

describe('CurrencyGbpPipe', () => {
  const pipe = new CurrencyGbpPipe();

  it('formats positive numbers', () => {
    expect(pipe.transform(1234.5)).toBe('£1,234.50');
  });

  it('formats negative numbers with a leading minus', () => {
    expect(pipe.transform(-42)).toBe('-£42.00');
  });

  it('handles null/undefined', () => {
    expect(pipe.transform(null)).toBe('£0.00');
    expect(pipe.transform(undefined)).toBe('£0.00');
  });
});
