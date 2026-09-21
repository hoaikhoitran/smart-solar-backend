using System.Security.Cryptography;
using System.Text;

namespace SmartSolar.Modules.Identity.Security;

/// <summary>
/// Creates high-entropy tokens for links and refresh tokens. Only the SHA-256
/// hash is ever persisted; the raw token exists to be handed to its owner.
/// </summary>
public sealed class SecureTokenFactory
{
    private const int TokenBytes = 32;

    public SecureToken Create()
    {
        var raw = ToBase64Url(RandomNumberGenerator.GetBytes(TokenBytes));

        return new SecureToken(raw, Hash(raw));
    }

    public static string Hash(string rawToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
    }

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

public sealed record SecureToken(string RawToken, string TokenHash);
