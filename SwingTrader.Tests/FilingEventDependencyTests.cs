using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SwingTrader.Agents.FilingEvents;
using SwingTrader.Core.Interfaces;
using SwingTrader.Infrastructure.Configuration;
using SwingTrader.Infrastructure.Edgar;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.RateLimiting;
using Xunit;

namespace SwingTrader.Tests;

// 6-7 Aug 2026: five candlesync-jobs messages dead-lettered with NO log line
// anywhere, because a constructor dependency of the queue consumer could not
// be activated - and activation failures happen inside the Functions host,
// before user code, so nothing was written. These tests pin that the
// filing-events object graph is constructible from its registered
// dependencies, so the same class of failure fails a build instead of a
// production queue.
public class FilingEventDependencyTests
{
    [Fact]
    public void FilingEventScanService_ResolvesFromItsRegisteredDependencies()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        // Mirrors the Functions host registrations for this graph.
        services.Configure<FilingEventsConfig>(_ => { });
        services.Configure<ClaudeConfig>(_ => { });
        services.AddSingleton(Substitute.For<IEdgarClient>());
        services.AddSingleton(Substitute.For<IFilingEventRepository>());
        services.AddSingleton(Substitute.For<SwingTrader.Infrastructure.Market.IMarketUniverseService>());
        services.AddSingleton(Substitute.For<IUserHttpClientFactory>());
        services.AddSingleton(Substitute.For<IClaudeRateLimiter>());
        services.AddSingleton(Substitute.For<ITiingoPowerRateLimiter>());
        services.AddScoped<IFilingEventScanService, FilingEventScanService>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IFilingEventScanService>();

        resolved.Should().NotBeNull();
    }

    [Fact]
    public async Task ScanAsync_WhenDisabled_ReturnsSkippedRatherThanThrowing()
    {
        // The consumer always writes a breadcrumb from the result, so a
        // disabled scan must return cleanly - never throw, never dead-letter.
        var edgar = Substitute.For<IEdgarClient>();
        var service = new FilingEventScanService(
            edgar,
            Substitute.For<IFilingEventRepository>(),
            Substitute.For<SwingTrader.Infrastructure.Market.IMarketUniverseService>(),
            Substitute.For<IUserHttpClientFactory>(),
            Substitute.For<IClaudeRateLimiter>(),
            Substitute.For<ITiingoPowerRateLimiter>(),
            Microsoft.Extensions.Options.Options.Create(new FilingEventsConfig { Enabled = false }),
            Microsoft.Extensions.Options.Options.Create(new ClaudeConfig()),
            Substitute.For<ILogger<FilingEventScanService>>());

        var result = await service.ScanAsync(new DateOnly(2026, 8, 6));

        result.Enabled.Should().BeFalse();
        result.Summary.Should().Contain("disabled");
        await edgar.DidNotReceive().SearchEightKsAsync(Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }
}
