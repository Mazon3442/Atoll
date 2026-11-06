using System.Security.Cryptography;
using System.Text;

namespace Atoll.Settings;

/// <summary>
/// Encrypts small secrets (calendar ICS links) with the Windows Data Protection API so they cannot
/// be read from settings.json. Scoped to the current user: only the account that wrote them, on this
/// machine, can read them back.
/// </summary>
public static class SecretStore
{
    private static readonly byte[] Entropy = "Atoll.Secrets.v1"u8.ToArray();

    /// <summary>Encrypts text into a Base64 string for storage.</summary>
    public static string Protect(string plaintext)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Decrypts text stored with <see cref="Protect"/>. Returns null if it cannot be read back.</summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Wrong user/machine, or the value was never encrypted (e.g. hand-edited). Treat as blank.
            return null;
        }
    }
}
