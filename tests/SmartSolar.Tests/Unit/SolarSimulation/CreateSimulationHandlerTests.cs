using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Catalog.Enums;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.Surface;
using SmartSolar.Modules.PreSurvey.SubmitPreSurvey;
using SmartSolar.Modules.SolarSimulation.Access;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.CreateSimulation;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Modules.SolarSimulation.GetSimulation;
using SmartSolar.Modules.SolarSimulation.Installation;
using SmartSolar.Modules.SolarSimulation.Models;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.SolarSimulation;

/// <summary>Provider results in these tests come from SYNTHETIC fakes, never live services.</summary>
public sealed class CreateSimulationHandlerTests
{
    private static readonly InstallationRequest Defaults = new(null, null, null, null, null);

    private static CreateSimulationCommand Flush(Guid userId, Guid preSurveyId, Guid productId, int geometryVersion = 1, InstallationRequest? installation = null)
        => new(userId, preSurveyId, geometryVersion, productId, MountingTypes.Flush, null, null, installation ?? Defaults, null);

    private static CreateSimulationCommand Rack(Guid userId, Guid preSurveyId, Guid productId, decimal tilt = 12m, decimal azimuth = 180m, int geometryVersion = 1)
        => new(userId, preSurveyId, geometryVersion, productId, MountingTypes.Rack, tilt, azimuth, Defaults, null);

    [Fact]
    public async Task Flush_simulation_is_completed_with_building_primary_and_free_comparison()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync([new SurfaceObstacle("Water Tank", 6m, 4m, 1.5m, 2m, 1.8m)]);
        var product = await ctx.SeedPanelProductAsync();

