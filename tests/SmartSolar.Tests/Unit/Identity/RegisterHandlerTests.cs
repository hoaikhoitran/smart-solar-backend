using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Register;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Identity;

/// <summary>Local registration: the created account, its hashed secrets and verification email, duplicates and failure handling.</summary>
public class RegisterHandlerTests
{
    private static RegisterCommand Command(
        string email = "New.User@Example.com",
        string password = "correct horse battery",
        string fullName = "New User",
        string? phone = null)
        => new(email, password, fullName, phone);

    [Fact]
    public async Task A_valid_registration_creates_a_pending_customer_with_hashed_secrets_and_publishes_the_verification_email()
    {
        await using var ctx = new IdentityTestContext();
        var customer = await ctx.SeedCustomerRoleAsync();
        ctx.VerificationOptions.LifetimeMinutes = 1440;
        var before = DateTimeOffset.UtcNow;

        var result = await ctx.CreateRegisterHandler()
            .HandleAsync(Command(email: "  New.User@EXAMPLE.com  ", password: "super secret pw", phone: "0901234567"), CancellationToken.None);

        Assert.Equal(RegisterOutcome.Created, result.Outcome);
        var user = await ctx.Db.UserAccounts.SingleAsync();
        Assert.Equal(user.Id, result.UserId);
        Assert.Equal(("new.user@example.com", "new.user@example.com"), (user.Email, result.Email));   // normalized
        Assert.Equal(("New User", "0901234567"), (user.FullName, user.Phone));
        Assert.Equal(UserStatus.PendingVerification, user.Status);
        Assert.Null(user.EmailVerifiedAt);
        Assert.NotNull(user.PasswordHash);
        Assert.DoesNotContain("super secret pw", user.PasswordHash);
        var userRole = await ctx.Db.UserRoles.SingleAsync();
        Assert.Equal((customer.Id, user.Id), (userRole.RoleId, userRole.UserId));

        // Only the hash of the emailed token is stored; it expires after the configured lifetime.
        var token = await ctx.Db.AuthActionTokens.SingleAsync();
        var published = Assert.Single(ctx.Publisher.PublishedOf<EmailVerificationRequestedEvent>());
        Assert.StartsWith("https://app.test/verify-email?token=", published.VerificationUrl);
        Assert.Equal(("new.user@example.com", "New User"), (published.Email, published.FullName));
        Assert.Equal(AuthActionTokenType.EmailVerification, token.Type);
        Assert.Null(token.UsedAt);
        Assert.NotEqual(published.VerificationUrl.Split("token=")[1], token.TokenHash);
        Assert.Equal(64, token.TokenHash.Length);
        Assert.InRange(token.ExpiresAt, before.AddMinutes(1439), DateTimeOffset.UtcNow.AddMinutes(1441));
    }

    [Fact]
    public async Task Rejects_duplicate_email_ignoring_case_and_whitespace()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedCustomerRoleAsync();
        await ctx.SeedUserAsync("taken@example.com", passwordHash: "existing-hash");

        var result = await ctx.CreateRegisterHandler()
            .HandleAsync(Command(email: " TAKEN@Example.COM "), CancellationToken.None);

        Assert.Equal(RegisterOutcome.DuplicateEmail, result.Outcome);
        Assert.Equal(1, await ctx.Db.UserAccounts.CountAsync());
        Assert.Equal(0, await ctx.Db.AuthActionTokens.CountAsync());
        Assert.Empty(ctx.Publisher.Published);
    }

    [Fact]
    public async Task Duplicate_oauth_only_account_does_not_get_a_password_attached()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedCustomerRoleAsync();
        var oauthUser = await ctx.SeedUserAsync("oauth@example.com", passwordHash: null);

        var result = await ctx.CreateRegisterHandler()
            .HandleAsync(Command(email: "oauth@example.com"), CancellationToken.None);

        Assert.Equal(RegisterOutcome.DuplicateEmail, result.Outcome);
        var stored = await ctx.Db.UserAccounts.SingleAsync(u => u.Id == oauthUser.Id);
        Assert.Null(stored.PasswordHash);
        Assert.Equal(1, await ctx.Db.UserAccounts.CountAsync());
    }

    [Fact]
    public async Task Fails_loudly_when_the_customer_role_is_missing()
    {
        await using var ctx = new IdentityTestContext();

        await Assert.ThrowsAsync<CustomerRoleMissingException>(
            () => ctx.CreateRegisterHandler().HandleAsync(Command(), CancellationToken.None));

        Assert.Equal(0, await ctx.Db.UserAccounts.CountAsync());
        Assert.Equal(0, await ctx.Db.AuthActionTokens.CountAsync());
        Assert.Empty(ctx.Publisher.Published);
    }

    [Fact]
    public async Task Keeps_the_account_when_publishing_the_event_fails_after_commit()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedCustomerRoleAsync();
        ctx.Publisher.ThrowOnPublish = new InvalidOperationException("broker down");

        var result = await ctx.CreateRegisterHandler().HandleAsync(Command(), CancellationToken.None);

        Assert.Equal(RegisterOutcome.Created, result.Outcome);
        Assert.Equal(1, await ctx.Db.UserAccounts.CountAsync());
        Assert.Equal(1, await ctx.Db.AuthActionTokens.CountAsync());
        Assert.Equal(1, await ctx.Db.UserRoles.CountAsync());
    }
}
