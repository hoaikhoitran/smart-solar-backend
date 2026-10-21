using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Login;
using SmartSolar.Modules.Identity.RefreshTokens;
using SmartSolar.Modules.Identity.Security;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Identity;

/// <summary>Refresh-token rotation and logout (the race and replay-detection cases are in RefreshTokenSecurityTests).</summary>
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
    public async Task A_valid_refresh_token_is_rotated_into_a_new_hashed_pair_and_cannot_be_replayed()
    {
        await using var ctx = new IdentityTestContext();
        var (_, refreshToken) = await SignedIn(ctx);
        var handler = ctx.CreateRefreshTokenHandler();

        var result = await handler.HandleAsync(new RefreshTokenCommand(refreshToken), CancellationToken.None);

        Assert.Equal(RefreshOutcome.Succeeded, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.AccessToken));
        Assert.NotEqual(refreshToken, result.RefreshToken);
        ctx.Db.ChangeTracker.Clear();
        var old = await ctx.Db.RefreshTokens.SingleAsync(t => t.TokenHash == SecureTokenFactory.Hash(refreshToken));
        var fresh = await ctx.Db.RefreshTokens.SingleAsync(t => t.TokenHash == SecureTokenFactory.Hash(result.RefreshToken));
        Assert.NotNull(old.RevokedAt);
        Assert.Null(fresh.RevokedAt);

        Assert.Equal(RefreshOutcome.Invalid, (await handler.HandleAsync(new RefreshTokenCommand(refreshToken), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Expired_revoked_and_unknown_tokens_are_rejected_without_issuing_a_token()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);
        var expired = ctx.SecureTokens.Create();
        var revoked = ctx.SecureTokens.Create();
        await ctx.SeedRefreshTokenAsync(user.Id, expired.TokenHash, DateTimeOffset.UtcNow.AddMinutes(-1));
        await ctx.SeedRefreshTokenAsync(user.Id, revoked.TokenHash, DateTimeOffset.UtcNow.AddDays(7), revokedAt: DateTimeOffset.UtcNow);
        var handler = ctx.CreateRefreshTokenHandler();

        foreach (var raw in new[] { expired.RawToken, revoked.RawToken, "not-a-real-token" })
            Assert.Equal(RefreshOutcome.Invalid, (await handler.HandleAsync(new RefreshTokenCommand(raw), CancellationToken.None)).Outcome);

        Assert.Equal(2, await ctx.Db.RefreshTokens.CountAsync());
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
    public async Task Logout_revokes_the_session_once_and_answers_the_same_for_unknown_tokens()
    {
        await using var ctx = new IdentityTestContext();
        var (_, refreshToken) = await SignedIn(ctx);
        var handler = ctx.CreateLogoutHandler();

        var first = await handler.HandleAsync(new LogoutCommand(refreshToken), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        var firstRevokedAt = (await ctx.Db.RefreshTokens.SingleAsync()).RevokedAt;
        Assert.True(first.Accepted);
        Assert.NotNull(firstRevokedAt);

        // Idempotent: a second logout keeps the first revocation time; an unknown token gets the same answer.
        ctx.Db.ChangeTracker.Clear();
        var second = await handler.HandleAsync(new LogoutCommand(refreshToken), CancellationToken.None);
        var unknown = await handler.HandleAsync(new LogoutCommand("never-existed"), CancellationToken.None);
        Assert.True(second.Accepted);
        Assert.Equal(first.ToString(), unknown.ToString());
        ctx.Db.ChangeTracker.Clear();
        Assert.Equal(firstRevokedAt, (await ctx.Db.RefreshTokens.SingleAsync()).RevokedAt);

        Assert.Equal(RefreshOutcome.Invalid, (await ctx.CreateRefreshTokenHandler().HandleAsync(new RefreshTokenCommand(refreshToken), CancellationToken.None)).Outcome);
    }
}
