using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.Contracts.Security;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Security;

namespace SmartSolar.Modules.Identity.PasswordRecovery;

public sealed class ResetPasswordHandler
{
    private readonly IIdentityUnitOfWork _unitOfWork;
    private readonly IPasswordHashingService _passwordHasher;
    private readonly ILogger<ResetPasswordHandler> _logger;

    public ResetPasswordHandler(
        IIdentityUnitOfWork unitOfWork,
        IPasswordHashingService passwordHasher,
        ILogger<ResetPasswordHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    public async Task<ResetPasswordResult> HandleAsync(
        ResetPasswordCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Token))
        {
            return ResetPasswordResult.InvalidOrExpired;
        }

        var tokenHash = SecureTokenFactory.Hash(command.Token);

        return await _unitOfWork.ExecuteInTransactionAsync(
            token => ResetAsync(tokenHash, command.NewPassword, token),
            cancellationToken);
    }

    private async Task<ResetPasswordResult> ResetAsync(
        string tokenHash,
        string newPassword,
        CancellationToken cancellationToken)
    {
        var resetToken = await _unitOfWork.FindActionTokenAsync(
            tokenHash,
            AuthActionTokenType.PasswordReset,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;

        // One outcome for unknown, used and expired tokens.
        if (resetToken is null || resetToken.UsedAt is not null || resetToken.ExpiresAt <= now)
        {
            return ResetPasswordResult.InvalidOrExpired;
        }

        var user = await _unitOfWork.FindUserByIdAsync(resetToken.UserId, cancellationToken);

        if (user is null || user.Status != UserStatus.Active || user.DeletedAt is not null)
        {
            // A suspended account must not have its credentials rewritten.
            return ResetPasswordResult.InvalidOrExpired;
        }

        user.PasswordHash = _passwordHasher.HashPassword(user, newPassword);
        user.UpdatedAt = now;
        resetToken.UsedAt = now;

        // A refresh token stolen before the reset must not keep minting access tokens.
        await _unitOfWork.RevokeActiveRefreshTokensAsync(user.Id, now, cancellationToken);

        _logger.LogInformation("Password reset completed for user {UserId}.", user.Id);

        return ResetPasswordResult.Succeeded;
    }
}
