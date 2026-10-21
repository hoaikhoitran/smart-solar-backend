using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Login;
using SmartSolar.Modules.Identity.Security;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Identity;

/// <summary>Password login: what a successful sign-in issues and stores, and one indistinguishable answer for every failure cause.</summary>
public class LoginHandlerTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public async Task Valid_credentials_issue_a_role_bearing_access_token_and_a_hashed_refresh_token()
    {
        await using var ctx = new IdentityTestContext();
        var role = await ctx.SeedCustomerRoleAsync();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);
        ctx.Db.UserRoles.Add(new Modules.Identity.Entities.UserRole { UserId = user.Id, RoleId = role.Id, AssignedAt = DateTimeOffset.UtcNow });
        await ctx.Db.SaveChangesAsync();
        var passwordHash = (await ctx.Db.UserAccounts.SingleAsync(u => u.Id == user.Id)).PasswordHash!;
        ctx.Db.ChangeTracker.Clear();
        var before = DateTimeOffset.UtcNow;

        var result = await ctx.CreateLoginHandler().HandleAsync(new LoginCommand("Person@Example.com ", Password), CancellationToken.None);   // email normalized

        Assert.Equal((LoginOutcome.Succeeded, user.Id), (result.Outcome, result.UserId));
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(result.AccessToken);
        Assert.Contains(jwt.Claims, c => c.Type == System.Security.Claims.ClaimTypes.Role && c.Value == "CUSTOMER");
        var stored = await ctx.Db.RefreshTokens.SingleAsync();
        Assert.Equal(SecureTokenFactory.Hash(result.RefreshToken), stored.TokenHash);
        Assert.NotEqual(result.RefreshToken, stored.TokenHash);
        Assert.Null(stored.RevokedAt);
        Assert.DoesNotContain(passwordHash, result.ToString());
        Assert.DoesNotContain(Password, result.ToString());
        var account = await ctx.Db.UserAccounts.SingleAsync(u => u.Id == user.Id);
        Assert.InRange(account.LastLoginAt!.Value, before.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task Every_failure_cause_gets_the_same_answer_and_no_session()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedActiveUserAsync("active@example.com", Password);
        await ctx.SeedActiveUserAsync("pending@example.com", Password, UserStatus.PendingVerification);
        await ctx.SeedActiveUserAsync("suspended@example.com", Password, UserStatus.Suspended);
        await ctx.SeedActiveUserAsync("disabled@example.com", Password, UserStatus.Disabled);
        await ctx.SeedOauthOnlyActiveUserAsync("oauth@example.com");
        var handler = ctx.CreateLoginHandler();

        var results = new[]
        {
            await handler.HandleAsync(new LoginCommand("active@example.com", "wrong password"), CancellationToken.None),
            await handler.HandleAsync(new LoginCommand("pending@example.com", Password), CancellationToken.None),
            await handler.HandleAsync(new LoginCommand("suspended@example.com", Password), CancellationToken.None),
            await handler.HandleAsync(new LoginCommand("disabled@example.com", Password), CancellationToken.None),
            await handler.HandleAsync(new LoginCommand("oauth@example.com", Password), CancellationToken.None),   // no password to check
            await handler.HandleAsync(new LoginCommand("missing@example.com", Password), CancellationToken.None),
        };

        Assert.All(results, r => Assert.Equal(LoginOutcome.InvalidCredentials, r.Outcome));
        Assert.All(results, r => Assert.Empty(r.AccessToken));
        Assert.Single(results.Select(r => r.ToString()).Distinct());
        Assert.Equal(0, await ctx.Db.RefreshTokens.CountAsync());
    }
}
