namespace SwingTrader.Core.Interfaces;

// Envelope encryption: a per-account AES-256 data-encryption-key (DEK)
// encrypts the secret, and a per-account Key Vault RSA key wraps the DEK.
// Losing the DB doesn't leak keys without also compromising Key Vault, and
// one account's compromised key can't be used to decrypt another's.
public interface IKeyEncryptionService
{
    Task<(string EncryptedValue, string EncryptedDek)> EncryptAsync(
        int accountId, string plaintext, CancellationToken ct = default);

    Task<string> DecryptAsync(
        int accountId, string encryptedValue, string encryptedDek, CancellationToken ct = default);
}
