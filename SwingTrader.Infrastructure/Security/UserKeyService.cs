using Microsoft.Extensions.Configuration;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Infrastructure.Security;

public class UserKeyService(
    IUserApiKeyRepository repository,
    IKeyEncryptionService encryption,
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

        // Real provider connectivity checks (Finnhub/Tiingo/T212/Claude
        // ping calls) land once those HTTP clients are ported - for now
        // this only confirms the stored value decrypts to something.
        var decrypted = await encryption.DecryptAsync(accountId, key.EncryptedValue, key.EncryptedDek, ct);
        var isValid = !string.IsNullOrWhiteSpace(decrypted);

        key.IsValid = isValid;
        key.LastTestedAt = DateTime.UtcNow;
        key.LastTestResult = isValid ? "Decrypted successfully" : "Empty value";
        await repository.UpsertAsync(key, ct);

        return isValid;
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
