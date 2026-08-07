using FluentAssertions;
using SwingTrader.Agents.Research;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Services;
using Xunit;

namespace SwingTrader.Tests;

// The detector was copy-pasted into the live pipeline, the historic backtester
// and the local console tool, each carrying a "keep in sync" comment. They did
// not. On 7 Aug 2026 the local tool was still detecting OversoldRecovery with
// NO 4-bar recovery confirmation - the OversoldRecoveryLoose variant retired on
// 4 Aug because the survivorship-free dataset showed its edge was an artefact
// (+1.64% on survivors, -0.19% over 409 trades on the full universe). Every
// local backtest had been buying still-falling knives under the right label.
public class SetupDetectorTests
{
    private static IndicatorResult Ind(
        decimal? rsi = null, decimal? macdHist = null, decimal? bbUpper = null,
        decimal? bbLower = null, decimal? bbMid = null, decimal? ema9 = null,
        decimal? ema21 = null, decimal? volumeRatio = null) =>
        new(rsi, null, null, macdHist, bbUpper, bbLower, bbMid, ema9, ema21, volumeRatio);

    private static List<StockCandle> Closes(params decimal[] closes) =>
        closes.Select((c, i) => new StockCandle
        {
            Symbol = "T", Timestamp = new DateTime(2026, 1, 1).AddDays(i),
            Open = c, High = c, Low = c, Close = c, Volume = 1_000_000,
        }).ToList();

    [Fact]
    public void OversoldRecovery_RequiresThePriceToBeAboveFourBarsAgo()
    {
        // THE regression. A still-falling oversold name must NOT classify as
        // OversoldRecovery - that is the retired loose variant.
        var ind = Ind(rsi: 28m, bbLower: 90m);

        var falling = SetupDetector.Detect(ind, Closes(120m, 115m, 110m, 105m, 100m));

        falling.Should().NotBe(SetupType.OversoldRecovery,
            "an unconfirmed dip is the retired OversoldRecoveryLoose - the 4-bar recovery leg is the falling-knife guard");
    }

    [Fact]
    public void OversoldRecovery_FiresOnceTheBounceHasBegun()
    {
        var ind = Ind(rsi: 28m, bbLower: 90m);

        // Four bars ago the close was 96; now 100 - the bounce has started.
        var recovering = SetupDetector.Detect(ind, Closes(105m, 96m, 94m, 97m, 100m));

        recovering.Should().Be(SetupType.OversoldRecovery);
    }

    [Fact]
    public void OversoldRecovery_NeedsFourBarsOfHistoryBeforeItCanConfirm()
    {
        SetupDetector.Detect(Ind(rsi: 28m, bbLower: 90m), Closes(98m, 99m, 100m))
            .Should().NotBe(SetupType.OversoldRecovery);
    }

    [Theory]
    // Order matters - first match wins, so these pin the precedence too.
    [InlineData(105.0, 1.6, 0.5, SetupType.Breakout)]           // through the upper band on volume
    [InlineData(99.0, 1.2, 0.5, SetupType.MomentumContinuation)] // mid-RSI, rising EMAs
    public void ExpansionSetups_KeepTheirPrecedence(
        double price, double volumeRatio, double macdHist, SetupType expected)
    {
        var ind = Ind(rsi: 55m, macdHist: (decimal)macdHist, bbUpper: 100m, bbMid: 95m,
            ema9: 101m, ema21: 99m, volumeRatio: (decimal)volumeRatio);

        SetupDetector.Detect(ind, Closes(90m, 92m, 94m, 96m, (decimal)price)).Should().Be(expected);
    }

    [Fact]
    public void TrendFollowing_NeedsNoVolumeAndNoMove()
    {
        // The setup the old screener starved: no volume term, no move term.
        var ind = Ind(rsi: 58m, bbMid: 95m, ema9: 101m, ema21: 99m, volumeRatio: 0.8m);

        SetupDetector.Detect(ind, Closes(99m, 99.3m, 99.6m, 99.8m, 100m))
            .Should().Be(SetupType.TrendFollowing);
    }

    [Fact]
    public void NothingMatching_IsUnknown()
    {
        // Unknown is a real, TRADEABLE classification - the setup filter is a
        // blocklist, so these are taken unless explicitly excluded.
        SetupDetector.Detect(Ind(rsi: 45m), Closes(100m, 100m, 100m, 100m, 100m))
            .Should().Be(SetupType.Unknown);
    }

    [Fact]
    public void NoCandles_IsUnknownRatherThanAThrow()
    {
        SetupDetector.Detect(Ind(rsi: 20m), []).Should().Be(SetupType.Unknown);
    }
}
