using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.EmailVerification;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Identity;

public class ResendVerificationHandlerTests
{
    [Fact]
    public async Task Creates_a_replacement_token_for_a_local_unverified_account()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedUserAsync("user@example.com", passwordHash: "hash");

        var result = await ctx.CreateResendVerificationHandler()
            .HandleAsync(new ResendVerificationCommand("User@Example.com "), CancellationToken.None);

        Assert.True(result.Accepted);
        var token = await ctx.Db.AuthActionTokens.SingleAsync();
        Assert.Equal(user.Id, token.UserId);
        Assert.Equal(AuthActionTokenType.EmailVerification, token.Type);
        Assert.Null(token.UsedAt);
        Assert.Single(ctx.Publisher.PublishedOf<EmailVerificationRequestedEvent>());
    }

    [Fact]
    public async Task Removes_previous_unused_verification_tokens()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedUserAsync("user@example.com", passwordHash: "hash");
        var old = ctx.TokenFactory.Create();
        await ctx.SeedTokenAsync(
            user.Id, old.TokenHash, AuthActionTokenType.EmailVerification, DateTimeOffset.UtcNow.AddHours(1));

        await ctx.CreateResendVerificationHandler()
            .HandleAsync(new ResendVerificationCommand("user@example.com"), CancellationToken.None);

        var tokens = await ctx.Db.AuthActionTokens.ToListAsync();
        Assert.Single(tokens);
        Assert.NotEqual(old.TokenHash, tokens[0].TokenHash);
    }

    [Fact]
    public async Task Does_not_touch_password_reset_tokens()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedUserAsync("user@example.com", passwordHash: "hash");
        var resetToken = ctx.TokenFactory.Create();
        await ctx.SeedTokenAsync(
            user.Id, resetToken.TokenHash, AuthActionTokenType.PasswordReset, DateTimeOffset.UtcNow.AddHours(1));

        await ctx.CreateResendVerificationHandler()
            .HandleAsync(new ResendVerificationCommand("user@example.com"), CancellationToken.None);

        var reset = await ctx.Db.AuthActionTokens
            .SingleAsync(t => t.Type == AuthActionTokenType.PasswordReset);
        Assert.Equal(resetToken.TokenHash, reset.TokenHash);
    }

    [Fact]
    public async Task Keeps_already_used_verification_tokens()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedUserAsync("user@example.com", passwordHash: "hash");
        var used = ctx.TokenFactory.Create();
        await ctx.SeedTokenAsync(
            user.Id,
            used.TokenHash,
            AuthActionTokenType.EmailVerification,
            DateTimeOffset.UtcNow.AddHours(1),
            usedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await ctx.CreateResendVerificationHandler()
            .HandleAsync(new ResendVerificationCommand("user@example.com"), CancellationToken.None);

        Assert.Equal(2, await ctx.Db.AuthActionTokens.CountAsync());
    }

    [Fact]
    public async Task Returns_generic_success_and_does_nothing_for_a_missing_account()
    {
        await using var ctx = new IdentityTestContext();

        var result = await ctx.CreateResendVerificationHandler()
            .HandleAsync(new ResendVerificationCommand("nobody@example.com"), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal(0, await ctx.Db.AuthActionTokens.CountAsync());
        Assert.Empty(ctx.Publisher.Published);
    }

    [Fact]
    public async Task Returns_generic_success_and_does_nothing_for_a_verified_account()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedUserAsync(
            "verified@example.com", passwordHash: "hash", emailVerifiedAt: DateTimeOffset.UtcNow);

        var result = await ctx.CreateResendVerificationHandler()
            .HandleAsync(new ResendVerificationCommand("verified@example.com"), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal(0, await ctx.Db.AuthActionTokens.CountAsync());
        Assert.Empty(ctx.Publisher.Published);
    }

    [Fact]
    public async Task Returns_generic_success_and_does_nothing_for_an_oauth_only_account()
    {
        await using var ctx = new IdentityTestContext();
        var oauthUser = await ctx.SeedUserAsync("oauth@example.com", passwordHash: null);

        var result = await ctx.CreateResendVerificationHandler()
            .HandleAsync(new ResendVerificationCommand("oauth@example.com"), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal(0, await ctx.Db.AuthActionTokens.CountAsync());
        Assert.Empty(ctx.Publisher.Published);
        Assert.Null((await ctx.Db.UserAccounts.SingleAsync(u => u.Id == oauthUser.Id)).PasswordHash);
    }

    [Fact]
    public async Task Returns_the_same_result_for_every_account_state()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedUserAsync("local@example.com", passwordHash: "hash");
        await ctx.SeedUserAsync("oauth@example.com", passwordHash: null);
        await ctx.SeedUserAsync("done@example.com", passwordHash: "hash", emailVerifiedAt: DateTimeOffset.UtcNow);
        var handler = ctx.CreateResendVerificationHandler();

        var results = new[]
        {
            await handler.HandleAsync(new ResendVerificationCommand("local@example.com"), CancellationToken.None),
            await handler.HandleAsync(new ResendVerificationCommand("oauth@example.com"), CancellationToken.None),
            await handler.HandleAsync(new ResendVerificationCommand("done@example.com"), CancellationToken.None),
            await handler.HandleAsync(new ResendVerificationCommand("missing@example.com"), CancellationToken.None)
        };

        Assert.All(results, r => Assert.True(r.Accepted));
        // Only the eligible local account produced a token or an event.
        Assert.Equal(1, await ctx.Db.AuthActionTokens.CountAsync());
        Assert.Single(ctx.Publisher.Published);
    }

    [Fact]
    public async Task Keeps_the_new_token_when_publishing_fails()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedUserAsync("user@example.com", passwordHash: "hash");
        ctx.Publisher.ThrowOnPublish = new InvalidOperationException("broker down");

        var result = await ctx.CreateResendVerificationHandler()
            .HandleAsync(new ResendVerificationCommand("user@example.com"), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal(1, await ctx.Db.AuthActionTokens.CountAsync());
    }
}
