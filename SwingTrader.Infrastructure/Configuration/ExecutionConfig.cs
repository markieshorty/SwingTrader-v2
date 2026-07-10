namespace SwingTrader.Infrastructure.Configuration;

public class ExecutionConfig
{
    public const string SectionName = "Execution";

    public int RunHourEastern { get; set; } = 9;
    public int RunMinuteEastern { get; set; } = 20;
    public int MaxOrdersPerDay { get; set; } = 5;
    public int DelayBetweenOrdersSeconds { get; set; } = 2;
    public decimal CashBufferPct { get; set; } = 0.02m;

    // ── Intraday entry confirmation (Phase 3, IEX) ────────────────────────
    // Master switch - default OFF so behaviour is byte-for-byte unchanged
    // until deliberately enabled (Demo first).
    public bool IntradayConfirmationEnabled { get; set; } = false;
    // Reject when the session open/latest price is more than this % above
    // the price the signal was scored at (the setup already priced in).
    public decimal MaxGapUpPct { get; set; } = 4.0m;
    // Reject when cumulative IEX session volume is below this fraction of the
    // symbol's 20-day average IEX daily volume (same-source baseline - IEX is
    // only a few % of consolidated volume, so consolidated averages must
    // never be used as the denominator).
    public decimal MinSessionVolumeRatio { get; set; } = 0.15m;
    // The volume gate stays closed before this ET time - too little session
    // data to call anything dead-on-arrival.
    public int VolumeGateEarliestHourEt { get; set; } = 9;
    public int VolumeGateEarliestMinuteEt { get; set; } = 50;
}
