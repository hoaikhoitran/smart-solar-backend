using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.SolarSimulation;

/// <summary>
/// Surface and simulation endpoints through the real pipeline (JWT, roles, validation,
/// envelope, persistence). Providers are SYNTHETIC fakes; no external service is called.
/// </summary>
public sealed class SimulationEndpointsTests
{
    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> DataOf(HttpResponseMessage response)
    {
        var body = await BodyOf(response);
        Assert.True(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        return body.GetProperty("data");
    }

    private static async Task AssertFailureAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await BodyOf(response);
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
    }

    private static object SurfaceBody(int expectedRevision, object[]? obstacles = null) => new
    {
        expectedRevision,
        surfaceLengthM = 12,
        surfaceWidthM = 8,
        surfaceTiltDegree = 15,
        surfaceAzimuthDegree = 180,
        obstacles = obstacles ?? new object[]
        {
            new { name = "Rock", xM = 2, yM = 3, widthM = 2, lengthM = 1, heightM = 0.5 },
            new { name = "Water Tank", xM = 6, yM = 4, widthM = 1.5, lengthM = 2, heightM = 1.8 },
        }
    };

    private static object FlushBody(int expectedGeometryVersion, Guid productId) => new
    {
        expectedGeometryVersion,
        productId,
        mountingType = "FLUSH",
        installation = new { panelGapMm = 20 }
    };

