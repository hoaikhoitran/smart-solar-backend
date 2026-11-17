using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.Contracts.Security;
using SmartSolar.Modules.Identity.EmailVerification;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Modules.Identity.PasswordRecovery;

namespace SmartSolar.Modules.Identity.Register;

public sealed class RegisterHandler
{
    private readonly IIdentityUnitOfWork _unitOfWork;
    private readonly IPasswordHashingService _passwordHasher;
    private readonly EmailVerificationTokenFactory _tokenFactory;
    private readonly PasswordResetDispatcher _dispatcher;
    private readonly EmailVerificationOptions _verificationOptions;
    private readonly FrontendOptions _frontendOptions;
    private readonly ILogger<RegisterHandler> _logger;

    public RegisterHandler(
        IIdentityUnitOfWork unitOfWork,
        IPasswordHashingService passwordHasher,
        EmailVerificationTokenFactory tokenFactory,
        PasswordResetDispatcher dispatcher,
        EmailVerificationOptions verificationOptions,
        FrontendOptions frontendOptions,
        ILogger<RegisterHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _tokenFactory = tokenFactory;
        _dispatcher = dispatcher;
        _verificationOptions = verificationOptions;
        _frontendOptions = frontendOptions;
        _logger = logger;
    }

    public async Task<RegisterResult> HandleAsync(
        RegisterCommand command,
        CancellationToken cancellationToken)
    {
        var email = EmailNormalizer.Normalize(command.Email);

        if (await _unitOfWork.EmailExistsAsync(email, cancellationToken))
        {
            return RegisterResult.DuplicateEmail(email);
        }

        var customerRoleId = await _unitOfWork.FindRoleIdByCodeAsync(RoleCodes.Customer, cancellationToken)
            ?? throw new CustomerRoleMissingException(RoleCodes.Customer);

        CreatedAccount created;

        try
        {
            created = await _unitOfWork.ExecuteInTransactionAsync(
                token => Task.FromResult(CreateAccount(command, email, customerRoleId)),
                cancellationToken);
        }
        catch (DuplicateEmailException)
        {
            // Lost the race against the unique email index; same answer as the pre-check.
            return RegisterResult.DuplicateEmail(email);
        }

        await PublishVerificationEventAsync(created, cancellationToken);

        return RegisterResult.Created(created.UserId, created.Email);
    }

    private CreatedAccount CreateAccount(RegisterCommand command, string email, Guid customerRoleId)
    {
        var now = DateTimeOffset.UtcNow;

        var user = new UserAccount
        {
            Id = Guid.NewGuid(),
            Email = email,
            Phone = string.IsNullOrWhiteSpace(command.Phone) ? null : command.Phone.Trim(),
            FullName = command.FullName.Trim(),
            Status = UserStatus.PendingVerification,
            CreatedAt = now,
            UpdatedAt = now
        };
        user.PasswordHash = _passwordHasher.HashPassword(user, command.Password);

        var verificationToken = _tokenFactory.Create();

        _unitOfWork.AddUserAccount(user);
        _unitOfWork.AddUserRole(new UserRole
        {
            UserId = user.Id,
            RoleId = customerRoleId,
            AssignedAt = now
        });
        var tokenId = Guid.NewGuid();
        var expiresAt = now.AddMinutes(_verificationOptions.LifetimeMinutes);
        _unitOfWork.AddAuthActionToken(new AuthActionToken
        {
            Id = tokenId,
            UserId = user.Id,
            TokenHash = verificationToken.TokenHash,
            Type = AuthActionTokenType.EmailVerification,
            ExpiresAt = expiresAt
        });

        // The verification email is an outbox row of the same transaction: a broker or SMTP outage cannot lose it (audit INT-02).
        var dispatch = AuthLinkDispatches.Pending(user.Id, tokenId, user.Email, user.FullName,
            VerificationUrlBuilder.Build(_frontendOptions.EmailVerificationUrl, verificationToken.RawToken), now, expiresAt);
        _unitOfWork.AddPasswordResetDispatch(dispatch);

        return new CreatedAccount(user.Id, user.Email, dispatch.Id);
    }

    /// <summary>One publish attempt after the commit; on failure the outbox worker retries until the token expires.</summary>
    private Task PublishVerificationEventAsync(
        CreatedAccount account,
        CancellationToken cancellationToken)
        => _dispatcher.TryPublishAsync(account.DispatchId, cancellationToken);

    private sealed record CreatedAccount(Guid UserId, string Email, Guid DispatchId);
}
