using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Models;
using SwingTrader.Data;
using SwingTrader.Data.Repositories;
using Xunit;

namespace SwingTrader.Tests;

public class UserApiKeyRepositoryTests
{
    private static SwingTraderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<SwingTraderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task UpsertAsync_NoExistingRow_Inserts()
    {
        await using var db = CreateDb();
        var repo = new UserApiKeyRepository(db);

        await repo.UpsertAsync(new UserApiKey { AccountId = 1, Provider = "Finnhub", EncryptedValue = "enc", EncryptedDek = "dek" });

        var result = await repo.GetAsync(1, "Finnhub");
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task UpsertAsync_ExistingRow_UpdatesEncryptedValueAndValidity()
    {
        await using var db = CreateDb();
        var repo = new UserApiKeyRepository(db);
        await repo.UpsertAsync(new UserApiKey { AccountId = 1, Provider = "Finnhub", EncryptedValue = "old", EncryptedDek = "dek1", IsValid = false });

        await repo.UpsertAsync(new UserApiKey { AccountId = 1, Provider = "Finnhub", EncryptedValue = "new", EncryptedDek = "dek2", IsValid = true });

        db.UserApiKeys.Count(k => k.AccountId == 1 && k.Provider == "Finnhub").Should().Be(1);
        var result = await repo.GetAsync(1, "Finnhub");
        result!.EncryptedValue.Should().Be("new");
        result.IsValid.Should().BeTrue();
        result.UpdatedAt.Should().NotBe(default);
    }

    [Fact]
    public async Task ListAsync_ScopedToAccount()
    {
        await using var db = CreateDb();
        var repo = new UserApiKeyRepository(db);
        await repo.UpsertAsync(new UserApiKey { AccountId = 1, Provider = "Finnhub", EncryptedValue = "v", EncryptedDek = "d" });
        await repo.UpsertAsync(new UserApiKey { AccountId = 2, Provider = "Tiingo", EncryptedValue = "v", EncryptedDek = "d" });

        var result = await repo.ListAsync(1);

        result.Should().ContainSingle(k => k.Provider == "Finnhub");
    }

    [Fact]
    public async Task DeleteAsync_RemovesMatchingKey()
    {
        await using var db = CreateDb();
        var repo = new UserApiKeyRepository(db);
        await repo.UpsertAsync(new UserApiKey { AccountId = 1, Provider = "Finnhub", EncryptedValue = "v", EncryptedDek = "d" });

        await repo.DeleteAsync(1, "Finnhub");

        (await repo.GetAsync(1, "Finnhub")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_UnknownKey_DoesNotThrow()
    {
        await using var db = CreateDb();
        var repo = new UserApiKeyRepository(db);

        var act = async () => await repo.DeleteAsync(1, "Finnhub");

        await act.Should().NotThrowAsync();
    }
}
