using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Auth;

public class AuthEndpointsTests
{
    private static object RegisterBody(
        string email = "person@example.com",
        string password = "correct horse battery",
        string fullName = "Test Person",
        string? phone = null)
        => new { email, password, fullName, phone };

    private static async Task<JsonElement> ReadEnvelopeAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static void AssertEnvelopeShape(JsonElement body)
    {
        Assert.True(body.TryGetProperty("isSuccess", out _));
        Assert.True(body.TryGetProperty("traceId", out var traceId));
        Assert.True(body.TryGetProperty("data", out _));
        Assert.True(body.TryGetProperty("error", out _));
        Assert.False(string.IsNullOrWhiteSpace(traceId.GetString()));
    }

    [Fact]
    public async Task Register_returns_201_with_the_success_envelope()
    {
        using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", RegisterBody());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadEnvelopeAsync(response);
        AssertEnvelopeShape(body);
        Assert.True(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
        Assert.True(body.GetProperty("data").GetProperty("verificationRequired").GetBoolean());
        Assert.Equal("person@example.com", body.GetProperty("data").GetProperty("email").GetString());
        Assert.NotEqual(Guid.Empty, body.GetProperty("data").GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task Register_response_never_contains_password_material()
    {
        using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register", RegisterBody(password: "super-secret-pw"));

        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("super-secret-pw", raw);
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenHash", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_returns_409_for_a_duplicate_email()
    {
        using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", RegisterBody());

        var response = await client.PostAsJsonAsync(
            "/api/auth/register", RegisterBody(email: "PERSON@example.com"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadEnvelopeAsync(response);
        AssertEnvelopeShape(body);
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal(
            "AUTH_EMAIL_ALREADY_EXISTS",
            body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Register_returns_400_with_validation_details()
    {
        using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register", RegisterBody(email: "not-an-email", password: "short"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadEnvelopeAsync(response);
        AssertEnvelopeShape(body);
        Assert.Equal(
            "AUTH_VALIDATION_FAILED",
            body.GetProperty("error").GetProperty("code").GetString());
        Assert.NotEqual(
            JsonValueKind.Null,
            body.GetProperty("error").GetProperty("details").ValueKind);
    }

    [Fact]
    public async Task Verify_email_returns_200_for_a_valid_token()
    {
        using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", RegisterBody());
        var rawToken = factory.Publisher.Published
            .OfType<Modules.Identity.Events.EmailVerificationRequestedEvent>()
            .Single().VerificationUrl.Split("token=")[1];

        var response = await client.PostAsJsonAsync("/api/auth/verify-email", new { token = rawToken });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadEnvelopeAsync(response);
        AssertEnvelopeShape(body);
        Assert.True(body.GetProperty("data").GetProperty("emailVerified").GetBoolean());

        await using var db = factory.CreateDbContext();
        var user = await db.UserAccounts.SingleAsync();
        Assert.NotNull(user.EmailVerifiedAt);
        Assert.Equal(UserStatus.Active, user.Status);
    }

    [Fact]
    public async Task Verify_email_returns_400_for_an_unknown_token()
    {
        using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/verify-email", new { token = "nope" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadEnvelopeAsync(response);
        AssertEnvelopeShape(body);
        Assert.Equal(
            "AUTH_VERIFICATION_TOKEN_INVALID_OR_EXPIRED",
            body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Resend_returns_the_same_200_body_for_known_and_unknown_emails()
    {
        using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", RegisterBody(email: "known@example.com"));

        var known = await client.PostAsJsonAsync(
            "/api/auth/resend-verification", new { email = "known@example.com" });
        var unknown = await client.PostAsJsonAsync(
            "/api/auth/resend-verification", new { email = "unknown@example.com" });

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);

        var knownBody = await ReadEnvelopeAsync(known);
        var unknownBody = await ReadEnvelopeAsync(unknown);
        AssertEnvelopeShape(knownBody);
        Assert.True(knownBody.GetProperty("data").GetProperty("accepted").GetBoolean());
        Assert.Equal(
            knownBody.GetProperty("data").ToString(),
            unknownBody.GetProperty("data").ToString());
        Assert.Equal(
            knownBody.GetProperty("error").ValueKind,
            unknownBody.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task Rate_limited_requests_return_429_with_the_same_envelope()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:Register:PermitLimit"] = "2",
            ["RateLimiting:Register:WindowMinutes"] = "10"
        });
        var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/auth/register", RegisterBody(email: "one@example.com"));
        await client.PostAsJsonAsync("/api/auth/register", RegisterBody(email: "two@example.com"));
        var limited = await client.PostAsJsonAsync(
            "/api/auth/register", RegisterBody(email: "three@example.com"));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var body = await ReadEnvelopeAsync(limited);
        AssertEnvelopeShape(body);
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(
            "AUTH_TOO_MANY_REQUESTS",
            body.GetProperty("error").GetProperty("code").GetString());
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task Startup_seeds_the_five_system_roles_idempotently()
    {
        using var factory = new AuthApiFactory();
        factory.CreateClient();

        await using var db = factory.CreateDbContext();
        var codes = await db.Roles.Select(r => r.Code).OrderBy(c => c).ToListAsync();

        Assert.Equal(
            new[] { "ADMIN", "CUSTOMER", "MANAGER", "SALES", "TECHNICIAN" },
            codes);
    }
}
