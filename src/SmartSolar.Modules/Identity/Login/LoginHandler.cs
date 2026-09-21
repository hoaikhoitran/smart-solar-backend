using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.Contracts.Security;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.EmailVerification;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.RefreshTokens;

namespace SmartSolar.Modules.Identity.Login;

public sealed class LoginHandler
{
    /// <summary>
    /// A throwaway account and hash used to spend the same hashing time when no
    /// local password exists, so response time cannot reveal whether an
    /// address is registered.
    /// </summary>
    private static readonly UserAccount DummyAccount = new()
    {
        Id = Guid.Empty,
        Email = "dummy@invalid",
        FullName = "dummy",
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch
    };

    private readonly IIdentityUnitOfWork _unitOfWork;
    private readonly IPasswordHashingService _passwordHasher;
    private readonly IAccessTokenService _accessTokens;
    private readonly RefreshTokenIssuer _refreshTokens;
    private readonly ILogger<LoginHandler> _logger;

    public LoginHandler(
        IIdentityUnitOfWork unitOfWork,
        IPasswordHashingService passwordHasher,
        IAccessTokenService accessTokens,
        RefreshTokenIssuer refreshTokens,
        ILogger<LoginHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _accessTokens = accessTokens;
        _refreshTokens = refreshTokens;
        _logger = logger;
    }

    public async Task<LoginResult> HandleAsync(LoginCommand command, CancellationToken cancellationToken)
    {
        var email = EmailNormalizer.Normalize(command.Email);

        return await _unitOfWork.ExecuteInTransactionAsync(
            token => SignInAsync(email, command.Password, token),
            cancellationToken);
    }

    private async Task<LoginResult> SignInAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        var user = await _unitOfWork.FindUserByEmailAsync(email, cancellationToken);

        // Unknown account, OAuth-only account, wrong password and a non-ACTIVE
        // account all produce the same answer: nothing about the account leaks.
        if (user is null || user.PasswordHash is null)
        {
            // Pay the hashing cost anyway so the timing matches a real account.
            _passwordHasher.VerifyPassword(DummyAccount, DummyPasswordHash.Value, password);

            return LoginResult.InvalidCredentials;
        }

        if (!_passwordHasher.VerifyPassword(user, user.PasswordHash, password))
        {
            return LoginResult.InvalidCredentials;
        }

        if (user.Status != UserStatus.Active || user.DeletedAt is not null)
        {
            _logger.LogInformation(
                "Login refused for user {UserId} with status {Status}.", user.Id, user.Status);

            return LoginResult.InvalidCredentials;
        }

        var now = DateTimeOffset.UtcNow;
        user.LastLoginAt = now;
        user.UpdatedAt = now;

        var roleCodes = await _unitOfWork.GetRoleCodesAsync(user.Id, cancellationToken);
        var accessToken = _accessTokens.Generate(user, roleCodes);
        var refreshToken = _refreshTokens.Issue(user.Id, now);

        _logger.LogInformation("User {UserId} signed in.", user.Id);

        return new LoginResult(
            LoginOutcome.Succeeded,
            user.Id,
            user.Email,
            accessToken.Token,
            accessToken.ExpiresAt,
            refreshToken.RawToken,
            refreshToken.ExpiresAt);
    }
}
