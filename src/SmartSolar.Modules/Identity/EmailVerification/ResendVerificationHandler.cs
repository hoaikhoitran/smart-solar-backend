using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Messaging;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Options;

namespace SmartSolar.Modules.Identity.EmailVerification;

public sealed class ResendVerificationHandler
{
    private readonly IIdentityUnitOfWork _unitOfWork;
    private readonly EmailVerificationTokenFactory _tokenFactory;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly EmailVerificationOptions _verificationOptions;
    private readonly FrontendOptions _frontendOptions;
    private readonly ILogger<ResendVerificationHandler> _logger;

    public ResendVerificationHandler(
        IIdentityUnitOfWork unitOfWork,
        EmailVerificationTokenFactory tokenFactory,
        IIntegrationEventPublisher publisher,
        EmailVerificationOptions verificationOptions,
        FrontendOptions frontendOptions,
        ILogger<ResendVerificationHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _tokenFactory = tokenFactory;
        _publisher = publisher;
        _verificationOptions = verificationOptions;
        _frontendOptions = frontendOptions;
        _logger = logger;
    }

    public async Task<ResendVerificationResult> HandleAsync(
        ResendVerificationCommand command,
        CancellationToken cancellationToken)
    {
        var email = EmailNormalizer.Normalize(command.Email);

        var pending = await _unitOfWork.ExecuteInTransactionAsync(
            token => ReplaceTokenAsync(email, token),
            cancellationToken);

        if (pending is not null)
        {
            await PublishAsync(pending, cancellationToken);
        }

        // Identical result for every account state.
        return ResendVerificationResult.Accepted_;
    }

    private async Task<PendingVerification?> ReplaceTokenAsync(
        string email,
        CancellationToken cancellationToken)
    {
        var user = await _unitOfWork.FindAccountForResendAsync(email, cancellationToken);

        // Missing, already verified and OAuth-only accounts all fall through
        // without any side effect.
        if (user is null || user.IsVerified || !user.HasPassword)
        {
            return null;
        }

        // Only unused EMAIL_VERIFICATION rows are replaced; PASSWORD_RESET is untouched.
        await _unitOfWork.RemoveUnusedTokensAsync(
            user.UserId,
            AuthActionTokenType.EmailVerification,
            cancellationToken);

        var verificationToken = _tokenFactory.Create();
        var now = DateTimeOffset.UtcNow;

        _unitOfWork.AddAuthActionToken(new AuthActionToken
        {
            Id = Guid.NewGuid(),
            UserId = user.UserId,
            TokenHash = verificationToken.TokenHash,
            Type = AuthActionTokenType.EmailVerification,
            ExpiresAt = now.AddMinutes(_verificationOptions.LifetimeMinutes)
        });

        return new PendingVerification(user.UserId, user.Email, user.FullName, verificationToken.RawToken);
    }

    private async Task PublishAsync(PendingVerification pending, CancellationToken cancellationToken)
    {
        try
        {
            await _publisher.PublishAsync(
                new EmailVerificationRequestedEvent(
                    pending.UserId,
                    pending.Email,
                    pending.FullName,
                    VerificationUrlBuilder.Build(_frontendOptions.EmailVerificationUrl, pending.RawToken)),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to publish email verification event for user {UserId}.",
                pending.UserId);
        }
    }

    private sealed record PendingVerification(Guid UserId, string Email, string FullName, string RawToken);
}
