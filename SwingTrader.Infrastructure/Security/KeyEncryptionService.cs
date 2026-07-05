using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.Extensions.Configuration;
using SwingTrader.Core.Interfaces;

namespace SwingTrader.Infrastructure.Security;

public class KeyEncryptionService : IKeyEncryptionService
{
    private readonly string _keyVaultUrl;

    public KeyEncryptionService(IConfiguration config)
    {
        _keyVaultUrl = config["KeyVaultUrl"]
            ?? throw new InvalidOperationException("KeyVaultUrl not configured");
    }

    public async Task<(string EncryptedValue, string EncryptedDek)> EncryptAsync(
        int accountId, string plaintext, CancellationToken ct = default)
    {
        // 1. Generate a random AES-256 data-encryption-key (DEK).
        var dek = new byte[32];
        var iv = new byte[16];
        RandomNumberGenerator.Fill(dek);
        RandomNumberGenerator.Fill(iv);

        try
        {
            // 2. Encrypt the plaintext with the DEK.
            using var aes = Aes.Create();
            aes.Key = dek;
            aes.IV = iv;
            using var encryptor = aes.CreateEncryptor();
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var encrypted = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

            var combined = iv.Concat(encrypted).ToArray();
            var encryptedValue = Convert.ToBase64String(combined);

            // 3. Wrap the DEK with this account's Key Vault key.
            var keyName = GetKeyName(accountId);
            await EnsureKeyExistsAsync(keyName, ct);

            var cryptoClient = GetCryptoClient(keyName);
            var wrapResult = await cryptoClient.WrapKeyAsync(KeyWrapAlgorithm.RsaOaep, dek, ct);
            var encryptedDek = Convert.ToBase64String(wrapResult.EncryptedKey);

            return (encryptedValue, encryptedDek);
        }
        finally
        {
            Array.Clear(dek, 0, dek.Length);
        }
    }

    public async Task<string> DecryptAsync(
        int accountId, string encryptedValue, string encryptedDek, CancellationToken ct = default)
    {
        var keyName = GetKeyName(accountId);
        var cryptoClient = GetCryptoClient(keyName);

        var encryptedDekBytes = Convert.FromBase64String(encryptedDek);
        var unwrapResult = await cryptoClient.UnwrapKeyAsync(KeyWrapAlgorithm.RsaOaep, encryptedDekBytes, ct);
        var dek = unwrapResult.Key;

        try
        {
            var combined = Convert.FromBase64String(encryptedValue);
            var iv = combined[..16];
            var ciphertext = combined[16..];

            using var aes = Aes.Create();
            aes.Key = dek;
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            var decrypted = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);

            return Encoding.UTF8.GetString(decrypted);
        }
        finally
        {
            Array.Clear(dek, 0, dek.Length);
        }
    }

    private static string GetKeyName(int accountId) => $"account-{accountId}-key";

    private CryptographyClient GetCryptoClient(string keyName) =>
        new(new Uri($"{_keyVaultUrl}keys/{keyName}"), new DefaultAzureCredential());

    private async Task EnsureKeyExistsAsync(string keyName, CancellationToken ct)
    {
        var keyClient = new KeyClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
        try
        {
            await keyClient.GetKeyAsync(keyName, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            await keyClient.CreateRsaKeyAsync(
                new CreateRsaKeyOptions(keyName)
                {
                    KeySize = 2048,
                    KeyOperations = { KeyOperation.WrapKey, KeyOperation.UnwrapKey },
                }, ct);
        }
    }
}
