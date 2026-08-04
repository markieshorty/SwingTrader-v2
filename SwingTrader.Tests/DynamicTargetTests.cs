using FluentAssertions;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Trading;
using Xunit;

namespace SwingTrader.Tests;

// Dynamic take-profit targets (1 Aug 2026): derived from the stock's own
// behaviour, clamped to a [floor, ceiling] band; every missing input falls
// back to the flat target.
public class DynamicTargetTests
{
    private const decimal Flat = 0.25m;
    private const decimal Floor = 0.05m;
    private const decimal Ceiling = 0.25m;

    [Fact]
    public void Flat_ReturnsConfiguredTargetUntouched()
    {
        DynamicTarget.ResolvePct(TargetMode.Flat, Flat, atr: 1m, price: 100m,
            nearestResistance: 105m, atrTargetMultiple: 3.5m, Floor, Ceiling)
            .Should().Be(Flat);
    }

    [Fact]
    public void AtrScaled_DerivesFromAtrOverPrice()
    {
        // 3.5 x $4 ATR on a $100 stock = 14% - inside the band.
        DynamicTarget.ResolvePct(TargetMode.AtrScaled, Flat, 4m, 100m, null, 3.5m, Floor, Ceiling)
            .Should().Be(0.14m);
    }

    [Fact]
    public void AtrScaled_CalmStock_ClampsToFloor()
    {
        // 3.5 x $0.80 on $100 = 2.8% - below the 5% floor.
        DynamicTarget.ResolvePct(TargetMode.AtrScaled, Flat, 0.8m, 100m, null, 3.5m, Floor, Ceiling)
            .Should().Be(Floor);
    }

    [Fact]
    public void AtrScaled_VolatileStock_ClampsToCeiling()
    {
        // 3.5 x $12 on $100 = 42% - above the 25% ceiling.
        DynamicTarget.ResolvePct(TargetMode.AtrScaled, Flat, 12m, 100m, null, 3.5m, Floor, Ceiling)
            .Should().Be(Ceiling);
    }

    [Fact]
    public void AtrScaled_MissingAtr_FallsBackToFlat()
    {
        DynamicTarget.ResolvePct(TargetMode.AtrScaled, Flat, null, 100m, null, 3.5m, Floor, Ceiling)
            .Should().Be(Flat);
    }

    [Fact]
    public void ResistanceCapped_CapsJustUnderResistance()
    {
        // Resistance at $110 on a $100 entry: (110*0.995 - 100)/100 = 9.45%.
        DynamicTarget.ResolvePct(TargetMode.ResistanceCapped, Flat, null, 100m, 110m, 3.5m, Floor, Ceiling)
            .Should().Be(0.0945m);
    }

    [Fact]
    public void ResistanceCapped_DistantResistance_KeepsFlatTarget()
    {
        // Resistance at $200: cap (98.5%) exceeds the flat 25% - min() keeps flat.
        DynamicTarget.ResolvePct(TargetMode.ResistanceCapped, Flat, null, 100m, 200m, 3.5m, Floor, Ceiling)
            .Should().Be(Flat);
    }

    [Fact]
    public void ResistanceCapped_VeryCloseResistance_ClampsToFloor()
    {
        // Resistance at $101: derived ~0.5% - the floor keeps the target honest.
        DynamicTarget.ResolvePct(TargetMode.ResistanceCapped, Flat, null, 100m, 101m, 3.5m, Floor, Ceiling)
            .Should().Be(Floor);
    }

    [Theory]
    [InlineData(null)]  // no resistance data
    [InlineData(99.0)]  // resistance below entry = stale
    public void ResistanceCapped_MissingOrStaleResistance_FallsBackToFlat(double? resistance)
    {
        DynamicTarget.ResolvePct(TargetMode.ResistanceCapped, Flat, null, 100m,
            (decimal?)resistance, 3.5m, Floor, Ceiling)
            .Should().Be(Flat);
    }

    [Fact]
    public void DegenerateBand_FallsBackToFlat()
    {
        DynamicTarget.ResolvePct(TargetMode.AtrScaled, Flat, 4m, 100m, null, 3.5m,
            bandFloorPct: 0.10m, bandCeilingPct: 0.10m)
            .Should().Be(Flat);
    }
}
