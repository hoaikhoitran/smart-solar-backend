using FluentValidation;

namespace SmartSolar.Modules.Identity.EmailVerification;

public sealed record VerifyEmailCommand(string Token);

public enum VerifyEmailOutcome
{
    Verified = 1,
    InvalidOrExpired = 2
}

public sealed record VerifyEmailResult(VerifyEmailOutcome Outcome)
{
    public static readonly VerifyEmailResult Verified = new(VerifyEmailOutcome.Verified);

    public static readonly VerifyEmailResult InvalidOrExpired = new(VerifyEmailOutcome.InvalidOrExpired);
}

public sealed class VerifyEmailCommandValidator : AbstractValidator<VerifyEmailCommand>
{
    public VerifyEmailCommandValidator()
    {
        RuleFor(x => x.Token)
            .NotEmpty();
    }
}
