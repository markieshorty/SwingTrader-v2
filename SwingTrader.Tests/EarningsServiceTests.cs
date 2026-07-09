using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SwingTrader.Core.Enums;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Market;
using SwingTrader.Infrastructure.RateLimiting;
using Xunit;

namespace SwingTrader.Tests;

public class EarningsServiceTests
{
    private readonly IFinnhubRateLimiter _rateLimiter = Substitute.For<IFinnhubRateLimiter>();
    private readonly IFinnhubClient _finnhub = Substitute.For<IFinnhubClient>();
    private readonly EarningsConfig _config = new();

    private EarningsService CreateSut(IMemoryCache? cache = null) =>
        new(_rateLimiter, cache ?? new MemoryCache(new MemoryCacheOptions()), Options.Create(_config), NullLogger<EarningsService>.Instance);

    private void SetupNoUpcoming() =>
        _finnhub.GetEarningsCalendarAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new FinnhubEarningsCalendarResponse([]));

    private void SetupNoHistory() =>
        _finnhub.GetEarningsHistoryAsync(Arg.Any<string>(), Arg.Any<int>()).Returns(new List<FinnhubEarningsEvent>());

    [Fact]
    public async Task GetEarningsContextAsync_UpcomingWithinGate_ReturnsUpcomingEarnings()
    {
        var today = DateTime.UtcNow.Date;
        var reportDate = today.AddDays(3);
        _finnhub.GetEarningsCalendarAsync(Arg.Any<string>(), Arg.Any<string>(), "AAPL").Returns(
            new FinnhubEarningsCalendarResponse([new FinnhubEarningsEvent("AAPL", reportDate.ToString("yyyy-MM-dd"), "amc", null, null, null)]));

        var result = await CreateSut().GetEarningsContextAsync(_finnhub, "AAPL", CancellationToken.None, gateDays: 5);

        result.SetupType.Should().Be(EarningsSetupType.UpcomingEarnings);
        result.HasUpcomingEarnings.Should().BeTrue();
        result.DaysUntilEarnings.Should().Be(3);
    }

    [Fact]
    public async Task GetEarningsContextAsync_UpcomingOutsideGate_FallsThroughToHistoryCheck()
    {
        var today = DateTime.UtcNow.Date;
        var reportDate = today.AddDays(6); // beyond the default 5-day gate
        _finnhub.GetEarningsCalendarAsync(Arg.Any<string>(), Arg.Any<string>(), "AAPL").Returns(
            new FinnhubEarningsCalendarResponse([new FinnhubEarningsEvent("AAPL", reportDate.ToString("yyyy-MM-dd"), "amc", null, null, null)]));
        SetupNoHistory();

        var result = await CreateSut().GetEarningsContextAsync(_finnhub, "AAPL", CancellationToken.None, gateDays: 5);

        result.SetupType.Should().Be(EarningsSetupType.None);
        result.HasUpcomingEarnings.Should().BeFalse();
    }

    [Fact]
    public async Task GetEarningsContextAsync_NoUpcomingNoHistory_ReturnsNone()
    {
        SetupNoUpcoming();
        SetupNoHistory();

        var result = await CreateSut().GetEarningsContextAsync(_finnhub, "AAPL", CancellationToken.None);

        result.SetupType.Should().Be(EarningsSetupType.None);
    }

    [Fact]
    public async Task GetEarningsContextAsync_RecentBeatAboveThreshold_ReturnsPostEarningsBeat()
    {
        SetupNoUpcoming();
        var reportDate = DateTime.UtcNow.Date.AddDays(-1);
        _finnhub.GetEarningsHistoryAsync("AAPL", 4).Returns(new List<FinnhubEarningsEvent>
        {
            new("AAPL", reportDate.ToString("yyyy-MM-dd"), "amc", 1.0m, 1.1m, 5.0m), // +5% > 3% threshold
        });

        var result = await CreateSut().GetEarningsContextAsync(_finnhub, "AAPL", CancellationToken.None);

        result.SetupType.Should().Be(EarningsSetupType.PostEarningsBeat);
        result.BeatEstimate.Should().BeTrue();
        result.EpsSurprisePct.Should().Be(5.0m);
    }

    [Fact]
    public async Task GetEarningsContextAsync_RecentMissBelowThreshold_ReturnsPostEarningsMiss()
    {
        SetupNoUpcoming();
        var reportDate = DateTime.UtcNow.Date.AddDays(-1);
        _finnhub.GetEarningsHistoryAsync("AAPL", 4).Returns(new List<FinnhubEarningsEvent>
        {
            new("AAPL", reportDate.ToString("yyyy-MM-dd"), "amc", 1.0m, 0.8m, -6.0m),
        });

        var result = await CreateSut().GetEarningsContextAsync(_finnhub, "AAPL", CancellationToken.None);

        result.SetupType.Should().Be(EarningsSetupType.PostEarningsMiss);
        result.BeatEstimate.Should().BeFalse();
    }

    [Fact]
    public async Task GetEarningsContextAsync_RecentSurpriseWithinThreshold_ReturnsPostEarningsNeutral()
    {
        SetupNoUpcoming();
        var reportDate = DateTime.UtcNow.Date.AddDays(-1);
        _finnhub.GetEarningsHistoryAsync("AAPL", 4).Returns(new List<FinnhubEarningsEvent>
        {
            new("AAPL", reportDate.ToString("yyyy-MM-dd"), "amc", 1.0m, 1.01m, 1.0m), // within ±3%
        });

        var result = await CreateSut().GetEarningsContextAsync(_finnhub, "AAPL", CancellationToken.None);

        result.SetupType.Should().Be(EarningsSetupType.PostEarningsNeutral);
    }

    [Fact]
    public async Task GetEarningsContextAsync_EarningsOutsidePostWindow_ReturnsNone()
    {
        SetupNoUpcoming();
        // Default PostEarningsWindowDays is 3 - report 10 days ago is stale.
        var reportDate = DateTime.UtcNow.Date.AddDays(-10);
        _finnhub.GetEarningsHistoryAsync("AAPL", 4).Returns(new List<FinnhubEarningsEvent>
        {
            new("AAPL", reportDate.ToString("yyyy-MM-dd"), "amc", 1.0m, 1.5m, 50.0m),
        });

        var result = await CreateSut().GetEarningsContextAsync(_finnhub, "AAPL", CancellationToken.None);

        result.SetupType.Should().Be(EarningsSetupType.None);
    }

    [Fact]
    public async Task GetEarningsContextAsync_CalendarFetchThrows_ReturnsNoneWithoutBlowingUp()
    {
        _finnhub.GetEarningsCalendarAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns<Task<FinnhubEarningsCalendarResponse>>(_ => throw new InvalidOperationException("boom"));

        var result = await CreateSut().GetEarningsContextAsync(_finnhub, "AAPL", CancellationToken.None);

        result.SetupType.Should().Be(EarningsSetupType.None);
        result.HasUpcomingEarnings.Should().BeFalse();
    }

    [Fact]
    public async Task GetEarningsContextAsync_HistoryFetchThrows_ReturnsNoneWithoutBlowingUp()
    {
        SetupNoUpcoming();
        _finnhub.GetEarningsHistoryAsync(Arg.Any<string>(), Arg.Any<int>())
            .Returns<Task<List<FinnhubEarningsEvent>>>(_ => throw new InvalidOperationException("boom"));

        var result = await CreateSut().GetEarningsContextAsync(_finnhub, "AAPL", CancellationToken.None);

        result.SetupType.Should().Be(EarningsSetupType.None);
    }

    [Fact]
    public async Task GetEarningsContextAsync_SecondCallSameSymbolAndGate_UsesCacheNotSecondFetch()
    {
        SetupNoUpcoming();
        SetupNoHistory();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = CreateSut(cache);

        await sut.GetEarningsContextAsync(_finnhub, "AAPL", CancellationToken.None, gateDays: 5);
        await sut.GetEarningsContextAsync(_finnhub, "AAPL", CancellationToken.None, gateDays: 5);

        await _finnhub.Received(1).GetEarningsCalendarAsync(Arg.Any<string>(), Arg.Any<string>(), "AAPL");
    }
}
