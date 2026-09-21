using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Login;
using SmartSolar.Modules.Identity.RefreshTokens;
using SmartSolar.Modules.Identity.Security;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Identity;

public class RefreshAndLogoutHandlerTests
{
    private const string Password = "correct horse battery";

    private static async Task<(IdentityTestContext Ctx, string RefreshToken)> SignedIn(
        IdentityTestContext ctx,
        string email = "person@example.com")
    {
        await ctx.SeedActiveUserAsync(email, Password);
        var login = await ctx.CreateLoginHandler()
            .HandleAsync(new LoginCommand(email, Password), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        return (ctx, login.RefreshToken);
    }

    [Fact]
    public async Task Valid_refresh_token_issues_a_new_pair()
    {
        await using var ctx = new IdentityTestContext();
        var (_, refreshToken) = await SignedIn(ctx);

        var result = await ctx.CreateRefreshTokenHandler()
            .HandleAsync(new RefreshTokenCommand(refreshToken), CancellationToken.None);

        Assert.Equal(RefreshOutcome.Succeeded, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.AccessToken));
        Assert.NotEqual(refreshToken, result.RefreshToken);
    }

    [Fact]
    public async Task Rotation_revokes_the_old_token_and_stores_the_new_one_hashed()
    {
        await using var ctx = new IdentityTestContext();
        var (_, refreshToken) = await SignedIn(ctx);

        var result = await ctx.CreateRefreshTokenHandler()
            .HandleAsync(new RefreshTokenCommand(refreshToken), CancellationToken.None);

        ctx.Db.ChangeTracker.Clear();
        var old = await ctx.Db.RefreshTokens.SingleAsync(t => t.TokenHash == SecureTokenFactory.Hash(refreshToken));
        var fresh = await ctx.Db.RefreshTokens
            .SingleAsync(t => t.TokenHash == SecureTokenFactory.Hash(result.RefreshToken));

        Assert.NotNull(old.RevokedAt);
        Assert.Null(fresh.RevokedAt);
        Assert.NotEqual(result.RefreshToken, fresh.TokenHash);
    }

    [Fact]
    public async Task A_rotated_token_cannot_be_used_again()
    {
        await using var ctx = new IdentityTestContext();
        var (_, refreshToken) = await SignedIn(ctx);
        var handler = ctx.CreateRefreshTokenHandler();
        await handler.HandleAsync(new RefreshTokenCommand(refreshToken), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();

        var replay = await handler.HandleAsync(new RefreshTokenCommand(refreshToken), CancellationToken.None);

        Assert.Equal(RefreshOutcome.Invalid, replay.Outcome);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);
        var token = ctx.SecureTokens.Create();
        await ctx.SeedRefreshTokenAsync(user.Id, token.TokenHash, DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await ctx.CreateRefreshTokenHandler()
            .HandleAsync(new RefreshTokenCommand(token.RawToken), CancellationToken.None);

        Assert.Equal(RefreshOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Revoked_token_is_rejected()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);
        var token = ctx.SecureTokens.Create();
        await ctx.SeedRefreshTokenAsync(
            user.Id, token.TokenHash, DateTimeOffset.UtcNow.AddDays(7), revokedAt: DateTimeOffset.UtcNow);

        var result = await ctx.CreateRefreshTokenHandler()
            .HandleAsync(new RefreshTokenCommand(token.RawToken), CancellationToken.None);

        Assert.Equal(RefreshOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Unknown_token_is_rejected()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedActiveUserAsync("person@example.com", Password);

        var result = await ctx.CreateRefreshTokenHandler()
            .HandleAsync(new RefreshTokenCommand("not-a-real-token"), CancellationToken.None);

        Assert.Equal(RefreshOutcome.Invalid, result.Outcome);
    }

    [Theory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Disabled)]
    [InlineData(UserStatus.PendingVerification)]
    public async Task Token_of_an_account_that_cannot_sign_in_is_rejected(UserStatus status)
    {
        await using var ctx = new IdentityTestContext();
        var (_, refreshToken) = await SignedIn(ctx);
        var stored = await ctx.Db.UserAccounts.SingleAsync();
        stored.Status = status;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        var result = await ctx.CreateRefreshTokenHandler()
            .HandleAsync(new RefreshTokenCommand(refreshToken), CancellationToken.None);

        Assert.Equal(RefreshOutcome.Invalid, result.Outcome);
        // The old token must not have been consumed by a failed rotation.
        Assert.Equal(1, await ctx.Db.RefreshTokens.CountAsync());
    }

    [Fact]
    public async Task A_failed_rotation_adds_no_token()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedActiveUserAsync("person@example.com", Password);

        await ctx.CreateRefreshTokenHandler()
            .HandleAsync(new RefreshTokenCommand("unknown"), CancellationToken.None);

        Assert.Equal(0, await ctx.Db.RefreshTokens.CountAsync());
    }

    [Fact]
    public async Task Logout_revokes_the_token()
    {
        await using var ctx = new IdentityTestContext();
        var (_, refreshToken) = await SignedIn(ctx);

        var result = await ctx.CreateLogoutHandler()
            .HandleAsync(new LogoutCommand(refreshToken), CancellationToken.None);

        Assert.True(result.Accepted);
        ctx.Db.ChangeTracker.Clear();
        Assert.NotNull((await ctx.Db.RefreshTokens.SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task Logout_is_idempotent_and_keeps_the_first_revocation_time()
    {
        await using var ctx = new IdentityTestContext();
        var (_, refreshToken) = await SignedIn(ctx);
        var handler = ctx.CreateLogoutHandler();
        await handler.HandleAsync(new LogoutCommand(refreshToken), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        var firstRevokedAt = (await ctx.Db.RefreshTokens.SingleAsync()).RevokedAt;
        ctx.Db.ChangeTracker.Clear();

        var second = await handler.HandleAsync(new LogoutCommand(refreshToken), CancellationToken.None);

        Assert.True(second.Accepted);
        ctx.Db.ChangeTracker.Clear();
        Assert.Equal(firstRevokedAt, (await ctx.Db.RefreshTokens.SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task Logout_answers_the_same_for_a_token_that_never_existed()
    {
        await using var ctx = new IdentityTestContext();
        var (_, refreshToken) = await SignedIn(ctx);
        var handler = ctx.CreateLogoutHandler();

        var known = await handler.HandleAsync(new LogoutCommand(refreshToken), CancellationToken.None);
        var unknown = await handler.HandleAsync(new LogoutCommand("never-existed"), CancellationToken.None);

        Assert.Equal(known.ToString(), unknown.ToString());
    }

    [Fact]
    public async Task A_logged_out_token_can_no_longer_refresh()
    {
        await using var ctx = new IdentityTestContext();
        var (_, refreshToken) = await SignedIn(ctx);
        await ctx.CreateLogoutHandler().HandleAsync(new LogoutCommand(refreshToken), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();

        var result = await ctx.CreateRefreshTokenHandler()
            .HandleAsync(new RefreshTokenCommand(refreshToken), CancellationToken.None);

        Assert.Equal(RefreshOutcome.Invalid, result.Outcome);
    }
}
