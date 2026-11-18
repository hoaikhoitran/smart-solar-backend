using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.RateLimiting;

/// <summary>
/// RATE-LIMIT coverage (2026-11-18 remediation): every controller action of the real routing table must name a rate-limit policy. The
/// inventory comes from the endpoint metadata the API actually serves (not from parsing source), so a new action without a policy fails here.
/// The only exemptions are listed below with their justification.
/// </summary>
public sealed class EndpointRateLimitCoverageTests
{
    /// <summary>Actions deliberately without a named policy (none today: even the 410 retired routes and the PayOS webhook carry one).</summary>
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal);

    private static IReadOnlyList<(string Route, string Method, string? Policy, bool Disabled)> Inventory()
    {
        using var factory = new AuthApiFactory();
        var sources = factory.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();
        return sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
            .Select(e => (
                Route: e.RoutePattern.RawText ?? "",
                Method: string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"]),
                Policy: e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName,
                Disabled: e.Metadata.GetMetadata<DisableRateLimitingAttribute>() is not null))
            .OrderBy(e => e.Route, StringComparer.Ordinal).ThenBy(e => e.Method, StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void Every_controller_action_names_a_rate_limit_policy()
    {
        var missing = Inventory()
            .Where(e => e.Policy is null && !e.Disabled && !Exempt.Contains($"{e.Method} {e.Route}"))
            .Select(e => $"{e.Method} {e.Route}")
            .ToList();

        Assert.True(missing.Count == 0, "Actions without a rate-limit policy:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void No_controller_action_disables_rate_limiting()
    {
        var disabled = Inventory().Where(e => e.Disabled).Select(e => $"{e.Method} {e.Route}").ToList();

        Assert.True(disabled.Count == 0, "Actions with rate limiting disabled:\n" + string.Join("\n", disabled));
    }

    [Theory]
    // Sales queue and claim (claim is a contended state change: the stricter scheduling write policy).
    [InlineData("api/survey-requests/pending", "GET", RateLimitingExtensions.PortalReadPolicy)]
    [InlineData("api/survey-requests/my", "GET", RateLimitingExtensions.PortalReadPolicy)]
    [InlineData("api/survey-requests/{surveyRequestId:guid}/claim", "POST", RateLimitingExtensions.SchedulingWritePolicy)]
    // Customer reads and pre-survey writes.
    [InlineData("api/customers/me/survey-requests", "GET", RateLimitingExtensions.PortalReadPolicy)]
    [InlineData("api/pre-surveys", "POST", RateLimitingExtensions.PortalWritePolicy)]
    [InlineData("api/pre-surveys/{preSurveyId:guid}/submit", "POST", RateLimitingExtensions.PortalWritePolicy)]
    [InlineData("api/pre-surveys/{preSurveyId:guid}/surface", "PUT", RateLimitingExtensions.PortalWritePolicy)]
    // Retired routes keep a bounded budget.
    [InlineData("api/quotations/pending-review", "GET", RateLimitingExtensions.RetiredEndpointPolicy)]
    // Payment link creation stays on its cluster-wide policy; the webhook keeps its own generous IP guard.
    [InlineData("api/customers/me/contracts/{contractId:guid}/payments", "POST", RateLimitingExtensions.PaymentCreatePolicy)]
    [InlineData("api/payments/payos/webhook", "POST", RateLimitingExtensions.PayOsWebhookPolicy)]
    public void Abuse_sensitive_actions_use_the_expected_policy(string route, string method, string policy)
    {
        var endpoint = Inventory().SingleOrDefault(e => e.Route == route && e.Method == method);

        Assert.True(endpoint.Route is not null, $"{method} {route} is not served");
        Assert.Equal(policy, endpoint.Policy);
    }

    [Theory]
    [InlineData("PortalRead", 60, 1)]
    [InlineData("PortalWrite", 20, 1)]
    [InlineData("RetiredEndpoint", 30, 1)]
    public void Shipped_configuration_matches_the_code_defaults(string section, int permitLimit, int windowMinutes)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "SmartSolar.Api", "appsettings.json"));
        var settings = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("RateLimiting").GetProperty(section);
        var definition = RateLimitingExtensions.PortalPolicies.Single(p => p.Section == $"RateLimiting:{section}");

        Assert.Equal((permitLimit, windowMinutes), (settings.GetProperty("PermitLimit").GetInt32(), settings.GetProperty("WindowMinutes").GetInt32()));
        Assert.Equal((permitLimit, windowMinutes), (definition.PermitLimit, definition.WindowMinutes));
    }

    /// <summary>RATE-LIMIT-PORTAL: below the limit 200, above it the enveloped 429 with Retry-After; another user (same IP) keeps their own budget.</summary>
    [Fact]
    public async Task Portal_read_limit_is_per_user_with_an_enveloped_429_and_retry_after()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?> { ["RateLimiting:PortalRead:PermitLimit"] = "2" });
        var first = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);
        var second = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/customers/me/survey-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/customers/me/sites")).StatusCode);
        var limited = await first.GetAsync("/api/customers/me/pre-surveys");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.InRange(limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
        var body = JsonDocument.Parse(await limited.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(AuthErrorCodes.TooManyRequests, body.GetProperty("error").GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/customers/me/survey-requests")).StatusCode);
    }

    /// <summary>An exhausted portal budget never blocks an unrelated policy of the same user (separate partitions).</summary>
    [Fact]
    public async Task An_exhausted_portal_budget_does_not_block_other_policies()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?> { ["RateLimiting:PortalWrite:PermitLimit"] = "1" });
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);

        await customer.PostAsync("/api/pre-surveys", JsonContent.Create(new { }));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await customer.PostAsync("/api/pre-surveys", JsonContent.Create(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/customers/me/survey-requests")).StatusCode);
    }

    /// <summary>A retired route still answers 410 below its limit (the policy does not change the retirement contract).</summary>
    [Fact]
    public async Task Retired_routes_keep_answering_410_below_their_limit()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?> { ["Features:InstallationWorkflowEnabled"] = "true" });
        var sales = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Sales);

        Assert.Equal(HttpStatusCode.Gone, (await sales.GetAsync("/api/quotations/pending-review")).StatusCode);
    }
}
