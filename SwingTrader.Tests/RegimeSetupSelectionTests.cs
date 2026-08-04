using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Agents.Sharing;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// Regime-conditional setup selection P1 (docs/regime-setups-plan): per-book
// excluded setups flow book -> envelope -> fingerprint. Live wiring is P2.
public class RegimeSetupSelectionTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("VolumeSpike", new[] { SetupType.VolumeSpike })]
    [InlineData("VolumeSpike, Breakout", new[] { SetupType.VolumeSpike, SetupType.Breakout })]
    [InlineData("volumespike,NotASetup,volumespike", new[] { SetupType.VolumeSpike })] // tolerant: case, dups, typos
    public void ParseSetupCsv_IsTolerant(string? csv, SetupType[]? expected)
    {
        var result = BacktestConfigFactory.ParseSetupCsv(csv);
        if (expected is null) result.Should().BeNull();
        else result.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void WithLiveRegimeBooks_CarriesPerBookExclusions()
    {
        var cfg = new HistoricConfig(new StrategyWeights());
        var books = new Dictionary<MarketRegime, AccountRiskProfile>
        {
            [MarketRegime.Bull] = new() { AccountId = 1, Regime = MarketRegime.Bull, DisabledSetupsCsv = "OversoldRecovery" },
            [MarketRegime.Bear] = new() { AccountId = 1, Regime = MarketRegime.Bear },
        };

        var mixed = BacktestConfigFactory.WithLiveRegimeBooks(cfg, books);

        mixed.RegimeBooks![MarketRegime.Bull].ExcludedSetups.Should().BeEquivalentTo([SetupType.OversoldRecovery]);
        mixed.RegimeBooks[MarketRegime.Bear].ExcludedSetups.Should().BeNull();
    }

    [Fact]
    public void Fingerprint_DistinguishesRegimeSetupExclusions()
    {
        var cfg = new HistoricConfig(new StrategyWeights());
        var plain = BacktestConfigFactory.WithLiveRegimeBooks(cfg, new Dictionary<MarketRegime, AccountRiskProfile>
        {
            [MarketRegime.Bull] = new() { AccountId = 1, Regime = MarketRegime.Bull },
        });
        var conditional = BacktestConfigFactory.WithLiveRegimeBooks(cfg, new Dictionary<MarketRegime, AccountRiskProfile>
        {
            [MarketRegime.Bull] = new() { AccountId = 1, Regime = MarketRegime.Bull, DisabledSetupsCsv = "OversoldRecovery" },
        });

        ConfigFingerprint.Compute(conditional).Should().NotBe(ConfigFingerprint.Compute(plain));
    }

    [Fact]
    public void Fingerprint_ExclusionOrderDoesNotMatter()
    {
        var cfg = new HistoricConfig(new StrategyWeights());
        HistoricConfig Build(string csv) => BacktestConfigFactory.WithLiveRegimeBooks(cfg,
            new Dictionary<MarketRegime, AccountRiskProfile>
            {
                [MarketRegime.Bull] = new() { AccountId = 1, Regime = MarketRegime.Bull, DisabledSetupsCsv = csv },
            });

        ConfigFingerprint.Compute(Build("VolumeSpike,Breakout"))
            .Should().Be(ConfigFingerprint.Compute(Build("Breakout, VolumeSpike")));
    }
}

// Strategy-share completeness (Mark, 4 Aug 2026: "make sure send strategy to
// users still works"): every dial added since the snapshot shape froze must
// round-trip build -> apply, and pre-existing snapshot JSON without the new
// fields must deserialize to feature-off defaults.
public class SnapshotDialCompletenessTests
{
    [Fact]
    public void SnapshotRiskBook_NewDialDefaults_AreFeatureOff()
    {
        // Simulates deserializing an OLD stored snapshot (fields absent).
        var book = new SnapshotRiskBook(
            "Neutral", true, false, 0.2m, 2, 0.10m, 10, 0.05, 0.03, 5, 3, 0.35m,
            0.05m, 0.08m, "Flat", 0.5m, 0.8m, 2.5m);

        book.SizingStyle.Should().Be("FlatPercent");
        book.TargetMode.Should().Be("Flat");
        book.MaxConvictionForBuy.Should().Be(0m);
        book.RiskPerTradePct.Should().Be(0.01m);
        book.AtrStopMultiple.Should().Be(2.0m);
        book.AtrTargetMultiple.Should().Be(3.5m);
        book.TargetBandFloorPct.Should().Be(0.05m);
        book.TargetBandCeilingPct.Should().Be(0.25m);
        book.DisabledSetupsCsv.Should().BeNull();
    }
}
