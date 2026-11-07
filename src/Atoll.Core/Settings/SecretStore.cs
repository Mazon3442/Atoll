using System.Security.Cryptography;
using System.Text;

namespace Atoll.Settings;

/// <summary>
/// Encrypts calendar links with Windows DPAPI or AES-GCM and a private per-user key on Unix.
/// </summary>
public static class SecretStore
{
    private static readonly byte[] Entropy = "Atoll.Secrets.v1"u8.ToArray();

    /// <summary>Encrypts text into a Base64 string for storage.</summary>
    public static string Protect(string plaintext)
    {
        if (OperatingSystem.IsWindows())
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var input = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[input.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(ReadKey(), 16);
        aes.Encrypt(nonce, input, output, tag, Entropy);
        return "aes1:" + Convert.ToBase64String([.. nonce, .. tag, .. output]);
    }

    /// <summary>Decrypts text stored with <see cref="Protect"/>. Returns null if it cannot be read back.</summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try
        {
            if (OperatingSystem.IsWindows() && !protectedBase64.StartsWith("aes1:", StringComparison.Ordinal))
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser));
            if (!protectedBase64.StartsWith("aes1:", StringComparison.Ordinal)) return null;
            var bytes = Convert.FromBase64String(protectedBase64[5..]);
            if (bytes.Length < 28) return null;
            var plaintext = new byte[bytes.Length - 28];
            using var aes = new AesGcm(ReadKey(), 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext, Entropy);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException)
        {
            // Wrong user/machine, or the value was never encrypted (e.g. hand-edited). Treat as blank.
            return null;
        }
    }

    private static readonly object KeyGate = new();
    private static byte[]? _key;

    /// <summary>The per-user key, read once. Callers on several threads share one load.</summary>
    private static byte[] ReadKey()
    {
        lock (KeyGate) return _key ??= LoadOrCreateKey();
    }

    private static byte[] LoadOrCreateKey()
    {
        var directory = Path.GetDirectoryName(SettingsStore.DefaultPath)!;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "secrets.key");
        if (!File.Exists(path))
        {
            // Write the whole key under a temporary name, then move it into place: another thread or process never
            // sees a locked or half-written key file.
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temp, options))
                {
                    stream.Write(RandomNumberGenerator.GetBytes(32));
                    stream.Flush(true);
                }
                try { File.Move(temp, path); }
                catch (IOException) when (File.Exists(path)) { } // another process created it first; use theirs
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var key = File.ReadAllBytes(path);
        if (key.Length != 32) throw new CryptographicException("ATOLL's secret key is invalid. Restore it from your backup.");
        return key;
    }
}
