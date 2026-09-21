using FluentValidation;

namespace SmartSolar.Modules.Identity.Login;

public sealed record LoginCommand(string Email, string Password);

public enum LoginOutcome
{
    Succeeded = 1,

    /// <summary>Unknown email, wrong password, or an account that cannot sign in.</summary>
    InvalidCredentials = 2
}

public sealed record LoginResult(
    LoginOutcome Outcome,
    Guid UserId,
    string Email,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt)
{
    public static readonly LoginResult InvalidCredentials = new(
        LoginOutcome.InvalidCredentials,
        Guid.Empty,
        string.Empty,
        string.Empty,
        default,
        string.Empty,
        default);
}

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .MaximumLength(255);

        RuleFor(x => x.Password)
            .NotEmpty()
            .MaximumLength(128);
    }
}
