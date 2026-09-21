using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Auth;

public class AuthSessionEndpointsTests
{
    private const string Password = "correct horse battery";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static void AssertEnvelope(JsonElement body)
    {
        Assert.True(body.TryGetProperty("isSuccess", out _));
        Assert.True(body.TryGetProperty("data", out _));
        Assert.True(body.TryGetProperty("error", out _));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }

    /// <summary>Registers, then activates the account so it can sign in.</summary>
    private static async Task<HttpClient> RegisteredAndActive(
        AuthApiFactory factory,
        string email = "person@example.com")
    {
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = Password,
            fullName = "Test Person"
        });

        await using var db = factory.CreateDbContext();
        var user = await db.UserAccounts.SingleAsync(u => u.Email == email);
        user.Status = UserStatus.Active;
        user.EmailVerifiedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return client;
    }

    private static async Task<(string AccessToken, string RefreshToken)> SignIn(
        HttpClient client,
        string email = "person@example.com",
        string password = Password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        var data = (await BodyOf(response)).GetProperty("data");
        return (data.GetProperty("accessToken").GetString()!, data.GetProperty("refreshToken").GetString()!);
    }

    [Fact]
    public async Task Login_returns_tokens_in_the_envelope()
    {
        using var factory = new AuthApiFactory();
        var client = await RegisteredAndActive(factory);

        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "person@example.com", password = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyOf(response);
        AssertEnvelope(body);
        Assert.True(body.GetProperty("isSuccess").GetBoolean());
        var data = body.GetProperty("data");
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("refreshToken").GetString()));
        Assert.Equal("Bearer", data.GetProperty("tokenType").GetString());
    }

    [Fact]
    public async Task Login_response_never_contains_password_material()
    {
        using var factory = new AuthApiFactory();
        var client = await RegisteredAndActive(factory);

        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "person@example.com", password = Password });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, raw);
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_401_with_the_generic_code()
    {
        using var factory = new AuthApiFactory();
        var client = await RegisteredAndActive(factory);

        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "person@example.com", password = "wrong password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await BodyOf(response);
        AssertEnvelope(body);
        Assert.Equal("AUTH_INVALID_CREDENTIALS", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_email_and_unverified_account_answer_exactly_like_a_wrong_password()
    {
        using var factory = new AuthApiFactory();
        var client = await RegisteredAndActive(factory);
        // This second account stays PENDING_VERIFICATION.
        await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "pending@example.com",
            password = Password,
            fullName = "Pending Person"
        });

        var wrongPassword = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "person@example.com", password = "wrong" });
        var unknown = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "nobody@example.com", password = Password });
        var pending = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "pending@example.com", password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, pending.StatusCode);

        var bodies = new[] { wrongPassword, unknown, pending };
        var errors = new List<string>();
        foreach (var response in bodies)
        {
            errors.Add((await BodyOf(response)).GetProperty("error").GetProperty("message").GetString()!);
        }

        Assert.Single(errors.Distinct());
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_the_old_one_stops_working()
    {
        using var factory = new AuthApiFactory();
        var client = await RegisteredAndActive(factory);
        var (_, refreshToken) = await SignIn(client);

        var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var newRefresh = (await BodyOf(refreshed)).GetProperty("data").GetProperty("refreshToken").GetString();
        Assert.NotEqual(refreshToken, newRefresh);

        var replay = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(
            "AUTH_REFRESH_TOKEN_INVALID",
            (await BodyOf(replay)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Logout_is_idempotent_and_ends_the_session()
    {
        using var factory = new AuthApiFactory();
        var client = await RegisteredAndActive(factory);
        var (_, refreshToken) = await SignIn(client);

        var first = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken });
        var second = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken });
        var unknown = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = "never-existed" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            (await BodyOf(first)).GetProperty("data").ToString(),
            (await BodyOf(unknown)).GetProperty("data").ToString());

        var afterLogout = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Change_password_requires_authentication()
    {
        using var factory = new AuthApiFactory();
        var client = await RegisteredAndActive(factory);

        var response = await client.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = Password,
            newPassword = "a brand new passphrase"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await BodyOf(response);
        AssertEnvelope(body);
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal("AUTH_UNAUTHORIZED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Change_password_succeeds_with_a_bearer_token_and_revokes_sessions()
    {
        using var factory = new AuthApiFactory();
        var client = await RegisteredAndActive(factory);
        var (accessToken, refreshToken) = await SignIn(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = Password,
            newPassword = "a brand new passphrase"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await BodyOf(response)).GetProperty("data").GetProperty("passwordChanged").GetBoolean());

        // The old session cannot refresh any more.
        client.DefaultRequestHeaders.Authorization = null;
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        // The new password works.
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "person@example.com", password = "a brand new passphrase" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Change_password_with_a_wrong_current_password_is_rejected()
    {
        using var factory = new AuthApiFactory();
        var client = await RegisteredAndActive(factory);
        var (accessToken, _) = await SignIn(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = "not my password",
            newPassword = "a brand new passphrase"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "AUTH_CURRENT_PASSWORD_INVALID",
            (await BodyOf(response)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Forgot_password_answers_identically_for_known_and_unknown_emails()
    {
        using var factory = new AuthApiFactory();
        var client = await RegisteredAndActive(factory);

        var known = await client.PostAsJsonAsync(
            "/api/auth/forgot-password", new { email = "person@example.com" });
        var unknown = await client.PostAsJsonAsync(
            "/api/auth/forgot-password", new { email = "nobody@example.com" });

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        var knownBody = await BodyOf(known);
        AssertEnvelope(knownBody);
        Assert.Equal(
            knownBody.GetProperty("data").ToString(),
            (await BodyOf(unknown)).GetProperty("data").ToString());
    }

    [Fact]
    public async Task Reset_password_completes_the_flow_from_the_emailed_link()
    {
        using var factory = new AuthApiFactory();
        var client = await RegisteredAndActive(factory);
        var (_, refreshToken) = await SignIn(client);
        await client.PostAsJsonAsync("/api/auth/forgot-password", new { email = "person@example.com" });

        var rawToken = factory.Publisher
            .PublishedOf<Modules.Identity.Events.PasswordResetRequestedEvent>()
            .Single().ResetUrl.Split("token=")[1];

        var reset = await client.PostAsJsonAsync(
            "/api/auth/reset-password", new { token = rawToken, newPassword = "a brand new passphrase" });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.True((await BodyOf(reset)).GetProperty("data").GetProperty("passwordChanged").GetBoolean());

        // Sessions from before the reset are dead...
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken })).StatusCode);

        // ...and the new password signs in.
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync(
                "/api/auth/login",
                new { email = "person@example.com", password = "a brand new passphrase" })).StatusCode);
    }

    [Fact]
    public async Task Reset_password_rejects_an_unknown_token_with_one_generic_code()
    {
        using var factory = new AuthApiFactory();
        var client = await RegisteredAndActive(factory);

        var response = await client.PostAsJsonAsync(
            "/api/auth/reset-password", new { token = "not-real", newPassword = "a brand new passphrase" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "AUTH_PASSWORD_RESET_TOKEN_INVALID_OR_EXPIRED",
            (await BodyOf(response)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Validation_errors_use_the_same_envelope()
    {
        using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = "", password = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        AssertEnvelope(body);
        Assert.Equal("AUTH_VALIDATION_FAILED", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Rate_limited_auth_requests_use_the_envelope()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:Login:PermitLimit"] = "2",
            ["RateLimiting:Login:WindowMinutes"] = "10"
        });
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/auth/login", new { email = "a@example.com", password = Password });
        await client.PostAsJsonAsync("/api/auth/login", new { email = "b@example.com", password = Password });
        var limited = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "c@example.com", password = Password });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var body = await BodyOf(limited);
        AssertEnvelope(body);
        Assert.Equal("AUTH_TOO_MANY_REQUESTS", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Disabled)]
    public async Task Non_active_accounts_answer_exactly_like_a_wrong_password(UserStatus status)
    {
        using var factory = new AuthApiFactory();
        var client = await RegisteredAndActive(factory);

        var baseline = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "person@example.com", password = "wrong" });
        var baselineBody = await baseline.Content.ReadAsStringAsync();

        await using (var db = factory.CreateDbContext())
        {
            var user = await db.UserAccounts.SingleAsync(u => u.Email == "person@example.com");
            user.Status = status;
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "person@example.com", password = Password });

        Assert.Equal(baseline.StatusCode, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(
            JsonDocument.Parse(baselineBody).RootElement.GetProperty("error").GetProperty("code").GetString(),
            body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            JsonDocument.Parse(baselineBody).RootElement.GetProperty("error").GetProperty("message").GetString(),
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task An_oauth_only_account_answers_exactly_like_a_wrong_password()
    {
        using var factory = new AuthApiFactory();
        var client = await RegisteredAndActive(factory);

        await using (var db = factory.CreateDbContext())
        {
            var user = await db.UserAccounts.SingleAsync(u => u.Email == "person@example.com");
            user.PasswordHash = null;
            await db.SaveChangesAsync();
        }

        var oauth = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "person@example.com", password = Password });
        var unknown = await client.PostAsJsonAsync(
            "/api/auth/login", new { email = "nobody@example.com", password = Password });

        Assert.Equal(unknown.StatusCode, oauth.StatusCode);
        Assert.Equal(
            await unknown.Content.ReadAsStringAsync() is var _ ? (await BodyOf(unknown)).GetProperty("error").ToString() : null,
            (await BodyOf(oauth)).GetProperty("error").ToString());
    }

    [Fact]
    public async Task Forgot_password_never_returns_the_raw_reset_token()
    {
        using var factory = new AuthApiFactory();
        var client = await RegisteredAndActive(factory);

        var response = await client.PostAsJsonAsync(
            "/api/auth/forgot-password", new { email = "person@example.com" });

        var rawToken = factory.Publisher
            .PublishedOf<Modules.Identity.Events.PasswordResetRequestedEvent>()
            .Single().ResetUrl.Split("token=")[1];
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(rawToken, body);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
    }
}
