using FluentAssertions;
using SwingTrader.Agents.Watchlist;
using Xunit;

namespace SwingTrader.Tests;

public class CrossSectionalRankerTests
{
    private static ScreenedCandidate C(string symbol, decimal changePct, decimal price, decimal volume) =>
        new(symbol, symbol + " Inc", price, changePct, volume, "NASDAQ");

    [Fact]
    public void StampPercentiles_StrongestNameGets100_WeakestGets0()
    {
        var candidates = new List<ScreenedCandidate>
        {
            C("WEAK", 0.5m, 10m, 100_000m),        // smallest move, smallest dollar volume
            C("MID", 3.0m, 50m, 1_000_000m),
            C("STRONG", 8.0m, 100m, 10_000_000m),  // biggest move, biggest dollar volume
        };

        var stamped = CrossSectionalRanker.StampPercentiles(candidates);

        stamped.Single(c => c.Symbol == "STRONG").SelectionPercentile.Should().Be(100m);
        stamped.Single(c => c.Symbol == "WEAK").SelectionPercentile.Should().Be(0m);
        stamped.Single(c => c.Symbol == "MID").SelectionPercentile.Should().Be(50m);
    }

    [Fact]
    public void StampPercentiles_MomentumDominatesDollarVolume()
    {
        // MOVER has the far bigger move but smaller dollar volume; LIQUID the
        // reverse. With the 60/40 blend the mover must rank higher.
        var candidates = new List<ScreenedCandidate>
        {
            C("MOVER", 9.0m, 10m, 100_000m),
            C("LIQUID", 1.0m, 500m, 50_000_000m),
            C("FILLER", 2.0m, 20m, 500_000m),
        };

        var stamped = CrossSectionalRanker.StampPercentiles(candidates);

        stamped.Single(c => c.Symbol == "MOVER").SelectionPercentile
            .Should().BeGreaterThan(stamped.Single(c => c.Symbol == "LIQUID").SelectionPercentile!.Value);
    }

    [Fact]
    public void StampPercentiles_NegativeMoves_RankByMagnitude()
    {
        // The screener's premise is "meaningful move" in either direction - a
        // -8% day out-ranks a +1% day.
        var candidates = new List<ScreenedCandidate>
        {
            C("DOWN", -8.0m, 50m, 1_000_000m),
            C("FLAT", 1.0m, 50m, 1_000_000m),
        };

        var stamped = CrossSectionalRanker.StampPercentiles(candidates);

        stamped.Single(c => c.Symbol == "DOWN").SelectionPercentile
            .Should().BeGreaterThan(stamped.Single(c => c.Symbol == "FLAT").SelectionPercentile!.Value);
    }

    [Fact]
    public void StampPercentiles_PreservesInputOrderAndCount()
    {
        var candidates = Enumerable.Range(0, 20)
            .Select(i => C($"S{i}", i * 0.5m, 10m + i, 100_000m * (i + 1)))
            .ToList();

        var stamped = CrossSectionalRanker.StampPercentiles(candidates);

        stamped.Select(c => c.Symbol).Should().Equal(candidates.Select(c => c.Symbol));
        stamped.Should().OnlyContain(c => c.SelectionPercentile >= 0m && c.SelectionPercentile <= 100m);
    }

    [Fact]
    public void StampPercentiles_FewerThanTwoCandidates_ComeBackUnstamped()
    {
        var one = new List<ScreenedCandidate> { C("ONLY", 5m, 50m, 1_000_000m) };

        CrossSectionalRanker.StampPercentiles(one).Single().SelectionPercentile.Should().BeNull();
        CrossSectionalRanker.StampPercentiles([]).Should().BeEmpty();
    }
}
