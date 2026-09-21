using FluentValidation;

namespace SmartSolar.Modules.Identity.PasswordRecovery;

public sealed record ForgotPasswordCommand(string Email);

/// <summary>Always reports acceptance, so no account state is disclosed.</summary>
public sealed record ForgotPasswordResult(bool Accepted)
{
    public static readonly ForgotPasswordResult Accepted_ = new(true);
}

public sealed class ForgotPasswordCommandValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .MaximumLength(255)
            .EmailAddress();
    }
}

public sealed record ResetPasswordCommand(string Token, string NewPassword);

public enum ResetPasswordOutcome
{
    Succeeded = 1,

    /// <summary>Unknown, expired or already used token.</summary>
    InvalidOrExpiredToken = 2
}

public sealed record ResetPasswordResult(ResetPasswordOutcome Outcome)
{
    public static readonly ResetPasswordResult Succeeded = new(ResetPasswordOutcome.Succeeded);

    public static readonly ResetPasswordResult InvalidOrExpired = new(ResetPasswordOutcome.InvalidOrExpiredToken);
}

public sealed class ResetPasswordCommandValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordCommandValidator()
    {
        RuleFor(x => x.Token).NotEmpty();

        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MinimumLength(8)
            .MaximumLength(128);
    }
}

public sealed record ChangePasswordCommand(Guid UserId, string CurrentPassword, string NewPassword);

public enum ChangePasswordOutcome
{
    Succeeded = 1,
    InvalidCurrentPassword = 2,

    /// <summary>The account can no longer sign in, so it cannot change its password.</summary>
    AccountNotActive = 3
}

public sealed record ChangePasswordResult(ChangePasswordOutcome Outcome)
{
    public static readonly ChangePasswordResult Succeeded = new(ChangePasswordOutcome.Succeeded);

    public static readonly ChangePasswordResult InvalidCurrentPassword =
        new(ChangePasswordOutcome.InvalidCurrentPassword);

    public static readonly ChangePasswordResult AccountNotActive = new(ChangePasswordOutcome.AccountNotActive);
}

public sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();

        RuleFor(x => x.NewPassword)
            .NotEmpty()
            .MinimumLength(8)
            .MaximumLength(128);
    }
}
