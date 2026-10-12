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

        if (resetToken is null)
        {
            // Admin-provisioned accounts receive an ACCOUNT_SETUP link that posts here too.
            var setupToken = await _unitOfWork.FindActionTokenAsync(tokenHash, AuthActionTokenType.AccountSetup, cancellationToken);
            if (setupToken is not null)
            {
                return await CompleteAccountSetupAsync(setupToken, newPassword, now, cancellationToken);
            }
        }

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

    /// <summary>
    /// First password of an admin-provisioned account. Opening the emailed single-use link is the
    /// proof of email ownership, so only here does the account become verified and ACTIVE.
    /// Suspended, disabled and deleted accounts are refused with the same answer as a bad token.
    /// </summary>
    private async Task<ResetPasswordResult> CompleteAccountSetupAsync(
        Entities.AuthActionToken setupToken,
        string newPassword,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (setupToken.UsedAt is not null || setupToken.ExpiresAt <= now)
        {
            return ResetPasswordResult.InvalidOrExpired;
        }

        var user = await _unitOfWork.FindUserByIdAsync(setupToken.UserId, cancellationToken);

        if (user is null
            || user.DeletedAt is not null
            || user.Status is not (UserStatus.PendingVerification or UserStatus.Active))
        {
            return ResetPasswordResult.InvalidOrExpired;
        }

        user.PasswordHash = _passwordHasher.HashPassword(user, newPassword);
        user.EmailVerifiedAt ??= now;
        user.Status = UserStatus.Active;
        user.UpdatedAt = now;
        setupToken.UsedAt = now;

        await _unitOfWork.RevokeActiveRefreshTokensAsync(user.Id, now, cancellationToken);

        _logger.LogInformation("Account setup completed for user {UserId}.", user.Id);

        return ResetPasswordResult.Succeeded;
    }
}
