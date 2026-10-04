using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.PreSurvey;

public sealed class GetPendingSurveyRequestsHandlerTests
{
    [Fact]
    public async Task Returns_only_pending_unassigned_requests_oldest_first()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var sale = await ctx.SeedSalesUserAsync();
        var now = DateTimeOffset.UtcNow;

        var preSurveyA = await ctx.SeedSubmittedPreSurveyAsync("Customer A", "Site A");
        var preSurveyB = await ctx.SeedSubmittedPreSurveyAsync("Customer B", "Site B");
        var preSurveyC = await ctx.SeedSubmittedPreSurveyAsync("Customer C", "Site C");

        var requestC = await ctx.SeedSurveyRequestAsync(preSurveyC.Id, submittedAt: now.AddHours(-3));
        var requestA = await ctx.SeedSurveyRequestAsync(preSurveyA.Id, submittedAt: now.AddHours(-1));
        var requestB = await ctx.SeedSurveyRequestAsync(
            preSurveyB.Id,
            SurveyRequestStatus.Assigned,
            sale.Id,
            submittedAt: now.AddHours(-5),
            assignedAt: now.AddHours(-4));

        // Act
        var items = await ctx
            .GetPendingSurveyRequestsHandler()
            .HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(
            new[] { requestC.Id, requestA.Id },
            items.Select(x => x.SurveyRequestId));
        Assert.DoesNotContain(items, x => x.SurveyRequestId == requestB.Id);
    }

    [Fact]
    public async Task Excludes_pending_requests_that_already_have_a_sale()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var sale = await ctx.SeedSalesUserAsync();
        var preSurvey = await ctx.SeedSubmittedPreSurveyAsync();
        await ctx.SeedSurveyRequestAsync(
            preSurvey.Id,
            SurveyRequestStatus.Pending,
            sale.Id,
            assignedAt: DateTimeOffset.UtcNow);

        // Act
        var items = await ctx
            .GetPendingSurveyRequestsHandler()
            .HandleAsync(CancellationToken.None);

        // Assert
        Assert.Empty(items);
    }

    [Fact]
    public async Task Maps_customer_property_and_pre_survey_fields()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var preSurvey = await ctx.SeedSubmittedPreSurveyAsync(
            customerName: "Nguyen Van A",
            propertyName: "Factory Roof",
            province: "Da Nang",
            district: "Hai Chau");
        var submittedAt = new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero);
        var request = await ctx.SeedSurveyRequestAsync(preSurvey.Id, submittedAt: submittedAt);

        // Act
        var items = await ctx
            .GetPendingSurveyRequestsHandler()
            .HandleAsync(CancellationToken.None);

        // Assert
        var item = Assert.Single(items);
        Assert.Equal(request.Id, item.SurveyRequestId);
        Assert.Equal(preSurvey.Id, item.PreSurveyId);
        Assert.Equal("Nguyen Van A", item.CustomerName);
        Assert.Equal("Factory Roof", item.PropertyName);
        Assert.Equal("Da Nang", item.Province);
        Assert.Equal("Hai Chau", item.District);
        Assert.Equal(InstallationSurfaceType.Rooftop, item.InstallationSurfaceType);
        Assert.Equal(preSurvey.TotalAreaM2, item.TotalAreaM2);
        Assert.Equal(preSurvey.UsableAreaM2, item.UsableAreaM2);
        Assert.Equal(submittedAt, item.SubmittedAt);
    }
}
