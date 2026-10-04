using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.PreSurvey;

/// <summary>
/// Sales-side survey request endpoints through the real pipeline: JWT, role
/// authorization, ApiResponse envelope and persistence.
/// </summary>
public class SurveyRequestsEndpointsTests
{
    private const string BaseUrl = "/api/survey-requests";

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task AssertEnvelopeFailureAsync(HttpResponseMessage response, string expectedCode)
    {
        var body = await BodyOf(response);
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal(expectedCode, body.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>Asserts a success envelope and returns its data.</summary>
    private static async Task<JsonElement> SuccessDataOf(HttpResponseMessage response)
    {
        var body = await BodyOf(response);
        Assert.True(body.GetProperty("isSuccess").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
        return body.GetProperty("data");
    }

    private static async Task<SurveyRequest> SeedRequestAsync(
        AuthApiFactory factory,
        string customerName = "Customer",
        SurveyRequestStatus status = SurveyRequestStatus.Pending,
        Guid? assignedSaleId = null,
        DateTimeOffset? submittedAt = null,
        DateTimeOffset? assignedAt = null)
    {
        var customer = await factory.SeedCustomerWithSiteAsync(customerName);
        var preSurvey = await factory.SeedAsync(
            PreSurveyTestContext.NewPreSurvey(customer.Site.Id, PreSurveyStatus.Submitted));
        return await factory.SeedAsync(PreSurveyTestContext.NewSurveyRequest(
            preSurvey.Id, status, assignedSaleId, submittedAt, assignedAt));
    }

    private static List<Guid> IdsOf(JsonElement array)
        => array.EnumerateArray().Select(x => x.GetProperty("surveyRequestId").GetGuid()).ToList();

    // ---------- GET /pending ----------

    [Fact]
    public async Task Pending_rejects_anonymous_requests()
    {
        // Arrange
        using var factory = new AuthApiFactory();

        // Act
        var response = await factory.CreateClient().GetAsync($"{BaseUrl}/pending");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertEnvelopeFailureAsync(response, AuthErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task Pending_is_forbidden_for_customers()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var (_, customer) = await factory.SeedUserClientAsync(RoleCodes.Customer);

        // Act
        var response = await customer.GetAsync($"{BaseUrl}/pending");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertEnvelopeFailureAsync(response, AuthErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Pending_returns_only_unassigned_pending_requests_to_sales()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var (otherSale, _) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Other Sales");
        var (_, sales) = await factory.SeedUserClientAsync(RoleCodes.Sales);
        var now = DateTimeOffset.UtcNow;

        var newer = await SeedRequestAsync(factory, "Customer A", submittedAt: now.AddHours(-1));
        await SeedRequestAsync(
            factory, "Customer B", SurveyRequestStatus.Assigned, otherSale.Id, now.AddHours(-5), now.AddHours(-4));
        var older = await SeedRequestAsync(factory, "Customer C", submittedAt: now.AddHours(-3));

        // Act
        var response = await sales.GetAsync($"{BaseUrl}/pending");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await SuccessDataOf(response);
        Assert.Equal(new[] { older.Id, newer.Id }, IdsOf(data));
        Assert.Equal("Customer C", data[0].GetProperty("customerName").GetString());
    }

    // ---------- POST /{id}/claim ----------

    [Fact]
    public async Task Claim_assigns_pending_request_to_sales()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var (sale, client) = await factory.SeedUserClientAsync(RoleCodes.Sales);
        var pending = await SeedRequestAsync(factory);

        // Act
        var response = await client.PostAsync($"{BaseUrl}/{pending.Id}/claim", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await SuccessDataOf(response);
        Assert.Equal(pending.Id, data.GetProperty("surveyRequestId").GetGuid());

        var request = Assert.Single(await factory.SurveyRequestsAsync());
        Assert.Equal(sale.Id, request.AssignedSaleId);
        Assert.Equal(SurveyRequestStatus.Assigned, request.Status);
        Assert.NotNull(request.AssignedAt);
    }

    [Fact]
    public async Task Claim_returns_conflict_when_another_sale_already_claimed()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var (saleA, clientA) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Sales A");
        var (_, clientB) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Sales B");
        var pending = await SeedRequestAsync(factory);
        var first = await clientA.PostAsync($"{BaseUrl}/{pending.Id}/claim", null);

        // Act
        var second = await clientB.PostAsync($"{BaseUrl}/{pending.Id}/claim", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await AssertEnvelopeFailureAsync(second, PreSurveyErrorCodes.SurveyRequestUnavailable);

        var request = Assert.Single(await factory.SurveyRequestsAsync());
        Assert.Equal(saleA.Id, request.AssignedSaleId);
    }

    [Fact]
    public async Task Claim_is_forbidden_for_customers()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var (_, customer) = await factory.SeedUserClientAsync(RoleCodes.Customer);
        var pending = await SeedRequestAsync(factory);

        // Act
        var response = await customer.PostAsync($"{BaseUrl}/{pending.Id}/claim", null);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertEnvelopeFailureAsync(response, AuthErrorCodes.Forbidden);

        var request = Assert.Single(await factory.SurveyRequestsAsync());
        Assert.Null(request.AssignedSaleId);
        Assert.Equal(SurveyRequestStatus.Pending, request.Status);
    }

    [Fact]
    public async Task Claim_rejects_anonymous_requests()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var pending = await SeedRequestAsync(factory);

        // Act
        var response = await factory.CreateClient().PostAsync($"{BaseUrl}/{pending.Id}/claim", null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertEnvelopeFailureAsync(response, AuthErrorCodes.Unauthorized);
        Assert.Null(Assert.Single(await factory.SurveyRequestsAsync()).AssignedSaleId);
    }

    // ---------- GET /my ----------

    [Fact]
    public async Task My_returns_only_requests_assigned_to_current_sale()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var (saleA, clientA) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Sales A");
        var (saleB, _) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Sales B");
        var now = DateTimeOffset.UtcNow;

        var older = await SeedRequestAsync(
            factory, "Customer A", SurveyRequestStatus.Assigned, saleA.Id, now.AddDays(-2), now.AddDays(-2));
        var newer = await SeedRequestAsync(
            factory, "Customer B", SurveyRequestStatus.Assigned, saleA.Id, now.AddDays(-2), now.AddDays(-1));
        await SeedRequestAsync(
            factory, "Customer C", SurveyRequestStatus.Assigned, saleB.Id, now.AddDays(-2), now);

        // Act
        var response = await clientA.GetAsync($"{BaseUrl}/my");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { newer.Id, older.Id }, IdsOf(await SuccessDataOf(response)));
    }

    // ---------- GET /{id} ----------

    [Fact]
    public async Task Detail_returns_request_to_assigned_sale()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var (sale, client) = await factory.SeedUserClientAsync(RoleCodes.Sales);
        var request = await SeedRequestAsync(
            factory, "Detail Customer", SurveyRequestStatus.Assigned, sale.Id, assignedAt: DateTimeOffset.UtcNow);

        // Act
        var response = await client.GetAsync($"{BaseUrl}/{request.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await SuccessDataOf(response);
        Assert.Equal(request.Id, body.GetProperty("surveyRequestId").GetGuid());
        Assert.Equal(request.PreSurveyId, body.GetProperty("preSurveyId").GetGuid());
        Assert.Equal(sale.Id, body.GetProperty("assignedSaleId").GetGuid());
        Assert.Equal("Detail Customer", body.GetProperty("customerName").GetString());
        Assert.Equal((int)SurveyRequestStatus.Assigned, body.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Detail_is_forbidden_for_another_sale()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var (saleA, _) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Sales A");
        var (_, clientB) = await factory.SeedUserClientAsync(RoleCodes.Sales, "Sales B");
        var request = await SeedRequestAsync(
            factory, status: SurveyRequestStatus.Assigned, assignedSaleId: saleA.Id, assignedAt: DateTimeOffset.UtcNow);

        // Act
        var response = await clientB.GetAsync($"{BaseUrl}/{request.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertEnvelopeFailureAsync(response, PreSurveyErrorCodes.SurveyRequestNotAssigned);
    }

    [Fact]
    public async Task Detail_returns_not_found_for_nonexistent_request()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var (_, client) = await factory.SeedUserClientAsync(RoleCodes.Sales);

        // Act
        var response = await client.GetAsync($"{BaseUrl}/{Guid.NewGuid()}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertEnvelopeFailureAsync(response, PreSurveyErrorCodes.SurveyRequestNotFound);
    }

    // ---------- End-to-end ----------

    [Fact]
    public async Task Customer_submission_flows_through_to_sales_claim_and_detail()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var (_, customer) = await factory.SeedUserClientAsync(RoleCodes.Customer, "Flow Customer");
        var (sale, sales) = await factory.SeedUserClientAsync(RoleCodes.Sales);

        // Act
        var profile = await customer.PostAsJsonAsync("/api/customers/me", new { customerType = (int)CustomerType.Individual });
        var site = await customer.PostAsJsonAsync("/api/customers/me/sites", new
        {
            name = "Home",
            province = "Hue",
            installationSurfaceType = (int)InstallationSurfaceType.Rooftop
        });
        var siteId = (await BodyOf(site)).GetProperty("data").GetProperty("propertySiteId").GetGuid();
        var draft = await customer.PostAsJsonAsync("/api/pre-surveys", new { propertySiteId = siteId });
        var preSurveyId = (await BodyOf(draft)).GetProperty("data").GetProperty("preSurveyId").GetGuid();
        var update = await customer.PutAsJsonAsync($"/api/pre-surveys/{preSurveyId}", new
        {
            totalAreaM2 = 60m,
            usableAreaM2 = 45m,
            tiltDegree = 10m,
            azimuthDegree = 180m,
            hasObstruction = false
        });
        var submit = await customer.PostAsync($"/api/pre-surveys/{preSurveyId}/submit", null);
        var requestId = (await BodyOf(submit)).GetProperty("data").GetProperty("surveyRequestId").GetGuid();

        var pending = await sales.GetAsync($"{BaseUrl}/pending");
        var claim = await sales.PostAsync($"{BaseUrl}/{requestId}/claim", null);
        var pendingAfterClaim = await sales.GetAsync($"{BaseUrl}/pending");
        var my = await sales.GetAsync($"{BaseUrl}/my");
        var detail = await sales.GetAsync($"{BaseUrl}/{requestId}");

        // Assert
        Assert.Equal(HttpStatusCode.Created, profile.StatusCode);
        Assert.Equal(HttpStatusCode.Created, site.StatusCode);
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        Assert.Equal(new[] { requestId }, IdsOf(await SuccessDataOf(pending)));
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        Assert.Empty(IdsOf(await SuccessDataOf(pendingAfterClaim)));
        Assert.Equal(new[] { requestId }, IdsOf(await SuccessDataOf(my)));

        var detailBody = await SuccessDataOf(detail);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal("Flow Customer", detailBody.GetProperty("customerName").GetString());
        Assert.Equal(60m, detailBody.GetProperty("totalAreaM2").GetDecimal());
        Assert.Equal(sale.Id, detailBody.GetProperty("assignedSaleId").GetGuid());
        Assert.Equal((int)SurveyRequestStatus.Assigned, detailBody.GetProperty("status").GetInt32());
    }
}
