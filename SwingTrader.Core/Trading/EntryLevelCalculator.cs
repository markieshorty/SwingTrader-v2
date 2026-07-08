using SwingTrader.Core.Enums;

namespace SwingTrader.Core.Trading;

// Shared by ReportGenerationService (which computes StockSignal.CalculatedStopLoss/
// CalculatedTarget for display, off whatever price is live when Report runs ~6:30 ET)
// and ExecutionService (which used to just trust those hours-old absolute price
// levels when actually placing the order). The percentage table itself only
// depends on SetupType/ConvictionScore, not on any particular price snapshot -
// so both callers get the same distances, each applied to their own live price.
public static class EntryLevelCalculator
{
    public static (decimal StopLoss, decimal Target) Calculate(SetupType setupType, decimal convictionScore, decimal price)
    {
        var stopLoss = setupType switch
        {
            SetupType.Breakout => price * 0.940m,
            SetupType.VolumeSpike => price * 0.960m,
            _ => price * 0.950m
        };

        var target = convictionScore switch
        {
            >= 9.0m => price * 1.120m,
            >= 8.0m => price * 1.100m,
            _ => price * 1.080m
        };

        return (Math.Round(stopLoss, 2), Math.Round(target, 2));
    }
}
