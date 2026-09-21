using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.Contracts.Security;
using SmartSolar.Modules.Identity.Enums;

namespace SmartSolar.Modules.Identity.PasswordRecovery;

public sealed class ChangePasswordHandler
{
    private readonly IIdentityUnitOfWork _unitOfWork;
    private readonly IPasswordHashingService _passwordHasher;
    private readonly ILogger<ChangePasswordHandler> _logger;

    public ChangePasswordHandler(
        IIdentityUnitOfWork unitOfWork,
        IPasswordHashingService passwordHasher,
        ILogger<ChangePasswordHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _logger = logger;
    }

    public Task<ChangePasswordResult> HandleAsync(
        ChangePasswordCommand command,
        CancellationToken cancellationToken)
        => _unitOfWork.ExecuteInTransactionAsync(
            token => ChangeAsync(command, token),
            cancellationToken);

    private async Task<ChangePasswordResult> ChangeAsync(
        ChangePasswordCommand command,
        CancellationToken cancellationToken)
    {
        // The identifier comes from the authenticated principal, never the request body.
        var user = await _unitOfWork.FindUserByIdAsync(command.UserId, cancellationToken);

        if (user is null || user.Status != UserStatus.Active || user.DeletedAt is not null)
        {
            return ChangePasswordResult.AccountNotActive;
        }

        if (user.PasswordHash is null
            || !_passwordHasher.VerifyPassword(user, user.PasswordHash, command.CurrentPassword))
        {
            return ChangePasswordResult.InvalidCurrentPassword;
        }

        var now = DateTimeOffset.UtcNow;
        user.PasswordHash = _passwordHasher.HashPassword(user, command.NewPassword);
        user.UpdatedAt = now;

        // Other sessions must not survive a password change.
        await _unitOfWork.RevokeActiveRefreshTokensAsync(user.Id, now, cancellationToken);

        _logger.LogInformation("Password changed for user {UserId}.", user.Id);

        return ChangePasswordResult.Succeeded;
    }
}
