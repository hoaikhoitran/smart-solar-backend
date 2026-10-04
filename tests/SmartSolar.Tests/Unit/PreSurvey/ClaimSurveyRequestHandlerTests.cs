using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.ClaimSurveyRequest;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.PreSurvey;

public sealed class ClaimSurveyRequestHandlerTests
{
    [Fact]
    public async Task Claims_pending_request_for_the_sale()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var sale = await ctx.SeedSalesUserAsync();
        var preSurvey = await ctx.SeedSubmittedPreSurveyAsync();
        var pending = await ctx.SeedSurveyRequestAsync(preSurvey.Id);
        var before = DateTimeOffset.UtcNow;

        // Act
        var result = await ctx
            .ClaimSurveyRequestHandler()
            .HandleAsync(new ClaimSurveyRequestCommand(pending.Id, sale.Id), CancellationToken.None);

        // Assert
        Assert.Equal(ClaimSurveyRequestOutcome.Claimed, result.Outcome);

        var request = await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync();
        Assert.Equal(sale.Id, request.AssignedSaleId);
        Assert.Equal(SurveyRequestStatus.Assigned, request.Status);
        Assert.NotNull(request.AssignedAt);
        Assert.True(request.AssignedAt >= before);
        Assert.Equal(pending.SubmittedAt, request.SubmittedAt);
    }

    [Fact]
    public async Task Returns_unavailable_when_request_has_already_been_claimed()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var saleA = await ctx.SeedSalesUserAsync("Sales A");
        var saleB = await ctx.SeedSalesUserAsync("Sales B");
        var preSurvey = await ctx.SeedSubmittedPreSurveyAsync();
        var assignedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var claimed = await ctx.SeedSurveyRequestAsync(
            preSurvey.Id,
            SurveyRequestStatus.Assigned,
            saleA.Id,
            assignedAt: assignedAt);

        // Act
        var result = await ctx
            .ClaimSurveyRequestHandler()
            .HandleAsync(new ClaimSurveyRequestCommand(claimed.Id, saleB.Id), CancellationToken.None);

        // Assert
        Assert.Equal(ClaimSurveyRequestOutcome.Unavailable, result.Outcome);

        var request = await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync();
        Assert.Equal(saleA.Id, request.AssignedSaleId);
        Assert.Equal(SurveyRequestStatus.Assigned, request.Status);
        Assert.Equal(assignedAt, request.AssignedAt);
    }

    [Fact]
    public async Task Returns_unavailable_when_request_does_not_exist()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var sale = await ctx.SeedSalesUserAsync();

        // Act
        var result = await ctx
            .ClaimSurveyRequestHandler()
            .HandleAsync(new ClaimSurveyRequestCommand(Guid.NewGuid(), sale.Id), CancellationToken.None);

        // Assert
        Assert.Equal(ClaimSurveyRequestOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task Returns_unavailable_when_the_same_sale_claims_twice()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var sale = await ctx.SeedSalesUserAsync();
        var preSurvey = await ctx.SeedSubmittedPreSurveyAsync();
        var pending = await ctx.SeedSurveyRequestAsync(preSurvey.Id);
        var command = new ClaimSurveyRequestCommand(pending.Id, sale.Id);

        // Act
        var first = await ctx.ClaimSurveyRequestHandler().HandleAsync(command, CancellationToken.None);
        var second = await ctx.ClaimSurveyRequestHandler().HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal(ClaimSurveyRequestOutcome.Claimed, first.Outcome);
        Assert.Equal(ClaimSurveyRequestOutcome.Unavailable, second.Outcome);
    }

    /// <summary>
    /// Each sale runs on its own SQLite connection to a shared temp-file database, so
    /// the two conditional UPDATEs genuinely race and SQLite's write lock decides the
    /// order. A single in-memory connection would serialise them in one handle and
    /// prove nothing. PostgreSQL row locks give the same one-winner guarantee.
    /// </summary>
    [Fact]
    public async Task Only_one_sale_wins_when_two_claim_the_same_request_concurrently()
    {
        // Arrange
        await using var ctx = PreSurveyTestContext.CreateFileBacked();
        var saleA = await ctx.SeedSalesUserAsync("Sales A");
        var saleB = await ctx.SeedSalesUserAsync("Sales B");
        var preSurvey = await ctx.SeedSubmittedPreSurveyAsync();
        var pending = await ctx.SeedSurveyRequestAsync(preSurvey.Id);

        var handlerA = new ClaimSurveyRequestHandler(ctx.CreateUnitOfWorkOnNewConnection());
        var handlerB = new ClaimSurveyRequestHandler(ctx.CreateUnitOfWorkOnNewConnection());
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<(Guid SaleId, ClaimSurveyRequestOutcome Outcome)> ClaimAsync(
            ClaimSurveyRequestHandler handler,
            Guid saleId)
        {
            await start.Task;
            var result = await handler.HandleAsync(
                new ClaimSurveyRequestCommand(pending.Id, saleId),
                CancellationToken.None);
            return (saleId, result.Outcome);
        }

        var claimA = Task.Run(() => ClaimAsync(handlerA, saleA.Id));
        var claimB = Task.Run(() => ClaimAsync(handlerB, saleB.Id));

        // Act
        start.SetResult();
        var results = await Task.WhenAll(claimA, claimB);

        // Assert
        var winner = Assert.Single(results, x => x.Outcome == ClaimSurveyRequestOutcome.Claimed);
        Assert.Single(results, x => x.Outcome == ClaimSurveyRequestOutcome.Unavailable);

        ctx.Db.ChangeTracker.Clear();
        var request = await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync();
        Assert.Equal(winner.SaleId, request.AssignedSaleId);
        Assert.Equal(SurveyRequestStatus.Assigned, request.Status);
        Assert.NotNull(request.AssignedAt);
    }
}
