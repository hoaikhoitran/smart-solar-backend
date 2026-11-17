using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Modules.Identity.PasswordRecovery;

namespace SmartSolar.Modules.Identity.EmailVerification;

public sealed class ResendVerificationHandler
{
    private readonly IIdentityUnitOfWork _unitOfWork;
    private readonly EmailVerificationTokenFactory _tokenFactory;
    private readonly PasswordResetDispatcher _dispatcher;
    private readonly EmailVerificationOptions _verificationOptions;
    private readonly FrontendOptions _frontendOptions;
    private readonly ILogger<ResendVerificationHandler> _logger;

    public ResendVerificationHandler(
        IIdentityUnitOfWork unitOfWork,
        EmailVerificationTokenFactory tokenFactory,
        PasswordResetDispatcher dispatcher,
        EmailVerificationOptions verificationOptions,
        FrontendOptions frontendOptions,
        ILogger<ResendVerificationHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _tokenFactory = tokenFactory;
        _dispatcher = dispatcher;
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

        var tokenId = Guid.NewGuid();
        var expiresAt = now.AddMinutes(_verificationOptions.LifetimeMinutes);
        _unitOfWork.AddAuthActionToken(new AuthActionToken
        {
            Id = tokenId,
            UserId = user.UserId,
            TokenHash = verificationToken.TokenHash,
            Type = AuthActionTokenType.EmailVerification,
            ExpiresAt = expiresAt
        });

        // Outbox row of the same transaction (audit INT-02); the replaced token's pending row is removed with it (cascade).
        var dispatch = AuthLinkDispatches.Pending(user.UserId, tokenId, user.Email, user.FullName,
            VerificationUrlBuilder.Build(_frontendOptions.EmailVerificationUrl, verificationToken.RawToken), now, expiresAt);
        _unitOfWork.AddPasswordResetDispatch(dispatch);

        return new PendingVerification(dispatch.Id);
    }

    /// <summary>One publish attempt after the commit; on failure the outbox worker retries until the token expires.</summary>
    private Task PublishAsync(PendingVerification pending, CancellationToken cancellationToken)
        => _dispatcher.TryPublishAsync(pending.DispatchId, cancellationToken);

    private sealed record PendingVerification(Guid DispatchId);
}
