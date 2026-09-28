using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Controllers;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Catalog;

public class CatalogRateLimitTests
{
    private static JsonElement RateLimitingSection()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "SmartSolar.Api", "appsettings.json");

        return JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(path)))
            .RootElement.GetProperty("RateLimiting");
    }

    [Theory]
    [InlineData(typeof(ProductsController), RateLimitingExtensions.CatalogReadPolicy)]
    [InlineData(typeof(AdminProductsController), RateLimitingExtensions.CatalogWritePolicy)]
    public void Controllers_reference_their_catalog_policy(Type controller, string expectedPolicy)
    {
        Assert.Equal(expectedPolicy, controller.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);

        // No action may opt into a different policy or out of limiting entirely.
        var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.All(actions, a => Assert.Null(a.GetCustomAttribute<EnableRateLimitingAttribute>()));
        Assert.All(actions, a => Assert.Null(a.GetCustomAttribute<DisableRateLimitingAttribute>()));
    }

    [Theory]
    [InlineData("CatalogRead", 60, 1)]
    [InlineData("CatalogWrite", 30, 1)]
    public void Shipped_configuration_matches_the_agreed_limits(
        string section,
        int expectedPermitLimit,
        int expectedWindowMinutes)
    {
        var settings = RateLimitingSection().GetProperty(section);

        Assert.Equal(expectedPermitLimit, settings.GetProperty("PermitLimit").GetInt32());
        Assert.Equal(expectedWindowMinutes, settings.GetProperty("WindowMinutes").GetInt32());
    }

    [Fact]
    public async Task Write_limit_is_per_user_and_returns_the_enveloped_429()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:CatalogWrite:PermitLimit"] = "2"
        });

        // Both clients share one IP, so only a per-user partition keeps them apart.
        var first = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var second = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var missing = $"/api/admin/products/{Guid.NewGuid()}";

        Assert.Equal(HttpStatusCode.NotFound, (await first.DeleteAsync(missing)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await first.DeleteAsync(missing)).StatusCode);
        var limited = await first.DeleteAsync(missing);
        var otherUser = await second.DeleteAsync(missing);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var body = JsonDocument.Parse(await limited.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(AuthErrorCodes.TooManyRequests, body.GetProperty("error").GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NotFound, otherUser.StatusCode);
    }

    [Fact]
    public async Task Read_and_write_budgets_are_separate()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:CatalogRead:PermitLimit"] = "1"
        });
        var client = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PatchAsJsonAsync($"/api/admin/products/{Guid.NewGuid()}/status", new { status = "ACTIVE" })).StatusCode);
    }
}