        var result = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.Created, result.Outcome);
        var detail = result.Simulation!;
        Assert.Equal(SimulationStatusCodes.Completed, detail.Status);
        Assert.True(detail.IsSelected);
        Assert.False(detail.IsStale);
        Assert.True(detail.EngineeringReviewRequired);
        Assert.True(detail.Layout.PanelCount > 0);
        Assert.Equal(Math.Round(detail.Layout.PanelCount * 550m / 1000m, 3), detail.Layout.InstalledCapacityKwp);
        Assert.Equal(detail.Layout.PanelCount, detail.Layout.Details.Placements.Count);

        Assert.Equal(ComponentStatusCodes.Succeeded, detail.Energy.Status);
        Assert.Equal(PvgisMountingPlaces.Building, detail.Energy.PrimaryMountingPlace);
        Assert.Equal(2, detail.Energy.Scenarios.Count);
        Assert.Equal(EnergyScenarioRoles.PrimaryConservativeReference, detail.Energy.Scenarios[0].Role);
        Assert.Equal(EnergyScenarioRoles.Comparison, detail.Energy.Scenarios[1].Role);
        Assert.Equal(Math.Round(1400m * detail.Layout.InstalledCapacityKwp, 2), detail.Energy.AnnualEnergyKwh);
        Assert.Equal(12, detail.Energy.Monthly.Count);
        Assert.Contains(detail.Warnings, w => w.Code == SimulationWarningCodes.FlushMountThermalAssumption);
        Assert.Contains(detail.Warnings, w => w.Code == SimulationWarningCodes.BuildingFireCodeNotChecked);

        Assert.Equal(ComponentStatusCodes.Succeeded, detail.Climate.Status);

        // One PVGIS call per scenario with the actual panel orientation and installed capacity.
        Assert.Equal(2, ctx.PvEnergy.Requests.Count);
        Assert.All(ctx.PvEnergy.Requests, r =>
        {
            Assert.Equal(15m, r.TiltDegree);
            Assert.Equal(180m, r.CompassAzimuthDegree);
            Assert.Equal(detail.Layout.InstalledCapacityKwp, r.PeakPowerKwp);
            Assert.Equal(14m, r.SystemLossPercent);
        });

        var stored = await ctx.Db.SolarSimulations.AsNoTracking().SingleAsync();
        var preSurvey = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync();
        Assert.Equal(stored.Id, preSurvey.SelectedSimulationId);
        Assert.Equal(1, stored.PreSurveyGeometryVersion);
        Assert.Equal("PNL-550", stored.ProductSku);
        Assert.True(stored.IsReusable);
    }

    [Fact]
    public async Task Rack_simulation_uses_a_single_free_scenario_and_absolute_panel_angles()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync(tilt: 0m, length: 20m, width: 15m);
        var product = await ctx.SeedPanelProductAsync();

        var result = await ctx.CreateSimulationHandler().HandleAsync(Rack(user.Id, draft.Id, product.Id, tilt: 12m, azimuth: 135m), CancellationToken.None);

        var detail = result.Simulation!;
        var request = Assert.Single(ctx.PvEnergy.Requests);
        Assert.Equal(PvgisMountingPlaces.Free, request.MountingPlace);
        Assert.Equal(12m, request.TiltDegree);
        Assert.Equal(135m, request.CompassAzimuthDegree);
        Assert.Equal(EnergyScenarioRoles.Primary, Assert.Single(detail.Energy.Scenarios).Role);
        Assert.Equal(InstallationSources.ComputedShadeEstimate, detail.Installation.RowGapMm.Source);
        Assert.Equal(-45m, detail.Mounting.PvgisAspectDegree);
    }

    [Fact]
    public async Task Zero_panels_is_a_valid_result_without_pvgis_calls()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync(length: 1.5m, width: 1.5m);
        var product = await ctx.SeedPanelProductAsync();

        var result = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.Created, result.Outcome);
        var detail = result.Simulation!;
        Assert.Equal(0, detail.Layout.PanelCount);
        Assert.Equal(0m, detail.Layout.InstalledCapacityKwp);
        Assert.Empty(detail.Layout.Details.Placements);
        Assert.NotNull(detail.Layout.Details.NoPanelsReason);
        Assert.Equal(ComponentStatusCodes.NotApplicable, detail.Energy.Status);
        Assert.Equal("NO_PANELS", detail.Energy.Reason);
        Assert.Null(detail.Energy.AnnualEnergyKwh);
        Assert.Empty(ctx.PvEnergy.Requests);
        Assert.Equal(SimulationStatusCodes.Completed, detail.Status);
    }

    [Fact]
    public async Task Missing_coordinates_saves_the_layout_as_partially_completed()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync(withCoordinates: false);
        var product = await ctx.SeedPanelProductAsync();

        var result = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);

        var detail = result.Simulation!;
        Assert.Equal(SimulationStatusCodes.PartiallyCompleted, detail.Status);
        Assert.True(detail.Layout.PanelCount > 0);
        Assert.Equal(ComponentStatusCodes.Unavailable, detail.Energy.Status);
        Assert.Null(detail.Energy.AnnualEnergyKwh);
        Assert.Equal(ComponentStatusCodes.Unavailable, detail.Climate.Status);
        Assert.Contains(detail.Warnings, w => w.Code == SimulationWarningCodes.LocationMissing);
        Assert.Empty(ctx.PvEnergy.Requests);
        Assert.Equal(0, ctx.Climate.Calls);
        Assert.Single(await ctx.Db.SolarSimulations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Provider_failure_is_explicit_and_a_retry_recalculates()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        ctx.PvEnergy.Handler = (_, _) => Task.FromResult(PvEnergyResult.Fail(ProviderFailureCodes.Unavailable, "synthetic outage"));

        var failed = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);

        Assert.Equal(SimulationStatusCodes.PartiallyCompleted, failed.Simulation!.Status);
        Assert.Equal(ComponentStatusCodes.Failed, failed.Simulation.Energy.Status);
        Assert.Equal(ProviderFailureCodes.Unavailable, failed.Simulation.Energy.Reason);
        Assert.Null(failed.Simulation.Energy.AnnualEnergyKwh);
        Assert.Equal(ComponentStatusCodes.Succeeded, failed.Simulation.Climate.Status);

        ctx.PvEnergy.Handler = null;
        var retried = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.Created, retried.Outcome);
        Assert.Equal(SimulationStatusCodes.Completed, retried.Simulation!.Status);
        Assert.Equal(2, await ctx.Db.SolarSimulations.CountAsync());
        Assert.Equal(retried.Simulation.SimulationId, (await ctx.Db.PreSurveys.AsNoTracking().SingleAsync()).SelectedSimulationId);
    }

    [Fact]
    public async Task Partially_completed_snapshots_always_store_energy_and_climate_documents()
    {
        await using var ctx = new PreSurveyTestContext();
        var (failingUser, failingDraft) = await ctx.SeedDraftWithSurfaceAsync();
        var (noLocationUser, noLocationDraft) = await ctx.SeedDraftWithSurfaceAsync(withCoordinates: false);
        var product = await ctx.SeedPanelProductAsync();
        ctx.PvEnergy.Handler = (_, _) => Task.FromResult(PvEnergyResult.Fail(ProviderFailureCodes.Unavailable, "synthetic outage"));

        var failed = await ctx.CreateSimulationHandler().HandleAsync(Flush(failingUser.Id, failingDraft.Id, product.Id), CancellationToken.None);
        var noLocation = await ctx.CreateSimulationHandler().HandleAsync(Flush(noLocationUser.Id, noLocationDraft.Id, product.Id), CancellationToken.None);

        Assert.Equal(SimulationStatusCodes.PartiallyCompleted, failed.Simulation!.Status);
        Assert.Equal(SimulationStatusCodes.PartiallyCompleted, noLocation.Simulation!.Status);
        var rows = await ctx.Db.SolarSimulations.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Energy));
            Assert.False(string.IsNullOrWhiteSpace(r.Climate));
        });

        var entity = ctx.Db.Model.FindEntityType(typeof(Modules.SolarSimulation.Entities.SolarSimulation))!;
        Assert.False(entity.FindProperty(nameof(Modules.SolarSimulation.Entities.SolarSimulation.Energy))!.IsNullable);
        Assert.False(entity.FindProperty(nameof(Modules.SolarSimulation.Entities.SolarSimulation.Climate))!.IsNullable);
    }

    [Theory]
    [InlineData(null, "PVGIS documented default")]
    [InlineData(14.0, "site-specified")]
    [InlineData(10.0, "site-specified")]
    public async Task System_loss_source_label_reflects_whether_the_customer_supplied_it(double? loss, string expectedLabel)
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();

        var result = await ctx.CreateSimulationHandler().HandleAsync(
            Flush(user.Id, draft.Id, product.Id) with { SystemLossPercent = (decimal?)loss }, CancellationToken.None);

        Assert.Contains(result.Simulation!.Energy.Assumptions, a => a.StartsWith("System losses") && a.Contains(expectedLabel));
    }

    [Fact]
    public async Task Explicit_default_system_loss_is_a_different_snapshot_from_an_omitted_one()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();

        var omitted = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);
        var explicitValue = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id) with { SystemLossPercent = 14m }, CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.Created, explicitValue.Outcome);
        Assert.NotEqual(omitted.Simulation!.SimulationId, explicitValue.Simulation!.SimulationId);
    }

    [Fact]
    public async Task Shading_limitation_text_follows_the_configured_window()
    {
        await using var ctx = new PreSurveyTestContext();
        ctx.SimulationOptions.Shading.StartSolarHour = 8;
        ctx.SimulationOptions.Shading.EndSolarHour = 16;
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();

        var result = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);

        var shading = Assert.Single(result.Simulation!.Limitations, l => l.Contains("shading", StringComparison.OrdinalIgnoreCase) && l.Contains("advisory"));
        Assert.Contains("08:00", shading);
        Assert.Contains("16:00", shading);
        Assert.DoesNotContain("09:00", shading);
    }

    [Fact]
    public async Task Provider_time_budget_turns_a_hang_into_a_timeout_failure()
    {
        await using var ctx = new PreSurveyTestContext();
        ctx.SimulationOptions.ProviderBudgetSeconds = 1;
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        ctx.PvEnergy.Handler = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return PvEnergyResult.Fail("unreachable", "unreachable");
        };

        var started = DateTimeOffset.UtcNow;
        var result = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Equal(ComponentStatusCodes.Failed, result.Simulation!.Energy.Status);
        Assert.Equal(ProviderFailureCodes.Timeout, result.Simulation.Energy.Reason);
    }

    [Fact]
    public async Task Identical_request_reuses_the_snapshot_without_provider_calls()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();

        var first = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);
        var second = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.Created, first.Outcome);
        Assert.Equal(CreateSimulationOutcome.Reused, second.Outcome);
        Assert.Equal(first.Simulation!.SimulationId, second.Simulation!.SimulationId);
        Assert.Equal(2, ctx.PvEnergy.Requests.Count);
        Assert.Equal(1, ctx.Climate.Calls);
        Assert.Single(await ctx.Db.SolarSimulations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Explicit_value_equal_to_the_default_is_labeled_site_specified()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();

        await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);
        var explicitDefault = await ctx.CreateSimulationHandler().HandleAsync(
            Flush(user.Id, draft.Id, product.Id, installation: Defaults with { PanelGapMm = 20m }), CancellationToken.None);

        // Same number, different source label: the snapshot must say where the value came from, so a new one is made.
        Assert.Equal(CreateSimulationOutcome.Created, explicitDefault.Outcome);
        Assert.Equal(InstallationSources.SiteSpecified, explicitDefault.Simulation!.Installation.PanelGapMm.Source);
    }

    [Fact]
    public async Task Changed_input_creates_a_new_snapshot_and_selects_it()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();

        var first = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);
        var second = await ctx.CreateSimulationHandler().HandleAsync(
            Flush(user.Id, draft.Id, product.Id, installation: Defaults with { EdgeSetbackMm = 500m }), CancellationToken.None);
        var back = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);

        Assert.NotEqual(first.Simulation!.SimulationId, second.Simulation!.SimulationId);
        Assert.Equal(CreateSimulationOutcome.Reused, back.Outcome);
        Assert.Equal(first.Simulation.SimulationId, (await ctx.Db.PreSurveys.AsNoTracking().SingleAsync()).SelectedSimulationId);
    }

    [Fact]
    public async Task Simultaneous_identical_requests_store_one_snapshot_and_call_providers_once()
    {
        await using var ctx = PreSurveyTestContext.CreateFileBacked();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.PvEnergy.Handler = async (request, token) =>
        {
            await gate.Task.WaitAsync(token);
            return FakePvEnergyEstimator.Synthetic(request);
        };

        var handlerA = ctx.CreateSimulationHandlerOnNewConnection();
        var handlerB = ctx.CreateSimulationHandlerOnNewConnection();
        var a = Task.Run(() => handlerA.HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None));
        var b = Task.Run(() => handlerB.HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None));
        await Task.Delay(300);
        gate.SetResult();
        var results = await Task.WhenAll(a, b);

        Assert.Single(results, r => r.Outcome == CreateSimulationOutcome.Created);
        Assert.Single(results, r => r.Outcome == CreateSimulationOutcome.Reused);
        Assert.Equal(results[0].Simulation!.SimulationId, results[1].Simulation!.SimulationId);
        Assert.Equal(2, ctx.PvEnergy.Requests.Count);
        Assert.Single(await ctx.Db.SolarSimulations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Geometry_changed_during_calculation_stores_nothing()
    {
        await using var ctx = PreSurveyTestContext.CreateFileBacked();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        var editor = ctx.CreateUnitOfWorkOnNewConnection();
        var edited = false;
        ctx.PvEnergy.Handler = async (request, _) =>
        {
            if (!edited)
            {
                edited = true;
                var preSurvey = (await editor.FindPreSurveyByIdAsync(draft.Id, CancellationToken.None))!;
                preSurvey.SurfaceWidthM = 9m;
                preSurvey.GeometryVersion++;
                preSurvey.Revision++;
                Assert.True(await editor.TrySaveDraftChangesAsync(CancellationToken.None));
            }
            return FakePvEnergyEstimator.Synthetic(request);
        };

        var result = await ctx.CreateSimulationHandlerOnNewConnection().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.SourceChanged, result.Outcome);
        Assert.Empty(await ctx.Db.SolarSimulations.AsNoTracking().ToListAsync());
        Assert.Null((await ctx.Db.PreSurveys.AsNoTracking().SingleAsync()).SelectedSimulationId);
    }

    [Fact]
    public async Task Submit_during_calculation_stores_nothing()
    {
        await using var ctx = PreSurveyTestContext.CreateFileBacked();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        var submitter = new SubmitPreSurveyHandler(ctx.CreateUnitOfWorkOnNewConnection());
        var submitted = false;
        ctx.Climate.Handler = async (lat, lon, _) =>
        {
            if (!submitted)
            {
                submitted = true;
                Assert.Equal(SubmitPreSurveyOutcome.Submitted,
                    (await submitter.HandleAsync(new SubmitPreSurveyCommand(user.Id, draft.Id), CancellationToken.None)).Outcome);
            }
            return FakeClimateContextProvider.Synthetic(lat, lon);
        };

        var result = await ctx.CreateSimulationHandlerOnNewConnection().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.NotEditable, result.Outcome);
        Assert.Empty(await ctx.Db.SolarSimulations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Stale_expected_geometry_version_is_rejected()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();

        var result = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id, geometryVersion: 0), CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.SourceChanged, result.Outcome);
        Assert.Empty(ctx.PvEnergy.Requests);
    }

    [Fact]
    public async Task Geometry_edit_marks_old_snapshots_stale_but_keeps_them_readable()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        var first = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);

        var preSurvey = (await ctx.UnitOfWork.FindPreSurveyByIdAsync(draft.Id, CancellationToken.None))!;
        preSurvey.SurfaceWidthM = 9m;
        preSurvey.GeometryVersion++;
        preSurvey.Revision++;
        Assert.True(await ctx.UnitOfWork.TrySaveDraftChangesAsync(CancellationToken.None));
        ctx.Db.ChangeTracker.Clear();

        var viewer = new SimulationViewer(user.Id, IsCustomer: true, IsSales: false);
        var old = await ctx.GetSimulationHandler().HandleAsync(viewer, draft.Id, first.Simulation!.SimulationId, CancellationToken.None);
        var list = await ctx.ListSimulationsHandler().HandleAsync(viewer, draft.Id, CancellationToken.None);

        Assert.Equal(SimulationQueryOutcome.Found, old.Outcome);
        Assert.True(old.Simulation!.IsStale);
        Assert.Equal(8m, old.Simulation.Surface.WidthM);
        Assert.True(Assert.Single(list.Simulations!).IsStale);

        var renewed = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id, geometryVersion: 2), CancellationToken.None);
        Assert.Equal(CreateSimulationOutcome.Created, renewed.Outcome);
        Assert.Equal(9m, renewed.Simulation!.Surface.WidthM);
    }

    [Fact]
    public async Task Product_changes_do_not_alter_existing_snapshots()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        var created = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);

        var tracked = await ctx.Db.Products.SingleAsync();
        tracked.RatedPowerW = 600m;
        tracked.Status = ProductStatus.Inactive;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        var viewer = new SimulationViewer(user.Id, true, false);
        var read = await ctx.GetSimulationHandler().HandleAsync(viewer, draft.Id, created.Simulation!.SimulationId, CancellationToken.None);

        Assert.Equal(550m, read.Simulation!.Product.RatedPowerW);
        Assert.Equal(created.Simulation.Layout.InstalledCapacityKwp, read.Simulation.Layout.InstalledCapacityKwp);
    }

    [Fact]
    public async Task Submitted_pre_survey_rejects_new_simulations_but_keeps_old_ones_readable()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        var created = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);
        Assert.Equal(SubmitPreSurveyOutcome.Submitted,
            (await ctx.SubmitPreSurveyHandler().HandleAsync(new SubmitPreSurveyCommand(user.Id, draft.Id), CancellationToken.None)).Outcome);
        ctx.Db.ChangeTracker.Clear();

        var rejected = await ctx.CreateSimulationHandler().HandleAsync(
            Flush(user.Id, draft.Id, product.Id, installation: Defaults with { PanelGapMm = 30m }), CancellationToken.None);
        var read = await ctx.GetSimulationHandler().HandleAsync(new SimulationViewer(user.Id, true, false), draft.Id, created.Simulation!.SimulationId, CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.NotEditable, rejected.Outcome);
        Assert.Equal(SimulationQueryOutcome.Found, read.Outcome);
        Assert.True(read.Simulation!.IsSelected);
    }

    [Fact]
    public async Task Another_customer_cannot_create_a_simulation()
    {
        await using var ctx = new PreSurveyTestContext();
        var (_, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        var stranger = await ctx.SeedUserAsync();

        var result = await ctx.CreateSimulationHandler().HandleAsync(Flush(stranger.Id, draft.Id, product.Id), CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.NotOwned, result.Outcome);
    }

    [Fact]
    public async Task Legacy_pre_survey_without_surface_is_rejected()
    {
        await using var ctx = new PreSurveyTestContext();
        var user = await ctx.SeedUserAsync();
        var customer = await ctx.SeedCustomerAsync(user.Id);
        var site = await ctx.SeedPropertySiteAsync(customer.Id);
        var legacy = await ctx.SeedPreSurveyAsync(site.Id);
        var product = await ctx.SeedPanelProductAsync();

        var result = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, legacy.Id, product.Id, geometryVersion: 0), CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.SurfaceNotDefined, result.Outcome);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("inactive")]
    public async Task Unavailable_products_are_not_found(string kind)
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var productId = kind == "missing" ? Guid.NewGuid() : (await ctx.SeedPanelProductAsync(status: ProductStatus.Inactive)).Id;

        var result = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, productId), CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.ProductNotFound, result.Outcome);
    }

    [Fact]
    public async Task Non_panel_and_incomplete_products_are_rejected()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var inverter = await ctx.SeedPanelProductAsync(sku: "INV-1", productType: "INVERTER");
        var incomplete = await ctx.SeedPanelProductAsync(sku: "PNL-X", ratedPowerW: null);

        var notPanel = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, inverter.Id), CancellationToken.None);
        var noPower = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, incomplete.Id), CancellationToken.None);

        Assert.Equal(SimulationErrorCodes.ProductNotSolarPanel, notPanel.ErrorCode);
        Assert.Equal(SimulationErrorCodes.ProductSpecIncomplete, noPower.ErrorCode);
    }

    [Fact]
    public async Task Value_below_a_documented_minimum_is_rejected()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync(spec:
            """{"installation":{"schemaVersion":1,"requirements":{"panelGapMm":{"minMm":15,"documentRef":"Manual rev 3"}}}}""");

        var result = await ctx.CreateSimulationHandler().HandleAsync(
            Flush(user.Id, draft.Id, product.Id, installation: Defaults with { PanelGapMm = 10m }), CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.Rejected, result.Outcome);
        Assert.Equal(SimulationErrorCodes.InstallationBelowDocumentedMinimum, result.ErrorCode);
    }

    [Fact]
    public async Task Invalid_product_installation_block_is_ignored_with_a_warning()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync(spec: """{"installation":{"legacy":"format"}}""");

        var result = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.Created, result.Outcome);
        Assert.Equal("INVALID", result.Simulation!.Installation.ProductInstallationSpecStatus);
        Assert.Contains(result.Simulation.Warnings, w => w.Code == SimulationWarningCodes.ProductInstallationSpecInvalid);
        // Internal Catalog validation messages are not exposed to customers.
        Assert.Empty(result.Simulation.Installation.ProductInstallationSpecErrors);
    }

    [Fact]
    public async Task Flush_with_different_panel_angles_is_rejected()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();

        var result = await ctx.CreateSimulationHandler().HandleAsync(
            Flush(user.Id, draft.Id, product.Id) with { PanelTiltDegree = 30m }, CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.Rejected, result.Outcome);
        Assert.Equal(SimulationErrorCodes.ValidationFailed, result.ErrorCode);
    }

    [Fact]
    public async Task Rack_facing_into_a_steep_surface_is_rejected()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync(tilt: 60m);
        var product = await ctx.SeedPanelProductAsync();

        var result = await ctx.CreateSimulationHandler().HandleAsync(Rack(user.Id, draft.Id, product.Id, tilt: 60m, azimuth: 0m), CancellationToken.None);

        Assert.Equal(SimulationErrorCodes.PanelFacesIntoSurface, result.ErrorCode);
    }

    [Fact]
    public async Task Selected_simulation_must_belong_to_the_same_pre_survey()
    {
        await using var ctx = new PreSurveyTestContext();
        var (userA, draftA) = await ctx.SeedDraftWithSurfaceAsync();
        var (_, draftB) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        var simulationA = (await ctx.CreateSimulationHandler().HandleAsync(Flush(userA.Id, draftA.Id, product.Id), CancellationToken.None)).Simulation!;

        // Application level: the conditional select refuses a foreign snapshot.
        var outcome = await ctx.SimulationUnitOfWork.TrySelectAsync(draftB.Id, simulationA.SimulationId, 1, CancellationToken.None);
        Assert.NotEqual(Modules.SolarSimulation.Contracts.Persistence.SelectSimulationOutcome.Selected, outcome);

        // Database level: the composite foreign key rejects it even when bypassing the application.
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Db.PreSurveys
            .Where(p => p.Id == draftB.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.SelectedSimulationId, simulationA.SimulationId)));
        Assert.Null((await ctx.Db.PreSurveys.AsNoTracking().SingleAsync(p => p.Id == draftB.Id)).SelectedSimulationId);
    }

    [Fact]
    public async Task Snapshot_records_are_never_updated_by_later_simulations()
    {
        await using var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        var first = (await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None)).Simulation!;
        var before = await ctx.Db.SolarSimulations.AsNoTracking().SingleAsync();

        await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id, installation: Defaults with { EdgeSetbackMm = 600m }), CancellationToken.None);
        var after = await ctx.Db.SolarSimulations.AsNoTracking().SingleAsync(s => s.Id == first.SimulationId);

        Assert.Equal(before.Layout, after.Layout);
        Assert.Equal(before.Energy, after.Energy);
        Assert.Equal(before.PanelCount, after.PanelCount);
    }
}
