using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.PreSurvey;

/// <summary>
/// Customer-side PreSurvey endpoints through the real pipeline: JWT, role
/// authorization, FluentValidation, envelope and persistence.
/// </summary>
public class PreSurveyCustomerEndpointsTests
{
    private const string SitesUrl = "/api/customers/me/sites";
    private const string PreSurveysUrl = "/api/pre-surveys";

    private static object SiteBody(decimal? latitude = 10.77m, decimal? longitude = 106.70m) => new
    {
        name = "  Main Rooftop  ",
        province = "Ho Chi Minh",
        district = "District 1",
        ward = (string?)null,
        streetLine = "1 Le Loi",
        latitude,
        longitude,
        installationSurfaceType = (int)InstallationSurfaceType.Rooftop,
        surfaceMaterial = "Concrete",
        note = "   "
    };

    private static object PreSurveyBody(Guid propertySiteId, decimal? totalArea = 120m, decimal? usableArea = 90m) => new
    {
        propertySiteId,
        totalAreaM2 = totalArea,
        usableAreaM2 = usableArea,
        tiltDegree = 15m,
        azimuthDegree = 180m,
        hasObstruction = false
    };

    private static object UpdateBody() => new
    {
        totalAreaM2 = 200m,
        usableAreaM2 = 150m,
        tiltDegree = 25m,
        azimuthDegree = 170m,
        hasObstruction = true
    };

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task AssertFailureAsync(HttpResponseMessage response, string expectedCode)
    {
        var body = await BodyOf(response);
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(expectedCode, body.GetProperty("error").GetProperty("code").GetString());
    }

    // ---------- POST /api/customers/me/sites ----------

    [Fact]
    public async Task Create_site_rejects_anonymous_requests()
    {
        // Arrange
        using var factory = new AuthApiFactory();

        // Act
        var response = await factory.CreateClient().PostAsJsonAsync(SitesUrl, SiteBody());

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertFailureAsync(response, AuthErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task Create_site_succeeds_for_customer_with_profile()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var (user, client) = await factory.SeedUserClientAsync(RoleCodes.Customer);
        var customer = await factory.SeedAsync(PreSurveyTestContext.NewCustomer(user.Id));

        // Act
        var response = await client.PostAsJsonAsync(SitesUrl, SiteBody());

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);
        Assert.True(body.GetProperty("isSuccess").GetBoolean());
        var siteId = body.GetProperty("data").GetProperty("propertySiteId").GetGuid();

        await using var db = factory.CreateDbContext();
        var site = await db.PropertySites.AsNoTracking().SingleAsync();
        Assert.Equal(siteId, site.Id);
        Assert.Equal(customer.Id, site.CustomerId);
        Assert.Equal("Main Rooftop", site.Name);
        Assert.Equal(InstallationSurfaceType.Rooftop, site.InstallationSurfaceType);
        Assert.Null(site.Note);
    }

    [Fact]
    public async Task Create_site_is_forbidden_for_sales()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var (_, sales) = await factory.SeedUserClientAsync(RoleCodes.Sales);

        // Act
        var response = await sales.PostAsJsonAsync(SitesUrl, SiteBody());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertFailureAsync(response, AuthErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData(91, 106)]
    [InlineData(-91, 106)]
    [InlineData(10, 181)]
    [InlineData(10, -181)]
    public async Task Create_site_rejects_out_of_range_coordinates(int latitude, int longitude)
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var (user, client) = await factory.SeedUserClientAsync(RoleCodes.Customer);
        await factory.SeedAsync(PreSurveyTestContext.NewCustomer(user.Id));

        // Act
        var response = await client.PostAsJsonAsync(SitesUrl, SiteBody(latitude, longitude));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertFailureAsync(response, PreSurveyErrorCodes.ValidationFailed);

