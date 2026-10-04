using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.CreatePropertySite;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.PreSurvey;

public sealed class CreatePropertySiteHandlerTests
{
    private static CreatePropertySiteCommand ValidCommand(Guid userId) => new(
        userId,
        "Main Rooftop",
        "Ho Chi Minh",
        "District 1",
        "Ben Nghe",
        "1 Le Loi",
        10.776889m,
        106.700806m,
        InstallationSurfaceType.Rooftop,
        "Concrete",
        "Access via stairs");

    [Fact]
    public async Task Creates_property_site_when_customer_exists()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);

        // Act
        var result = await ctx
            .CreatePropertySiteHandler()
            .HandleAsync(ValidCommand(user.Id), CancellationToken.None);

        // Assert
        Assert.Equal(CreatePropertySiteOutcome.Created, result.Outcome);

        var site = await ctx.Db.PropertySites.AsNoTracking().SingleAsync();

        Assert.Equal(site.Id, result.PropertySiteId);
        Assert.Equal(customer.Id, site.CustomerId);
        Assert.Equal("Main Rooftop", site.Name);
        Assert.Equal("Ho Chi Minh", site.Province);
        Assert.Equal("District 1", site.District);
        Assert.Equal("Ben Nghe", site.Ward);
        Assert.Equal("1 Le Loi", site.StreetLine);
        Assert.Equal(10.776889m, site.Latitude);
        Assert.Equal(106.700806m, site.Longitude);
        Assert.Equal(InstallationSurfaceType.Rooftop, site.InstallationSurfaceType);
        Assert.Equal("Concrete", site.SurfaceMaterial);
        Assert.Equal("Access via stairs", site.Note);
    }

    [Fact]
    public async Task Returns_customer_not_found_when_profile_does_not_exist()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();

        // Act
        var result = await ctx
            .CreatePropertySiteHandler()
            .HandleAsync(ValidCommand(user.Id), CancellationToken.None);

        // Assert
        Assert.Equal(CreatePropertySiteOutcome.CustomerNotFound, result.Outcome);
        Assert.Null(result.PropertySiteId);
        Assert.Equal(0, await ctx.Db.PropertySites.CountAsync());
    }

    [Fact]
    public async Task Links_property_site_to_the_current_users_customer_only()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var otherUser = await ctx.SeedUserAsync();
        await ctx.SeedCustomerAsync(otherUser.Id);
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);

        // Act
        await ctx
            .CreatePropertySiteHandler()
            .HandleAsync(ValidCommand(user.Id), CancellationToken.None);

        // Assert
        var site = await ctx.Db.PropertySites.AsNoTracking().SingleAsync();
        Assert.Equal(customer.Id, site.CustomerId);
    }

    [Fact]
    public async Task Trims_name_and_province_before_persisting()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        await ctx.SeedCustomerAsync(user.Id);

        var command = ValidCommand(user.Id) with
        {
            Name = "  Main Rooftop  ",
            Province = "  Ho Chi Minh  ",
            District = "  District 1  ",
            SurfaceMaterial = "  Concrete  "
        };

        // Act
        await ctx
            .CreatePropertySiteHandler()
            .HandleAsync(command, CancellationToken.None);

        // Assert
        var site = await ctx.Db.PropertySites.AsNoTracking().SingleAsync();
        Assert.Equal("Main Rooftop", site.Name);
        Assert.Equal("Ho Chi Minh", site.Province);
        Assert.Equal("District 1", site.District);
        Assert.Equal("Concrete", site.SurfaceMaterial);
    }

    [Fact]
    public async Task Stores_optional_whitespace_fields_as_null()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        await ctx.SeedCustomerAsync(user.Id);

        var command = ValidCommand(user.Id) with
        {
            District = "   ",
            Ward = " ",
            StreetLine = "\t",
            SurfaceMaterial = "  ",
            Note = "    "
        };

        // Act
        await ctx
            .CreatePropertySiteHandler()
            .HandleAsync(command, CancellationToken.None);

        // Assert
        var site = await ctx.Db.PropertySites.AsNoTracking().SingleAsync();
        Assert.Null(site.District);
        Assert.Null(site.Ward);
        Assert.Null(site.StreetLine);
        Assert.Null(site.SurfaceMaterial);
        Assert.Null(site.Note);
    }

    [Fact]
    public async Task Persists_coordinates_and_surface_type_exactly()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        await ctx.SeedCustomerAsync(user.Id);

        var command = ValidCommand(user.Id) with
        {
            Latitude = -89.123456m,
            Longitude = 179.654321m,
            InstallationSurfaceType = InstallationSurfaceType.Carport
        };

        // Act
        await ctx
            .CreatePropertySiteHandler()
            .HandleAsync(command, CancellationToken.None);

        // Assert
        var site = await ctx.Db.PropertySites.AsNoTracking().SingleAsync();
        Assert.Equal(-89.123456m, site.Latitude);
        Assert.Equal(179.654321m, site.Longitude);
        Assert.Equal(InstallationSurfaceType.Carport, site.InstallationSurfaceType);
    }

    [Fact]
    public async Task Persists_null_coordinates_and_surface_type_when_omitted()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        await ctx.SeedCustomerAsync(user.Id);

        var command = ValidCommand(user.Id) with
        {
            Latitude = null,
            Longitude = null,
            InstallationSurfaceType = null
        };

        // Act
        await ctx
            .CreatePropertySiteHandler()
            .HandleAsync(command, CancellationToken.None);

        // Assert
        var site = await ctx.Db.PropertySites.AsNoTracking().SingleAsync();
        Assert.Null(site.Latitude);
        Assert.Null(site.Longitude);
        Assert.Null(site.InstallationSurfaceType);
    }
}
