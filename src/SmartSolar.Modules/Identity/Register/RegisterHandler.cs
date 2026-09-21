using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Messaging;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.Contracts.Security;
using SmartSolar.Modules.Identity.EmailVerification;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Options;

namespace SmartSolar.Modules.Identity.Register;

public sealed class RegisterHandler
{
    private readonly IIdentityUnitOfWork _unitOfWork;
    private readonly IPasswordHashingService _passwordHasher;
    private readonly EmailVerificationTokenFactory _tokenFactory;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly EmailVerificationOptions _verificationOptions;
    private readonly FrontendOptions _frontendOptions;
    private readonly ILogger<RegisterHandler> _logger;

    public RegisterHandler(
        IIdentityUnitOfWork unitOfWork,
        IPasswordHashingService passwordHasher,
        EmailVerificationTokenFactory tokenFactory,
        IIntegrationEventPublisher publisher,
        EmailVerificationOptions verificationOptions,
        FrontendOptions frontendOptions,
        ILogger<RegisterHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _tokenFactory = tokenFactory;
        _publisher = publisher;
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
        _unitOfWork.AddAuthActionToken(new AuthActionToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = verificationToken.TokenHash,
            Type = AuthActionTokenType.EmailVerification,
            ExpiresAt = now.AddMinutes(_verificationOptions.LifetimeMinutes)
        });

        return new CreatedAccount(user.Id, user.Email, user.FullName, verificationToken.RawToken);
    }

    private async Task PublishVerificationEventAsync(
        CreatedAccount account,
        CancellationToken cancellationToken)
    {
        try
        {
            await _publisher.PublishAsync(
                new EmailVerificationRequestedEvent(
                    account.UserId,
                    account.Email,
                    account.FullName,
                    VerificationUrlBuilder.Build(_frontendOptions.EmailVerificationUrl, account.RawToken)),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // The account is already committed; the user can request a new email.
            // The URL is never logged because it carries the raw token.
            _logger.LogError(
                ex,
                "Failed to publish email verification event for user {UserId}.",
                account.UserId);
        }
    }

    private sealed record CreatedAccount(Guid UserId, string Email, string FullName, string RawToken);
}
