using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.Enums;

namespace SmartSolar.Modules.Identity.EmailVerification;

public sealed class VerifyEmailHandler
{
    private readonly IIdentityUnitOfWork _unitOfWork;
    private readonly ILogger<VerifyEmailHandler> _logger;

    public VerifyEmailHandler(IIdentityUnitOfWork unitOfWork, ILogger<VerifyEmailHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<VerifyEmailResult> HandleAsync(
        VerifyEmailCommand command,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Token))
        {
            return VerifyEmailResult.InvalidOrExpired;
        }

        var tokenHash = EmailVerificationTokenFactory.Hash(command.Token);

        // The lookup and the update share one retriable transaction.
        return await _unitOfWork.ExecuteInTransactionAsync(
            token => VerifyAsync(tokenHash, token),
            cancellationToken);
    }

    private async Task<VerifyEmailResult> VerifyAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var actionToken = await _unitOfWork.FindActionTokenAsync(
            tokenHash,
            AuthActionTokenType.EmailVerification,
            cancellationToken);

        if (actionToken is null)
        {
            return VerifyEmailResult.InvalidOrExpired;
        }

        var user = await _unitOfWork.FindUserByIdAsync(actionToken.UserId, cancellationToken);

        if (user is null)
        {
            return VerifyEmailResult.InvalidOrExpired;
        }

        if (user.EmailVerifiedAt is not null)
        {
            // Idempotent: a repeated click on a real link succeeds without
            // moving the original timestamp, whether or not the token was used.
            return VerifyEmailResult.Verified;
        }

        var now = DateTimeOffset.UtcNow;

        if (actionToken.UsedAt is not null || actionToken.ExpiresAt <= now)
        {
            // One outcome for unknown, used and expired tokens: no state disclosure.
            return VerifyEmailResult.InvalidOrExpired;
        }

        user.EmailVerifiedAt = now;
        user.UpdatedAt = now;
        actionToken.UsedAt = now;

        if (user.Status == UserStatus.PendingVerification)
        {
            // Suspended or disabled accounts stay that way.
            user.Status = UserStatus.Active;
        }

        _logger.LogInformation("Email verified for user {UserId}.", user.Id);

        return VerifyEmailResult.Verified;
    }
}
