using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Agents.Research;

// THE setup detector. Single definition, called by the live research pipeline,
// the historic backtester and the local backtest tool.
//
// It used to be copy-pasted into all three, each carrying a "keep in sync"
// comment. They did not stay in sync: on 7 Aug 2026 the local tool was found
// still detecting OversoldRecovery WITHOUT the 4-bar recovery confirmation -
// i.e. the OversoldRecoveryLoose variant that was retired on 4 Aug precisely
// because the survivorship-free dataset showed its edge was an artefact
// (+1.64% on the survivors, -0.19% over 409 trades on the full universe: the
// unconfirmed dips were where the corpses lived). Every local backtest since
// had been buying still-falling knives and labelling them OversoldRecovery.
//
// Order matters - first match wins, so a name qualifying for several setups
// takes the earliest listed.
public static class SetupDetector
{
    public static SetupType Detect(IndicatorResult ind, IReadOnlyList<StockCandle> candles)
    {
        if (candles.Count == 0) return SetupType.Unknown;
        var price = candles[^1].Close;

        // Oversold requires the 4-bar recovery confirmation (price higher than
        // four bars ago - the bounce has begun). Without it this is the retired
        // "loose" variant; the confirmation IS the falling-knife guard and must
        // never be dropped for convenience.
        if (ind.Rsi14 < 35 && ind.BollingerLower.HasValue && price > ind.BollingerLower.Value
            && candles.Count >= 4 && price > candles[^4].Close)
            return SetupType.OversoldRecovery;

        if (ind.BollingerUpper.HasValue && price > ind.BollingerUpper.Value
            && ind.VolumeRatio > 1.5m && ind.MacdHistogram > 0)
            return SetupType.Breakout;

        if (ind.Rsi14 >= 50 && ind.Rsi14 <= 65
            && ind.Ema9.HasValue && ind.Ema21.HasValue && ind.Ema9 > ind.Ema21
            && ind.MacdHistogram > 0 && ind.VolumeRatio > 1.0m)
            return SetupType.MomentumContinuation;

        if (ind.VolumeRatio > 2.0m && candles.Count >= 2)
        {
            var prevClose = candles[^2].Close;
            if (prevClose > 0 && (price - prevClose) / prevClose * 100 > 1.5m)
                return SetupType.VolumeSpike;
        }

        if (ind.Ema9.HasValue && ind.Ema21.HasValue && ind.Ema9 > ind.Ema21
            && ind.Rsi14 > 50
            && ind.BollingerMid.HasValue && price > ind.BollingerMid.Value)
            return SetupType.TrendFollowing;

        return SetupType.Unknown;
    }
}
