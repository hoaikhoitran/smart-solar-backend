using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Login;
using SmartSolar.Modules.Identity.PasswordRecovery;
using SmartSolar.Modules.Identity.Security;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Identity;

public class ForgotPasswordHandlerTests
{
    private const string Password = "correct horse battery";

    private static string RawTokenFrom(IdentityTestContext ctx)
        => ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>().Single().ResetUrl.Split("token=")[1];

    [Fact]
    public async Task An_eligible_account_gets_a_hashed_reset_token_and_the_reset_email()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);

        var result = await ctx.CreateForgotPasswordHandler()
            .HandleAsync(new ForgotPasswordCommand("Person@Example.com "), CancellationToken.None);

        Assert.True(result.Accepted);
        var published = Assert.Single(ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>());
        Assert.Equal("person@example.com", published.Email);
        Assert.StartsWith(ctx.FrontendOptions.PasswordResetUrl, published.ResetUrl);
        var token = await ctx.Db.AuthActionTokens.SingleAsync();
        Assert.Equal((AuthActionTokenType.PasswordReset, user.Id), (token.Type, token.UserId));
        Assert.Equal(SecureTokenFactory.Hash(RawTokenFrom(ctx)), token.TokenHash);
        Assert.NotEqual(RawTokenFrom(ctx), token.TokenHash);
    }

    [Fact]
    public async Task Replaces_previous_unused_reset_tokens()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);
        var old = ctx.SecureTokens.Create();
        await ctx.SeedTokenAsync(
            user.Id, old.TokenHash, AuthActionTokenType.PasswordReset, DateTimeOffset.UtcNow.AddHours(1));

        await ctx.CreateForgotPasswordHandler()
            .HandleAsync(new ForgotPasswordCommand("person@example.com"), CancellationToken.None);

        var tokens = await ctx.Db.AuthActionTokens.ToListAsync();
        Assert.Single(tokens);
        Assert.NotEqual(old.TokenHash, tokens[0].TokenHash);
    }

    [Fact]
    public async Task Does_not_touch_email_verification_tokens()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);
        var verification = ctx.SecureTokens.Create();
        await ctx.SeedTokenAsync(
            user.Id,
            verification.TokenHash,
            AuthActionTokenType.EmailVerification,
            DateTimeOffset.UtcNow.AddHours(1));

        await ctx.CreateForgotPasswordHandler()
            .HandleAsync(new ForgotPasswordCommand("person@example.com"), CancellationToken.None);

        var stored = await ctx.Db.AuthActionTokens
            .SingleAsync(t => t.Type == AuthActionTokenType.EmailVerification);
        Assert.Equal(verification.TokenHash, stored.TokenHash);
    }

    [Fact]
    public async Task Every_account_state_gets_the_same_answer_and_only_an_eligible_account_gets_a_token()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedActiveUserAsync("active@example.com", Password);
        await ctx.SeedOauthOnlyActiveUserAsync("oauth@example.com");
        await ctx.SeedActiveUserAsync("suspended@example.com", Password, UserStatus.Suspended);
        var handler = ctx.CreateForgotPasswordHandler();

        var results = new[]
        {
            await handler.HandleAsync(new ForgotPasswordCommand("active@example.com"), CancellationToken.None),
            await handler.HandleAsync(new ForgotPasswordCommand("oauth@example.com"), CancellationToken.None),
            await handler.HandleAsync(new ForgotPasswordCommand("suspended@example.com"), CancellationToken.None),
            await handler.HandleAsync(new ForgotPasswordCommand("missing@example.com"), CancellationToken.None)
        };

        Assert.All(results, r => Assert.True(r.Accepted));
        Assert.Single(results.Select(r => r.ToString()).Distinct());
        Assert.Equal(1, await ctx.Db.AuthActionTokens.CountAsync());
        Assert.Single(ctx.Publisher.Published);
    }

    [Fact]
    public async Task Keeps_the_token_when_publishing_fails()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedActiveUserAsync("person@example.com", Password);
        ctx.Publisher.ThrowOnPublish = new InvalidOperationException("broker down");

        var result = await ctx.CreateForgotPasswordHandler()
            .HandleAsync(new ForgotPasswordCommand("person@example.com"), CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal(1, await ctx.Db.AuthActionTokens.CountAsync());
    }
}

public class ResetPasswordHandlerTests
{
    private const string Password = "correct horse battery";
    private const string NewPassword = "a brand new passphrase";

    private static async Task<(IdentityTestContext Ctx, Guid UserId, string RawToken)> WithResetToken(
        IdentityTestContext ctx)
    {
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);
        await ctx.CreateForgotPasswordHandler()
            .HandleAsync(new ForgotPasswordCommand("person@example.com"), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        var raw = ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>().Single().ResetUrl.Split("token=")[1];
        return (ctx, user.Id, raw);
    }

