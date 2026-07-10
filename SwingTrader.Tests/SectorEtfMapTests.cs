using FluentAssertions;
using SwingTrader.Infrastructure.Market;
using Xunit;

namespace SwingTrader.Tests;

// Resolution precedence for the RS benchmark: symbol override (kept for
// scoring continuity + SMH for semis) > GICS sector > SPY.
public class SectorEtfMapTests
{
    [Fact]
    public void Resolve_SymbolOverride_BeatsSector()
    {
        // NVDA's GICS sector is Information Technology (XLK), but the
        // override pins it to the tighter semiconductor benchmark.
        SectorEtfMap.Resolve("NVDA", "Information Technology").Should().Be("SMH");
    }

    [Theory]
    [InlineData("Information Technology", "XLK")]
    [InlineData("Health Care", "XLV")]
    [InlineData("Financials", "XLF")]
    [InlineData("Consumer Discretionary", "XLY")]
    [InlineData("Consumer Staples", "XLP")]
    [InlineData("Energy", "XLE")]
    [InlineData("Industrials", "XLI")]
    [InlineData("Materials", "XLB")]
    [InlineData("Utilities", "XLU")]
    [InlineData("Real Estate", "XLRE")]
    [InlineData("Communication Services", "XLC")]
    public void Resolve_AllElevenGicsSectors_MapToSpdrEtfs(string sector, string etf)
    {
        SectorEtfMap.Resolve("XXXX", sector).Should().Be(etf);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown Sector")]
    public void Resolve_NoUsableSector_FallsBackToSpy(string? sector)
    {
        SectorEtfMap.Resolve("XXXX", sector).Should().Be("SPY");
    }

    [Fact]
    public void Resolve_SectorNameCaseAndPaddingTolerant()
    {
        SectorEtfMap.Resolve("XXXX", "  health care ").Should().Be("XLV");
    }

    [Fact]
    public void AllEtfs_ContainsAllTwelveBenchmarks()
    {
        SectorEtfMap.AllEtfs().Should().BeEquivalentTo(
            ["XLK", "SMH", "XLV", "XLF", "XLY", "XLP", "XLE", "XLI", "XLB", "XLU", "XLRE", "XLC"]);
    }
}
