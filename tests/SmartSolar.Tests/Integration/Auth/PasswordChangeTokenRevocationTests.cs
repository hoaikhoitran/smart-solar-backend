using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Auth;

/// <summary>
/// Through the real pipeline: a password reset or change ends every session at once — the old JWT is rejected on its next request (not
/// only at expiry), the old refresh token is dead, the old password fails and the new one signs in. Sessions created after the change work.
/// </summary>
public sealed class PasswordChangeTokenRevocationTests
{
    private const string Email = "person@example.com";
    private const string Password = "correct horse battery";
    private const string NewPassword = "a brand new passphrase";

    private static async Task<JsonElement> DataOf(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data");

    private static async Task<HttpClient> ActiveAccountAsync(AuthApiFactory factory)
    {
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new { email = Email, password = Password, fullName = "Test Person" });
        await using var db = factory.CreateDbContext();
        var user = await db.UserAccounts.SingleAsync(u => u.Email == Email);
        (user.Status, user.EmailVerifiedAt) = (UserStatus.Active, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task<(string Access, string Refresh)> SignInAsync(HttpClient client, string password)
    {
        var data = await DataOf(await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password }));
        return (data.GetProperty("accessToken").GetString()!, data.GetProperty("refreshToken").GetString()!);
    }

    /// <summary>An authenticated request that changes nothing: a change-password call with a wrong current password (400 when authenticated).</summary>
    private static async Task<HttpStatusCode> ProbeAsync(AuthApiFactory factory, string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return (await client.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "definitely wrong", newPassword = "another new passphrase" })).StatusCode;
    }

    private static async Task AssertOnlyTheNewPasswordWorksAsync(AuthApiFactory factory, HttpClient client, string oldAccess, string oldRefresh)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await ProbeAsync(factory, oldAccess));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = oldRefresh })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password })).StatusCode);

        var (access, refresh) = await SignInAsync(client, NewPassword);
        Assert.Equal(HttpStatusCode.BadRequest, await ProbeAsync(factory, access));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh })).StatusCode);
    }

    [Fact]
    public async Task Change_password_rejects_the_previous_access_token_immediately()
    {
        using var factory = new AuthApiFactory();
        var client = await ActiveAccountAsync(factory);
        var (access, refresh) = await SignInAsync(client, Password);
        var other = await SignInAsync(client, Password);   // a second device
        Assert.Equal(HttpStatusCode.BadRequest, await ProbeAsync(factory, access));

        var caller = factory.CreateClient();
        caller.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);
        var changed = await caller.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = Password, newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await ProbeAsync(factory, other.Access));
        await AssertOnlyTheNewPasswordWorksAsync(factory, client, access, refresh);
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, (await db.UserAccounts.SingleAsync(u => u.Email == Email)).AuthVersion);
    }

    [Fact]
    public async Task Reset_password_rejects_the_previous_access_token_and_the_link_works_once()
    {
        using var factory = new AuthApiFactory { EmailCanDeliver = true };
        var client = await ActiveAccountAsync(factory);
        var (access, refresh) = await SignInAsync(client, Password);
        await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = Email });
        var token = factory.Publisher.PublishedOf<PasswordResetRequestedEvent>().Single().ResetUrl.Split("token=")[1];

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = NewPassword })).StatusCode);

        await AssertOnlyTheNewPasswordWorksAsync(factory, client, access, refresh);
        // The link is single-use: a second reset is refused and does not touch the account (no further version bump).
        var again = await client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = "yet another passphrase" });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, (await db.UserAccounts.SingleAsync(u => u.Email == Email)).AuthVersion);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task Expired_or_unknown_reset_tokens_and_a_wrong_current_password_change_nothing()
    {
        using var factory = new AuthApiFactory { EmailCanDeliver = true };
        var client = await ActiveAccountAsync(factory);
        var (access, _) = await SignInAsync(client, Password);
        await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = Email });
        var token = factory.Publisher.PublishedOf<PasswordResetRequestedEvent>().Single().ResetUrl.Split("token=")[1];
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.AuthActionTokens.SingleAsync(t => t.Type == AuthActionTokenType.PasswordReset);
            row.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/reset-password", new { token, newPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/reset-password", new { token = "not-a-real-token", newPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, await ProbeAsync(factory, access));   // wrong current password: 400, account unchanged

        // The session and the old password are untouched.
        Assert.Equal(HttpStatusCode.BadRequest, await ProbeAsync(factory, access));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password })).StatusCode);
        await using var check = factory.CreateDbContext();
        Assert.Equal(0, (await check.UserAccounts.SingleAsync(u => u.Email == Email)).AuthVersion);
    }

    [Fact]
    public async Task Forgot_password_answers_the_same_for_unknown_accounts_and_stores_no_outbox_row_for_them()
    {
        using var factory = new AuthApiFactory { EmailCanDeliver = true };
        var client = await ActiveAccountAsync(factory);

        var known = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = Email });
        var unknown = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "nobody@example.com" });

        Assert.Equal((known.StatusCode, (await DataOf(known)).ToString()), (unknown.StatusCode, (await DataOf(unknown)).ToString()));
        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.PasswordResetDispatches.CountAsync());
    }
}
