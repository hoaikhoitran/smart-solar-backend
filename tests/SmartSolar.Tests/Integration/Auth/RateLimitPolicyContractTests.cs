using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Controllers;
using SmartSolar.Api.Extensions;

namespace SmartSolar.Tests.Integration.Auth;

/// <summary>
/// Pins the shipped rate-limit contract: the endpoints must reference the
/// named policies, and appsettings.json must carry the agreed limits.
/// </summary>
public class RateLimitPolicyContractTests
{
    private static JsonElement RateLimitingSection()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "SmartSolar.Api", "appsettings.json");

        return JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(path)))
            .RootElement.GetProperty("RateLimiting");
    }

    private static string PolicyOf(string actionName)
    {
        var action = typeof(AuthController).GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance)!;

        return action.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName!;
    }

    [Theory]
    [InlineData(nameof(AuthController.Register), RateLimitingExtensions.RegisterPolicy)]
    [InlineData(nameof(AuthController.VerifyEmail), RateLimitingExtensions.VerifyEmailPolicy)]
    [InlineData(nameof(AuthController.ResendVerification), RateLimitingExtensions.ResendVerificationPolicy)]
    public void Endpoint_references_its_named_policy(string actionName, string expectedPolicy)
    {
        Assert.Equal(expectedPolicy, PolicyOf(actionName));
    }

    [Theory]
    [InlineData("Register", 5, 10)]
    [InlineData("VerifyEmail", 20, 10)]
    [InlineData("ResendVerification", 3, 15)]
    public void Shipped_configuration_matches_the_agreed_limits(
        string section,
        int expectedPermitLimit,
        int expectedWindowMinutes)
    {
        var settings = RateLimitingSection().GetProperty(section);

        Assert.Equal(expectedPermitLimit, settings.GetProperty("PermitLimit").GetInt32());
        Assert.Equal(expectedWindowMinutes, settings.GetProperty("WindowMinutes").GetInt32());
    }
}
