using System.Security.Cryptography;
using System.Text;

namespace CloudDriveMount.Core.Services;

public static class CredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CloudDriveMount.DPAPI.v1");

    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

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

    public static string Unprotect(string? encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64))
            return string.Empty;

        try
        {
            var cipherBytes = Convert.FromBase64String(encryptedBase64);
            var decrypted = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encryptedBase64));
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
