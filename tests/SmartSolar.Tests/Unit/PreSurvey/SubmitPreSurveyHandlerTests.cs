using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.SubmitPreSurvey;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.PreSurvey;

public sealed class SubmitPreSurveyHandlerTests
{
    [Fact]
    public async Task Creates_pending_survey_request_when_pre_survey_is_submitted()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var draft = await ctx.SeedPreSurveyAsync(site.Id);
        var before = DateTimeOffset.UtcNow;

        // Act
        var result = await ctx
            .SubmitPreSurveyHandler()
            .HandleAsync(new SubmitPreSurveyCommand(user.Id, draft.Id), CancellationToken.None);

        // Assert
        Assert.Equal(SubmitPreSurveyOutcome.Submitted, result.Outcome);

        ctx.Db.ChangeTracker.Clear();
        var persistedPreSurvey = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();
        var surveyRequest = await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync();

        Assert.Equal(PreSurveyStatus.Submitted, persistedPreSurvey.Status);
        Assert.True(persistedPreSurvey.UpdatedAt >= before);

        Assert.Equal(result.SurveyRequestId, surveyRequest.Id);
        Assert.Equal(draft.Id, surveyRequest.PreSurveyId);
        Assert.Equal(SurveyRequestStatus.Pending, surveyRequest.Status);
        Assert.Null(surveyRequest.AssignedSaleId);
        Assert.Null(surveyRequest.AssignedAt);
        Assert.Null(surveyRequest.ScheduledAt);
        Assert.Null(surveyRequest.SalesNote);
        Assert.NotEqual(default, surveyRequest.SubmittedAt);
        Assert.True(surveyRequest.SubmittedAt >= before);
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
            .SubmitPreSurveyHandler()
            .HandleAsync(new SubmitPreSurveyCommand(userWithoutProfile.Id, draft.Id), CancellationToken.None);

        // Assert
        Assert.Equal(SubmitPreSurveyOutcome.CustomerNotFound, result.Outcome);
        Assert.Null(result.SurveyRequestId);
        await AssertStillDraftWithoutRequestAsync(ctx);
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
            .SubmitPreSurveyHandler()
            .HandleAsync(new SubmitPreSurveyCommand(user.Id, Guid.NewGuid()), CancellationToken.None);

        // Assert
        Assert.Equal(SubmitPreSurveyOutcome.PreSurveyNotFound, result.Outcome);
        Assert.Equal(0, await ctx.Db.SurveyRequests.CountAsync());
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
            .SubmitPreSurveyHandler()
            .HandleAsync(new SubmitPreSurveyCommand(intruder.Id, draft.Id), CancellationToken.None);

