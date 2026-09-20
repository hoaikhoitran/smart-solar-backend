using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.EmailVerification;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Identity;

public class VerifyEmailHandlerTests
{
    [Fact]
    public async Task Verifies_account_for_a_valid_token()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedUserAsync("user@example.com", passwordHash: "hash");
        var token = ctx.TokenFactory.Create();
        await ctx.SeedTokenAsync(
            user.Id, token.TokenHash, AuthActionTokenType.EmailVerification, DateTimeOffset.UtcNow.AddHours(1));

        var result = await ctx.CreateVerifyEmailHandler()
            .HandleAsync(new VerifyEmailCommand(token.RawToken), CancellationToken.None);

        Assert.Equal(VerifyEmailOutcome.Verified, result.Outcome);
        var stored = await ctx.Db.UserAccounts.SingleAsync();
        Assert.NotNull(stored.EmailVerifiedAt);
    }

    [Fact]
    public async Task Marks_the_token_as_used()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedUserAsync("user@example.com", passwordHash: "hash");
        var token = ctx.TokenFactory.Create();
        await ctx.SeedTokenAsync(
            user.Id, token.TokenHash, AuthActionTokenType.EmailVerification, DateTimeOffset.UtcNow.AddHours(1));

        await ctx.CreateVerifyEmailHandler()
            .HandleAsync(new VerifyEmailCommand(token.RawToken), CancellationToken.None);

        var stored = await ctx.Db.AuthActionTokens.SingleAsync();
        Assert.NotNull(stored.UsedAt);
    }

    [Fact]
    public async Task Activates_the_pending_account()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedUserAsync("user@example.com", passwordHash: "hash");
        var token = ctx.TokenFactory.Create();
        await ctx.SeedTokenAsync(
            user.Id, token.TokenHash, AuthActionTokenType.EmailVerification, DateTimeOffset.UtcNow.AddHours(1));

        await ctx.CreateVerifyEmailHandler()
            .HandleAsync(new VerifyEmailCommand(token.RawToken), CancellationToken.None);

        var stored = await ctx.Db.UserAccounts.SingleAsync();
        Assert.Equal(UserStatus.Active, stored.Status);
    }

    [Fact]
    public async Task Looks_up_the_token_by_hash_not_by_raw_value()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedUserAsync("user@example.com", passwordHash: "hash");
        var token = ctx.TokenFactory.Create();
        // Stored raw instead of hashed: lookup by hash must not find it.
        await ctx.SeedTokenAsync(
            user.Id, token.RawToken, AuthActionTokenType.EmailVerification, DateTimeOffset.UtcNow.AddHours(1));

        var result = await ctx.CreateVerifyEmailHandler()
            .HandleAsync(new VerifyEmailCommand(token.RawToken), CancellationToken.None);

        Assert.Equal(VerifyEmailOutcome.InvalidOrExpired, result.Outcome);
    }

    [Fact]
    public async Task Rejects_an_unknown_token()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedUserAsync("user@example.com", passwordHash: "hash");

        var result = await ctx.CreateVerifyEmailHandler()
            .HandleAsync(new VerifyEmailCommand("not-a-real-token"), CancellationToken.None);

        Assert.Equal(VerifyEmailOutcome.InvalidOrExpired, result.Outcome);
    }

    [Fact]
    public async Task Rejects_an_expired_token_without_verifying_the_account()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedUserAsync("user@example.com", passwordHash: "hash");
        var token = ctx.TokenFactory.Create();
        await ctx.SeedTokenAsync(
            user.Id, token.TokenHash, AuthActionTokenType.EmailVerification, DateTimeOffset.UtcNow.AddMinutes(-1));

        var result = await ctx.CreateVerifyEmailHandler()
            .HandleAsync(new VerifyEmailCommand(token.RawToken), CancellationToken.None);

        Assert.Equal(VerifyEmailOutcome.InvalidOrExpired, result.Outcome);
        var stored = await ctx.Db.UserAccounts.SingleAsync();
        Assert.Null(stored.EmailVerifiedAt);
        Assert.Null((await ctx.Db.AuthActionTokens.SingleAsync()).UsedAt);
    }

    [Fact]
    public async Task Rejects_a_used_token_for_an_unverified_account()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedUserAsync("user@example.com", passwordHash: "hash");
        var token = ctx.TokenFactory.Create();
        await ctx.SeedTokenAsync(
            user.Id,
            token.TokenHash,
            AuthActionTokenType.EmailVerification,
            DateTimeOffset.UtcNow.AddHours(1),
            usedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        var result = await ctx.CreateVerifyEmailHandler()
            .HandleAsync(new VerifyEmailCommand(token.RawToken), CancellationToken.None);

        Assert.Equal(VerifyEmailOutcome.InvalidOrExpired, result.Outcome);
        Assert.Null((await ctx.Db.UserAccounts.SingleAsync()).EmailVerifiedAt);
    }

    [Fact]
    public async Task Reusing_a_valid_token_on_a_verified_account_succeeds_without_changing_the_timestamp()
    {
        await using var ctx = new IdentityTestContext();
        var verifiedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var user = await ctx.SeedUserAsync("user@example.com", passwordHash: "hash", emailVerifiedAt: verifiedAt);
        var token = ctx.TokenFactory.Create();
        await ctx.SeedTokenAsync(
            user.Id, token.TokenHash, AuthActionTokenType.EmailVerification, DateTimeOffset.UtcNow.AddHours(1));

        var result = await ctx.CreateVerifyEmailHandler()
            .HandleAsync(new VerifyEmailCommand(token.RawToken), CancellationToken.None);

        Assert.Equal(VerifyEmailOutcome.Verified, result.Outcome);
        var stored = await ctx.Db.UserAccounts.SingleAsync();
        Assert.Equal(
            verifiedAt.ToUnixTimeMilliseconds(),
            stored.EmailVerifiedAt!.Value.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Ignores_password_reset_tokens_with_the_same_hash()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedUserAsync("user@example.com", passwordHash: "hash");
        var token = ctx.TokenFactory.Create();
        await ctx.SeedTokenAsync(
            user.Id, token.TokenHash, AuthActionTokenType.PasswordReset, DateTimeOffset.UtcNow.AddHours(1));

        var result = await ctx.CreateVerifyEmailHandler()
            .HandleAsync(new VerifyEmailCommand(token.RawToken), CancellationToken.None);

        Assert.Equal(VerifyEmailOutcome.InvalidOrExpired, result.Outcome);
        Assert.Null((await ctx.Db.AuthActionTokens.SingleAsync()).UsedAt);
    }

    [Fact]
    public async Task Does_not_reactivate_a_suspended_account()
    {
        await using var ctx = new IdentityTestContext();
        var user = await ctx.SeedUserAsync("suspended@example.com", passwordHash: "hash");
        var stored = await ctx.Db.UserAccounts.SingleAsync(u => u.Id == user.Id);
        stored.Status = UserStatus.Suspended;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        var token = ctx.TokenFactory.Create();
        await ctx.SeedTokenAsync(
            user.Id, token.TokenHash, AuthActionTokenType.EmailVerification, DateTimeOffset.UtcNow.AddHours(1));

        var result = await ctx.CreateVerifyEmailHandler()
            .HandleAsync(new VerifyEmailCommand(token.RawToken), CancellationToken.None);

        Assert.Equal(VerifyEmailOutcome.Verified, result.Outcome);
        ctx.Db.ChangeTracker.Clear();
        var after = await ctx.Db.UserAccounts.SingleAsync();
        Assert.Equal(UserStatus.Suspended, after.Status);
        Assert.NotNull(after.EmailVerifiedAt);
    }

    [Fact]
    public async Task Reusing_a_used_token_on_a_verified_account_stays_successful()
    {
        await using var ctx = new IdentityTestContext();
        var verifiedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var user = await ctx.SeedUserAsync("done@example.com", passwordHash: "hash", emailVerifiedAt: verifiedAt);
        var token = ctx.TokenFactory.Create();
        await ctx.SeedTokenAsync(
            user.Id,
            token.TokenHash,
            AuthActionTokenType.EmailVerification,
            DateTimeOffset.UtcNow.AddHours(1),
            usedAt: DateTimeOffset.UtcNow.AddMinutes(-10));

        var result = await ctx.CreateVerifyEmailHandler()
            .HandleAsync(new VerifyEmailCommand(token.RawToken), CancellationToken.None);

        Assert.Equal(VerifyEmailOutcome.Verified, result.Outcome);
    }
}
