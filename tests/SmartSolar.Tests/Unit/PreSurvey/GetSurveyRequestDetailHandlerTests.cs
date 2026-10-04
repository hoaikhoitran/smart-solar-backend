using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.PreSurvey;

public sealed class GetSurveyRequestDetailHandlerTests
{
    [Fact]
    public async Task Returns_full_detail_to_the_assigned_sale()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var sale = await ctx.SeedSalesUserAsync();
        var customerUser = await ctx.SeedUserAsync(
            "Le Van C",
            email: "le.van.c@test.com",
            phone: "0901234567");
        var customer = await ctx.SeedCustomerAsync(customerUser.Id);
        var site = await ctx.SeedPropertySiteAsync(
            customer.Id,
            "Villa Roof",
            "Ha Noi",
            "Ba Dinh",
            InstallationSurfaceType.Ground);
        var preSurvey = await ctx.SeedPreSurveyAsync(site.Id, PreSurveyStatus.Submitted);
        var submittedAt = new DateTimeOffset(2026, 10, 2, 7, 0, 0, TimeSpan.Zero);
        var assignedAt = new DateTimeOffset(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);
        var request = await ctx.SeedSurveyRequestAsync(
            preSurvey.Id,
            SurveyRequestStatus.Assigned,
            sale.Id,
            submittedAt,
            assignedAt);

        // Act
        var result = await ctx
            .GetSurveyRequestDetailHandler()
            .HandleAsync(request.Id, sale.Id, CancellationToken.None);

        // Assert
        Assert.Equal(GetSurveyRequestDetailOutcome.Found, result.Outcome);
        var detail = Assert.IsType<SurveyRequestDetail>(result.Detail);

        Assert.Equal(request.Id, detail.SurveyRequestId);
        Assert.Equal(preSurvey.Id, detail.PreSurveyId);
        Assert.Equal(sale.Id, detail.AssignedSaleId);

        Assert.Equal("Le Van C", detail.CustomerName);
        Assert.Equal("0901234567", detail.CustomerPhone);
        Assert.Equal("le.van.c@test.com", detail.CustomerEmail);

        Assert.Equal("Villa Roof", detail.PropertyName);
        Assert.Equal("Ha Noi", detail.Province);
        Assert.Equal("Ba Dinh", detail.District);
        Assert.Equal(site.Ward, detail.Ward);
        Assert.Equal(site.StreetLine, detail.StreetLine);
        Assert.Equal(site.Latitude, detail.Latitude);
        Assert.Equal(site.Longitude, detail.Longitude);
        Assert.Equal(InstallationSurfaceType.Ground, detail.InstallationSurfaceType);
        Assert.Equal(site.SurfaceMaterial, detail.SurfaceMaterial);

        Assert.Equal(preSurvey.TotalAreaM2, detail.TotalAreaM2);
        Assert.Equal(preSurvey.UsableAreaM2, detail.UsableAreaM2);
        Assert.Equal(preSurvey.TiltDegree, detail.TiltDegree);
        Assert.Equal(preSurvey.AzimuthDegree, detail.AzimuthDegree);
        Assert.Equal(preSurvey.HasObstruction, detail.HasObstruction);

        Assert.Equal(SurveyRequestStatus.Assigned, detail.Status);
        Assert.Equal(submittedAt, detail.SubmittedAt);
        Assert.Equal(assignedAt, detail.AssignedAt);
        Assert.Null(detail.ScheduledAt);
        Assert.Null(detail.SalesNote);
    }

    [Fact]
    public async Task Returns_not_assigned_to_sale_when_another_sale_owns_the_request()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var saleA = await ctx.SeedSalesUserAsync("Sales A");
        var saleB = await ctx.SeedSalesUserAsync("Sales B");
        var preSurvey = await ctx.SeedSubmittedPreSurveyAsync();
        var request = await ctx.SeedSurveyRequestAsync(
            preSurvey.Id,
            SurveyRequestStatus.Assigned,
            saleA.Id,
            assignedAt: DateTimeOffset.UtcNow);

        // Act
        var result = await ctx
            .GetSurveyRequestDetailHandler()
            .HandleAsync(request.Id, saleB.Id, CancellationToken.None);

        // Assert
        Assert.Equal(GetSurveyRequestDetailOutcome.NotAssignedToSale, result.Outcome);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task Returns_not_assigned_to_sale_when_request_is_still_pending()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var sale = await ctx.SeedSalesUserAsync();
        var request = await ctx.SeedSurveyRequestAsync((await ctx.SeedSubmittedPreSurveyAsync()).Id);

        // Act
        var result = await ctx
            .GetSurveyRequestDetailHandler()
            .HandleAsync(request.Id, sale.Id, CancellationToken.None);

        // Assert
        Assert.Equal(GetSurveyRequestDetailOutcome.NotAssignedToSale, result.Outcome);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task Returns_not_found_when_request_does_not_exist()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var sale = await ctx.SeedSalesUserAsync();

        // Act
        var result = await ctx
            .GetSurveyRequestDetailHandler()
            .HandleAsync(Guid.NewGuid(), sale.Id, CancellationToken.None);

        // Assert
        Assert.Equal(GetSurveyRequestDetailOutcome.NotFound, result.Outcome);
        Assert.Null(result.Detail);
    }
}
