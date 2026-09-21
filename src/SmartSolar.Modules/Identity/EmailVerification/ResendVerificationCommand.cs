using FluentValidation;

namespace SmartSolar.Modules.Identity.EmailVerification;

public sealed record ResendVerificationCommand(string Email);

/// <summary>
/// Always reports acceptance. The caller cannot tell whether an email was sent,
/// which prevents account enumeration.
/// </summary>
public sealed record ResendVerificationResult(bool Accepted)
{
    public static readonly ResendVerificationResult Accepted_ = new(true);
}

public sealed class ResendVerificationCommandValidator : AbstractValidator<ResendVerificationCommand>
{
    public ResendVerificationCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .MaximumLength(255)
            .EmailAddress();
    }
}
