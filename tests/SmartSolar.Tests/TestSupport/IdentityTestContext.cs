using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Security;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.EmailVerification;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Modules.Identity.Register;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Real AppDbContext on an in-memory SQLite database, wired to the real
/// unit of work and password hasher so handler tests exercise production code.
/// </summary>
public sealed class IdentityTestContext : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public IdentityTestContext()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection, sqlite => sqlite
                .ExecutionStrategy(dependencies => new TestRetryingExecutionStrategy(dependencies)))
            .Options;

        Db = new AppDbContext(options);
        Db.Database.EnsureCreated();

        UnitOfWork = new IdentityUnitOfWork(Db);
        Publisher = new FakeIntegrationEventPublisher();
        PasswordHasher = new PasswordHashingService();
        TokenFactory = new EmailVerificationTokenFactory();
        VerificationOptions = new EmailVerificationOptions { LifetimeMinutes = 1440 };
        FrontendOptions = new FrontendOptions { EmailVerificationUrl = "https://app.test/verify-email" };
    }

    public AppDbContext Db { get; }

    public IdentityUnitOfWork UnitOfWork { get; }

    public FakeIntegrationEventPublisher Publisher { get; }

    public PasswordHashingService PasswordHasher { get; }

    public EmailVerificationTokenFactory TokenFactory { get; }

    public EmailVerificationOptions VerificationOptions { get; }

    public FrontendOptions FrontendOptions { get; }

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
