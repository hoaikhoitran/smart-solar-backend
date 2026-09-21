using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Login;
using SmartSolar.Modules.Identity.RefreshTokens;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Identity;

/// <summary>
/// The properties that make rotation a security control rather than a ritual.
/// </summary>
public class RefreshTokenSecurityTests
{
    private const string Password = "correct horse battery";

    private static async Task<string> SignIn(IdentityTestContext ctx, string email = "person@example.com")
    {
        await ctx.SeedActiveUserAsync(email, Password);
        var login = await ctx.CreateLoginHandler()
            .HandleAsync(new LoginCommand(email, Password), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        return login.RefreshToken;
    }

    [Fact]
    public async Task A_token_can_only_be_spent_once_even_if_the_revoke_races()
    {
        await using var ctx = new IdentityTestContext();
        var refreshToken = await SignIn(ctx);
        var stored = await ctx.Db.RefreshTokens.SingleAsync();
        ctx.Db.ChangeTracker.Clear();

        // Simulates the competing request winning the race first.
        var first = await ctx.UnitOfWork.TryRevokeRefreshTokenAsync(
            stored.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        var second = await ctx.UnitOfWork.TryRevokeRefreshTokenAsync(
            stored.Id, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task Losing_the_revoke_race_issues_no_second_session()
    {
        await using var ctx = new IdentityTestContext();
        var refreshToken = await SignIn(ctx);
        var stored = await ctx.Db.RefreshTokens.SingleAsync();
        // Another request rotated this token a moment earlier, but this request
        // read it before that happened.
        await ctx.UnitOfWork.TryRevokeRefreshTokenAsync(
            stored.Id, DateTimeOffset.UtcNow, CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();

        var result = await ctx.CreateRefreshTokenHandler()
            .HandleAsync(new RefreshTokenCommand(refreshToken), CancellationToken.None);

        Assert.Equal(RefreshOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Replaying_a_rotated_token_revokes_every_session_for_that_user()
    {
        await using var ctx = new IdentityTestContext();
        var stolen = await SignIn(ctx);
        var handler = ctx.CreateRefreshTokenHandler();
        // The thief rotates first and gets a live session.
        var thief = await handler.HandleAsync(new RefreshTokenCommand(stolen), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        Assert.Equal(RefreshOutcome.Succeeded, thief.Outcome);

        // The real owner then presents the token they still hold: that replay
        // is the evidence of theft.
        var replay = await handler.HandleAsync(new RefreshTokenCommand(stolen), CancellationToken.None);

        Assert.Equal(RefreshOutcome.Invalid, replay.Outcome);
        ctx.Db.ChangeTracker.Clear();
        Assert.All(await ctx.Db.RefreshTokens.ToListAsync(), t => Assert.NotNull(t.RevokedAt));

        // The thief's rotated token is dead too.
        var thiefRetry = await handler.HandleAsync(
            new RefreshTokenCommand(thief.RefreshToken), CancellationToken.None);
        Assert.Equal(RefreshOutcome.Invalid, thiefRetry.Outcome);
    }

    [Fact]
    public async Task An_unknown_token_does_not_revoke_anybodys_sessions()
    {
        await using var ctx = new IdentityTestContext();
        await SignIn(ctx);

        await ctx.CreateRefreshTokenHandler()
            .HandleAsync(new RefreshTokenCommand("never-issued"), CancellationToken.None);

        ctx.Db.ChangeTracker.Clear();
        Assert.All(await ctx.Db.RefreshTokens.ToListAsync(), t => Assert.Null(t.RevokedAt));
    }

    [Fact]
    public async Task A_retried_transaction_does_not_leave_an_orphan_refresh_token()
    {
        // ExecuteInTransactionAsync may run its delegate more than once. Without
        // clearing the change tracker between attempts, the first attempt's
        // refresh token would be inserted again.
        await using var ctx = new IdentityTestContext(retryOnce: true);
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);
        var attempts = 0;

        await ctx.UnitOfWork.ExecuteInTransactionAsync<object?>(
            _ =>
            {
                ctx.CreateRefreshTokenIssuer().Issue(user.Id, DateTimeOffset.UtcNow);
                attempts++;

                if (attempts == 1)
                {
                    throw new TransientTestException();
                }

                return Task.FromResult<object?>(null);
            },
            CancellationToken.None);

        Assert.Equal(2, attempts);
        ctx.Db.ChangeTracker.Clear();
        Assert.Equal(1, await ctx.Db.RefreshTokens.CountAsync());
    }
}