    private static async Task<(SeededCustomer Customer, Guid PreSurveyId, Guid ProductId)> SeedDraftAsync(AuthApiFactory factory)
    {
        var customer = await factory.SeedCustomerWithSiteAsync();
        var preSurvey = await factory.SeedAsync(PreSurveyTestContext.NewPreSurvey(customer.Site.Id));
        var product = await factory.SeedAsync(PreSurveyTestContext.NewPanelProduct());
        return (customer, preSurvey.Id, product.Id);
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        using var factory = new AuthApiFactory();
        var client = factory.CreateClient();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/pre-surveys/{id}/surface")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/pre-surveys/{id}/simulations", FlushBody(1, Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/pre-surveys/{id}/simulations")).StatusCode);
    }

    [Fact]
    public async Task Sales_cannot_create_simulations()
    {
        using var factory = new AuthApiFactory();
        var (_, preSurveyId, productId) = await SeedDraftAsync(factory);
        var (_, sales) = await factory.SeedUserClientAsync(RoleCodes.Sales);

        var response = await sales.PostAsJsonAsync($"/api/pre-surveys/{preSurveyId}/simulations", FlushBody(0, productId));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Customer_saves_surface_simulates_and_sales_reads_the_selected_simulation()
    {
        using var factory = new AuthApiFactory();
        var (customer, preSurveyId, productId) = await SeedDraftAsync(factory);
        var client = customer.Client;

        // Save and reload the surface.
        var put = await client.PutAsJsonAsync($"/api/pre-surveys/{preSurveyId}/surface", SurfaceBody(0));
        var saved = await DataOf(put);
        Assert.Equal(1, saved.GetProperty("geometryVersion").GetInt32());

        var surface = await DataOf(await client.GetAsync($"/api/pre-surveys/{preSurveyId}/surface"));
        Assert.True(surface.GetProperty("surfaceDefined").GetBoolean());
        Assert.Equal(2m, surface.GetProperty("obstacles")[0].GetProperty("xM").GetDecimal());
        Assert.Equal(120m, surface.GetProperty("declared").GetProperty("totalAreaM2").GetDecimal());

        // Create, then repeat identically.
        var created = await client.PostAsJsonAsync($"/api/pre-surveys/{preSurveyId}/simulations", FlushBody(1, productId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var simulation = await DataOf(created);
        var simulationId = simulation.GetProperty("simulationId").GetGuid();
        Assert.Equal(SimulationStatusCodes.Completed, simulation.GetProperty("status").GetString());
        Assert.True(simulation.GetProperty("layout").GetProperty("panelCount").GetInt32() > 0);
        Assert.Equal("mm", simulation.GetProperty("installation").GetProperty("units").GetString());
        Assert.Equal(20m, simulation.GetProperty("installation").GetProperty("panelGapMm").GetProperty("valueMm").GetDecimal());
        var placement = simulation.GetProperty("layout").GetProperty("details").GetProperty("placements")[0];
        Assert.True(placement.GetProperty("frontCenter").TryGetProperty("xM", out _));
        Assert.True(placement.GetProperty("worldRotation").TryGetProperty("w", out _));

        var repeated = await client.PostAsJsonAsync($"/api/pre-surveys/{preSurveyId}/simulations", FlushBody(1, productId));
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.Equal(simulationId, (await DataOf(repeated)).GetProperty("simulationId").GetGuid());

        var list = await DataOf(await client.GetAsync($"/api/pre-surveys/{preSurveyId}/simulations"));
        Assert.Equal(1, list.GetArrayLength());
        Assert.True(list[0].GetProperty("isSelected").GetBoolean());

        // Submit (no simulation requirement), claim as Sales, read through the survey request.
        var submit = await DataOf(await client.PostAsync($"/api/pre-surveys/{preSurveyId}/submit", null));
        var surveyRequestId = submit.GetProperty("surveyRequestId").GetGuid();
        var (_, sales) = await factory.SeedUserClientAsync(RoleCodes.Sales);
        Assert.Equal(HttpStatusCode.OK, (await sales.PostAsync($"/api/survey-requests/{surveyRequestId}/claim", null)).StatusCode);

        var detail = await DataOf(await sales.GetAsync($"/api/survey-requests/{surveyRequestId}"));
        var selected = detail.GetProperty("selectedSimulation");
        Assert.Equal(simulationId, selected.GetProperty("simulationId").GetGuid());
        Assert.False(selected.GetProperty("isStale").GetBoolean());

        var salesView = await sales.GetAsync($"/api/pre-surveys/{preSurveyId}/simulations/{simulationId}");
        Assert.Equal(HttpStatusCode.OK, salesView.StatusCode);

        // Submitted pre-surveys accept no new simulations.
        await AssertFailureAsync(
            await client.PostAsJsonAsync($"/api/pre-surveys/{preSurveyId}/simulations", FlushBody(1, productId)),
            HttpStatusCode.Conflict, PreSurveyErrorCodes.PreSurveyNotEditable);
    }

    [Fact]
    public async Task Survey_request_without_simulation_has_a_null_summary()
    {
        using var factory = new AuthApiFactory();
        var customer = await factory.SeedCustomerWithSiteAsync();
        var preSurvey = await factory.SeedAsync(PreSurveyTestContext.NewPreSurvey(customer.Site.Id, Modules.PreSurvey.Enums.PreSurveyStatus.Submitted));
        var (sale, sales) = await factory.SeedUserClientAsync(RoleCodes.Sales);
        var request = await factory.SeedAsync(PreSurveyTestContext.NewSurveyRequest(
            preSurvey.Id, Modules.PreSurvey.Enums.SurveyRequestStatus.Assigned, sale.Id, assignedAt: DateTimeOffset.UtcNow));

        var detail = await DataOf(await sales.GetAsync($"/api/survey-requests/{request.Id}"));

        Assert.Equal(JsonValueKind.Null, detail.GetProperty("selectedSimulation").ValueKind);
    }

    [Fact]
    public async Task Obstacle_outside_the_surface_returns_field_errors()
    {
        using var factory = new AuthApiFactory();
        var (customer, preSurveyId, _) = await SeedDraftAsync(factory);

        var response = await customer.Client.PutAsJsonAsync($"/api/pre-surveys/{preSurveyId}/surface",
            SurfaceBody(0, [new { name = "Shed", xM = 7, yM = 1, widthM = 2, lengthM = 1 }]));

        await AssertFailureAsync(response, HttpStatusCode.BadRequest, PreSurveyErrorCodes.ValidationFailed);
        var details = (await BodyOf(response)).GetProperty("error").GetProperty("details");
        Assert.True(details.TryGetProperty("Obstacles[0].XM", out _));
    }

    [Fact]
    public async Task Null_obstacle_item_returns_the_validation_envelope_not_a_server_error()
    {
        using var factory = new AuthApiFactory();
        var (customer, preSurveyId, _) = await SeedDraftAsync(factory);

        var response = await customer.Client.PutAsJsonAsync($"/api/pre-surveys/{preSurveyId}/surface",
            SurfaceBody(0, [null!]));

        await AssertFailureAsync(response, HttpStatusCode.BadRequest, PreSurveyErrorCodes.ValidationFailed);
        var details = (await BodyOf(response)).GetProperty("error").GetProperty("details");
        Assert.True(details.TryGetProperty("Obstacles[0]", out _));
    }

    [Fact]
    public async Task Empty_obstacle_array_is_accepted()
    {
        using var factory = new AuthApiFactory();
        var (customer, preSurveyId, _) = await SeedDraftAsync(factory);

        var response = await customer.Client.PutAsJsonAsync($"/api/pre-surveys/{preSurveyId}/surface", SurfaceBody(0, []));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Stale_surface_revision_returns_conflict()
    {
        using var factory = new AuthApiFactory();
        var (customer, preSurveyId, _) = await SeedDraftAsync(factory);

        await AssertFailureAsync(
            await customer.Client.PutAsJsonAsync($"/api/pre-surveys/{preSurveyId}/surface", SurfaceBody(5)),
            HttpStatusCode.Conflict, PreSurveyErrorCodes.PreSurveyConcurrentlyModified);
    }

    [Fact]
    public async Task Simulation_without_a_surface_is_unprocessable()
    {
        using var factory = new AuthApiFactory();
        var (customer, preSurveyId, productId) = await SeedDraftAsync(factory);

        await AssertFailureAsync(
            await customer.Client.PostAsJsonAsync($"/api/pre-surveys/{preSurveyId}/simulations", FlushBody(0, productId)),
            (HttpStatusCode)422, SimulationErrorCodes.SurfaceNotDefined);
    }

    [Fact]
    public async Task Unknown_product_returns_not_found()
    {
        using var factory = new AuthApiFactory();
        var (customer, preSurveyId, _) = await SeedDraftAsync(factory);
        await customer.Client.PutAsJsonAsync($"/api/pre-surveys/{preSurveyId}/surface", SurfaceBody(0));

        await AssertFailureAsync(
            await customer.Client.PostAsJsonAsync($"/api/pre-surveys/{preSurveyId}/simulations", FlushBody(1, Guid.NewGuid())),
            HttpStatusCode.NotFound, SimulationErrorCodes.ProductNotFound);
    }

    [Fact]
    public async Task Invalid_mounting_input_returns_validation_errors()
    {
        using var factory = new AuthApiFactory();
        var (customer, preSurveyId, productId) = await SeedDraftAsync(factory);

        var response = await customer.Client.PostAsJsonAsync($"/api/pre-surveys/{preSurveyId}/simulations",
            new { expectedGeometryVersion = 0, productId, mountingType = "RACK", installation = new { panelGapMm = -1 } });

        await AssertFailureAsync(response, HttpStatusCode.BadRequest, SimulationErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Provider_failure_returns_a_partially_completed_snapshot_without_energy()
    {
        using var factory = new AuthApiFactory();
        factory.PvEnergy.Handler = (_, _) => Task.FromResult(PvEnergyResult.Fail(ProviderFailureCodes.Unavailable, "synthetic outage"));
        var (customer, preSurveyId, productId) = await SeedDraftAsync(factory);
        await customer.Client.PutAsJsonAsync($"/api/pre-surveys/{preSurveyId}/surface", SurfaceBody(0));

        var response = await customer.Client.PostAsJsonAsync($"/api/pre-surveys/{preSurveyId}/simulations", FlushBody(1, productId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = await DataOf(response);
        Assert.Equal(SimulationStatusCodes.PartiallyCompleted, data.GetProperty("status").GetString());
        Assert.Equal(ComponentStatusCodes.Failed, data.GetProperty("energy").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("energy").GetProperty("annualEnergyKwh").ValueKind);
    }

    [Fact]
    public async Task Another_customer_cannot_read_or_create()
    {
        using var factory = new AuthApiFactory();
        var (_, preSurveyId, productId) = await SeedDraftAsync(factory);
        var stranger = await factory.SeedCustomerWithSiteAsync("Stranger");

        await AssertFailureAsync(await stranger.Client.GetAsync($"/api/pre-surveys/{preSurveyId}/simulations"),
            HttpStatusCode.Forbidden, SimulationErrorCodes.AccessDenied);
        await AssertFailureAsync(await stranger.Client.GetAsync($"/api/pre-surveys/{preSurveyId}/surface"),
            HttpStatusCode.Forbidden, PreSurveyErrorCodes.PreSurveyNotOwned);
        await AssertFailureAsync(await stranger.Client.PostAsJsonAsync($"/api/pre-surveys/{preSurveyId}/simulations", FlushBody(0, productId)),
            HttpStatusCode.Forbidden, PreSurveyErrorCodes.PreSurveyNotOwned);
    }

    [Fact]
    public async Task Unassigned_sales_cannot_read_simulations()
    {
        using var factory = new AuthApiFactory();
        var (_, preSurveyId, _) = await SeedDraftAsync(factory);
        var (_, sales) = await factory.SeedUserClientAsync(RoleCodes.Sales);

        await AssertFailureAsync(await sales.GetAsync($"/api/pre-surveys/{preSurveyId}/simulations"),
            HttpStatusCode.Forbidden, SimulationErrorCodes.AccessDenied);
    }
}
