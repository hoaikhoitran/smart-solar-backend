using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Auth;

/// <summary>
/// Exercises the bearer configuration in the real pipeline: dropping any of the
/// validations in JwtAuthenticationExtensions must fail one of these.
/// </summary>
public class BearerTokenValidationTests
{
    private const string PipelineKey = "integration-test-signing-key-32-bytes!!";

    private const string Password = "correct horse battery";

    /// <summary>
    /// Registers and activates a real account, so a token that the pipeline
    /// wrongly accepts would visibly succeed rather than fail for another reason.
    /// </summary>
    private static async Task<(HttpClient Client, Guid UserId)> ActiveUser(AuthApiFactory factory)
    {
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "person@example.com",
            password = Password,
            fullName = "Test Person"
        });

        await using var db = factory.CreateDbContext();
        var user = await db.UserAccounts.SingleAsync();
        user.Status = UserStatus.Active;
        user.EmailVerifiedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return (client, user.Id);
    }

    private static string Mint(
        Guid subject,
        string issuer = "SmartSolar",
        string audience = "SmartSolarClients",
        string signingKey = PipelineKey,
        int lifetimeMinutes = 15)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(lifetimeMinutes);
        // An already-expired token must still have notBefore before expires.
        var notBefore = expires.AddMinutes(-1) < now ? expires.AddMinutes(-1) : now.AddMinutes(-1);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, subject.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, "person@example.com")
            },
            notBefore: notBefore.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<HttpResponseMessage> CallChangePassword(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // The current password is correct, so a token the pipeline accepts
        // would return 200 rather than 401.
        return await client.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = Password,
            newPassword = "a brand new passphrase"
        });
    }

    [Fact]
    public async Task A_token_from_another_issuer_is_rejected()
    {
        using var factory = new AuthApiFactory();
        var (client, userId) = await ActiveUser(factory);

        var response = await CallChangePassword(client, Mint(userId, issuer: "EvilIssuer"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_for_another_audience_is_rejected()
    {
        using var factory = new AuthApiFactory();
        var (client, userId) = await ActiveUser(factory);

        var response = await CallChangePassword(client, Mint(userId, audience: "SomeOtherApp"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        using var factory = new AuthApiFactory();
        var (client, userId) = await ActiveUser(factory);

        // Well beyond the 30 second clock skew allowance.
        var response = await CallChangePassword(client, Mint(userId, lifetimeMinutes: -10));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_rejected()
    {
        using var factory = new AuthApiFactory();
        var (client, userId) = await ActiveUser(factory);

        var response = await CallChangePassword(
            client, Mint(userId, signingKey: "a-completely-different-signing-key!!!!!!"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejections_use_the_standard_envelope()
    {
        using var factory = new AuthApiFactory();
        var (client, userId) = await ActiveUser(factory);

        var response = await CallChangePassword(client, Mint(userId, issuer: "EvilIssuer"));

        var body = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal("AUTH_UNAUTHORIZED", body.GetProperty("error").GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }
}
