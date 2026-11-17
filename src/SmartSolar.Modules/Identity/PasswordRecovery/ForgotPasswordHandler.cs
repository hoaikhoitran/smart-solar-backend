using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.EmailVerification;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Modules.Identity.Security;

namespace SmartSolar.Modules.Identity.PasswordRecovery;

/// <summary>
/// Creates the reset token and, in the same transaction, the outbox row of its email (<see cref="PasswordResetDispatch"/>). After the
/// commit one publish is attempted; a broker outage no longer loses the request (the worker retries until the token expires). The
/// answer is the same for every account state (no account enumeration) and only means "accepted", never "email sent".
/// </summary>
public sealed class ForgotPasswordHandler
{
    private readonly IIdentityUnitOfWork _unitOfWork;
    private readonly SecureTokenFactory _tokenFactory;
    private readonly PasswordResetDispatcher _dispatcher;
    private readonly PasswordResetOptions _resetOptions;
    private readonly FrontendOptions _frontendOptions;
    private readonly TimeProvider _time;

    public ForgotPasswordHandler(
        IIdentityUnitOfWork unitOfWork,
        SecureTokenFactory tokenFactory,
        PasswordResetDispatcher dispatcher,
        PasswordResetOptions resetOptions,
        FrontendOptions frontendOptions,
        TimeProvider time)
    {
        _time = time;
        _unitOfWork = unitOfWork;
        _tokenFactory = tokenFactory;
        _dispatcher = dispatcher;
        _resetOptions = resetOptions;
        _frontendOptions = frontendOptions;
    }

    public async Task<ForgotPasswordResult> HandleAsync(
        ForgotPasswordCommand command,
        CancellationToken cancellationToken)
    {
        var email = EmailNormalizer.Normalize(command.Email);

        var dispatchId = await _unitOfWork.ExecuteInTransactionAsync(
            token => CreateResetTokenAsync(email, token),
            cancellationToken);

        // Token and outbox row are committed together; publishing happens after the commit and may be retried later.
        if (dispatchId is { } id)
        {
            await _dispatcher.TryPublishAsync(id, cancellationToken);
        }

        return ForgotPasswordResult.Accepted_;
    }

    private async Task<Guid?> CreateResetTokenAsync(
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
        // Same clock as the dispatcher that claims the outbox row (due immediately).
        var now = _time.GetUtcNow();
        var expiresAt = now.AddMinutes(_resetOptions.LifetimeMinutes);
        var tokenId = Guid.NewGuid();

        _unitOfWork.AddAuthActionToken(new AuthActionToken
        {
            Id = tokenId,
            UserId = user.Id,
            TokenHash = resetToken.TokenHash,
            Type = AuthActionTokenType.PasswordReset,
            ExpiresAt = expiresAt
        });

        var dispatch = AuthLinkDispatches.Pending(user.Id, tokenId, user.Email, user.FullName,
            VerificationUrlBuilder.Build(_frontendOptions.PasswordResetUrl, resetToken.RawToken), now, expiresAt);
        _unitOfWork.AddPasswordResetDispatch(dispatch);

        return dispatch.Id;
    }
}
