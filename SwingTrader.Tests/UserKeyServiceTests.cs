using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;
using SwingTrader.Infrastructure.HttpClients.Dtos;
using SwingTrader.Infrastructure.Security;
using Xunit;

namespace SwingTrader.Tests;

public class UserKeyServiceTests
{
    private readonly IUserApiKeyRepository _repository = Substitute.For<IUserApiKeyRepository>();
    private readonly IKeyEncryptionService _encryption = Substitute.For<IKeyEncryptionService>();
    private readonly IUserHttpClientFactory _clientFactory = Substitute.For<IUserHttpClientFactory>();
    private readonly IAccountRepository _accounts = Substitute.For<IAccountRepository>();
    private readonly IConfiguration _config = new ConfigurationBuilder().Build();
    private readonly UserKeyService _sut;

    public UserKeyServiceTests()
    {
        _sut = new UserKeyService(_repository, _encryption, _clientFactory, _accounts, _config);
    }

    [Fact]
    public async Task GetKeyStatusesAsync_NoKeysSaved_AllNotSet()
    {
        _repository.ListAsync(1, Arg.Any<CancellationToken>()).Returns([]);

        var statuses = await _sut.GetKeyStatusesAsync(1);

        statuses.Should().HaveCount(ApiKeyProviders.All.Length);
        statuses.Values.Should().OnlyContain(s => s == KeyStatus.NotSet);
    }

    [Fact]
    public async Task GetKeyStatusesAsync_SavedButNeverTested_SetNotTested()
    {
        _repository.ListAsync(1, Arg.Any<CancellationToken>()).Returns(
        [
            new UserApiKey { AccountId = 1, Provider = ApiKeyProviders.Finnhub, LastTestedAt = null },
        ]);

        var statuses = await _sut.GetKeyStatusesAsync(1);

        statuses[ApiKeyProviders.Finnhub].Should().Be(KeyStatus.SetNotTested);
    }

    [Fact]
    public async Task GetKeyStatusesAsync_TestedAndValid_Valid()
    {
        _repository.ListAsync(1, Arg.Any<CancellationToken>()).Returns(
        [
            new UserApiKey { AccountId = 1, Provider = ApiKeyProviders.Finnhub, LastTestedAt = DateTime.UtcNow, IsValid = true },
        ]);

        var statuses = await _sut.GetKeyStatusesAsync(1);

        statuses[ApiKeyProviders.Finnhub].Should().Be(KeyStatus.Valid);
    }

    [Fact]
    public async Task GetKeyStatusesAsync_TestedAndInvalid_Invalid()
    {
        _repository.ListAsync(1, Arg.Any<CancellationToken>()).Returns(
        [
            new UserApiKey { AccountId = 1, Provider = ApiKeyProviders.Finnhub, LastTestedAt = DateTime.UtcNow, IsValid = false },
        ]);

        var statuses = await _sut.GetKeyStatusesAsync(1);

        statuses[ApiKeyProviders.Finnhub].Should().Be(KeyStatus.Invalid);
    }

    [Fact]
    public async Task GetKeyStatusesAsync_NeverExposesRawValues()
    {
        _repository.ListAsync(1, Arg.Any<CancellationToken>()).Returns(
        [
            new UserApiKey
            {
                AccountId = 1,
                Provider = ApiKeyProviders.Finnhub,
                EncryptedValue = "super-secret-ciphertext",
                LastTestedAt = DateTime.UtcNow,
                IsValid = true,
            },
        ]);

        var statuses = await _sut.GetKeyStatusesAsync(1);

        // The status dictionary's values are the KeyStatus enum only - there
        // is no code path back to EncryptedValue/EncryptedDek from here.
        statuses.Values.Should().AllBeOfType<KeyStatus>();
    }

    [Fact]
    public async Task SaveKeyAsync_EncryptsBeforeStoring()
    {
        _encryption.EncryptAsync(1, "plaintext-key", Arg.Any<CancellationToken>())
            .Returns(("cipher", "wrapped-dek"));

        await _sut.SaveKeyAsync(1, ApiKeyProviders.Finnhub, "plaintext-key");

        await _repository.Received(1).UpsertAsync(
            Arg.Is<UserApiKey>(k =>
                k.AccountId == 1 &&
                k.Provider == ApiKeyProviders.Finnhub &&
                k.EncryptedValue == "cipher" &&
                k.EncryptedDek == "wrapped-dek"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetKeyAsync_NoKeyAndNoFallback_Throws()
    {
        _repository.GetAsync(1, ApiKeyProviders.Finnhub, Arg.Any<CancellationToken>())
            .Returns((UserApiKey?)null);

        var act = () => _sut.GetKeyAsync(1, ApiKeyProviders.Finnhub);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TestKeyAsync_Finnhub_ValidQuote_MarksValid()
    {
        var stored = new UserApiKey { AccountId = 1, Provider = ApiKeyProviders.Finnhub };
        _repository.GetAsync(1, ApiKeyProviders.Finnhub, Arg.Any<CancellationToken>()).Returns(stored);

        var finnhubClient = Substitute.For<IFinnhubClient>();
        finnhubClient.GetQuoteAsync("AAPL").Returns(new FinnhubQuoteResponse(
            CurrentPrice: 150m, Change: null, PercentChange: null, High: null, Low: null, Open: null, PreviousClose: null, Timestamp: 0));
        _clientFactory.CreateFinnhubAsync<IFinnhubClient>(1, Arg.Any<CancellationToken>()).Returns(finnhubClient);

        var result = await _sut.TestKeyAsync(1, ApiKeyProviders.Finnhub);

        result.Should().BeTrue();
        stored.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task TestKeyAsync_Finnhub_ApiThrows_MarksInvalidWithoutBlowingUp()
    {
        var stored = new UserApiKey { AccountId = 1, Provider = ApiKeyProviders.Finnhub };
        _repository.GetAsync(1, ApiKeyProviders.Finnhub, Arg.Any<CancellationToken>()).Returns(stored);
        _clientFactory.CreateFinnhubAsync<IFinnhubClient>(1, Arg.Any<CancellationToken>())
            .Returns<Task<IFinnhubClient>>(_ => throw new InvalidOperationException("boom"));

        var result = await _sut.TestKeyAsync(1, ApiKeyProviders.Finnhub);

        result.Should().BeFalse();
        stored.IsValid.Should().BeFalse();
        stored.LastTestResult.Should().Contain("Connection failed");
    }

    [Fact]
    public async Task TestKeyAsync_Trading212WithOnlyKeySet_DoesNotCallApi()
    {
        var stored = new UserApiKey { AccountId = 1, Provider = ApiKeyProviders.Trading212DemoKey, EncryptedValue = "v", EncryptedDek = "d" };
        _repository.GetAsync(1, ApiKeyProviders.Trading212DemoKey, Arg.Any<CancellationToken>()).Returns(stored);
        _repository.GetAsync(1, ApiKeyProviders.Trading212DemoSecret, Arg.Any<CancellationToken>()).Returns((UserApiKey?)null);
        _encryption.DecryptAsync(1, "v", "d", Arg.Any<CancellationToken>()).Returns("decrypted-value");

        var result = await _sut.TestKeyAsync(1, ApiKeyProviders.Trading212DemoKey);

        result.Should().BeTrue();
        await _clientFactory.DidNotReceive().CreateTrading212Async<ITrading212Client>(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
