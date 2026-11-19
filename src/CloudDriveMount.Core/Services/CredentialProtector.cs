using System.Security.Cryptography;
using System.Text;

namespace CloudDriveMount.Core.Services;

public static class CredentialProtector
{
    private const string VaultCipherPrefix = "v1:";
    private const string VaultCheckPlaintext = "CloudDriveMount.VaultCheck.v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudDriveMount.DPAPI.v1");
    private static readonly object VaultLock = new();
    private static byte[]? _vaultKey;

    public static bool IsVaultUnlocked
    {
        get
        {
            lock (VaultLock)
            {
                return _vaultKey is { Length: 32 };
            }
        }
    }

    public static void SetVaultKey(byte[]? key)
    {
        lock (VaultLock)
        {
            if (_vaultKey != null)
            {
                CryptographicOperations.ZeroMemory(_vaultKey);
                _vaultKey = null;
            }

            if (key != null)
            {
                if (key.Length != 32)
                    throw new ArgumentException("Vault key must be 256 bits (32 bytes).", nameof(key));
                _vaultKey = (byte[])key.Clone();
            }
        }
    }

    public static byte[] DeriveKey(string password, byte[] salt, int iterations = 300_000)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            32);
    }

    public static string CreateKeyCheck(byte[] key) => ProtectWithKey(VaultCheckPlaintext, key);

    public static bool VerifyKeyCheck(string? keyCheck, byte[] key)
    {
        if (string.IsNullOrEmpty(keyCheck))
            return false;

        var decrypted = UnprotectWithKey(keyCheck, key);
        return string.Equals(decrypted, VaultCheckPlaintext, StringComparison.Ordinal);
    }

    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        lock (VaultLock)
        {
            if (_vaultKey is { Length: 32 })
                return ProtectWithKey(plainText, _vaultKey);
        }

        try
        {
            var rawBytes = Encoding.UTF8.GetBytes(plainText);
            // Use LocalMachine scope so both user tray UI and SYSTEM mount helper can read credentials
            var encrypted = ProtectedData.Protect(rawBytes, Entropy, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(encrypted);
        }
        catch
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));
        }
    }

    public static string Unprotect(string? encryptedValue)
    {
        if (string.IsNullOrEmpty(encryptedValue))
            return string.Empty;

        if (encryptedValue.StartsWith(VaultCipherPrefix, StringComparison.Ordinal))
        {
            lock (VaultLock)
            {
                if (_vaultKey is { Length: 32 })
                    return UnprotectWithKey(encryptedValue, _vaultKey);
            }
            return string.Empty;
        }

        try
        {
            var cipherBytes = Convert.FromBase64String(encryptedValue);
            var decrypted = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encryptedValue));
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public static string ProtectWithKey(string plainText, byte[] key)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var combined = new byte[NonceSize + TagSize + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, combined, NonceSize, TagSize);
        Buffer.BlockCopy(cipherBytes, 0, combined, NonceSize + TagSize, cipherBytes.Length);

        return VaultCipherPrefix + Convert.ToBase64String(combined);
    }

    public static string UnprotectWithKey(string encryptedValue, byte[] key)
    {
        if (!encryptedValue.StartsWith(VaultCipherPrefix, StringComparison.Ordinal))
            return string.Empty;

        try
        {
            var combined = Convert.FromBase64String(encryptedValue[VaultCipherPrefix.Length..]);
            if (combined.Length < NonceSize + TagSize)
                return string.Empty;

            var nonce = combined.AsSpan(0, NonceSize);
            var tag = combined.AsSpan(NonceSize, TagSize);
            var cipherBytes = combined.AsSpan(NonceSize + TagSize);
            var plainBytes = new byte[cipherBytes.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}
