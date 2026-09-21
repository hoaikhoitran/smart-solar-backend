using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Security;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.EmailVerification;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Modules.Identity.Login;
using SmartSolar.Modules.Identity.PasswordRecovery;
using SmartSolar.Modules.Identity.RefreshTokens;
using SmartSolar.Modules.Identity.Register;
using SmartSolar.Modules.Identity.Security;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Real AppDbContext on an in-memory SQLite database, wired to the real
/// unit of work and password hasher so handler tests exercise production code.
/// </summary>
public sealed class IdentityTestContext : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public IdentityTestContext(bool retryOnce = false)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection, sqlite => sqlite
                .ExecutionStrategy(dependencies => new TestRetryingExecutionStrategy(dependencies, retryOnce)))
            .Options;

        Db = new AppDbContext(options);
        Db.Database.EnsureCreated();

        UnitOfWork = new IdentityUnitOfWork(Db);
        Publisher = new FakeIntegrationEventPublisher();
        PasswordHasher = new PasswordHashingService();
        TokenFactory = new EmailVerificationTokenFactory();
        VerificationOptions = new EmailVerificationOptions { LifetimeMinutes = 1440 };
        SecureTokens = new SecureTokenFactory();
        RefreshTokenOptions = new RefreshTokenOptions { LifetimeDays = 14 };
        PasswordResetOptions = new PasswordResetOptions { LifetimeMinutes = 60 };
        AccessTokens = new JwtAccessTokenService(new JwtOptions
        {
            Issuer = "SmartSolar",
            Audience = "SmartSolarClients",
            SigningKey = "integration-test-signing-key-32-bytes!!",
            AccessTokenLifetimeMinutes = 15
        });
        FrontendOptions = new FrontendOptions { EmailVerificationUrl = "https://app.test/verify-email", PasswordResetUrl = "https://app.test/reset-password" };
    }

    public AppDbContext Db { get; }

    public IdentityUnitOfWork UnitOfWork { get; }

    public FakeIntegrationEventPublisher Publisher { get; }

    public PasswordHashingService PasswordHasher { get; }

    public EmailVerificationTokenFactory TokenFactory { get; }

    public EmailVerificationOptions VerificationOptions { get; }

    public FrontendOptions FrontendOptions { get; }

    public SecureTokenFactory SecureTokens { get; }

    public RefreshTokenOptions RefreshTokenOptions { get; }

    public PasswordResetOptions PasswordResetOptions { get; }

    public JwtAccessTokenService AccessTokens { get; }

    public RefreshTokenIssuer CreateRefreshTokenIssuer()
        => new(UnitOfWork, SecureTokens, RefreshTokenOptions);

    public LoginHandler CreateLoginHandler()
        => new(
            UnitOfWork,
            PasswordHasher,
            AccessTokens,
            CreateRefreshTokenIssuer(),
            NullLogger<LoginHandler>.Instance);

    public RefreshTokenHandler CreateRefreshTokenHandler()
        => new(
            UnitOfWork,
            AccessTokens,
            CreateRefreshTokenIssuer(),
            NullLogger<RefreshTokenHandler>.Instance);

    public LogoutHandler CreateLogoutHandler() => new(UnitOfWork);

    public ForgotPasswordHandler CreateForgotPasswordHandler()
        => new(
            UnitOfWork,
            SecureTokens,
            Publisher,
            PasswordResetOptions,
            FrontendOptions,
            NullLogger<ForgotPasswordHandler>.Instance);

    public ResetPasswordHandler CreateResetPasswordHandler()
        => new(UnitOfWork, PasswordHasher, NullLogger<ResetPasswordHandler>.Instance);

    public ChangePasswordHandler CreateChangePasswordHandler()
        => new(UnitOfWork, PasswordHasher, NullLogger<ChangePasswordHandler>.Instance);

    /// <summary>Creates an ACTIVE account with a real password hash.</summary>
    public async Task<Modules.Identity.Entities.UserAccount> SeedActiveUserAsync(
        string email,
        string password,
        Modules.Identity.Enums.UserStatus status = Modules.Identity.Enums.UserStatus.Active)
    {
        var user = new Modules.Identity.Entities.UserAccount
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = "Seeded User",
            Status = status,
            EmailVerifiedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        user.PasswordHash = PasswordHasher.HashPassword(user, password);

        Db.UserAccounts.Add(user);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return user;
    }

    /// <summary>An ACTIVE account with no local password, as Google sign-in produces.</summary>
    public async Task<Modules.Identity.Entities.UserAccount> SeedOauthOnlyActiveUserAsync(string email)
    {
        var user = new Modules.Identity.Entities.UserAccount
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = "OAuth User",
            PasswordHash = null,
            Status = Modules.Identity.Enums.UserStatus.Active,
            EmailVerifiedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        Db.UserAccounts.Add(user);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return user;
    }

    public async Task<Modules.Identity.Entities.RefreshToken> SeedRefreshTokenAsync(
        Guid userId,
        string tokenHash,
        DateTimeOffset expiresAt,
        DateTimeOffset? revokedAt = null)
    {
        var token = new Modules.Identity.Entities.RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt
        };

        Db.RefreshTokens.Add(token);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return token;
    }

    public RegisterHandler CreateRegisterHandler()
        => new(
            UnitOfWork,
            PasswordHasher,
            TokenFactory,
            Publisher,
            VerificationOptions,
            FrontendOptions,
            NullLogger<RegisterHandler>.Instance);

    public VerifyEmailHandler CreateVerifyEmailHandler()
        => new(UnitOfWork, NullLogger<VerifyEmailHandler>.Instance);

    public ResendVerificationHandler CreateResendVerificationHandler()
        => new(
            UnitOfWork,
            TokenFactory,
            Publisher,
            VerificationOptions,
            FrontendOptions,
            NullLogger<ResendVerificationHandler>.Instance);

    public async Task<Role> SeedRoleAsync(string code)
    {
        var role = new Role { Id = Guid.NewGuid(), Code = code, Name = code };
        Db.Roles.Add(role);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return role;
    }

    public Task<Role> SeedCustomerRoleAsync() => SeedRoleAsync(RoleCodes.Customer);

    /// <summary>Creates an account directly, bypassing the register flow.</summary>
    public async Task<UserAccount> SeedUserAsync(
        string email,
        string? passwordHash,
        DateTimeOffset? emailVerifiedAt = null)
    {
        var user = new UserAccount
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = "Seeded User",
            PasswordHash = passwordHash,
            Status = emailVerifiedAt is null
                ? Modules.Identity.Enums.UserStatus.PendingVerification
                : Modules.Identity.Enums.UserStatus.Active,
            EmailVerifiedAt = emailVerifiedAt,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        Db.UserAccounts.Add(user);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return user;
    }

    public async Task<AuthActionToken> SeedTokenAsync(
        Guid userId,
        string tokenHash,
        Modules.Identity.Enums.AuthActionTokenType type,
        DateTimeOffset expiresAt,
        DateTimeOffset? usedAt = null)
    {
        var token = new AuthActionToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = tokenHash,
            Type = type,
            ExpiresAt = expiresAt,
            UsedAt = usedAt
        };

        Db.AuthActionTokens.Add(token);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return token;
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