        await using var db = factory.CreateDbContext();
        Assert.Equal(0, await db.PropertySites.CountAsync());
    }

    [Fact]
    public async Task Create_site_returns_not_found_when_customer_profile_is_missing()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var (_, client) = await factory.SeedUserClientAsync(RoleCodes.Customer);

        // Act
        var response = await client.PostAsJsonAsync(SitesUrl, SiteBody());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertFailureAsync(response, PreSurveyErrorCodes.CustomerProfileNotFound);

        await using var db = factory.CreateDbContext();
        Assert.Equal(0, await db.PropertySites.CountAsync());
    }

    // ---------- POST /api/pre-surveys ----------

    [Fact]
    public async Task Create_pre_survey_rejects_anonymous_requests()
    {
        // Arrange
        using var factory = new AuthApiFactory();

        // Act
        var response = await factory.CreateClient().PostAsJsonAsync(PreSurveysUrl, PreSurveyBody(Guid.NewGuid()));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertFailureAsync(response, AuthErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task Create_pre_survey_creates_draft_for_customer()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var owner = await factory.SeedCustomerWithSiteAsync();

        // Act
        var response = await owner.Client.PostAsJsonAsync(PreSurveysUrl, PreSurveyBody(owner.Site.Id));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await BodyOf(response)).GetProperty("data").GetProperty("preSurveyId").GetGuid();

        var stored = await factory.ReloadPreSurveyAsync(id);
        Assert.Equal(owner.Site.Id, stored.PropertyId);
        Assert.Equal(PreSurveyStatus.Draft, stored.Status);
        Assert.Equal(120m, stored.TotalAreaM2);
        Assert.Equal(90m, stored.UsableAreaM2);
    }

    [Fact]
    public async Task Create_pre_survey_is_forbidden_for_sales()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var owner = await factory.SeedCustomerWithSiteAsync();
        var (_, sales) = await factory.SeedUserClientAsync(RoleCodes.Sales);

        // Act
        var response = await sales.PostAsJsonAsync(PreSurveysUrl, PreSurveyBody(owner.Site.Id));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertFailureAsync(response, AuthErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Create_pre_survey_rejects_property_site_owned_by_another_customer()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var owner = await factory.SeedCustomerWithSiteAsync("Owner");
        var intruder = await factory.SeedCustomerWithSiteAsync("Intruder");

        // Act
        var response = await intruder.Client.PostAsJsonAsync(PreSurveysUrl, PreSurveyBody(owner.Site.Id));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertFailureAsync(response, PreSurveyErrorCodes.PropertySiteNotOwned);

        await using var db = factory.CreateDbContext();
        Assert.Equal(0, await db.PreSurveys.CountAsync());
    }

    [Fact]
    public async Task Create_pre_survey_rejects_invalid_data()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var owner = await factory.SeedCustomerWithSiteAsync();

        // Act
        var response = await owner.Client.PostAsJsonAsync(
            PreSurveysUrl,
            PreSurveyBody(owner.Site.Id, totalArea: 50m, usableArea: 80m));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyOf(response);
        Assert.Equal(PreSurveyErrorCodes.ValidationFailed, body.GetProperty("error").GetProperty("code").GetString());
        Assert.True(body.GetProperty("error").GetProperty("details").TryGetProperty("UsableAreaM2", out _));

        await using var db = factory.CreateDbContext();
        Assert.Equal(0, await db.PreSurveys.CountAsync());
    }

    // ---------- PUT /api/pre-surveys/{id} ----------

    [Fact]
    public async Task Update_succeeds_for_owner_of_draft()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var owner = await factory.SeedCustomerWithSiteAsync();
        var draft = await factory.SeedAsync(PreSurveyTestContext.NewPreSurvey(owner.Site.Id));

        // Act
        var response = await owner.Client.PutAsJsonAsync($"{PreSurveysUrl}/{draft.Id}", UpdateBody());

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var stored = await factory.ReloadPreSurveyAsync(draft.Id);
        Assert.Equal(200m, stored.TotalAreaM2);
        Assert.Equal(150m, stored.UsableAreaM2);
        Assert.Equal(25m, stored.TiltDegree);
        Assert.Equal(170m, stored.AzimuthDegree);
        Assert.True(stored.HasObstruction);
        Assert.True(stored.UpdatedAt > draft.UpdatedAt);
    }

    [Fact]
    public async Task Update_returns_conflict_for_submitted_pre_survey()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var owner = await factory.SeedCustomerWithSiteAsync();
        var submitted = await factory.SeedAsync(
            PreSurveyTestContext.NewPreSurvey(owner.Site.Id, PreSurveyStatus.Submitted));

        // Act
        var response = await owner.Client.PutAsJsonAsync($"{PreSurveysUrl}/{submitted.Id}", UpdateBody());

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertFailureAsync(response, PreSurveyErrorCodes.PreSurveyNotEditable);

        var stored = await factory.ReloadPreSurveyAsync(submitted.Id);
        Assert.Equal(submitted.TotalAreaM2, stored.TotalAreaM2);
        Assert.Equal(submitted.UpdatedAt, stored.UpdatedAt);
    }

    [Fact]
    public async Task Update_is_forbidden_for_another_customer()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var owner = await factory.SeedCustomerWithSiteAsync("Owner");
        var intruder = await factory.SeedCustomerWithSiteAsync("Intruder");
        var draft = await factory.SeedAsync(PreSurveyTestContext.NewPreSurvey(owner.Site.Id));

        // Act
        var response = await intruder.Client.PutAsJsonAsync($"{PreSurveysUrl}/{draft.Id}", UpdateBody());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertFailureAsync(response, PreSurveyErrorCodes.PreSurveyNotOwned);

        var stored = await factory.ReloadPreSurveyAsync(draft.Id);
        Assert.Equal(draft.TotalAreaM2, stored.TotalAreaM2);
    }

    [Fact]
    public async Task Update_returns_not_found_for_nonexistent_pre_survey()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var owner = await factory.SeedCustomerWithSiteAsync();

        // Act
        var response = await owner.Client.PutAsJsonAsync($"{PreSurveysUrl}/{Guid.NewGuid()}", UpdateBody());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertFailureAsync(response, PreSurveyErrorCodes.PreSurveyNotFound);
    }

    // ---------- POST /api/pre-surveys/{id}/submit ----------

    [Fact]
    public async Task Submit_creates_pending_survey_request_for_complete_draft()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var owner = await factory.SeedCustomerWithSiteAsync();
        var draft = await factory.SeedAsync(PreSurveyTestContext.NewPreSurvey(owner.Site.Id));

        // Act
        var response = await owner.Client.PostAsync($"{PreSurveysUrl}/{draft.Id}/submit", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requestId = (await BodyOf(response)).GetProperty("data").GetProperty("surveyRequestId").GetGuid();

        var stored = await factory.ReloadPreSurveyAsync(draft.Id);
        Assert.Equal(PreSurveyStatus.Submitted, stored.Status);

        var request = Assert.Single(await factory.SurveyRequestsAsync());
        Assert.Equal(requestId, request.Id);
        Assert.Equal(draft.Id, request.PreSurveyId);
        Assert.Equal(SurveyRequestStatus.Pending, request.Status);
        Assert.Null(request.AssignedSaleId);
    }

    [Fact]
    public async Task Submit_returns_bad_request_for_incomplete_draft()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var owner = await factory.SeedCustomerWithSiteAsync();
        var draft = await factory.SeedAsync(PreSurveyTestContext.NewPreSurvey(owner.Site.Id, complete: false));

        // Act
        var response = await owner.Client.PostAsync($"{PreSurveysUrl}/{draft.Id}/submit", null);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertFailureAsync(response, PreSurveyErrorCodes.PreSurveyIncomplete);

        Assert.Equal(PreSurveyStatus.Draft, (await factory.ReloadPreSurveyAsync(draft.Id)).Status);
        Assert.Empty(await factory.SurveyRequestsAsync());
    }

    [Fact]
    public async Task Submit_returns_conflict_when_already_submitted()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var owner = await factory.SeedCustomerWithSiteAsync();
        var draft = await factory.SeedAsync(PreSurveyTestContext.NewPreSurvey(owner.Site.Id));
        var first = await owner.Client.PostAsync($"{PreSurveysUrl}/{draft.Id}/submit", null);

        // Act
        var second = await owner.Client.PostAsync($"{PreSurveysUrl}/{draft.Id}/submit", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await AssertFailureAsync(second, PreSurveyErrorCodes.PreSurveyAlreadySubmitted);
        Assert.Single(await factory.SurveyRequestsAsync());
    }

    [Fact]
    public async Task Submit_is_forbidden_for_another_customer()
    {
        // Arrange
        using var factory = new AuthApiFactory();
        var owner = await factory.SeedCustomerWithSiteAsync("Owner");
        var intruder = await factory.SeedCustomerWithSiteAsync("Intruder");
        var draft = await factory.SeedAsync(PreSurveyTestContext.NewPreSurvey(owner.Site.Id));

        // Act
        var response = await intruder.Client.PostAsync($"{PreSurveysUrl}/{draft.Id}/submit", null);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertFailureAsync(response, PreSurveyErrorCodes.PreSurveyNotOwned);

        Assert.Equal(PreSurveyStatus.Draft, (await factory.ReloadPreSurveyAsync(draft.Id)).Status);
        Assert.Empty(await factory.SurveyRequestsAsync());
    }
}
