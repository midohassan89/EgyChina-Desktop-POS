using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EgyChina.Pos.Data;

namespace EgyChina.Pos.Sync;

/// <summary>
/// Persists the ERP device bearer on the till. On Windows the blob is DPAPI-protected
/// (CurrentUser). Never log the raw token. Not a staff Auth.js cookie.
/// </summary>
public static class DeviceCredentialStore
{
    public const string FileName = "device.cred";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EgyChina.Pos.Device.v1");

    public static string CredentialPath() => Path.Combine(PosPaths.Root(), FileName);

    public static void Save(string rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            throw new ArgumentException("Device token is required.", nameof(rawToken));

        Directory.CreateDirectory(PosPaths.Root());
        var plain = Encoding.UTF8.GetBytes(rawToken.Trim());
        byte[] stored;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            stored = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        }
        else
        {
            // Dev VMs only. Production tills are Windows + DPAPI.
            stored = plain;
            PosLog.Error("DeviceCredentialStore: non-Windows store is plaintext (dev only).");
        }

        File.WriteAllBytes(CredentialPath(), stored);
        CryptographicOperations.ZeroMemory(plain);
    }

    public static string? Load()
    {
        var path = CredentialPath();
        if (!File.Exists(path))
            return null;

        try
        {
            var stored = File.ReadAllBytes(path);
            if (stored.Length == 0)
                return null;

            byte[] plain;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                plain = ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser);
            }
            else
            {
                plain = stored;
            }

            var token = Encoding.UTF8.GetString(plain).Trim();
            CryptographicOperations.ZeroMemory(plain);
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            PosLog.Error("DeviceCredentialStore: failed to load device token", ex);
            return null;
        }
    }

    public static void Clear()
    {
        var path = CredentialPath();
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>
    /// If <c>EGYCHINA_POS_DEVICE_TOKEN</c> is set, persist it under DPAPI and keep using the store.
    /// Does not clear the environment variable (caller owns the shell).
    /// </summary>
    public static string? ImportFromEnvironmentIfPresent()
    {
        var fromEnv = Environment.GetEnvironmentVariable(SyncOptions.DeviceTokenVariable);
        if (string.IsNullOrWhiteSpace(fromEnv))
            return Load();

        Save(fromEnv);
        return fromEnv.Trim();
    }
}
