using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Market;
using Xunit;

namespace SwingTrader.Tests;

public class ForexServiceTests
{
    private readonly IExchangeRateClient _rates = Substitute.For<IExchangeRateClient>();

    private ForexService CreateSut(IMemoryCache? cache = null) =>
        new(_rates, cache ?? new MemoryCache(new MemoryCacheOptions()), NullLogger<ForexService>.Instance);

    [Fact]
    public async Task GetGbpUsdRateAsync_ValidResponse_ReturnsGbpRate()
    {
        _rates.GetLatestRatesAsync("USD", "GBP").Returns(
            new FrankfurterRatesResponse(1, "USD", "2026-01-01", new Dictionary<string, decimal> { ["GBP"] = 0.82m }));

        var rate = await CreateSut().GetGbpUsdRateAsync(CancellationToken.None);

        rate.Should().Be(0.82m);
    }

    [Fact]
    public async Task GetGbpUsdRateAsync_NullRatesDictionary_ReturnsFallback()
    {
        _rates.GetLatestRatesAsync("USD", "GBP").Returns(
            new FrankfurterRatesResponse(1, "USD", "2026-01-01", null!));

        var rate = await CreateSut().GetGbpUsdRateAsync(CancellationToken.None);

        rate.Should().Be(0.79m);
    }

    [Fact]
    public async Task GetGbpUsdRateAsync_MissingGbpKey_ReturnsFallback()
    {
        _rates.GetLatestRatesAsync("USD", "GBP").Returns(
            new FrankfurterRatesResponse(1, "USD", "2026-01-01", new Dictionary<string, decimal> { ["EUR"] = 0.9m }));

        var rate = await CreateSut().GetGbpUsdRateAsync(CancellationToken.None);

        rate.Should().Be(0.79m);
    }

    [Fact]
    public async Task GetGbpUsdRateAsync_ZeroOrNegativeRate_ReturnsFallback()
    {
        _rates.GetLatestRatesAsync("USD", "GBP").Returns(
            new FrankfurterRatesResponse(1, "USD", "2026-01-01", new Dictionary<string, decimal> { ["GBP"] = 0m }));

        var rate = await CreateSut().GetGbpUsdRateAsync(CancellationToken.None);

        rate.Should().Be(0.79m);
    }

    [Fact]
    public async Task GetGbpUsdRateAsync_ClientThrows_ReturnsFallbackWithoutBlowingUp()
    {
        _rates.GetLatestRatesAsync("USD", "GBP").Returns<Task<FrankfurterRatesResponse>>(_ => throw new HttpRequestException("network down"));

        var rate = await CreateSut().GetGbpUsdRateAsync(CancellationToken.None);

        rate.Should().Be(0.79m);
    }

    [Fact]
    public async Task GetGbpUsdRateAsync_SecondCall_UsesCacheNotSecondFetch()
    {
        _rates.GetLatestRatesAsync("USD", "GBP").Returns(
            new FrankfurterRatesResponse(1, "USD", "2026-01-01", new Dictionary<string, decimal> { ["GBP"] = 0.82m }));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = CreateSut(cache);

        await sut.GetGbpUsdRateAsync(CancellationToken.None);
        await sut.GetGbpUsdRateAsync(CancellationToken.None);

        await _rates.Received(1).GetLatestRatesAsync("USD", "GBP");
    }

    [Fact]
    public async Task GetGbpUsdRateAsync_AfterFailure_SubsequentCallsBackOffWithoutRefetching()
    {
        _rates.GetLatestRatesAsync("USD", "GBP").Returns<Task<FrankfurterRatesResponse>>(_ => throw new HttpRequestException("down"));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = CreateSut(cache);

        var first = await sut.GetGbpUsdRateAsync(CancellationToken.None);
        var second = await sut.GetGbpUsdRateAsync(CancellationToken.None);

        first.Should().Be(0.79m);
        second.Should().Be(0.79m);
        // First call attempts the fetch (and fails); the second should be
        // served from the failure-backoff cache, not a second live attempt.
        await _rates.Received(1).GetLatestRatesAsync("USD", "GBP");
    }
}
