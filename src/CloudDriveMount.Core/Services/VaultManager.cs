using System.Security.Cryptography;
using System.Text;
using CloudDriveMount.Core.Models;

namespace CloudDriveMount.Core.Services;

public static class VaultManager
{
    private static readonly byte[] CacheEntropy = Encoding.UTF8.GetBytes("CloudDriveMount.VaultCache.v1");

    public static string GetDefaultCacheDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudDriveMount",
            "vault-cache");

    private static string GetCacheFilePath(string vaultId, string? cacheDirOverride = null)
    {
        var safeId = new string(vaultId.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        if (string.IsNullOrEmpty(safeId))
            safeId = "default";

        var dir = string.IsNullOrWhiteSpace(cacheDirOverride) ? GetDefaultCacheDirectory() : cacheDirOverride;
        return Path.Combine(dir, $"{safeId}.key");
    }

    public static bool HasRememberedKey(string? vaultId, string? cacheDirOverride = null)
    {
        if (string.IsNullOrWhiteSpace(vaultId))
            return false;

        return File.Exists(GetCacheFilePath(vaultId, cacheDirOverride));
    }

    public static bool TryAutoUnlock(VaultMetadata vault, string? cacheDirOverride = null)
    {
        if (string.IsNullOrWhiteSpace(vault.VaultId) || string.IsNullOrWhiteSpace(vault.KeyCheck))
            return false;

        var cacheFile = GetCacheFilePath(vault.VaultId, cacheDirOverride);
        if (!File.Exists(cacheFile))
            return false;

        byte[]? key = null;
        try
        {
            var encrypted = File.ReadAllBytes(cacheFile);
            key = ProtectedData.Unprotect(encrypted, CacheEntropy, DataProtectionScope.CurrentUser);
            if (key.Length == 32 && CredentialProtector.VerifyKeyCheck(vault.KeyCheck, key))
            {
                CredentialProtector.SetVaultKey(key);
                return true;
            }
        }
        catch
        {
            // Corrupted or different-user cache; fall back to password prompt
        }
        finally
        {
            if (key != null)
                CryptographicOperations.ZeroMemory(key);
        }

        return false;
    }

    public static bool TryUnlockWithPassword(
        VaultMetadata vault,
        string password,
        bool rememberOnThisPc,
        string? cacheDirOverride = null)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(vault.SaltBase64))
            return false;

        byte[]? key = null;
        try
        {
            var salt = Convert.FromBase64String(vault.SaltBase64);
            var iterations = vault.Iterations > 0 ? vault.Iterations : 300_000;
            key = CredentialProtector.DeriveKey(password, salt, iterations);

            if (!CredentialProtector.VerifyKeyCheck(vault.KeyCheck, key))
                return false;

            CredentialProtector.SetVaultKey(key);

            if (rememberOnThisPc)
                SaveRememberedKey(vault.VaultId, key, cacheDirOverride);
            else
                ForgetThisPc(vault.VaultId, cacheDirOverride);

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (key != null)
                CryptographicOperations.ZeroMemory(key);
        }
    }

    public static void EnableOrChangeMasterPassword(
        ConfigStore store,
        IList<DriveConfig> drives,
        string newPassword,
        bool rememberOnThisPc,
        string? cacheDirOverride = null)
    {
        if (string.IsNullOrWhiteSpace(newPassword))
            throw new ArgumentException("Master password cannot be empty.", nameof(newPassword));

        // 1. Decrypt all existing drive secrets using the currently active mode (DPAPI or old vault key)
        var plaintextSnapshots = drives.Select(SnapshotSecrets).ToList();

        // 2. Derive new vault key
        var salt = RandomNumberGenerator.GetBytes(16);
        const int iterations = 300_000;
        byte[]? newKey = null;
        try
        {
            newKey = CredentialProtector.DeriveKey(newPassword, salt, iterations);
            var vaultId = store.CurrentVault?.VaultId;
            if (string.IsNullOrWhiteSpace(vaultId))
                vaultId = Guid.NewGuid().ToString("N");

            var newVault = new VaultMetadata
            {
                VaultId = vaultId,
                SaltBase64 = Convert.ToBase64String(salt),
                Iterations = iterations,
                KeyCheck = CredentialProtector.CreateKeyCheck(newKey)
            };

            // 3. Activate new key and re-encrypt all drive secrets as portable AES-256-GCM
            CredentialProtector.SetVaultKey(newKey);
            for (int i = 0; i < drives.Count; i++)
            {
                ApplySecrets(drives[i], plaintextSnapshots[i]);
            }

            store.CurrentVault = newVault;
            store.SaveDrives(drives);

            if (rememberOnThisPc)
                SaveRememberedKey(newVault.VaultId, newKey, cacheDirOverride);
            else
                ForgetThisPc(newVault.VaultId, cacheDirOverride);
        }
        finally
        {
            if (newKey != null)
                CryptographicOperations.ZeroMemory(newKey);
        }
    }

    public static void DisableMasterPassword(
        ConfigStore store,
        IList<DriveConfig> drives,
        string? cacheDirOverride = null)
    {
        var plaintextSnapshots = drives.Select(SnapshotSecrets).ToList();
        var oldVaultId = store.CurrentVault?.VaultId;

        CredentialProtector.SetVaultKey(null);
        if (!string.IsNullOrWhiteSpace(oldVaultId))
            ForgetThisPc(oldVaultId, cacheDirOverride);

        for (int i = 0; i < drives.Count; i++)
        {
            ApplySecrets(drives[i], plaintextSnapshots[i]);
        }

        store.CurrentVault = null;
        store.SaveDrives(drives);
    }

    public static void SaveRememberedKey(string vaultId, byte[] key, string? cacheDirOverride = null)
    {
        if (string.IsNullOrWhiteSpace(vaultId) || key.Length != 32)
            return;

        try
        {
            var cacheFile = GetCacheFilePath(vaultId, cacheDirOverride);
            var dir = Path.GetDirectoryName(cacheFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var encrypted = ProtectedData.Protect(key, CacheEntropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(cacheFile, encrypted);
        }
        catch
        {
            // Ignore local cache write failures on restricted systems
        }
    }

    public static void ForgetThisPc(string? vaultId, string? cacheDirOverride = null)
    {
        if (string.IsNullOrWhiteSpace(vaultId))
            return;

        try
        {
            var cacheFile = GetCacheFilePath(vaultId, cacheDirOverride);
            if (File.Exists(cacheFile))
                File.Delete(cacheFile);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private sealed record SecretSnapshot(
        string CfAccessClientSecret,
        string Password,
        string PrivateKeyPassphrase,
        string R2SecretAccessKey,
        string GoogleClientSecret,
        string GoogleRefreshToken,
        string GoogleAccessToken);

    private static SecretSnapshot SnapshotSecrets(DriveConfig d) => new(
        CredentialProtector.Unprotect(d.EncryptedCfAccessClientSecret),
        CredentialProtector.Unprotect(d.EncryptedPassword),
        CredentialProtector.Unprotect(d.EncryptedPrivateKeyPassphrase),
        CredentialProtector.Unprotect(d.EncryptedR2SecretAccessKey),
        CredentialProtector.Unprotect(d.EncryptedGoogleClientSecret),
        CredentialProtector.Unprotect(d.EncryptedGoogleRefreshToken),
        CredentialProtector.Unprotect(d.EncryptedGoogleAccessToken));

    private static void ApplySecrets(DriveConfig d, SecretSnapshot s)
    {
        d.EncryptedCfAccessClientSecret = CredentialProtector.Protect(s.CfAccessClientSecret);
        d.EncryptedPassword = CredentialProtector.Protect(s.Password);
        d.EncryptedPrivateKeyPassphrase = CredentialProtector.Protect(s.PrivateKeyPassphrase);
        d.EncryptedR2SecretAccessKey = CredentialProtector.Protect(s.R2SecretAccessKey);
        d.EncryptedGoogleClientSecret = CredentialProtector.Protect(s.GoogleClientSecret);
        d.EncryptedGoogleRefreshToken = CredentialProtector.Protect(s.GoogleRefreshToken);
        d.EncryptedGoogleAccessToken = CredentialProtector.Protect(s.GoogleAccessToken);
    }
}
