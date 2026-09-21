using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Register;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Identity;

public class RegisterHandlerTests
{
    private static RegisterCommand Command(
        string email = "New.User@Example.com",
        string password = "correct horse battery",
        string fullName = "New User",
        string? phone = null)
        => new(email, password, fullName, phone);

    [Fact]
    public async Task Creates_account_for_a_valid_local_registration()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedCustomerRoleAsync();

        var result = await ctx.CreateRegisterHandler().HandleAsync(Command(), CancellationToken.None);

        Assert.Equal(RegisterOutcome.Created, result.Outcome);
        var user = await ctx.Db.UserAccounts.SingleAsync();
        Assert.Equal(user.Id, result.UserId);
        Assert.Equal("New User", user.FullName);
    }

    [Fact]
    public async Task Normalizes_email_before_persisting()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedCustomerRoleAsync();

        var result = await ctx.CreateRegisterHandler()
            .HandleAsync(Command(email: "  New.User@EXAMPLE.com  "), CancellationToken.None);

        var user = await ctx.Db.UserAccounts.SingleAsync();
        Assert.Equal("new.user@example.com", user.Email);
        Assert.Equal("new.user@example.com", result.Email);
    }

    [Fact]
    public async Task Stores_password_as_a_hash_not_plaintext()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedCustomerRoleAsync();

        await ctx.CreateRegisterHandler()
            .HandleAsync(Command(password: "super secret pw"), CancellationToken.None);

        var user = await ctx.Db.UserAccounts.SingleAsync();
        Assert.NotNull(user.PasswordHash);
        Assert.NotEqual("super secret pw", user.PasswordHash);
        Assert.DoesNotContain("super secret pw", user.PasswordHash);
    }

    [Fact]
    public async Task Sets_status_to_pending_verification()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedCustomerRoleAsync();

        await ctx.CreateRegisterHandler().HandleAsync(Command(), CancellationToken.None);

        var user = await ctx.Db.UserAccounts.SingleAsync();
        Assert.Equal(UserStatus.PendingVerification, user.Status);
        Assert.Null(user.EmailVerifiedAt);
    }

    [Fact]
    public async Task Assigns_the_customer_role()
    {
        await using var ctx = new IdentityTestContext();
        var customer = await ctx.SeedCustomerRoleAsync();

        await ctx.CreateRegisterHandler().HandleAsync(Command(), CancellationToken.None);

        var userRole = await ctx.Db.UserRoles.SingleAsync();
        var user = await ctx.Db.UserAccounts.SingleAsync();
        Assert.Equal(customer.Id, userRole.RoleId);
        Assert.Equal(user.Id, userRole.UserId);
    }

    [Fact]
    public async Task Creates_an_email_verification_token_storing_only_the_hash()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedCustomerRoleAsync();

        await ctx.CreateRegisterHandler().HandleAsync(Command(), CancellationToken.None);

        var token = await ctx.Db.AuthActionTokens.SingleAsync();
        var published = Assert.Single(ctx.Publisher.PublishedOf<EmailVerificationRequestedEvent>());
        var rawToken = published.VerificationUrl.Split("token=")[1];

        Assert.Equal(AuthActionTokenType.EmailVerification, token.Type);
        Assert.Null(token.UsedAt);
        Assert.NotEqual(rawToken, token.TokenHash);
        Assert.Equal(64, token.TokenHash.Length);
    }

    [Fact]
    public async Task Token_expires_after_the_configured_lifetime()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedCustomerRoleAsync();
        ctx.VerificationOptions.LifetimeMinutes = 1440;

        var before = DateTimeOffset.UtcNow;
        await ctx.CreateRegisterHandler().HandleAsync(Command(), CancellationToken.None);

        var token = await ctx.Db.AuthActionTokens.SingleAsync();
        Assert.InRange(
            token.ExpiresAt,
            before.AddMinutes(1439),
            DateTimeOffset.UtcNow.AddMinutes(1441));
    }

    [Fact]
    public async Task Publishes_verification_event_with_url_from_configuration()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedCustomerRoleAsync();

        await ctx.CreateRegisterHandler().HandleAsync(Command(), CancellationToken.None);

        var published = Assert.Single(ctx.Publisher.PublishedOf<EmailVerificationRequestedEvent>());
        Assert.StartsWith("https://app.test/verify-email?token=", published.VerificationUrl);
        Assert.Equal("new.user@example.com", published.Email);
        Assert.Equal("New User", published.FullName);
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

    [Fact]
    public async Task Persists_optional_phone_when_supplied()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedCustomerRoleAsync();

        await ctx.CreateRegisterHandler().HandleAsync(Command(phone: "0901234567"), CancellationToken.None);

        var user = await ctx.Db.UserAccounts.SingleAsync();
        Assert.Equal("0901234567", user.Phone);
    }
}
