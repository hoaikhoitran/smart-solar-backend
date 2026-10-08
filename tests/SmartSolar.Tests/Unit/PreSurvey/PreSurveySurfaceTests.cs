using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.GetPreSurveySurface;
using SmartSolar.Modules.PreSurvey.Surface;
using SmartSolar.Modules.PreSurvey.UpdatePreSurveySurface;
using SmartSolar.Modules.SolarSimulation.Access;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.CreateSimulation;
using SmartSolar.Modules.SolarSimulation.Installation;
using SmartSolar.Modules.SolarSimulation.Options;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.PreSurvey;

public sealed class PreSurveySurfaceTests
{
    private static readonly SurfaceObstacleInput Tank = new("Water Tank", 6m, 4m, 1.5m, 2m, 1.8m);

    private static UpdatePreSurveySurfaceCommand Command(
        Guid userId, Guid preSurveyId, int expectedRevision = 0,
        decimal? length = 12m, decimal? width = 8m, decimal? tilt = 15m, decimal? azimuth = 180m,
        IReadOnlyList<SurfaceObstacleInput>? obstacles = null)
        => new(userId, preSurveyId, expectedRevision, length, width, tilt, azimuth, obstacles ?? [Tank]);

    private static List<string> Errors(UpdatePreSurveySurfaceCommand command)
        => new UpdatePreSurveySurfaceCommandValidator(new SolarSimulationOptions()).Validate(command).Errors.Select(e => e.PropertyName).ToList();

    private static async Task<(PreSurveyTestContext Ctx, Guid UserId, Modules.PreSurvey.Entities.PreSurvey Draft)> SeedAsync(PreSurveyStatus status = PreSurveyStatus.Draft)
    {
        var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var preSurvey = await ctx.SeedPreSurveyAsync(site.Id, status);
        return (ctx, user.Id, preSurvey);
    }

    [Fact]
    public async Task Saving_a_surface_stores_geometry_and_bumps_both_versions()
    {
        var (ctx, userId, draft) = await SeedAsync();
        await using var _ = ctx;

        var result = await ctx.UpdatePreSurveySurfaceHandler().HandleAsync(Command(userId, draft.Id), CancellationToken.None);

        Assert.Equal(UpdatePreSurveySurfaceOutcome.Updated, result.Outcome);
        Assert.Equal(1, result.Revision);
        Assert.Equal(1, result.GeometryVersion);
        ctx.Db.ChangeTracker.Clear();
        var stored = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();
        Assert.Equal(12m, stored.SurfaceLengthM);
        Assert.Equal(8m, stored.SurfaceWidthM);
        Assert.Equal(15m, stored.TiltDegree);
        Assert.Equal(180m, stored.AzimuthDegree);
        var obstacle = Assert.Single(SurfaceObstacleSerializer.Deserialize(stored.Obstacles)!);
        Assert.Equal(new SurfaceObstacle("Water Tank", 6m, 4m, 1.5m, 2m, 1.8m), obstacle);
    }

    [Fact]
    public async Task Saving_does_not_overwrite_customer_declared_fields()
    {
        var (ctx, userId, draft) = await SeedAsync();
        await using var _ = ctx;

        await ctx.UpdatePreSurveySurfaceHandler().HandleAsync(Command(userId, draft.Id), CancellationToken.None);

        ctx.Db.ChangeTracker.Clear();
        var stored = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();
        Assert.Equal(120m, stored.TotalAreaM2);
        Assert.Equal(90m, stored.UsableAreaM2);
        Assert.False(stored.HasObstruction);
    }

