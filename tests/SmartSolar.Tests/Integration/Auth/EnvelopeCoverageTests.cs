using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Auth;

/// <summary>
/// The envelope must cover framework-generated responses too, not just the
/// results the controller returns itself.
/// </summary>
public class EnvelopeCoverageTests
{
    private static void AssertEnvelope(JsonElement body, string expectedCode)
    {
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal(expectedCode, body.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Malformed_json_returns_the_validation_envelope()
    {
        using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/auth/register",
            new StringContent("{\"email\": }", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEnvelope(await BodyOf(response), "AUTH_VALIDATION_FAILED");
    }

    [Fact]
    public async Task Wrong_property_type_returns_the_validation_envelope()
    {
        using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/auth/register",
            new StringContent("{\"email\": 123}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEnvelope(await BodyOf(response), "AUTH_VALIDATION_FAILED");
    }

    [Fact]
    public async Task Empty_body_returns_the_validation_envelope()
    {
        using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/auth/register",
            new StringContent("", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEnvelope(await BodyOf(response), "AUTH_VALIDATION_FAILED");
    }

    [Fact]
    public async Task Unsupported_content_type_returns_an_envelope()
    {
        using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/auth/register",
            new StringContent("email=x", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var body = await BodyOf(response);
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Unknown_route_returns_an_envelope()
    {
        using var factory = new AuthApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/does-not-exist", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await BodyOf(response);
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
    }

    [Fact]
    public void Startup_fails_when_the_verification_url_is_not_configured()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?>
        {
            ["Frontend:EmailVerificationUrl"] = ""
        });

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(ex);
        Assert.Contains("Frontend:EmailVerificationUrl", ex!.ToString());
    }

    [Fact]
    public void Startup_fails_when_the_token_lifetime_is_not_positive()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?>
        {
            ["Auth:EmailVerification:LifetimeMinutes"] = "0"
        });

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(ex);
        Assert.Contains("LifetimeMinutes", ex!.ToString());
    }
}
