using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>Installation Flow through the real pipeline: feature gates (including the deprecated flag), PayOS webhook exposure.</summary>
public sealed class InstallationWorkflowEndpointsTests
{
    [Fact]
    public async Task Installation_workflow_endpoints_answer_503_until_the_migration_is_applied()
    {
        using var factory = new AuthApiFactory();
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);

        await AssertFailureAsync(await customer.GetAsync($"/api/customers/me/contracts/{Guid.NewGuid()}/payments"), HttpStatusCode.ServiceUnavailable, "FEATURE_PENDING_MIGRATION");
        await AssertFailureAsync(await customer.GetAsync("/api/customers/me/quotations"), HttpStatusCode.ServiceUnavailable, "FEATURE_PENDING_MIGRATION");
        await AssertFailureAsync(await customer.GetAsync($"/api/files/{Guid.NewGuid()}"), HttpStatusCode.ServiceUnavailable, "FEATURE_PENDING_MIGRATION");
        await AssertFailureAsync(await factory.CreateClient().PostAsync("/api/payments/payos/webhook", JsonContent.Create(new { })), HttpStatusCode.ServiceUnavailable,
            "FEATURE_PENDING_MIGRATION");
        // Authentication still comes first.
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/customers/me/quotations")).StatusCode);
    }

    [Fact]
    public async Task Each_flag_gates_only_its_own_feature_and_files_follow_either()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), new Dictionary<string, string?>
        {
            ["Features:KnowledgeBaseEnabled"] = "false",
        });
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);

        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/customers/me/quotations")).StatusCode);
        await AssertFailureAsync(await customer.GetAsync("/api/chat/sessions"), HttpStatusCode.ServiceUnavailable, "FEATURE_PENDING_MIGRATION");
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, (await customer.GetAsync($"/api/files/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task The_deprecated_flow2_flag_still_enables_both_features()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?> { ["Features:Flow2Enabled"] = "true", ["Rag:Enabled"] = "false" });
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);

        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/customers/me/quotations")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/chat/sessions")).StatusCode);
        Assert.True(factory.Services.GetRequiredService<SmartSolar.Modules.Common.Features.FeatureOptions>().UsesLegacyFlag);
    }

    [Fact]
    public async Task Payos_webhooks_are_anonymous_and_never_throttled_by_user_limits()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var anonymous = factory.CreateClient();
        for (var i = 0; i < 25; i++)
        {
            var response = await anonymous.PostAsync("/api/payments/payos/webhook",
                new StringContent("{\"code\":\"00\",\"data\":{\"orderCode\":1},\"signature\":\"bad\"}", System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}
