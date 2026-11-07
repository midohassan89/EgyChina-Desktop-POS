using System.Security.Cryptography;
using System.Text;

namespace EgyChina.Pos.Core;

/// <summary>
/// Offline PIN verifier. PBKDF2-HMAC-SHA256 as fixed in the architecture doc.
/// Never log the PIN. Hashes are still offline-attackable for short codes.
/// </summary>
public static class PinHasher
{
    public const int Iterations = 210_000;
    public const int SaltSizeBytes = 16;
    public const int KeySizeBytes = 32;
    /// <summary>Matches ERP <c>User.pinCode</c> (4 digits) while still accepting longer local PINs.</summary>
    public const int MinPinDigits = 4;

    /// <summary>Demo cashier PIN until ERP staff sync exists. Not a production secret.</summary>
    public const string BootstrapCashierPin = "246810";

    /// <summary>Demo supervisor PIN until ERP staff sync exists. Not a production secret.</summary>
    public const string BootstrapSupervisorPin = "135790";

    public static bool IsValidFormat(string? pin)
    {
        if (string.IsNullOrEmpty(pin) || pin.Length < MinPinDigits || pin.Length > 12)
            return false;
        for (var i = 0; i < pin.Length; i++)
        {
            if (!char.IsAsciiDigit(pin[i]))
                return false;
        }

        return true;
    }

    public static (string HashBase64, string SaltBase64) Hash(string pin)
    {
        if (!IsValidFormat(pin))
            throw new ArgumentException("PIN must be 4–12 ASCII digits.", nameof(pin));

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Pbkdf2(pin, salt);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string pin, string hashBase64, string saltBase64)
    {
        if (!IsValidFormat(pin))
            return false;
        if (string.IsNullOrWhiteSpace(hashBase64) || string.IsNullOrWhiteSpace(saltBase64))
            return false;
        if (string.Equals(hashBase64, "pending-pin-sync", StringComparison.Ordinal)
            || string.Equals(saltBase64, "local", StringComparison.Ordinal))
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(saltBase64);
            expected = Convert.FromBase64String(hashBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length != SaltSizeBytes || expected.Length != KeySizeBytes)
            return false;

        var actual = Pbkdf2(pin, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Pbkdf2(string pin, byte[] salt)
    {
        var bytes = Encoding.UTF8.GetBytes(pin);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                bytes,
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                KeySizeBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
