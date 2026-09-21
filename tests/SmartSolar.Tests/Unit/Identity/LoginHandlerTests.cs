using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Login;
using SmartSolar.Modules.Identity.Security;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Identity;

public class LoginHandlerTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Valid_credentials_sign_the_user_in()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);

        var result = await ctx.CreateLoginHandler()
            .HandleAsync(new LoginCommand("Person@Example.com ", Password), CancellationToken.None);

        Assert.Equal(LoginOutcome.Succeeded, result.Outcome);
        Assert.Equal(user.Id, result.UserId);
        Assert.False(string.IsNullOrWhiteSpace(result.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(result.RefreshToken));
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedActiveUserAsync("person@example.com", Password);

        var result = await ctx.CreateLoginHandler()
            .HandleAsync(new LoginCommand("person@example.com", "wrong password"), CancellationToken.None);

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        Assert.Empty(result.AccessToken);
        Assert.Equal(0, await ctx.Db.RefreshTokens.CountAsync());
    }

    [Fact]
    public async Task Unknown_email_is_rejected()
    {
        await using var ctx = new IdentityTestContext();

        var result = await ctx.CreateLoginHandler()
            .HandleAsync(new LoginCommand("nobody@example.com", Password), CancellationToken.None);

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
    }

    [Fact]
    public async Task Oauth_only_account_cannot_sign_in_with_a_password()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedOauthOnlyActiveUserAsync("oauth@example.com");

        var result = await ctx.CreateLoginHandler()
            .HandleAsync(new LoginCommand("oauth@example.com", Password), CancellationToken.None);

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
    }

    [Theory]
    [InlineData(UserStatus.PendingVerification)]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Disabled)]
    public async Task Only_active_accounts_may_sign_in(UserStatus status)
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedActiveUserAsync("person@example.com", Password, status);

        var result = await ctx.CreateLoginHandler()
            .HandleAsync(new LoginCommand("person@example.com", Password), CancellationToken.None);

        Assert.Equal(LoginOutcome.InvalidCredentials, result.Outcome);
        Assert.Equal(0, await ctx.Db.RefreshTokens.CountAsync());
    }

    [Fact]
    public async Task Every_failure_reports_the_same_outcome_regardless_of_cause()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedActiveUserAsync("active@example.com", Password);
        await ctx.SeedActiveUserAsync("suspended@example.com", Password, UserStatus.Suspended);
        await ctx.SeedOauthOnlyActiveUserAsync("oauth@example.com");
        var handler = ctx.CreateLoginHandler();

        var results = new[]
        {
            await handler.HandleAsync(new LoginCommand("active@example.com", "wrong"), CancellationToken.None),
            await handler.HandleAsync(new LoginCommand("suspended@example.com", Password), CancellationToken.None),
            await handler.HandleAsync(new LoginCommand("oauth@example.com", Password), CancellationToken.None),
            await handler.HandleAsync(new LoginCommand("missing@example.com", Password), CancellationToken.None)
        };

        Assert.All(results, r => Assert.Equal(LoginOutcome.InvalidCredentials, r.Outcome));
        Assert.Single(results.Select(r => r.ToString()).Distinct());
    }

    [Fact]
    public async Task Successful_login_updates_last_login_at()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);
        var before = DateTimeOffset.UtcNow;

        await ctx.CreateLoginHandler()
            .HandleAsync(new LoginCommand("person@example.com", Password), CancellationToken.None);

        ctx.Db.ChangeTracker.Clear();
        var stored = await ctx.Db.UserAccounts.SingleAsync(u => u.Id == user.Id);
        Assert.NotNull(stored.LastLoginAt);
        Assert.InRange(stored.LastLoginAt!.Value, before.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task Refresh_token_is_stored_only_as_a_hash()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedActiveUserAsync("person@example.com", Password);

        var result = await ctx.CreateLoginHandler()
            .HandleAsync(new LoginCommand("person@example.com", Password), CancellationToken.None);

        var stored = await ctx.Db.RefreshTokens.SingleAsync();
        Assert.NotEqual(result.RefreshToken, stored.TokenHash);
        Assert.Equal(SecureTokenFactory.Hash(result.RefreshToken), stored.TokenHash);
        Assert.Null(stored.RevokedAt);
    }

    [Fact]
    public async Task Result_never_carries_the_password_hash()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);
        var stored = await ctx.Db.UserAccounts.SingleAsync(u => u.Id == user.Id);

        var result = await ctx.CreateLoginHandler()
            .HandleAsync(new LoginCommand("person@example.com", Password), CancellationToken.None);

        Assert.DoesNotContain(stored.PasswordHash!, result.ToString());
        Assert.DoesNotContain(Password, result.ToString());
    }

    [Fact]
    public async Task Access_token_carries_the_users_roles()
    {
        await using var ctx = new IdentityTestContext();
        var role = await ctx.SeedCustomerRoleAsync();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);
        ctx.Db.UserRoles.Add(new Modules.Identity.Entities.UserRole
        {
            UserId = user.Id,
            RoleId = role.Id,
            AssignedAt = DateTimeOffset.UtcNow
        });
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        var result = await ctx.CreateLoginHandler()
            .HandleAsync(new LoginCommand("person@example.com", Password), CancellationToken.None);

        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(result.AccessToken);
        Assert.Contains(jwt.Claims, c => c.Type == System.Security.Claims.ClaimTypes.Role && c.Value == "CUSTOMER");
    }
}
