using Microsoft.Extensions.Configuration;
using Refit;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.HttpClients;

namespace SwingTrader.Infrastructure.Security;

public class UserKeyService(
    IUserApiKeyRepository repository,
    IKeyEncryptionService encryption,
    IUserHttpClientFactory clientFactory,
    IConfiguration config) : IUserKeyService
{
    public async Task<string> GetKeyAsync(int accountId, string provider, CancellationToken ct = default)
    {
        var key = await repository.GetAsync(accountId, provider, ct);
        if (key is not null)
            return await encryption.DecryptAsync(accountId, key.EncryptedValue, key.EncryptedDek, ct);

        // Claude is the only provider with a shared fallback - every other
        // provider is inherently per-account (brokerage/data credentials).
        if (provider == ApiKeyProviders.Claude)
        {
            var shared = config["Claude:ApiKey"];
            if (!string.IsNullOrEmpty(shared)) return shared;
        }

        throw new InvalidOperationException($"No {provider} key configured for account {accountId}.");
    }

    public async Task SaveKeyAsync(int accountId, string provider, string plaintext, CancellationToken ct = default)
    {
        var (encryptedValue, encryptedDek) = await encryption.EncryptAsync(accountId, plaintext, ct);
        await repository.UpsertAsync(new UserApiKey
        {
            AccountId = accountId,
            Provider = provider,
            EncryptedValue = encryptedValue,
            EncryptedDek = encryptedDek,
            IsValid = false,
            LastTestedAt = null,
            LastTestResult = null,
        }, ct);
    }

    public async Task<bool> TestKeyAsync(int accountId, string provider, CancellationToken ct = default)
    {
        var key = await repository.GetAsync(accountId, provider, ct);
        if (key is null) return false;

        var (isValid, message) = await RunConnectivityCheckAsync(accountId, provider, ct);

        key.IsValid = isValid;
        key.LastTestedAt = DateTime.UtcNow;
        key.LastTestResult = message;
        await repository.UpsertAsync(key, ct);

        return isValid;
    }

    private async Task<(bool IsValid, string Message)> RunConnectivityCheckAsync(
        int accountId, string provider, CancellationToken ct)
    {
        try
        {
            switch (provider)
            {
                case ApiKeyProviders.Finnhub:
                {
                    var finnhub = await clientFactory.CreateFinnhubAsync<IFinnhubClient>(accountId, ct);
                    var quote = await finnhub.GetQuoteAsync("AAPL");
                    return (quote.CurrentPrice is not null, "Connected");
                }
                case ApiKeyProviders.Tiingo:
                {
                    var tiingo = await clientFactory.CreateTiingoAsync<ITiingoClient>(accountId, ct);
                    var to = DateTime.UtcNow.Date;
                    var from = to.AddDays(-7);
                    var prices = await tiingo.GetDailyPricesAsync("AAPL", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
                    return (prices.Count > 0, "Connected");
                }
                // Trading212 needs both Key and Secret before a real call can
                // be made - testing either one alone just confirms it
                // decrypts, since there's nothing to call yet.
                case ApiKeyProviders.Trading212Key or ApiKeyProviders.Trading212Secret:
                {
                    var hasKey = await repository.GetAsync(accountId, ApiKeyProviders.Trading212Key, ct) is not null;
                    var hasSecret = await repository.GetAsync(accountId, ApiKeyProviders.Trading212Secret, ct) is not null;
                    if (!hasKey || !hasSecret)
                        return await DecryptOnlyCheckAsync(accountId, provider, ct);

                    var t212 = await clientFactory.CreateTrading212Async<ITrading212Client>(accountId, ct);
                    var cash = await t212.GetAccountCashAsync();
                    return (cash is not null, "Connected");
                }
                // Claude/email: no free connectivity check available (Claude
                // calls cost money per request, and there's no email client
                // yet) - fall back to confirming the value decrypts.
                default:
                    return await DecryptOnlyCheckAsync(accountId, provider, ct);
            }
        }
        catch (ApiException ex)
        {
            return (false, $"Connection failed: {(int)ex.StatusCode} {ex.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    private async Task<(bool, string)> DecryptOnlyCheckAsync(int accountId, string provider, CancellationToken ct)
    {
        var key = await repository.GetAsync(accountId, provider, ct);
        if (key is null) return (false, "Not set");
        var decrypted = await encryption.DecryptAsync(accountId, key.EncryptedValue, key.EncryptedDek, ct);
        var isValid = !string.IsNullOrWhiteSpace(decrypted);
        return (isValid, isValid ? "Saved (not independently verifiable)" : "Empty value");
    }

    public async Task<Dictionary<string, KeyStatus>> GetKeyStatusesAsync(int accountId, CancellationToken ct = default)
    {
        var existing = await repository.ListAsync(accountId, ct);
        var byProvider = existing.ToDictionary(k => k.Provider);

        var statuses = new Dictionary<string, KeyStatus>();
        foreach (var provider in ApiKeyProviders.All)
        {
            if (!byProvider.TryGetValue(provider, out var key))
            {
                statuses[provider] = KeyStatus.NotSet;
            }
            else if (key.LastTestedAt is null)
            {
                statuses[provider] = KeyStatus.SetNotTested;
            }
            else
            {
                statuses[provider] = key.IsValid ? KeyStatus.Valid : KeyStatus.Invalid;
            }
        }
        return statuses;
    }

    public Task DeleteKeyAsync(int accountId, string provider, CancellationToken ct = default) =>
        repository.DeleteAsync(accountId, provider, ct);
}
