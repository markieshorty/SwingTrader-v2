// Front-end mirrors of backend constants that the UI needs to reproduce a
// calculation (there's no shared source between C# and TS). Keep in sync with
// SwingTrader.Core.Constants.CapitalRules.

// The hard time-exit is this multiple of a position's (soft) guide-hold —
// mirrors CapitalRules.HoldCeilingMultiple. Used by the guide + settings pages
// to show the derived ceiling.
export const HOLD_CEILING_MULTIPLE = 2.5;
