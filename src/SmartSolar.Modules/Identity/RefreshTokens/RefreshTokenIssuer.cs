using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Modules.Identity.Security;

namespace SmartSolar.Modules.Identity.RefreshTokens;

/// <summary>
/// Creates a refresh token and queues only its hash for persistence. Callers
/// run this inside their own transaction and hand the raw token to the client.
/// </summary>
public sealed class RefreshTokenIssuer
{
    private readonly IIdentityUnitOfWork _unitOfWork;
    private readonly SecureTokenFactory _tokenFactory;
    private readonly RefreshTokenOptions _options;

    public RefreshTokenIssuer(
        IIdentityUnitOfWork unitOfWork,
        SecureTokenFactory tokenFactory,
        RefreshTokenOptions options)
    {
        _unitOfWork = unitOfWork;
        _tokenFactory = tokenFactory;
        _options = options;
    }

    public IssuedRefreshToken Issue(Guid userId, DateTimeOffset now)
    {
        var token = _tokenFactory.Create();
        var expiresAt = now.AddDays(_options.LifetimeDays);

        _unitOfWork.AddRefreshToken(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = token.TokenHash,
            ExpiresAt = expiresAt
        });

        return new IssuedRefreshToken(token.RawToken, expiresAt);
    }
}

public sealed record IssuedRefreshToken(string RawToken, DateTimeOffset ExpiresAt);
