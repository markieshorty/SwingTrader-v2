using Microsoft.EntityFrameworkCore;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;

namespace SwingTrader.Data.Repositories;

public class UserApiKeyRepository(SwingTraderDbContext db) : IUserApiKeyRepository
{
    public Task<UserApiKey?> GetAsync(int accountId, string provider, CancellationToken ct = default) =>
        db.UserApiKeys.FirstOrDefaultAsync(k => k.AccountId == accountId && k.Provider == provider, ct);

    public Task<List<UserApiKey>> ListAsync(int accountId, CancellationToken ct = default) =>
        db.UserApiKeys.Where(k => k.AccountId == accountId).ToListAsync(ct);

    public async Task UpsertAsync(UserApiKey key, CancellationToken ct = default)
    {
        var existing = await db.UserApiKeys
            .FirstOrDefaultAsync(k => k.AccountId == key.AccountId && k.Provider == key.Provider, ct);

        if (existing is null)
        {
            db.UserApiKeys.Add(key);
        }
        else
        {
            existing.EncryptedValue = key.EncryptedValue;
            existing.EncryptedDek = key.EncryptedDek;
            existing.IsValid = key.IsValid;
            existing.LastTestedAt = key.LastTestedAt;
            existing.LastTestResult = key.LastTestResult;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int accountId, string provider, CancellationToken ct = default)
    {
        var existing = await db.UserApiKeys
            .FirstOrDefaultAsync(k => k.AccountId == accountId && k.Provider == provider, ct);
        if (existing is null) return;

        db.UserApiKeys.Remove(existing);
        await db.SaveChangesAsync(ct);
    }
}
