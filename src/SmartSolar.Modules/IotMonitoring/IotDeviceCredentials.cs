using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartSolar.Modules.IotMonitoring;

/// <summary>
/// Device secrets: 256 random bits from the CSPRNG, handed out once, stored only as a SHA-256 hash (the same scheme as refresh
/// tokens: the secret has far too much entropy for brute force, so no slow password hash is needed). Comparison is constant time.
/// </summary>
public static partial class IotDeviceCredentials
{
    /// <summary>Prefix that makes a leaked secret recognisable in scanners and logs.</summary>
    public const string SecretPrefix = "ssdk_";

    private const int SecretBytes = 32;

    public static string NewSecret()
        => SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    public static bool Matches(string? presentedSecret, string storedHash)
    {
        if (string.IsNullOrEmpty(presentedSecret) || presentedSecret.Length > 256) return false;
        var presented = Encoding.UTF8.GetBytes(Hash(presentedSecret));
        var stored = Encoding.UTF8.GetBytes(storedHash);
        return presented.Length == stored.Length && CryptographicOperations.FixedTimeEquals(presented, stored);
    }

    /// <summary>Upper-cases and trims the firmware identifier; null when it is not a valid identifier (3–64 of A-Z 0-9 . _ -).</summary>
    public static string? NormalizeDeviceId(string? raw)
    {
        var value = raw?.Trim().ToUpperInvariant();
        return value is not null && DeviceIdPattern().IsMatch(value) ? value : null;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{2,63}$")]
    private static partial Regex DeviceIdPattern();
}
