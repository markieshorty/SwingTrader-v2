using FluentAssertions;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using Xunit;

namespace SwingTrader.Tests;

// The fingerprint is the structural tie between strategy-share evidence and
// the settings being shared - equal resolved configs MUST hash equal
// regardless of how the values were produced (request DTO vs SQL read-back,
// dictionary ordering), and any change the backtest would notice MUST change
// the hash.
public class ConfigFingerprintTests
{
    private static StrategyWeights Weights() => new()
    {
        RsiWeight = 0.23m, MacdWeight = 0.12m, VolumeWeight = 0.28m,
        SetupQualityWeight = 0.16m, RelativeStrengthWeight = 0.14m, PriceLevelWeight = 0.07m,
    };

    private static Dictionary<SetupType, HistoricSetupTactics> Tactics() => new()
    {
        [SetupType.Breakout] = new(0.05m, 0.08m, 10, 0.05m, 0.03m),
        [SetupType.OversoldRecovery] = new(0.06m, 0.09m, 8, 0.04m, 0.02m),
    };

    private static HistoricConfig Config() => new(
        Weights(), BuyThreshold: 6.0m, ExcludeBreakout: false, RegimeFilter: true,
        MaxOpenPositions: 3, MaxHoldDays: 10, ExcludedSetups: null,
        TrailingActivationPct: 0.05m, TrailingDistancePct: 0.03m,
        StopLossPct: 0.05m, TargetPct: 0.08m, SetupTactics: Tactics(),
        SimulateProbation: true, MinHoldDays: 3, MomentumHealthThreshold: 0.5m,
        PositionFraction: 0.10m, LockedCapitalPct: 0.20m);

    [Fact]
    public void IdenticalConfigs_HashEqual()
    {
        ConfigFingerprint.Compute(Config()).Should().Be(ConfigFingerprint.Compute(Config()));
    }

    [Fact]
    public void DecimalScale_DoesNotChangeHash()
    {
        // A SQL decimal(5,4) read-back gives 0.0500m where a request DTO gives
        // 0.05m - textually different, numerically equal. Both must hash equal
        // or the evidence gate breaks for every DB-sourced value.
        var scaled = Config() with { StopLossPct = 0.0500m, BuyThreshold = 6.00m, LockedCapitalPct = 0.2000m };
        ConfigFingerprint.Compute(scaled).Should().Be(ConfigFingerprint.Compute(Config()));
    }

    [Fact]
    public void TacticsDictionaryOrder_DoesNotChangeHash()
    {
        var reversed = new Dictionary<SetupType, HistoricSetupTactics>();
        foreach (var kv in Tactics().Reverse()) reversed[kv.Key] = kv.Value;
        var cfg = Config() with { SetupTactics = reversed };
        ConfigFingerprint.Compute(cfg).Should().Be(ConfigFingerprint.Compute(Config()));
    }

    [Fact]
    public void ExcludedSetups_ListOrder_DoesNotChangeHash()
    {
        var a = Config() with { ExcludedSetups = [SetupType.Breakout, SetupType.VolumeSpike] };
        var b = Config() with { ExcludedSetups = [SetupType.VolumeSpike, SetupType.Breakout] };
        ConfigFingerprint.Compute(a).Should().Be(ConfigFingerprint.Compute(b));
    }

    [Fact]
    public void LegacyExcludeBreakoutToggle_HashesLikeExplicitBreakoutList()
    {
        // The engine resolves null ExcludedSetups from the legacy toggle, so
        // the fingerprint must too - both spellings run the same simulation.
        var legacy = Config() with { ExcludeBreakout = true, ExcludedSetups = null };
        var expl = Config() with { ExcludeBreakout = false, ExcludedSetups = [SetupType.Breakout] };
        ConfigFingerprint.Compute(legacy).Should().Be(ConfigFingerprint.Compute(expl));
    }

    [Theory]
    [InlineData("weights")]
    [InlineData("buyThreshold")]
    [InlineData("regimeFilter")]
    [InlineData("stop")]
    [InlineData("positions")]
    [InlineData("tactic")]
    [InlineData("excluded")]
    public void AnyMaterialChange_ChangesHash(string change)
    {
        var baseline = ConfigFingerprint.Compute(Config());
        var cfg = change switch
        {
            "weights" => Config() with { Weights = new StrategyWeights { RsiWeight = 0.24m, MacdWeight = 0.11m, VolumeWeight = 0.28m, SetupQualityWeight = 0.16m, RelativeStrengthWeight = 0.14m, PriceLevelWeight = 0.07m } },
            "buyThreshold" => Config() with { BuyThreshold = 6.5m },
            "regimeFilter" => Config() with { RegimeFilter = false },
            "stop" => Config() with { StopLossPct = 0.06m },
            "positions" => Config() with { MaxOpenPositions = 4 },
            "tactic" => Config() with
            {
                SetupTactics = new Dictionary<SetupType, HistoricSetupTactics>(Tactics())
                {
                    [SetupType.Breakout] = new(0.055m, 0.08m, 10, 0.05m, 0.03m),
                },
            },
            "excluded" => Config() with { ExcludedSetups = [SetupType.VolumeSpike] },
            _ => throw new InvalidOperationException(),
        };
        ConfigFingerprint.Compute(cfg).Should().NotBe(baseline);
    }

