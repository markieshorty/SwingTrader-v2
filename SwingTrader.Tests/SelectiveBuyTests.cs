using FluentAssertions;
using SwingTrader.Agents.Monitor;
using Xunit;

namespace SwingTrader.Tests;

// docs/selective-buy-plan. Cadentic is narrowed to one job - choose a
// speculative stock, buy it, sell it - with the forward score as the only
// selector and one open position at a time.
public class SelectiveBuyTests
{
    // The ignored-tickers list replaces the sleeve architecture's only
    // load-bearing job: stopping the owner's own ETF holdings, sitting in the
    // same broker account, being adopted as swing positions or flagged as
    // drift every monitor cycle.
    [Theory]
    [InlineData("VUAGl_EQ", "VUAG", true)]      // broker suffixes must still match
    [InlineData("VUAG", "VUAG", true)]
    [InlineData("vuagl_eq", "VUAG", true)]      // case-insensitive
    [InlineData("AAPL_US_EQ", "VUAG,VWRP", false)]
    [InlineData("VWRPl_EQ", "VUAG, VWRP", true)] // whitespace tolerated
    [InlineData("AAPL_US_EQ", null, false)]      // nothing configured = ignore nothing
    [InlineData("AAPL_US_EQ", "", false)]
    [InlineData(null, "VUAG", false)]
    public void IsIgnoredTicker_PrefixMatchesCaseInsensitively(
        string? ticker, string? ignoredCsv, bool expected) =>
        MonitorService.IsIgnoredTicker(ticker, ignoredCsv).Should().Be(expected);

    [Fact]
    public void IsIgnoredTicker_DoesNotMatchOnASuffix()
    {
        // "VUAG" must not swallow an unrelated symbol that merely ends the
        // same way - prefix only.
        MonitorService.IsIgnoredTicker("XVUAG_EQ", "VUAG").Should().BeFalse();
    }
}