    [Fact]
    public async Task Saving_identical_geometry_keeps_the_geometry_version()
    {
        var (ctx, userId, draft) = await SeedAsync();
        await using var _ = ctx;

        await ctx.UpdatePreSurveySurfaceHandler().HandleAsync(Command(userId, draft.Id), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        var second = await ctx.UpdatePreSurveySurfaceHandler().HandleAsync(Command(userId, draft.Id, expectedRevision: 1), CancellationToken.None);

        Assert.Equal(UpdatePreSurveySurfaceOutcome.Updated, second.Outcome);
        Assert.Equal(2, second.Revision);
        Assert.Equal(1, second.GeometryVersion);
    }

    // ---------- Geometry change detection (structural, not JSON text) ----------

    /// <summary>
    /// Seeds a draft whose stored obstacle JSON is written the way PostgreSQL jsonb returns it:
    /// keys reordered, extra whitespace and different decimal scale. Revision 1, geometry version 1.
    /// </summary>
    private static async Task<(PreSurveyTestContext Ctx, Guid UserId, Guid PreSurveyId)> SeedSavedSurfaceAsync(string storedObstaclesJson)
    {
        var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var draft = PreSurveyTestContext.NewPreSurvey(site.Id);
        draft.SurfaceLengthM = 12m;
        draft.SurfaceWidthM = 8m;
        draft.TiltDegree = 15m;
        draft.AzimuthDegree = 180m;
        draft.Obstacles = storedObstaclesJson;
        draft.GeometryVersion = 1;
        draft.Revision = 1;
        ctx.Db.Add(draft);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        return (ctx, user.Id, draft.Id);
    }

    private const string JsonbStyleTank =
        "[{\"xM\": 6.0, \"yM\": 4.00, \"name\": \"Water Tank\", \"widthM\": 1.50, \"heightM\": 1.8, \"lengthM\": 2}]";

    private static async Task<int> GeometryVersionAfterSaveAsync(PreSurveyTestContext ctx, Guid userId, Guid preSurveyId, IReadOnlyList<SurfaceObstacleInput> obstacles)
    {
        var result = await ctx.UpdatePreSurveySurfaceHandler().HandleAsync(Command(userId, preSurveyId, expectedRevision: 1, obstacles: obstacles), CancellationToken.None);
        Assert.Equal(UpdatePreSurveySurfaceOutcome.Updated, result.Outcome);
        Assert.Equal(2, result.Revision);
        return result.GeometryVersion!.Value;
    }

    [Fact]
    public async Task Jsonb_normalized_storage_of_the_same_obstacles_is_not_a_geometry_change()
    {
        var (ctx, userId, id) = await SeedSavedSurfaceAsync(JsonbStyleTank);
        await using var _ = ctx;

        Assert.Equal(1, await GeometryVersionAfterSaveAsync(ctx, userId, id, [Tank]));
    }

    [Fact]
    public async Task Changed_obstacle_coordinates_are_a_geometry_change()
    {
        var (ctx, userId, id) = await SeedSavedSurfaceAsync(JsonbStyleTank);
        await using var _ = ctx;

        Assert.Equal(2, await GeometryVersionAfterSaveAsync(ctx, userId, id, [Tank with { XM = 6.01m }]));
    }

    [Fact]
    public async Task Added_obstacle_is_a_geometry_change()
    {
        var (ctx, userId, id) = await SeedSavedSurfaceAsync(JsonbStyleTank);
        await using var _ = ctx;

        Assert.Equal(2, await GeometryVersionAfterSaveAsync(ctx, userId, id, [Tank, new SurfaceObstacleInput("Rock", 1m, 1m, 1m, 1m, null)]));
    }

    [Fact]
    public async Task Removed_obstacle_is_a_geometry_change()
    {
        var (ctx, userId, id) = await SeedSavedSurfaceAsync(JsonbStyleTank);
        await using var _ = ctx;

        Assert.Equal(2, await GeometryVersionAfterSaveAsync(ctx, userId, id, []));
    }

    [Fact]
    public async Task Renamed_obstacle_is_a_change_because_names_are_part_of_the_snapshot()
    {
        var (ctx, userId, id) = await SeedSavedSurfaceAsync(JsonbStyleTank);
        await using var _ = ctx;

        Assert.Equal(2, await GeometryVersionAfterSaveAsync(ctx, userId, id, [Tank with { Name = "Tank" }]));
    }

    [Fact]
    public async Task Reordered_obstacles_are_a_change_because_order_is_preserved_in_snapshots()
    {
        var stored = "[{\"name\":\"A\",\"xM\":1,\"yM\":1,\"widthM\":1,\"lengthM\":1,\"heightM\":null},{\"name\":\"B\",\"xM\":3,\"yM\":3,\"widthM\":1,\"lengthM\":1,\"heightM\":null}]";
        var (ctx, userId, id) = await SeedSavedSurfaceAsync(stored);
        await using var _ = ctx;

        Assert.Equal(2, await GeometryVersionAfterSaveAsync(ctx, userId, id,
            [new SurfaceObstacleInput("B", 3m, 3m, 1m, 1m, null), new SurfaceObstacleInput("A", 1m, 1m, 1m, 1m, null)]));
    }

    [Fact]
    public async Task Empty_obstacle_list_saved_again_is_not_a_change()
    {
        var (ctx, userId, id) = await SeedSavedSurfaceAsync("[ ]");
        await using var _ = ctx;

        Assert.Equal(1, await GeometryVersionAfterSaveAsync(ctx, userId, id, []));
    }

    [Fact]
    public async Task First_surface_with_no_obstacles_is_a_change_from_no_surface()
    {
        var (ctx, userId, draft) = await SeedAsync();
        await using var _ = ctx;

        var result = await ctx.UpdatePreSurveySurfaceHandler().HandleAsync(Command(userId, draft.Id, obstacles: []), CancellationToken.None);

        Assert.Equal(1, result.GeometryVersion);
    }

    [Fact]
    public async Task Unchanged_save_keeps_the_simulation_current_and_a_real_change_makes_it_stale()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync([new SurfaceObstacle("Water Tank", 6m, 4m, 1.5m, 2m, 1.8m)]);
        // Store the obstacles the way jsonb would return them.
        var stored = await ctx.Db.PreSurveys.SingleAsync();
        stored.Obstacles = JsonbStyleTank;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        var product = await ctx.SeedPanelProductAsync();
        var simulation = (await ctx.CreateSimulationHandler().HandleAsync(
            new CreateSimulationCommand(user.Id, draft.Id, 1, product.Id, MountingTypes.Flush, null, null, new InstallationRequest(null, null, null, null, null), null),
            CancellationToken.None)).Simulation!;
        var viewer = new SimulationViewer(user.Id, true, false);

        var unchanged = await ctx.UpdatePreSurveySurfaceHandler().HandleAsync(Command(user.Id, draft.Id, expectedRevision: 1), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        var afterUnchanged = await ctx.GetSimulationHandler().HandleAsync(viewer, draft.Id, simulation.SimulationId, CancellationToken.None);

        var changed = await ctx.UpdatePreSurveySurfaceHandler().HandleAsync(Command(user.Id, draft.Id, expectedRevision: 2, width: 9m), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();
        var afterChanged = await ctx.GetSimulationHandler().HandleAsync(viewer, draft.Id, simulation.SimulationId, CancellationToken.None);

        Assert.Equal(1, unchanged.GeometryVersion);
        Assert.False(afterUnchanged.Simulation!.IsStale);
        Assert.Equal(2, changed.GeometryVersion);
        Assert.True(afterChanged.Simulation!.IsStale);
    }

    [Fact]
    public void Null_obstacle_item_is_rejected()
        => Assert.Contains("Obstacles[0]", Errors(Command(Guid.NewGuid(), Guid.NewGuid(), obstacles: [null!])));

    [Fact]
    public void Null_item_among_valid_obstacles_is_rejected_at_its_index()
        => Assert.Contains("Obstacles[1]", Errors(Command(Guid.NewGuid(), Guid.NewGuid(), obstacles: [Tank, null!])));

    [Fact]
    public async Task Stale_expected_revision_is_rejected_and_stores_nothing()
    {
        var (ctx, userId, draft) = await SeedAsync();
        await using var _ = ctx;

        var result = await ctx.UpdatePreSurveySurfaceHandler().HandleAsync(Command(userId, draft.Id, expectedRevision: 7), CancellationToken.None);

        Assert.Equal(UpdatePreSurveySurfaceOutcome.ConcurrentlyModified, result.Outcome);
        ctx.Db.ChangeTracker.Clear();
        var stored = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();
        Assert.Null(stored.SurfaceLengthM);
        Assert.Equal(0, stored.Revision);
    }

    [Fact]
    public async Task Submitted_pre_survey_surface_cannot_be_changed()
    {
        var (ctx, userId, submitted) = await SeedAsync(PreSurveyStatus.Submitted);
        await using var _ = ctx;

        var result = await ctx.UpdatePreSurveySurfaceHandler().HandleAsync(Command(userId, submitted.Id), CancellationToken.None);

        Assert.Equal(UpdatePreSurveySurfaceOutcome.NotEditable, result.Outcome);
    }

    [Fact]
    public async Task Another_customer_cannot_change_or_read_the_surface()
    {
        var (ctx, _, draft) = await SeedAsync();
        await using var __ = ctx;
        var stranger = await ctx.SeedUserAsync();
        await ctx.SeedCustomerAsync(stranger.Id);

        var update = await ctx.UpdatePreSurveySurfaceHandler().HandleAsync(Command(stranger.Id, draft.Id), CancellationToken.None);
        var read = await ctx.GetPreSurveySurfaceHandler().HandleAsync(stranger.Id, draft.Id, CancellationToken.None);

        Assert.Equal(UpdatePreSurveySurfaceOutcome.NotOwned, update.Outcome);
        Assert.Equal(GetPreSurveySurfaceOutcome.NotOwned, read.Outcome);
    }

    [Fact]
    public async Task Reading_returns_the_saved_surface_for_reload()
    {
        var (ctx, userId, draft) = await SeedAsync();
        await using var _ = ctx;
        await ctx.UpdatePreSurveySurfaceHandler().HandleAsync(Command(userId, draft.Id), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();

        var result = await ctx.GetPreSurveySurfaceHandler().HandleAsync(userId, draft.Id, CancellationToken.None);

        Assert.Equal(GetPreSurveySurfaceOutcome.Found, result.Outcome);
        var view = result.Surface!;
        Assert.True(view.SurfaceDefined);
        Assert.Equal(1, view.Revision);
        Assert.Equal(12m, view.SurfaceLengthM);
        Assert.Equal("DRAFT", view.Status);
        Assert.Single(view.Obstacles);
        Assert.Equal(120m, view.Declared.TotalAreaM2);
    }

    [Fact]
    public async Task Legacy_pre_survey_reads_back_without_a_surface()
    {
        var (ctx, userId, draft) = await SeedAsync();
        await using var _ = ctx;

        var result = await ctx.GetPreSurveySurfaceHandler().HandleAsync(userId, draft.Id, CancellationToken.None);

        Assert.False(result.Surface!.SurfaceDefined);
        Assert.Null(result.Surface.SurfaceLengthM);
        Assert.Empty(result.Surface.Obstacles);
        Assert.Equal(15m, result.Surface.SurfaceTiltDegree);
    }

    [Fact]
    public void Valid_command_has_no_errors() => Assert.Empty(Errors(Command(Guid.NewGuid(), Guid.NewGuid())));

    [Fact]
    public void Empty_obstacle_list_is_valid() => Assert.Empty(Errors(Command(Guid.NewGuid(), Guid.NewGuid(), obstacles: [])));

    [Fact]
    public void Obstacle_touching_the_far_boundary_is_valid()
        => Assert.Empty(Errors(Command(Guid.NewGuid(), Guid.NewGuid(), obstacles: [new SurfaceObstacleInput("Edge", 6.5m, 10m, 1.5m, 2m, null)])));

    [Theory]
    [InlineData(7.0, 4.0, 1.5, 2.0)]   // x + width = 8.5 > 8
    [InlineData(1.0, 11.0, 1.0, 2.0)]  // y + length = 13 > 12
    [InlineData(-0.1, 1.0, 1.0, 1.0)]
    [InlineData(1.0, 1.0, 0.0, 1.0)]
    [InlineData(1.0, 1.0, 1.0, -1.0)]
    public void Obstacles_outside_the_surface_or_degenerate_are_rejected(double x, double y, double w, double l)
    {
        var errors = Errors(Command(Guid.NewGuid(), Guid.NewGuid(),
            obstacles: [new SurfaceObstacleInput("Bad", (decimal)x, (decimal)y, (decimal)w, (decimal)l, null)]));

        Assert.Contains(errors, e => e.StartsWith("Obstacles[0]"));
    }

    [Fact]
    public void Overlapping_obstacles_are_allowed()
        => Assert.Empty(Errors(Command(Guid.NewGuid(), Guid.NewGuid(), obstacles:
            [new SurfaceObstacleInput("A", 1m, 1m, 2m, 2m, null), new SurfaceObstacleInput("B", 2m, 2m, 2m, 2m, 0m)])));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Obstacle_name_is_required(string name)
        => Assert.Contains("Obstacles[0].Name", Errors(Command(Guid.NewGuid(), Guid.NewGuid(), obstacles: [Tank with { Name = name }])));

    [Fact]
    public void Negative_obstacle_height_is_rejected()
        => Assert.Contains("Obstacles[0].HeightM", Errors(Command(Guid.NewGuid(), Guid.NewGuid(), obstacles: [Tank with { HeightM = -0.1m }])));

    [Fact]
    public void Too_many_obstacles_are_rejected()
    {
        var many = Enumerable.Range(0, 51).Select(i => new SurfaceObstacleInput($"O{i}", 0m, 0m, 0.1m, 0.1m, null)).ToList();

        Assert.Contains("Obstacles", Errors(Command(Guid.NewGuid(), Guid.NewGuid(), obstacles: many)));
    }

    [Theory]
    [InlineData(0.0, 8.0, "SurfaceLengthM")]
    [InlineData(12.0, -1.0, "SurfaceWidthM")]
    [InlineData(201.0, 8.0, "SurfaceLengthM")]
    public void Surface_dimensions_must_be_positive_and_within_limits(double length, double width, string property)
        => Assert.Contains(property, Errors(Command(Guid.NewGuid(), Guid.NewGuid(), length: (decimal)length, width: (decimal)width, obstacles: [])));

    [Theory]
    [InlineData(-1.0, 180.0, "SurfaceTiltDegree")]
    [InlineData(91.0, 180.0, "SurfaceTiltDegree")]
    [InlineData(10.0, 361.0, "SurfaceAzimuthDegree")]
    public void Surface_angles_must_be_in_range(double tilt, double azimuth, string property)
        => Assert.Contains(property, Errors(Command(Guid.NewGuid(), Guid.NewGuid(), tilt: (decimal)tilt, azimuth: (decimal)azimuth)));

    [Fact]
    public void Missing_values_are_reported()
    {
        var errors = Errors(new UpdatePreSurveySurfaceCommand(Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, null, null));

        Assert.Contains("ExpectedRevision", errors);
        Assert.Contains("SurfaceLengthM", errors);
        Assert.Contains("SurfaceWidthM", errors);
        Assert.Contains("SurfaceTiltDegree", errors);
        Assert.Contains("SurfaceAzimuthDegree", errors);
        Assert.Contains("Obstacles", errors);
    }
}
