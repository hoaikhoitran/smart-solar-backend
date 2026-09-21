using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Contracts.Persistence;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Identity;

public class IdentityUnitOfWorkTests
{
    private static UserAccount NewUser(string email) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        FullName = "Test User",
        PasswordHash = "hash",
        Status = UserStatus.PendingVerification,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Rolls_back_every_write_when_the_unit_of_work_throws_midway()
    {
        await using var ctx = new IdentityTestContext();
        var roleId = (await ctx.SeedCustomerRoleAsync()).Id;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ctx.UnitOfWork.ExecuteInTransactionAsync<object>(async _ =>
            {
                var user = NewUser("rollback@example.com");
                ctx.UnitOfWork.AddUserAccount(user);
                ctx.UnitOfWork.AddUserRole(new UserRole
                {
                    UserId = user.Id,
                    RoleId = roleId,
                    AssignedAt = DateTimeOffset.UtcNow
                });
                ctx.UnitOfWork.AddAuthActionToken(new AuthActionToken
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    TokenHash = "hash",
                    Type = AuthActionTokenType.EmailVerification,
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
                });

                await Task.Yield();
                throw new InvalidOperationException("failure inside the transaction");
            }, CancellationToken.None));

        ctx.Db.ChangeTracker.Clear();
        Assert.Equal(0, await ctx.Db.UserAccounts.CountAsync());
        Assert.Equal(0, await ctx.Db.UserRoles.CountAsync());
        Assert.Equal(0, await ctx.Db.AuthActionTokens.CountAsync());
    }

    [Fact]
    public async Task Commits_every_write_together_on_success()
    {
        await using var ctx = new IdentityTestContext();
        var roleId = (await ctx.SeedCustomerRoleAsync()).Id;

        await ctx.UnitOfWork.ExecuteInTransactionAsync<object?>(_ =>
        {
            var user = NewUser("committed@example.com");
            ctx.UnitOfWork.AddUserAccount(user);
            ctx.UnitOfWork.AddUserRole(new UserRole
            {
                UserId = user.Id,
                RoleId = roleId,
                AssignedAt = DateTimeOffset.UtcNow
            });
            return Task.FromResult<object?>(null);
        }, CancellationToken.None);

        ctx.Db.ChangeTracker.Clear();
        Assert.Equal(1, await ctx.Db.UserAccounts.CountAsync());
        Assert.Equal(1, await ctx.Db.UserRoles.CountAsync());
    }

    [Fact]
    public async Task Translates_a_unique_email_violation_into_DuplicateEmailException()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedUserAsync("taken@example.com", passwordHash: "hash");

        await Assert.ThrowsAsync<DuplicateEmailException>(() =>
            ctx.UnitOfWork.ExecuteInTransactionAsync<object?>(_ =>
            {
                ctx.UnitOfWork.AddUserAccount(NewUser("taken@example.com"));
                return Task.FromResult<object?>(null);
            }, CancellationToken.None));

        ctx.Db.ChangeTracker.Clear();
        Assert.Equal(1, await ctx.Db.UserAccounts.CountAsync());
    }

    [Fact]
    public async Task A_foreign_key_violation_is_not_reported_as_a_duplicate_email()
    {
        await using var ctx = new IdentityTestContext();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            ctx.UnitOfWork.ExecuteInTransactionAsync<object?>(_ =>
            {
                // No such user, so this violates the refresh_token foreign key.
                ctx.UnitOfWork.AddRefreshToken(new RefreshToken
                {
                    Id = Guid.NewGuid(),
                    UserId = Guid.NewGuid(),
                    TokenHash = "hash",
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
                });

                return Task.FromResult<object?>(null);
            }, CancellationToken.None));

        Assert.IsNotType<DuplicateEmailException>(ex);
    }
}
