using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.SubmitPreSurvey;
using SmartSolar.Modules.PreSurvey.UpdatePreSurvey;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.PreSurvey;

/// <summary>
/// Update versus Submit consistency, enforced by the revision concurrency token in the
/// database. Competing writes run on a second SQLite connection between the handler's
/// read and its write, the same window the production race has.
/// </summary>
public sealed class PreSurveyConcurrencyTests
{
    private static UpdatePreSurveyCommand UpdateOf(Guid userId, Guid preSurveyId, decimal tilt = 20m, decimal azimuth = 180m, decimal usable = 100m)
        => new(userId, preSurveyId, 150m, usable, tilt, azimuth, true);

    [Fact]
    public async Task Update_that_loses_the_race_to_submit_is_rejected_and_stores_nothing()
    {
        await using var ctx = PreSurveyTestContext.CreateFileBacked();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var draft = await ctx.SeedPreSurveyAsync(site.Id);

        var updatingUow = new InterleavingPreSurveyUnitOfWork(ctx.CreateUnitOfWorkOnNewConnection());
        var submitHandler = new SubmitPreSurveyHandler(ctx.CreateUnitOfWorkOnNewConnection());
        updatingUow.AfterPreSurveyRead = async () =>
        {
            var submitted = await submitHandler.HandleAsync(new SubmitPreSurveyCommand(user.Id, draft.Id), CancellationToken.None);
            Assert.Equal(SubmitPreSurveyOutcome.Submitted, submitted.Outcome);
        };

        var result = await new UpdatePreSurveyHandler(updatingUow).HandleAsync(UpdateOf(user.Id, draft.Id), CancellationToken.None);

        Assert.Equal(UpdatePreSurveyOutcome.NotEditable, result.Outcome);
        var stored = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();
        Assert.Equal(PreSurveyStatus.Submitted, stored.Status);
        Assert.Equal(120m, stored.TotalAreaM2);
        Assert.Equal(15m, stored.TiltDegree);
        Assert.Single(await ctx.Db.SurveyRequests.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Update_that_loses_the_race_to_another_update_reports_concurrent_modification()
    {
        await using var ctx = PreSurveyTestContext.CreateFileBacked();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var draft = await ctx.SeedPreSurveyAsync(site.Id);

        var slowUow = new InterleavingPreSurveyUnitOfWork(ctx.CreateUnitOfWorkOnNewConnection());
        var fastHandler = new UpdatePreSurveyHandler(ctx.CreateUnitOfWorkOnNewConnection());
        slowUow.AfterPreSurveyRead = async () =>
        {
            var first = await fastHandler.HandleAsync(UpdateOf(user.Id, draft.Id, tilt: 25m), CancellationToken.None);
            Assert.Equal(UpdatePreSurveyOutcome.Updated, first.Outcome);
        };

        var result = await new UpdatePreSurveyHandler(slowUow).HandleAsync(UpdateOf(user.Id, draft.Id, tilt: 30m), CancellationToken.None);

        Assert.Equal(UpdatePreSurveyOutcome.ConcurrentlyModified, result.Outcome);
        var stored = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();
        Assert.Equal(25m, stored.TiltDegree);
        Assert.Equal(1, stored.Revision);
    }

    [Fact]
    public async Task Submit_that_read_before_an_update_is_rejected_and_creates_no_request()
    {
        await using var ctx = PreSurveyTestContext.CreateFileBacked();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var draft = await ctx.SeedPreSurveyAsync(site.Id);

        var submittingUow = new InterleavingPreSurveyUnitOfWork(ctx.CreateUnitOfWorkOnNewConnection());
        var updateHandler = new UpdatePreSurveyHandler(ctx.CreateUnitOfWorkOnNewConnection());
        submittingUow.AfterPreSurveyRead = async () =>
        {
            // Clears a required field after Submit checked completeness.
            var cleared = await updateHandler.HandleAsync(
                new UpdatePreSurveyCommand(user.Id, draft.Id, null, null, 15m, 180m, false), CancellationToken.None);
            Assert.Equal(UpdatePreSurveyOutcome.Updated, cleared.Outcome);
        };

        var result = await new SubmitPreSurveyHandler(submittingUow).HandleAsync(new SubmitPreSurveyCommand(user.Id, draft.Id), CancellationToken.None);

        Assert.Equal(SubmitPreSurveyOutcome.ConcurrentlyModified, result.Outcome);
        var stored = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();
        Assert.Equal(PreSurveyStatus.Draft, stored.Status);
        Assert.Empty(await ctx.Db.SurveyRequests.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Changing_tilt_or_azimuth_bumps_the_geometry_version()
    {
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var draft = await ctx.SeedPreSurveyAsync(site.Id);

        await ctx.UpdatePreSurveyHandler().HandleAsync(UpdateOf(user.Id, draft.Id, tilt: 15m, azimuth: 180m, usable: 90m), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        var unchanged = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();

        await ctx.UpdatePreSurveyHandler().HandleAsync(UpdateOf(user.Id, draft.Id, tilt: 15m, azimuth: 200m, usable: 90m), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        var changed = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();

        Assert.Equal(1, unchanged.Revision);
        Assert.Equal(0, unchanged.GeometryVersion);
        Assert.Equal(2, changed.Revision);
        Assert.Equal(1, changed.GeometryVersion);
    }

    [Fact]
    public async Task Without_a_saved_surface_changing_the_usable_area_makes_simulations_stale_and_with_one_it_does_not()
    {
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var draft = await ctx.SeedPreSurveyAsync(site.Id);   // usable 90 m², no surface

        await ctx.UpdatePreSurveyHandler().HandleAsync(UpdateOf(user.Id, draft.Id, tilt: 15m, azimuth: 180m, usable: 80m), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await ctx.Db.PreSurveys.AsNoTracking().SingleAsync()).GeometryVersion);   // the simulated surface depends on it

        var withSurface = await ctx.Db.PreSurveys.SingleAsync();
        (withSurface.SurfaceLengthM, withSurface.SurfaceWidthM) = (10m, 8m);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        await ctx.UpdatePreSurveyHandler().HandleAsync(UpdateOf(user.Id, draft.Id, tilt: 15m, azimuth: 180m, usable: 70m), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await ctx.Db.PreSurveys.AsNoTracking().SingleAsync()).GeometryVersion);   // the saved surface is what is simulated
    }

    [Fact]
    public async Task Submit_increments_the_revision()
    {
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var draft = await ctx.SeedPreSurveyAsync(site.Id);

        await ctx.SubmitPreSurveyHandler().HandleAsync(new SubmitPreSurveyCommand(user.Id, draft.Id), CancellationToken.None);

        ctx.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await ctx.Db.PreSurveys.AsNoTracking().SingleAsync()).Revision);
    }
}
