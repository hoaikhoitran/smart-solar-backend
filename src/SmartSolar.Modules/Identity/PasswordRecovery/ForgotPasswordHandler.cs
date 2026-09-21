using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Messaging;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.EmailVerification;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Modules.Identity.Security;

namespace SmartSolar.Modules.Identity.PasswordRecovery;

public sealed class ForgotPasswordHandler
{
    private readonly IIdentityUnitOfWork _unitOfWork;
    private readonly SecureTokenFactory _tokenFactory;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly PasswordResetOptions _resetOptions;
    private readonly FrontendOptions _frontendOptions;
    private readonly ILogger<ForgotPasswordHandler> _logger;

    public ForgotPasswordHandler(
        IIdentityUnitOfWork unitOfWork,
        SecureTokenFactory tokenFactory,
        IIntegrationEventPublisher publisher,
        PasswordResetOptions resetOptions,
        FrontendOptions frontendOptions,
        ILogger<ForgotPasswordHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _tokenFactory = tokenFactory;
        _publisher = publisher;
        _resetOptions = resetOptions;
        _frontendOptions = frontendOptions;
        _logger = logger;
    }

    public async Task<ForgotPasswordResult> HandleAsync(
        ForgotPasswordCommand command,
        CancellationToken cancellationToken)
    {
        var email = EmailNormalizer.Normalize(command.Email);

        var pending = await _unitOfWork.ExecuteInTransactionAsync(
            token => CreateResetTokenAsync(email, token),
            cancellationToken);

        // The database is committed before the event is published.
        if (pending is not null)
        {
            await PublishAsync(pending, cancellationToken);
        }

        return ForgotPasswordResult.Accepted_;
    }

    private async Task<PendingReset?> CreateResetTokenAsync(
        string email,
        CancellationToken cancellationToken)
    {
        var user = await _unitOfWork.FindUserByEmailAsync(email, cancellationToken);

        // Unknown, OAuth-only (no local password) and non-ACTIVE accounts all
        // fall through without any side effect.
        if (user is null
            || user.PasswordHash is null
            || user.Status != UserStatus.Active
            || user.DeletedAt is not null)
        {
            return null;
        }

        // Only unused PASSWORD_RESET rows are replaced; EMAIL_VERIFICATION is untouched.
        await _unitOfWork.RemoveUnusedTokensAsync(
            user.Id,
            AuthActionTokenType.PasswordReset,
            cancellationToken);

        var resetToken = _tokenFactory.Create();
        var now = DateTimeOffset.UtcNow;

        _unitOfWork.AddAuthActionToken(new AuthActionToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = resetToken.TokenHash,
            Type = AuthActionTokenType.PasswordReset,
            ExpiresAt = now.AddMinutes(_resetOptions.LifetimeMinutes)
        });

        return new PendingReset(user.Id, user.Email, user.FullName, resetToken.RawToken);
    }

    private async Task PublishAsync(PendingReset pending, CancellationToken cancellationToken)
    {
        try
        {
            await _publisher.PublishAsync(
                new PasswordResetRequestedEvent(
                    pending.UserId,
                    pending.Email,
                    pending.FullName,
                    VerificationUrlBuilder.Build(_frontendOptions.PasswordResetUrl, pending.RawToken)),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // The token is already committed; the user can request another email.
            // The URL is never logged because it carries the raw token.
            _logger.LogError(ex, "Failed to publish password reset event for user {UserId}.", pending.UserId);
        }
    }

    private sealed record PendingReset(Guid UserId, string Email, string FullName, string RawToken);
}
