using SmartSolar.Modules.Identity.Security;

namespace SmartSolar.Modules.Identity.EmailVerification;

/// <summary>
/// Creates email verification tokens. Only the SHA-256 hash is ever persisted;
/// the raw token exists solely to be mailed to the user.
/// </summary>
public sealed class EmailVerificationTokenFactory
{
    private readonly SecureTokenFactory _tokens = new();

    public EmailVerificationToken Create()
    {
        var token = _tokens.Create();

        return new EmailVerificationToken(token.RawToken, token.TokenHash);
    }

    public static string Hash(string rawToken) => SecureTokenFactory.Hash(rawToken);
}

public sealed record EmailVerificationToken(string RawToken, string TokenHash);
