using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.PreSurvey;

public sealed class GetMySurveyRequestsHandlerTests
{
    [Fact]
    public async Task Returns_only_requests_assigned_to_current_sale()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var saleA = await ctx.SeedSalesUserAsync("Sales A");
        var saleB = await ctx.SeedSalesUserAsync("Sales B");
        var now = DateTimeOffset.UtcNow;

        var requestA = await ctx.SeedSurveyRequestAsync(
            (await ctx.SeedSubmittedPreSurveyAsync("Customer A")).Id,
            SurveyRequestStatus.Assigned, saleA.Id, now.AddDays(-3), now.AddDays(-2));
        var requestB = await ctx.SeedSurveyRequestAsync(
            (await ctx.SeedSubmittedPreSurveyAsync("Customer B")).Id,
            SurveyRequestStatus.Assigned, saleA.Id, now.AddDays(-3), now.AddDays(-1));
        var requestC = await ctx.SeedSurveyRequestAsync(
            (await ctx.SeedSubmittedPreSurveyAsync("Customer C")).Id,
            SurveyRequestStatus.Assigned, saleB.Id, now.AddDays(-3), now.AddHours(-1));
        await ctx.SeedSurveyRequestAsync((await ctx.SeedSubmittedPreSurveyAsync("Customer D")).Id);

        // Act
        var items = await ctx
            .GetMySurveyRequestsHandler()
            .HandleAsync(saleA.Id, CancellationToken.None);

        // Assert
        Assert.Equal(
            new[] { requestB.Id, requestA.Id },
            items.Select(x => x.SurveyRequestId));
        Assert.DoesNotContain(items, x => x.SurveyRequestId == requestC.Id);
    }

    [Fact]
    public async Task Returns_empty_list_when_sale_has_no_requests()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var sale = await ctx.SeedSalesUserAsync();
        await ctx.SeedSurveyRequestAsync((await ctx.SeedSubmittedPreSurveyAsync()).Id);

        // Act
        var items = await ctx
            .GetMySurveyRequestsHandler()
            .HandleAsync(sale.Id, CancellationToken.None);

        // Assert
        Assert.Empty(items);
    }

    [Fact]
    public async Task Maps_request_and_property_fields()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var sale = await ctx.SeedSalesUserAsync();
        var preSurvey = await ctx.SeedSubmittedPreSurveyAsync(
            customerName: "Tran Thi B",
            propertyName: "Warehouse",
            province: "Can Tho",
            district: "Ninh Kieu");
        var submittedAt = new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
        var assignedAt = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var request = await ctx.SeedSurveyRequestAsync(
            preSurvey.Id,
            SurveyRequestStatus.Assigned,
            sale.Id,
            submittedAt,
            assignedAt);

        // Act
        var items = await ctx
            .GetMySurveyRequestsHandler()
            .HandleAsync(sale.Id, CancellationToken.None);

        // Assert
        var item = Assert.Single(items);
        Assert.Equal(request.Id, item.SurveyRequestId);
        Assert.Equal(preSurvey.Id, item.PreSurveyId);
        Assert.Equal("Tran Thi B", item.CustomerName);
        Assert.Equal("Warehouse", item.PropertyName);
        Assert.Equal("Can Tho", item.Province);
        Assert.Equal("Ninh Kieu", item.District);
        Assert.Equal(SurveyRequestStatus.Assigned, item.Status);
        Assert.Equal(submittedAt, item.SubmittedAt);
        Assert.Equal(assignedAt, item.AssignedAt);
        Assert.Null(item.ScheduledAt);
    }
}
