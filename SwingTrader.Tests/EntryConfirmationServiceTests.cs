using System.Text.Json;
using FluentAssertions;
using SwingTrader.Agents.Execution;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using Xunit;

namespace SwingTrader.Tests;

// The pure intraday entry gate (Phase 3): gap-up boundary, instant-stop-out
// rejection, the time-gated volume floor with its same-source IEX baseline,
// and the fail-open Unavailable paths. Plus DTO deserialization pinned to a
// real captured Tiingo IEX payload.
public class EntryConfirmationServiceTests
{
    private static readonly ExecutionConfig Cfg = new(); // production defaults

    // A 9:55 ET clock: past the 9:50 volume-gate opening.
    private static readonly DateTime LateMorning = new(2026, 7, 10, 9, 55, 0);
    // A 9:35 ET clock: before the gate opens.
    private static readonly DateTime EarlyMorning = new(2026, 7, 10, 9, 35, 0);

    private static TiingoIexPrice Bar(decimal price, decimal volume = 10_000m) =>
        new(new DateTime(2026, 7, 10, 13, 30, 0, DateTimeKind.Utc), price, price, price, price, volume);

    private static List<TiingoIexPrice> Session(decimal open, decimal latest, decimal barVolume = 10_000m) =>
        [Bar(open, barVolume), Bar((open + latest) / 2, barVolume), Bar(latest, barVolume)];

    [Theory]
    [InlineData(104.0, true)]   // exactly at the 4% threshold — passes (gate is "more than")
    [InlineData(104.1, false)]  // just above — rejected
    [InlineData(103.0, true)]   // comfortably below — passes
    public void GapUpGate_BoundaryBehaviour(double latestPrice, bool passes)
    {
        var result = EntryConfirmationService.Evaluate(
            Session(100m, (decimal)latestPrice), scoredPrice: 100m, stopLossPrice: 90m,
            avgIexDailyVolume: null, EarlyMorning, Cfg);

        (result.Verdict == EntryConfirmationVerdict.Confirmed).Should().Be(passes);
        if (!passes) result.Reason.Should().Contain("gapped");
    }

    [Fact]
    public void GapUpGate_JudgedOnSessionOpenToo_NotJustLatest()
    {
        // Opened +6% then faded to +5%: still rejected — the worse of open
        // and latest is the reference.
        var result = EntryConfirmationService.Evaluate(
            Session(106m, 105m), 100m, 90m, null, EarlyMorning, Cfg);

        result.Verdict.Should().Be(EntryConfirmationVerdict.Rejected);
    }

    [Fact]
    public void PriceBelowStop_Rejected_AsInstantStopOut()
    {
        var result = EntryConfirmationService.Evaluate(
            Session(100m, 94m), 100m, stopLossPrice: 95m, null, EarlyMorning, Cfg);

        result.Verdict.Should().Be(EntryConfirmationVerdict.Rejected);
        result.Reason.Should().Contain("stop");
    }

    [Fact]
    public void VolumeGate_BeforeEarliestTime_IsSkipped()
    {
        // Session volume is 1% of a typical IEX day — would fail the 15%
        // floor — but at 9:35 ET the gate hasn't opened yet.
        var result = EntryConfirmationService.Evaluate(
            Session(100m, 101m, barVolume: 100m), 100m, 90m,
            avgIexDailyVolume: 30_000m, EarlyMorning, Cfg);

        result.Verdict.Should().Be(EntryConfirmationVerdict.Confirmed);
    }

    [Theory]
    [InlineData(1_000, false)]   // 3 bars x 1000 = 3000 = 10% of 30k — below the 15% floor, rejected
    [InlineData(1_500, true)]    // 4500 = exactly 15% — at-ratio passes (gate is "<")
    [InlineData(5_000, true)]    // healthy session
    public void VolumeGate_AfterEarliestTime_EnforcesSameSourceFloor(double barVolume, bool passes)
    {
        var result = EntryConfirmationService.Evaluate(
            Session(100m, 101m, (decimal)barVolume), 100m, 90m,
            avgIexDailyVolume: 30_000m, LateMorning, Cfg);

        (result.Verdict == EntryConfirmationVerdict.Confirmed).Should().Be(passes);
        if (!passes) result.Reason.Should().Contain("volume");
    }

    [Fact]
    public void NoBaseline_VolumeGateSkipped_OtherGatesStillApply()
    {
        EntryConfirmationService.Evaluate(
                Session(100m, 101m, 1m), 100m, 90m, avgIexDailyVolume: null, LateMorning, Cfg)
            .Verdict.Should().Be(EntryConfirmationVerdict.Confirmed);
    }

    [Fact]
    public void EmptySession_Unavailable_NeverRejected()
    {
        var result = EntryConfirmationService.Evaluate([], 100m, 90m, null, LateMorning, Cfg);

        result.Verdict.Should().Be(EntryConfirmationVerdict.Unavailable);
    }

    [Fact]
    public void AverageDailyVolume_SumsPerDay_ExcludesToday_AveragesRest()
    {
        TiingoIexPrice HourBar(int day, decimal volume) =>
            new(new DateTime(2026, 7, day, 14, 0, 0, DateTimeKind.Utc), null, null, null, null, volume);

        List<TiingoIexPrice> bars =
        [
            HourBar(8, 1_000m), HourBar(8, 2_000m),   // day 8: 3000
            HourBar(9, 4_000m), HourBar(9, 1_000m),   // day 9: 5000
            HourBar(10, 50m),                          // today: excluded
        ];

        EntryConfirmationService.AverageDailyVolume(bars, excludeDate: new DateOnly(2026, 7, 10))
            .Should().Be(4_000m); // (3000 + 5000) / 2
    }

    [Fact]
    public void TiingoIexPrice_DeserializesRealCapturedPayload()
    {
        // Verbatim from a real Power-plan response, 2026-07-10 (AAPL, 5min).
        const string payload = """
            [{"date":"2026-07-10T13:30:00.000Z","open":314.72,"high":315.565,"low":313.23,"close":315.48,"volume":43916.0},
             {"date":"2026-07-10T13:35:00.000Z","open":315.45,"high":316.38,"low":315.23,"close":315.415,"volume":47929.0}]
            """;

        var bars = JsonSerializer.Deserialize<List<TiingoIexPrice>>(payload)!;

        bars.Should().HaveCount(2);
        bars[0].Date.Should().Be(new DateTime(2026, 7, 10, 13, 30, 0, DateTimeKind.Utc));
        bars[0].Open.Should().Be(314.72m);
        bars[0].Volume.Should().Be(43916.0m);
        bars[1].Close.Should().Be(315.415m);
    }
}
