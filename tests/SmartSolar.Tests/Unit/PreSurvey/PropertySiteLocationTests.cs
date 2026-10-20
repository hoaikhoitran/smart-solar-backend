using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AdministrativeUnits;
using SmartSolar.Modules.PreSurvey.CreatePropertySite;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.PreSurvey;

/// <summary>The official location on site creation: formats in the validator, ACTIVE catalog rows in the handler (real codes).</summary>
public sealed class PropertySiteLocationTests
{
    private static CreatePropertySiteCommand Command(Guid userId, string? province, string? commune) => new(
        userId, "Main Rooftop", "Thành phố Hồ Chí Minh", null, "Phường Bình Tân", "1 Le Loi", null, null, InstallationSurfaceType.Rooftop, null, null,
        province, commune);

    [Theory]
    [InlineData("79", "2744", "CommuneCode")]          // lost a digit
    [InlineData("79", "79.777", "CommuneCode")]        // the old dotted model
    [InlineData("7", "27442", "ProvinceCode")]
    [InlineData(null, "27442", "ProvinceCode")]        // a commune requires its province
    public async Task Malformed_codes_are_rejected_by_the_validator(string? province, string? commune, string field)
    {
        var result = await new CreatePropertySiteCommandValidator().ValidateAsync(Command(Guid.NewGuid(), province, commune));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => string.Equals(e.PropertyName, field, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Well_formed_codes_and_no_codes_pass_the_validator()
    {
        Assert.True((await new CreatePropertySiteCommandValidator().ValidateAsync(Command(Guid.NewGuid(), "79", "27442"))).IsValid);
        Assert.True((await new CreatePropertySiteCommandValidator().ValidateAsync(Command(Guid.NewGuid(), "79", null))).IsValid);
        Assert.True((await new CreatePropertySiteCommandValidator().ValidateAsync(Command(Guid.NewGuid(), null, null))).IsValid);
    }

    [Fact]
    public async Task The_handler_stores_active_catalog_codes_and_refuses_unknown_wrong_province_or_superseded_ones()
    {
        await using var ctx = new PreSurveyTestContext();
        await AdministrativeCatalogTestData.SeedCatalogAsync(ctx.Db);
        var user = await ctx.SeedUserAsync();
        await ctx.SeedCustomerAsync(user.Id);
        var handler = ctx.CreatePropertySiteHandler();

        var created = await handler.HandleAsync(Command(user.Id, " 79 ", " 27442 "), CancellationToken.None);
        Assert.Equal(CreatePropertySiteOutcome.Created, created.Outcome);
        var site = await ctx.Db.PropertySites.AsNoTracking().SingleAsync();
        Assert.Equal(("79", "27442"), (site.ProvinceCode, site.CommuneCode));

        var unknown = await handler.HandleAsync(Command(user.Id, "79", "00001"), CancellationToken.None);
        Assert.Equal((CreatePropertySiteOutcome.InvalidLocation, AdministrativeUnitErrorCodes.LocationUnknown, "communeCode"),
            (unknown.Outcome, unknown.LocationProblem!.Code, unknown.LocationProblem.Field));
        // Hà Nội (01) is a real province but outside the service area: out of scope, distinct from an unknown code.
        Assert.Equal(AdministrativeUnitErrorCodes.LocationOutOfServiceArea, (await handler.HandleAsync(Command(user.Id, "01", "27442"), CancellationToken.None)).LocationProblem!.Code);

        await ctx.Db.AdministrativeCommunes.Where(c => c.Code == "26732").ExecuteUpdateAsync(x => x.SetProperty(c => c.ValidTo, new DateOnly(2026, 1, 1)));
        Assert.Equal(AdministrativeUnitErrorCodes.LocationSuperseded, (await handler.HandleAsync(Command(user.Id, "79", "26732"), CancellationToken.None)).LocationProblem!.Code);
        Assert.Equal(1, await ctx.Db.PropertySites.CountAsync());
    }

    [Fact]
    public async Task The_site_endpoint_answers_422_for_codes_outside_the_active_catalog()
    {
        using var factory = new AuthApiFactory();
        _ = factory.Services;
        await using (var db = factory.CreateDbContext()) await AdministrativeCatalogTestData.SeedCatalogAsync(db);
        var customer = await factory.SeedCustomerWithSiteAsync();
        object Body(string commune) => new { name = "Second roof", province = "Thành phố Hồ Chí Minh", provinceCode = "79", communeCode = commune };

        var refused = await customer.Client.PostAsJsonAsync("/api/customers/me/sites", Body("00001"));
        var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(AdministrativeUnitErrorCodes.LocationUnknown, body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.Client.PostAsJsonAsync("/api/customers/me/sites", Body("2744"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await customer.Client.PostAsJsonAsync("/api/customers/me/sites", Body("27442"))).StatusCode);
    }
}
