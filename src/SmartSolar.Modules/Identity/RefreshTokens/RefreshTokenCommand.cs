using FluentValidation;

namespace SmartSolar.Modules.Identity.RefreshTokens;

public sealed record RefreshTokenCommand(string RefreshToken);

public enum RefreshOutcome
{
    Succeeded = 1,

    /// <summary>Unknown, expired, revoked, or belonging to an account that cannot sign in.</summary>
    Invalid = 2
}

public sealed record RefreshResult(
    RefreshOutcome Outcome,
    Guid UserId,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt)
{
    public static readonly RefreshResult Invalid = new(
        RefreshOutcome.Invalid, Guid.Empty, string.Empty, default, string.Empty, default);
}

public sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenCommandValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}

public sealed record LogoutCommand(string RefreshToken);

/// <summary>Always reports acceptance: whether the token existed is not disclosed.</summary>
public sealed record LogoutResult(bool Accepted)
{
    public static readonly LogoutResult Accepted_ = new(true);
}

public sealed class LogoutCommandValidator : AbstractValidator<LogoutCommand>
{
    public LogoutCommandValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}
