using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.CreatePreSurvey;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.PreSurvey;

public sealed class CreatePreSurveyHandlerTests
{
    private static CreatePreSurveyCommand CommandFor(Guid userId, Guid propertySiteId) => new(
        userId,
        propertySiteId,
        150.5m,
        120.25m,
        22.5m,
        185m,
        true);

    [Fact]
    public async Task Creates_draft_pre_survey_when_property_site_is_owned_by_customer()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var before = DateTimeOffset.UtcNow;

        // Act
        var result = await ctx
            .CreatePreSurveyHandler()
            .HandleAsync(CommandFor(user.Id, site.Id), CancellationToken.None);

        // Assert
        Assert.Equal(CreatePreSurveyOutcome.Created, result.Outcome);

        var preSurvey = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();

        Assert.Equal(preSurvey.Id, result.PreSurveyId);
        Assert.Equal(site.Id, preSurvey.PropertyId);
        Assert.Equal(PreSurveyStatus.Draft, preSurvey.Status);
        Assert.NotEqual(default, preSurvey.CreatedAt);
        Assert.NotEqual(default, preSurvey.UpdatedAt);
        Assert.True(preSurvey.CreatedAt >= before);
        Assert.Equal(preSurvey.CreatedAt, preSurvey.UpdatedAt);
    }

    [Fact]
    public async Task Persists_technical_fields_exactly()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);

        // Act
        await ctx
            .CreatePreSurveyHandler()
            .HandleAsync(CommandFor(user.Id, site.Id), CancellationToken.None);

        // Assert
        var preSurvey = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();

        Assert.Equal(150.5m, preSurvey.TotalAreaM2);
        Assert.Equal(120.25m, preSurvey.UsableAreaM2);
        Assert.Equal(22.5m, preSurvey.TiltDegree);
        Assert.Equal(185m, preSurvey.AzimuthDegree);
        Assert.True(preSurvey.HasObstruction);
    }

    [Fact]
    public async Task Returns_property_site_not_found_when_site_does_not_exist()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        await ctx.SeedCustomerAsync(user.Id);

        // Act
        var result = await ctx
            .CreatePreSurveyHandler()
            .HandleAsync(CommandFor(user.Id, Guid.NewGuid()), CancellationToken.None);

        // Assert
        Assert.Equal(CreatePreSurveyOutcome.PropertySiteNotFound, result.Outcome);
        Assert.Null(result.PreSurveyId);
        Assert.Equal(0, await ctx.Db.PreSurveys.CountAsync());
    }

    [Fact]
    public async Task Returns_customer_not_found_when_profile_does_not_exist()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var owner = await ctx.SeedUserAsync();
        var ownerCustomer = await ctx.SeedCustomerAsync(owner.Id);
        var site = await ctx.SeedPropertySiteAsync(ownerCustomer.Id);
        var userWithoutProfile = await ctx.SeedUserAsync();

        // Act
        var result = await ctx
            .CreatePreSurveyHandler()
            .HandleAsync(CommandFor(userWithoutProfile.Id, site.Id), CancellationToken.None);

        // Assert
        Assert.Equal(CreatePreSurveyOutcome.CustomerNotFound, result.Outcome);
        Assert.Null(result.PreSurveyId);
        Assert.Equal(0, await ctx.Db.PreSurveys.CountAsync());
    }

    [Fact]
    public async Task Rejects_property_site_owned_by_another_customer()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var owner = await ctx.SeedUserAsync();
        var ownerCustomer = await ctx.SeedCustomerAsync(owner.Id);
        var site = await ctx.SeedPropertySiteAsync(ownerCustomer.Id);

        var intruder = await ctx.SeedUserAsync();
        await ctx.SeedCustomerAsync(intruder.Id);

        // Act
        var result = await ctx
            .CreatePreSurveyHandler()
            .HandleAsync(CommandFor(intruder.Id, site.Id), CancellationToken.None);

        // Assert
        Assert.Equal(CreatePreSurveyOutcome.PropertySiteNotOwned, result.Outcome);
        Assert.Null(result.PreSurveyId);
        Assert.Equal(0, await ctx.Db.PreSurveys.CountAsync());
    }
}
