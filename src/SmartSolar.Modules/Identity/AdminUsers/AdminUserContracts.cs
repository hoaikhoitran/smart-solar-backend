using FluentValidation;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.Enums;

namespace SmartSolar.Modules.Identity.AdminUsers;

public static class AdminUserErrorCodes
{
    public const string ValidationFailed = AuthErrorCodes.ValidationFailed;
    public const string UserNotFound = "ADMIN_USER_NOT_FOUND";
    public const string EmailAlreadyExists = AuthErrorCodes.EmailAlreadyExists;
    public const string InvalidRole = "ADMIN_INVALID_ROLE";
    public const string InvalidStatusTransition = "ADMIN_INVALID_STATUS_TRANSITION";
    public const string LastActiveAdmin = "ADMIN_LAST_ACTIVE_ADMIN";
    public const string CannotModifyOwnAccount = "ADMIN_CANNOT_MODIFY_OWN_ACCOUNT";
    public const string AccountHasActiveWork = "ACCOUNT_HAS_ACTIVE_WORK";
    public const string AccountDeleted = "ADMIN_USER_DELETED";
    public const string RecoveryNotAllowed = "ADMIN_RECOVERY_NOT_ALLOWED";
}

/// <summary>Handler outcome: a value or an error code with a safe message and optional details.</summary>
public sealed record AdminResult<T>(T? Value, string? ErrorCode, string? Message, object? Details = null)
{
    public bool Succeeded => ErrorCode is null;
    public static AdminResult<T> Ok(T value) => new(value, null, null);
    public static AdminResult<T> Fail(string code, string message, object? details = null) => new(default, code, message, details);
}

public static class UserStatusCodes
{
    public const string PendingVerification = "PENDING_VERIFICATION";
    public const string Active = "ACTIVE";
    public const string Suspended = "SUSPENDED";
    public const string Disabled = "DISABLED";

    public static string ToCode(UserStatus status) => status switch
    {
        UserStatus.PendingVerification => PendingVerification,
        UserStatus.Active => Active,
        UserStatus.Suspended => Suspended,
        UserStatus.Disabled => Disabled,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static bool TryParse(string? code, out UserStatus status)
    {
        switch (code?.Trim().ToUpperInvariant())
        {
            case PendingVerification: status = UserStatus.PendingVerification; return true;
            case Active: status = UserStatus.Active; return true;
            case Suspended: status = UserStatus.Suspended; return true;
            case Disabled: status = UserStatus.Disabled; return true;
            default: status = default; return false;
        }
    }
}

public sealed record ListAdminUsersQuery(int Page, int PageSize, string? Search, string? Role, string? Status, string? Sort, string? Direction, bool IncludeDeleted);

public sealed record CreateAdminUserCommand(Guid ActorUserId, string Email, string FullName, string? Phone, IReadOnlyList<string> Roles);

public sealed record UpdateAdminUserCommand(Guid ActorUserId, Guid UserId, string FullName, string? Phone, string? Email);

public sealed record ChangeUserStatusCommand(Guid ActorUserId, Guid UserId, string Status, string? Reason);

public sealed record ReplaceUserRolesCommand(Guid ActorUserId, Guid UserId, IReadOnlyList<string> Roles);

public sealed record DeleteAdminUserCommand(Guid ActorUserId, Guid UserId, string? Reason);

public sealed record SendRecoveryCommand(Guid ActorUserId, Guid UserId);

public sealed record AdminUserListItem(
    Guid Id, string Email, string FullName, string? Phone, string Status, IReadOnlyList<string> Roles,
    DateTimeOffset? EmailVerifiedAt, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt);

public sealed record AdminUserDetail(
    Guid Id, string Email, string FullName, string? Phone, string Status, IReadOnlyList<string> Roles,
    DateTimeOffset? EmailVerifiedAt, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt,
    bool HasPassword, TechnicianProfileSummary? Technician);

/// <summary>
/// How an admin-provisioned account gets activated. The raw link is never returned: it is only
/// emailed. When email delivery is switched off the state says so instead of pretending.
/// </summary>
public sealed record ActivationState(string Status, string EmailDelivery, DateTimeOffset? SetupLinkExpiresAt, string Message);

public static class ActivationStatuses
{
    public const string PendingAccountSetup = "PENDING_ACCOUNT_SETUP";
    public const string PendingEmailVerification = "PENDING_EMAIL_VERIFICATION";
    public const string PasswordResetIssued = "PASSWORD_RESET_ISSUED";
}

public static class EmailDeliveryStates
{
    public const string Queued = "QUEUED";
    public const string Disabled = "EMAIL_DELIVERY_DISABLED";
    public const string PublishFailed = "PUBLISH_FAILED";
}

public sealed record CreatedAdminUser(AdminUserDetail User, ActivationState Activation);

public sealed record UpdatedAdminUser(AdminUserDetail User, ActivationState? Activation);

public sealed record RecoveryIssued(Guid UserId, ActivationState Activation);

public sealed class CreateAdminUserCommandValidator : AbstractValidator<CreateAdminUserCommand>
{
    public CreateAdminUserCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(255).EmailAddress();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Phone).MaximumLength(20).Matches(@"^\+?[0-9 .\-]{6,20}$").When(x => !string.IsNullOrWhiteSpace(x.Phone));
        RuleFor(x => x.Roles).NotNull().Must(r => r is { Count: > 0 }).WithMessage("At least one role is required.");
    }
}

public sealed class UpdateAdminUserCommandValidator : AbstractValidator<UpdateAdminUserCommand>
{
    public UpdateAdminUserCommandValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Phone).MaximumLength(20).Matches(@"^\+?[0-9 .\-]{6,20}$").When(x => !string.IsNullOrWhiteSpace(x.Phone));
        RuleFor(x => x.Email).MaximumLength(255).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
    }
}

public sealed class ChangeUserStatusCommandValidator : AbstractValidator<ChangeUserStatusCommand>
{
    public ChangeUserStatusCommandValidator()
    {
        RuleFor(x => x.Status).Must(s => UserStatusCodes.TryParse(s, out _))
            .WithMessage("Status must be one of PENDING_VERIFICATION, ACTIVE, SUSPENDED, DISABLED.");
        RuleFor(x => x.Reason).MaximumLength(300);
    }
}

public sealed class ReplaceUserRolesCommandValidator : AbstractValidator<ReplaceUserRolesCommand>
{
    public ReplaceUserRolesCommandValidator()
    {
        RuleFor(x => x.Roles).NotNull().Must(r => r is { Count: > 0 }).WithMessage("At least one role is required.");
    }
}

public sealed class DeleteAdminUserCommandValidator : AbstractValidator<DeleteAdminUserCommand>
{
    public DeleteAdminUserCommandValidator()
    {
        RuleFor(x => x.Reason).MaximumLength(300);
    }
}
