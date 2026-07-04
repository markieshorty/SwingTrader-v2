import { PercentSignedPipe } from './percent-signed.pipe';

describe('PercentSignedPipe', () => {
  const pipe = new PercentSignedPipe();

  it('adds a plus sign for positive values', () => {
    expect(pipe.transform(4.2)).toBe('+4.2%');
  });

  it('keeps the minus sign for negative values', () => {
    expect(pipe.transform(-1.8)).toBe('-1.8%');
  });

  it('handles null/undefined', () => {
    expect(pipe.transform(null)).toBe('n/a');
    expect(pipe.transform(undefined)).toBe('n/a');
  });
});