    [Fact]
    public async Task A_valid_token_changes_the_password_once_and_ends_every_session()
    {
        await using var ctx = new IdentityTestContext();
        var (_, userId, rawToken) = await WithResetToken(ctx);
        var stolen = ctx.SecureTokens.Create();
        await ctx.SeedRefreshTokenAsync(userId, stolen.TokenHash, DateTimeOffset.UtcNow.AddDays(7));
        var handler = ctx.CreateResetPasswordHandler();

        var result = await handler.HandleAsync(new ResetPasswordCommand(rawToken, NewPassword), CancellationToken.None);

        Assert.Equal(ResetPasswordOutcome.Succeeded, result.Outcome);
        ctx.Db.ChangeTracker.Clear();
        var user = await ctx.Db.UserAccounts.SingleAsync(u => u.Id == userId);
        Assert.True(ctx.PasswordHasher.VerifyPassword(user, user.PasswordHash!, NewPassword));
        Assert.False(ctx.PasswordHasher.VerifyPassword(user, user.PasswordHash!, Password));
        Assert.NotNull((await ctx.Db.AuthActionTokens.SingleAsync()).UsedAt);
        Assert.All(await ctx.Db.RefreshTokens.ToListAsync(), t => Assert.NotNull(t.RevokedAt));
        Assert.Equal(LoginOutcome.Succeeded, (await ctx.CreateLoginHandler().HandleAsync(new LoginCommand("person@example.com", NewPassword), CancellationToken.None)).Outcome);

        // The token is single use.
        ctx.Db.ChangeTracker.Clear();
        var replay = await handler.HandleAsync(new ResetPasswordCommand(rawToken, "yet another password"), CancellationToken.None);
        Assert.Equal(ResetPasswordOutcome.InvalidOrExpiredToken, replay.Outcome);
    }

    [Fact]
    public async Task An_expired_token_is_rejected_and_the_password_stays()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);
        var token = ctx.SecureTokens.Create();
        await ctx.SeedTokenAsync(
            user.Id, token.TokenHash, AuthActionTokenType.PasswordReset, DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await ctx.CreateResetPasswordHandler()
            .HandleAsync(new ResetPasswordCommand(token.RawToken, NewPassword), CancellationToken.None);

        Assert.Equal(ResetPasswordOutcome.InvalidOrExpiredToken, result.Outcome);
        ctx.Db.ChangeTracker.Clear();
        var stored = await ctx.Db.UserAccounts.SingleAsync();
        Assert.True(ctx.PasswordHasher.VerifyPassword(stored, stored.PasswordHash!, Password));
    }

    [Fact]
    public async Task An_unknown_token_is_rejected()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedActiveUserAsync("person@example.com", Password);

        var result = await ctx.CreateResetPasswordHandler()
            .HandleAsync(new ResetPasswordCommand("not-a-real-token", NewPassword), CancellationToken.None);

        Assert.Equal(ResetPasswordOutcome.InvalidOrExpiredToken, result.Outcome);
    }

    [Fact]
    public async Task An_email_verification_token_cannot_reset_a_password()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);
        var token = ctx.SecureTokens.Create();
        await ctx.SeedTokenAsync(
            user.Id, token.TokenHash, AuthActionTokenType.EmailVerification, DateTimeOffset.UtcNow.AddHours(1));

        var result = await ctx.CreateResetPasswordHandler()
            .HandleAsync(new ResetPasswordCommand(token.RawToken, NewPassword), CancellationToken.None);

        Assert.Equal(ResetPasswordOutcome.InvalidOrExpiredToken, result.Outcome);
    }
}

public class ChangePasswordHandlerTests
{
    private const string Password = "correct horse battery";
    private const string NewPassword = "a brand new passphrase";

    [Fact]
    public async Task The_correct_current_password_changes_it_and_ends_every_session()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);
        var session = ctx.SecureTokens.Create();
        await ctx.SeedRefreshTokenAsync(user.Id, session.TokenHash, DateTimeOffset.UtcNow.AddDays(7));

        var result = await ctx.CreateChangePasswordHandler()
            .HandleAsync(new ChangePasswordCommand(user.Id, Password, NewPassword), CancellationToken.None);

        Assert.Equal(ChangePasswordOutcome.Succeeded, result.Outcome);
        ctx.Db.ChangeTracker.Clear();
        var stored = await ctx.Db.UserAccounts.SingleAsync();
        Assert.True(ctx.PasswordHasher.VerifyPassword(stored, stored.PasswordHash!, NewPassword));
        Assert.DoesNotContain(NewPassword, stored.PasswordHash!);
        Assert.All(await ctx.Db.RefreshTokens.ToListAsync(), t => Assert.NotNull(t.RevokedAt));
        Assert.Equal(LoginOutcome.Succeeded, (await ctx.CreateLoginHandler().HandleAsync(new LoginCommand("person@example.com", NewPassword), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Wrong_current_password_is_rejected_and_nothing_changes()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password);
        var before = (await ctx.Db.UserAccounts.SingleAsync()).PasswordHash;

        var result = await ctx.CreateChangePasswordHandler()
            .HandleAsync(new ChangePasswordCommand(user.Id, "wrong", NewPassword), CancellationToken.None);

        Assert.Equal(ChangePasswordOutcome.InvalidCurrentPassword, result.Outcome);
        ctx.Db.ChangeTracker.Clear();
        Assert.Equal(before, (await ctx.Db.UserAccounts.SingleAsync()).PasswordHash);
    }

    [Theory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Disabled)]
    [InlineData(UserStatus.PendingVerification)]
    public async Task An_account_that_cannot_sign_in_cannot_change_its_password(UserStatus status)
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedActiveUserAsync("person@example.com", Password, status);

        var result = await ctx.CreateChangePasswordHandler()
            .HandleAsync(new ChangePasswordCommand(user.Id, Password, NewPassword), CancellationToken.None);

        Assert.Equal(ChangePasswordOutcome.AccountNotActive, result.Outcome);
    }

    [Fact]
    public async Task An_unknown_user_id_changes_nothing()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedActiveUserAsync("person@example.com", Password);

        var result = await ctx.CreateChangePasswordHandler()
            .HandleAsync(new ChangePasswordCommand(Guid.NewGuid(), Password, NewPassword), CancellationToken.None);

        Assert.Equal(ChangePasswordOutcome.AccountNotActive, result.Outcome);
    }
}
