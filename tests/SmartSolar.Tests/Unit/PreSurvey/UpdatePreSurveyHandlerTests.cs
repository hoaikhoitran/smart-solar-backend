using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.UpdatePreSurvey;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.PreSurvey;

public sealed class UpdatePreSurveyHandlerTests
{
    private static UpdatePreSurveyCommand CommandFor(Guid userId, Guid preSurveyId) => new(
        userId,
        preSurveyId,
        200m,
        175.5m,
        30m,
        90m,
        true);

    [Fact]
    public async Task Updates_draft_owned_by_customer()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var draft = await ctx.SeedPreSurveyAsync(site.Id);

        // Act
        var result = await ctx
            .UpdatePreSurveyHandler()
            .HandleAsync(CommandFor(user.Id, draft.Id), CancellationToken.None);

        // Assert
        Assert.Equal(UpdatePreSurveyOutcome.Updated, result.Outcome);

        var updated = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();

        Assert.Equal(200m, updated.TotalAreaM2);
        Assert.Equal(175.5m, updated.UsableAreaM2);
        Assert.Equal(30m, updated.TiltDegree);
        Assert.Equal(90m, updated.AzimuthDegree);
        Assert.True(updated.HasObstruction);
        Assert.Equal(PreSurveyStatus.Draft, updated.Status);
    }

    [Fact]
    public async Task Refreshes_updated_at_but_keeps_created_at()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var draft = await ctx.SeedPreSurveyAsync(site.Id);

        // Act
        await ctx
            .UpdatePreSurveyHandler()
            .HandleAsync(CommandFor(user.Id, draft.Id), CancellationToken.None);

        // Assert
        var updated = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();

        Assert.True(updated.UpdatedAt > draft.UpdatedAt);
        Assert.Equal(draft.CreatedAt, updated.CreatedAt);
    }

    [Fact]
    public async Task Returns_customer_not_found_when_profile_does_not_exist()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var owner = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(owner.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var draft = await ctx.SeedPreSurveyAsync(site.Id);
        var userWithoutProfile = await ctx.SeedUserAsync();

        // Act
        var result = await ctx
            .UpdatePreSurveyHandler()
            .HandleAsync(CommandFor(userWithoutProfile.Id, draft.Id), CancellationToken.None);

        // Assert
        Assert.Equal(UpdatePreSurveyOutcome.CustomerNotFound, result.Outcome);
        await AssertUnchangedAsync(ctx, draft);
    }

    [Fact]
    public async Task Returns_pre_survey_not_found_when_it_does_not_exist()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        await ctx.SeedCustomerAsync(user.Id);

        // Act
        var result = await ctx
            .UpdatePreSurveyHandler()
            .HandleAsync(CommandFor(user.Id, Guid.NewGuid()), CancellationToken.None);

        // Assert
        Assert.Equal(UpdatePreSurveyOutcome.PreSurveyNotFound, result.Outcome);
    }

    [Fact]
    public async Task Rejects_pre_survey_owned_by_another_customer()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var owner = await ctx.SeedUserAsync();
        var ownerCustomer = await ctx.SeedCustomerAsync(owner.Id);
        var site = await ctx.SeedPropertySiteAsync(ownerCustomer.Id);
        var draft = await ctx.SeedPreSurveyAsync(site.Id);

        var intruder = await ctx.SeedUserAsync();
        await ctx.SeedCustomerAsync(intruder.Id);

        // Act
        var result = await ctx
            .UpdatePreSurveyHandler()
            .HandleAsync(CommandFor(intruder.Id, draft.Id), CancellationToken.None);

        // Assert
        Assert.Equal(UpdatePreSurveyOutcome.NotOwned, result.Outcome);
        await AssertUnchangedAsync(ctx, draft);
    }

    [Fact]
    public async Task Returns_not_editable_and_keeps_data_when_pre_survey_is_submitted()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var submitted = await ctx.SeedPreSurveyAsync(site.Id, PreSurveyStatus.Submitted);

        // Act
        var result = await ctx
            .UpdatePreSurveyHandler()
            .HandleAsync(CommandFor(user.Id, submitted.Id), CancellationToken.None);

        // Assert
        Assert.Equal(UpdatePreSurveyOutcome.NotEditable, result.Outcome);
        await AssertUnchangedAsync(ctx, submitted);
    }

    private static async Task AssertUnchangedAsync(
        PreSurveyTestContext ctx,
        Modules.PreSurvey.Entities.PreSurvey original)
    {
        ctx.Db.ChangeTracker.Clear();
        var stored = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync(x => x.Id == original.Id);

        Assert.Equal(original.TotalAreaM2, stored.TotalAreaM2);
        Assert.Equal(original.UsableAreaM2, stored.UsableAreaM2);
        Assert.Equal(original.TiltDegree, stored.TiltDegree);
        Assert.Equal(original.AzimuthDegree, stored.AzimuthDegree);
        Assert.Equal(original.HasObstruction, stored.HasObstruction);
        Assert.Equal(original.Status, stored.Status);
        Assert.Equal(original.UpdatedAt, stored.UpdatedAt);
    }
}