        // Assert
        Assert.Equal(SubmitPreSurveyOutcome.NotOwned, result.Outcome);
        await AssertStillDraftWithoutRequestAsync(ctx);
    }

    [Fact]
    public async Task Returns_already_submitted_when_pre_survey_is_not_a_draft()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var submitted = await ctx.SeedPreSurveyAsync(site.Id, PreSurveyStatus.Submitted);

        // Act
        var result = await ctx
            .SubmitPreSurveyHandler()
            .HandleAsync(new SubmitPreSurveyCommand(user.Id, submitted.Id), CancellationToken.None);

        // Assert
        Assert.Equal(SubmitPreSurveyOutcome.AlreadySubmitted, result.Outcome);
        Assert.Null(result.SurveyRequestId);
        Assert.Equal(0, await ctx.Db.SurveyRequests.CountAsync());
    }

    [Fact]
    public async Task Returns_incomplete_when_technical_fields_are_missing()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var draft = await ctx.SeedPreSurveyAsync(site.Id, complete: false);

        // Act
        var result = await ctx
            .SubmitPreSurveyHandler()
            .HandleAsync(new SubmitPreSurveyCommand(user.Id, draft.Id), CancellationToken.None);

        // Assert
        Assert.Equal(SubmitPreSurveyOutcome.Incomplete, result.Outcome);
        Assert.Null(result.SurveyRequestId);
        await AssertStillDraftWithoutRequestAsync(ctx);
    }

    [Theory]
    [InlineData(nameof(Modules.PreSurvey.Entities.PreSurvey.TotalAreaM2))]
    [InlineData(nameof(Modules.PreSurvey.Entities.PreSurvey.UsableAreaM2))]
    [InlineData(nameof(Modules.PreSurvey.Entities.PreSurvey.TiltDegree))]
    [InlineData(nameof(Modules.PreSurvey.Entities.PreSurvey.AzimuthDegree))]
    public async Task Returns_incomplete_when_a_single_required_field_is_missing(string missingField)
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var draft = await ctx.SeedPreSurveyAsync(site.Id);

        var tracked = await ctx.Db.PreSurveys.SingleAsync(x => x.Id == draft.Id);
        ctx.Db.Entry(tracked).Property(missingField).CurrentValue = null;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        // Act
        var result = await ctx
            .SubmitPreSurveyHandler()
            .HandleAsync(new SubmitPreSurveyCommand(user.Id, draft.Id), CancellationToken.None);

        // Assert
        Assert.Equal(SubmitPreSurveyOutcome.Incomplete, result.Outcome);
        await AssertStillDraftWithoutRequestAsync(ctx);
    }

    [Fact]
    public async Task Returns_incomplete_when_property_site_has_no_installation_surface_type()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id, installationSurfaceType: null);
        var draft = await ctx.SeedPreSurveyAsync(site.Id);

        // Act
        var result = await ctx
            .SubmitPreSurveyHandler()
            .HandleAsync(new SubmitPreSurveyCommand(user.Id, draft.Id), CancellationToken.None);

        // Assert
        Assert.Equal(SubmitPreSurveyOutcome.Incomplete, result.Outcome);
        await AssertStillDraftWithoutRequestAsync(ctx);
    }

    [Fact]
    public async Task Does_not_create_a_second_survey_request_when_submitted_twice()
    {
        // Arrange
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var draft = await ctx.SeedPreSurveyAsync(site.Id);
        var command = new SubmitPreSurveyCommand(user.Id, draft.Id);

        // Act
        var first = await ctx.SubmitPreSurveyHandler().HandleAsync(command, CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        var second = await ctx.SubmitPreSurveyHandler().HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal(SubmitPreSurveyOutcome.Submitted, first.Outcome);
        Assert.Equal(SubmitPreSurveyOutcome.AlreadySubmitted, second.Outcome);

        var requests = await ctx.Db.SurveyRequests.AsNoTracking().ToListAsync();
        var request = Assert.Single(requests);
        Assert.Equal(first.SurveyRequestId, request.Id);
    }

    /// <summary>
    /// Each submit runs on its own SQLite connection to a shared temp-file database, so
    /// both handlers can read the Draft before either writes. SQLite's write lock then
    /// serialises the two transactions the way PostgreSQL's row lock does.
    /// </summary>
    [Fact]
    public async Task Only_one_submit_wins_when_the_same_pre_survey_is_submitted_concurrently()
    {
        // Arrange
        await using var ctx = PreSurveyTestContext.CreateFileBacked();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var draft = await ctx.SeedPreSurveyAsync(site.Id);

        var handlerA = new SubmitPreSurveyHandler(ctx.CreateUnitOfWorkOnNewConnection());
        var handlerB = new SubmitPreSurveyHandler(ctx.CreateUnitOfWorkOnNewConnection());
        var command = new SubmitPreSurveyCommand(user.Id, draft.Id);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<SubmitPreSurveyResult> SubmitAsync(SubmitPreSurveyHandler handler)
        {
            await start.Task;
            return await handler.HandleAsync(command, CancellationToken.None);
        }

        var submitA = Task.Run(() => SubmitAsync(handlerA));
        var submitB = Task.Run(() => SubmitAsync(handlerB));

        // Act
        start.SetResult();
        var results = await Task.WhenAll(submitA, submitB);

        // Assert
        var winner = Assert.Single(results, x => x.Outcome == SubmitPreSurveyOutcome.Submitted);
        Assert.Single(results, x => x.Outcome == SubmitPreSurveyOutcome.AlreadySubmitted);

        ctx.Db.ChangeTracker.Clear();
        var requests = await ctx.Db.SurveyRequests.AsNoTracking()
            .Where(x => x.PreSurveyId == draft.Id)
            .ToListAsync();
        var request = Assert.Single(requests);
        Assert.Equal(winner.SurveyRequestId, request.Id);

        var preSurvey = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();
        Assert.Equal(PreSurveyStatus.Submitted, preSurvey.Status);
    }

    [Fact]
    public async Task Atomic_submit_stores_nothing_when_pre_survey_left_draft_after_it_was_read()
    {
        // Arrange: the state a losing request sees once the winner has committed.
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var submitted = await ctx.SeedPreSurveyAsync(site.Id, PreSurveyStatus.Submitted);
        var request = PreSurveyTestContext.NewSurveyRequest(submitted.Id);

        // Act
        var stored = await ctx.UnitOfWork.TrySubmitPreSurveyAsync(request, CancellationToken.None);

        // Assert
        Assert.False(stored);
        Assert.Equal(0, await ctx.Db.SurveyRequests.CountAsync());
        var preSurvey = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();
        Assert.Equal(submitted.UpdatedAt, preSurvey.UpdatedAt);
    }

    private static async Task AssertStillDraftWithoutRequestAsync(PreSurveyTestContext ctx)
    {
        ctx.Db.ChangeTracker.Clear();
        var preSurvey = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();

        Assert.Equal(PreSurveyStatus.Draft, preSurvey.Status);
        Assert.Equal(0, await ctx.Db.SurveyRequests.CountAsync());
    }
}
