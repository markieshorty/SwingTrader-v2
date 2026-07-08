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

    public async Task<KeyTestResult> TestKeyAsync(int accountId, string provider, CancellationToken ct = default)
    {
        var key = await repository.GetAsync(accountId, provider, ct);
        if (key is null) return new KeyTestResult(false, "Not set");

        var result = await RunConnectivityCheckAsync(accountId, provider, ct);

        key.IsValid = result.Valid;
        key.LastTestedAt = DateTime.UtcNow;
        key.LastTestResult = result.Message;
        await repository.UpsertAsync(key, ct);

        return result;
    }

    private async Task<KeyTestResult> RunConnectivityCheckAsync(
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
                    return new KeyTestResult(quote.CurrentPrice is not null, "Connected");
                }
                case ApiKeyProviders.Tiingo:
                {
                    var tiingo = await clientFactory.CreateTiingoAsync<ITiingoClient>(accountId, ct);
                    var to = DateTime.UtcNow.Date;
                    var from = to.AddDays(-7);
                    var prices = await tiingo.GetDailyPricesAsync("AAPL", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
                    return new KeyTestResult(prices.Count > 0, "Connected");
                }
                // A T212 pair is tested against its OWN environment's endpoint
                // regardless of the account's current TradingMode, so during
                // onboarding (default Demo) a user can still verify their Live
                // keys. Needs both key and secret before a real call is
                // possible; a lone key just confirms it decrypts. On success
                // we return the cash balance + environment so the user can
                // confirm the credentials aren't swapped or in the wrong slot.
                case ApiKeyProviders.Trading212DemoKey or ApiKeyProviders.Trading212DemoSecret:
                    return await TestTrading212PairAsync(accountId, TradingMode.Demo, provider, ct);

                case ApiKeyProviders.Trading212LiveKey or ApiKeyProviders.Trading212LiveSecret:
                    return await TestTrading212PairAsync(accountId, TradingMode.Live, provider, ct);

                // Claude/email: no free connectivity check available (Claude
                // calls cost money per request, and there's no email client
                // yet) - fall back to confirming the value decrypts.
                default:
                    return await DecryptOnlyCheckAsync(accountId, provider, ct);
            }
        }
        catch (ApiException ex)
        {
            return new KeyTestResult(false, $"Connection failed: {(int)ex.StatusCode} {ex.StatusCode}");
        }
        catch (Exception ex)
        {
            return new KeyTestResult(false, $"Connection failed: {ex.Message}");
        }
    }

    private async Task<KeyTestResult> TestTrading212PairAsync(int accountId, TradingMode mode, string provider, CancellationToken ct)
    {
        var (keyProvider, secretProvider) = mode == TradingMode.Live
            ? (ApiKeyProviders.Trading212LiveKey, ApiKeyProviders.Trading212LiveSecret)
            : (ApiKeyProviders.Trading212DemoKey, ApiKeyProviders.Trading212DemoSecret);

        var hasKey = await repository.GetAsync(accountId, keyProvider, ct) is not null;
        var hasSecret = await repository.GetAsync(accountId, secretProvider, ct) is not null;
        if (!hasKey || !hasSecret)
            return await DecryptOnlyCheckAsync(accountId, provider, ct);

        var t212 = await clientFactory.CreateTrading212ForModeAsync<ITrading212Client>(accountId, mode, ct);
        var cash = await t212.GetAccountCashAsync();

        string? currency = null;
        try
        {
            var info = await t212.GetAccountInfoAsync();
            currency = info.CurrencyCode;
        }
        catch
        {
            // Cash already proved connectivity; currency is a nicety, so a
            // failure on the info endpoint shouldn't fail the whole test.
        }

        var isDemo = mode == TradingMode.Demo;
        var moneyLabel = isDemo ? "practice" : "real";
        return new KeyTestResult(
            Valid: true,
            Message: $"Connected to {mode} account ({moneyLabel} money)",
            IsDemo: isDemo,
            CashTotal: cash.Total,
            CashFree: cash.Free,
            Currency: currency);
    }

    private async Task<KeyTestResult> DecryptOnlyCheckAsync(int accountId, string provider, CancellationToken ct)
    {
        var key = await repository.GetAsync(accountId, provider, ct);
        if (key is null) return new KeyTestResult(false, "Not set");
        var decrypted = await encryption.DecryptAsync(accountId, key.EncryptedValue, key.EncryptedDek, ct);
        var isValid = !string.IsNullOrWhiteSpace(decrypted);
        return new KeyTestResult(isValid, isValid ? "Saved (add the matching key/secret to verify the connection)" : "Empty value");
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
