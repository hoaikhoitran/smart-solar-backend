using System.Security.Cryptography;
using System.Text;

namespace SmartSolar.Modules.Identity.EmailVerification;

/// <summary>
/// Creates email verification tokens. Only the SHA-256 hash is ever persisted;
/// the raw token exists solely to be mailed to the user.
/// </summary>
public sealed class EmailVerificationTokenFactory
{
    private const int TokenBytes = 32;

    public EmailVerificationToken Create()
    {
        var raw = ToBase64Url(RandomNumberGenerator.GetBytes(TokenBytes));

        return new EmailVerificationToken(raw, Hash(raw));
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

public sealed record EmailVerificationToken(string RawToken, string TokenHash);