    [Fact]
    public void RegimeBooks_ChangeTheHash_AndAreOrderInsensitive()
    {
        // Mixed-frame evidence (20 Jul 2026): a Bull-book sizing change must
        // invalidate evidence; dictionary ordering must not.
        var books = new Dictionary<MarketRegime, RegimeEnvelope>
        {
            [MarketRegime.Bull] = new(false, 3, 0.10m, 0.20m),
            [MarketRegime.Neutral] = new(false, 3, 0.10m, 0.20m),
            [MarketRegime.Bear] = new(true, 2, 0.08m, 0.40m),
            [MarketRegime.Crisis] = new(true, 1, 0.05m, 0.60m),
        };
        var mixed = Config() with { RegimeBooks = books };
        var single = Config();

        ConfigFingerprint.Compute(mixed).Should().NotBe(ConfigFingerprint.Compute(single));

        var reversed = new Dictionary<MarketRegime, RegimeEnvelope>();
        foreach (var kv in books.Reverse()) reversed[kv.Key] = kv.Value;
        ConfigFingerprint.Compute(Config() with { RegimeBooks = reversed })
            .Should().Be(ConfigFingerprint.Compute(mixed));

        var bullResized = new Dictionary<MarketRegime, RegimeEnvelope>(books)
        {
            [MarketRegime.Bull] = new(false, 3, 0.15m, 0.20m),
        };
        ConfigFingerprint.Compute(Config() with { RegimeBooks = bullResized })
            .Should().NotBe(ConfigFingerprint.Compute(mixed));
    }

    [Fact]
    public void WithLiveRegimeBooks_ParityBetweenBookSourcesAndDirectEnvelopes()
    {
        // The consumer stamps from live books via WithLiveRegimeBooks; the
        // share service does the same - both must hash identically to a
        // hand-built envelope map with the same values.
        var books = new Dictionary<MarketRegime, AccountRiskProfile>
        {
            [MarketRegime.Bull] = new() { Regime = MarketRegime.Bull, AutopauseTrading = false, MaxOpenPositions = 3, FlatPositionPct = 0.10m, LockedCapitalPct = 0.20m },
            [MarketRegime.Neutral] = new() { Regime = MarketRegime.Neutral, AutopauseTrading = false, MaxOpenPositions = 3, FlatPositionPct = 0.10m, LockedCapitalPct = 0.20m },
            [MarketRegime.Bear] = new() { Regime = MarketRegime.Bear, AutopauseTrading = true, MaxOpenPositions = 2, FlatPositionPct = 0.08m, LockedCapitalPct = 0.40m },
            [MarketRegime.Crisis] = new() { Regime = MarketRegime.Crisis, AutopauseTrading = true, MaxOpenPositions = 1, FlatPositionPct = 0.05m, LockedCapitalPct = 0.60m },
            [MarketRegime.Default] = new() { Regime = MarketRegime.Default, MaxOpenPositions = 9 }, // must be excluded
        };
        var viaFactory = BacktestConfigFactory.WithLiveRegimeBooks(Config(), books);

        var direct = Config() with
        {
            RegimeBooks = new Dictionary<MarketRegime, RegimeEnvelope>
            {
                [MarketRegime.Bull] = new(false, 3, 0.10m, 0.20m),
                [MarketRegime.Neutral] = new(false, 3, 0.10m, 0.20m),
                [MarketRegime.Bear] = new(true, 2, 0.08m, 0.40m),
                [MarketRegime.Crisis] = new(true, 1, 0.05m, 0.60m),
            },
        };

        ConfigFingerprint.Compute(viaFactory).Should().Be(ConfigFingerprint.Compute(direct));
    }

    [Fact]
    public void FactoryResolvedConfig_MatchesAcrossEquivalentInputs()
    {
        // The consumer resolves from a request DTO; the share service resolves
        // from live entities. Same underlying values through the SAME factory
        // must fingerprint identically (parity through BacktestConfigFactory).
        var profile = new AccountRiskProfile
        {
            AccountId = 1, Regime = MarketRegime.Neutral,
            MaxOpenPositions = 3, MaxHoldDays = 10,
            TrailingActivationPct = 0.05, TrailingDistancePct = 0.03,
            StopLossPct = 0.05m, TargetPct = 0.08m,
            MinHoldDays = 3, MomentumHealthThreshold = 0.5m,
            FlatPositionPct = 0.10m, LockedCapitalPct = 0.20m,
        };
        var w = new HistoricBacktestWeights(0.23m, 0.12m, 0.28m, 0.16m, 0.14m, 0.07m);
        var tactics = Tactics();

        var fromNullRules = BacktestConfigFactory.ToConfig(w, 6.0m, false, true, profile, tactics, rules: null);
        var fromEmptyExclusionEquivalent = BacktestConfigFactory.ToConfig(
            w, 6.00m, false, true, profile, tactics, new HistoricTradingRules(ExcludedSetups: []));

        ConfigFingerprint.Compute(fromNullRules)
            .Should().Be(ConfigFingerprint.Compute(fromEmptyExclusionEquivalent));
    }
}
