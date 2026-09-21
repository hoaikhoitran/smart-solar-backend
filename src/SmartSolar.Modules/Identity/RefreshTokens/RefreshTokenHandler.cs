using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.Contracts.Security;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Security;

namespace SmartSolar.Modules.Identity.RefreshTokens;

public sealed class RefreshTokenHandler
{
    private readonly IIdentityUnitOfWork _unitOfWork;
    private readonly IAccessTokenService _accessTokens;
    private readonly RefreshTokenIssuer _refreshTokens;
    private readonly ILogger<RefreshTokenHandler> _logger;

    public RefreshTokenHandler(
        IIdentityUnitOfWork unitOfWork,
        IAccessTokenService accessTokens,
        RefreshTokenIssuer refreshTokens,
        ILogger<RefreshTokenHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _accessTokens = accessTokens;
        _refreshTokens = refreshTokens;
        _logger = logger;
    }

    public async Task<RefreshResult> HandleAsync(
        RefreshTokenCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            return RefreshResult.Invalid;
        }

        var tokenHash = SecureTokenFactory.Hash(command.RefreshToken);

        // Revoking the old token and storing the new one share one transaction,
        // so a rotation can never leave both or neither usable.
        return await _unitOfWork.ExecuteInTransactionAsync(
            token => RotateAsync(tokenHash, token),
            cancellationToken);
    }

    private async Task<RefreshResult> RotateAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var existing = await _unitOfWork.FindRefreshTokenAsync(tokenHash, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        // One outcome for unknown, revoked and expired tokens.
        if (existing is null || existing.ExpiresAt <= now)
        {
            return RefreshResult.Invalid;
        }

        if (existing.RevokedAt is not null)
        {
            // A token that was already rotated is being replayed: the only way
            // that happens is if it leaked. Cut off both branches of the fork
            // and force a password-backed sign-in.
            await _unitOfWork.RevokeActiveRefreshTokensAsync(existing.UserId, now, cancellationToken);

            _logger.LogWarning(
                "Replayed refresh token for user {UserId}; all sessions revoked.", existing.UserId);

            return RefreshResult.Invalid;
        }

        var user = await _unitOfWork.FindUserByIdAsync(existing.UserId, cancellationToken);

        if (user is null || user.Status != UserStatus.Active || user.DeletedAt is not null)
        {
            return RefreshResult.Invalid;
        }

        // Claim the token conditionally; losing the race means it was spent.
        if (!await _unitOfWork.TryRevokeRefreshTokenAsync(existing.Id, now, cancellationToken))
        {
            return RefreshResult.Invalid;
        }

        var roleCodes = await _unitOfWork.GetRoleCodesAsync(user.Id, cancellationToken);
        var accessToken = _accessTokens.Generate(user, roleCodes);
        var refreshToken = _refreshTokens.Issue(user.Id, now);

        _logger.LogInformation("Refresh token rotated for user {UserId}.", user.Id);

        return new RefreshResult(
            RefreshOutcome.Succeeded,
            user.Id,
            accessToken.Token,
            accessToken.ExpiresAt,
            refreshToken.RawToken,
            refreshToken.ExpiresAt);
    }
}

public sealed class LogoutHandler
{
    private readonly IIdentityUnitOfWork _unitOfWork;

    public LogoutHandler(IIdentityUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<LogoutResult> HandleAsync(LogoutCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            return LogoutResult.Accepted_;
        }

        var tokenHash = SecureTokenFactory.Hash(command.RefreshToken);

        await _unitOfWork.ExecuteInTransactionAsync<object?>(
            async token =>
            {
                var existing = await _unitOfWork.FindRefreshTokenAsync(tokenHash, token);

                // Idempotent: an unknown or already revoked token is left alone
                // and the caller cannot tell the difference.
                if (existing is not null && existing.RevokedAt is null)
                {
                    existing.RevokedAt = DateTimeOffset.UtcNow;
                }

                return null;
            },
            cancellationToken);

        return LogoutResult.Accepted_;
    }
}
