using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.SubmitPreSurvey;
using SmartSolar.Modules.SolarSimulation.Access;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.Contracts.Storage;
using SmartSolar.Modules.SolarSimulation.CreateSimulation;
using SmartSolar.Modules.SolarSimulation.Entities;
using SmartSolar.Modules.SolarSimulation.GetSimulation;
using SmartSolar.Modules.SolarSimulation.Installation;
using SmartSolar.Modules.SolarSimulation.Models;
using SmartSolar.Modules.SolarSimulation.SelectSimulation;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.SolarSimulation;

/// <summary>
/// Temporary results (fake distributed cache with a controllable clock and an outage switch),
/// explicit selection, the compact PostgreSQL summary and the optional snapshot (in-memory fake
/// store). Providers are SYNTHETIC fakes; no Redis, S3 or provider is contacted.
/// </summary>
public sealed class SimulationSelectionTests
{
    private static readonly InstallationRequest Defaults = new(null, null, null, null, null);

    private static CreateSimulationCommand Flush(Guid userId, Guid preSurveyId, Guid productId, int geometryVersion = 1, InstallationRequest? installation = null)
        => new(userId, preSurveyId, geometryVersion, productId, MountingTypes.Flush, null, null, installation ?? Defaults, null);

    private static async Task<(PreSurveyTestContext Ctx, Guid UserId, Guid PreSurveyId, Guid ProductId, SimulationDetail Simulation)> CreatedAsync()
    {
        var ctx = new PreSurveyTestContext();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        var created = await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None);
        Assert.Equal(CreateSimulationOutcome.Created, created.Outcome);
        return (ctx, user.Id, draft.Id, product.Id, created.Simulation!);
    }

    private static SimulationViewer Owner(Guid userId) => new(userId, IsCustomer: true, IsSales: false);

    private static void Expire(PreSurveyTestContext ctx) => ctx.Time.Now = ctx.Time.Now.AddMinutes(ctx.SimulationOptions.ResultTtlMinutes + 1);

    [Fact]
    public async Task Create_never_selects_and_explicit_select_stores_the_compact_summary()
    {
        var (ctx, userId, preSurveyId, _, simulation) = await CreatedAsync();
        await using var _ = ctx;

        Assert.False(simulation.IsSelected);
        Assert.Null((await ctx.Db.PreSurveys.AsNoTracking().SingleAsync()).SelectedSimulationId);

        var selected = await ctx.SelectSimulationAsync(userId, preSurveyId, simulation.SimulationId);

        Assert.Equal(SelectSimulationResultOutcome.Selected, selected.Outcome);
        var row = await ctx.Db.SelectedSimulations.AsNoTracking().SingleAsync();
        Assert.Equal(simulation.SimulationId, row.SimulationId);
        Assert.Equal(preSurveyId, row.PreSurveyId);
        Assert.Equal(userId, row.SelectedByUserId);
        Assert.Equal(ctx.Time.Now, row.SelectedAt);
        Assert.Equal(simulation.CreatedAt, row.CreatedAt);
        Assert.Equal(1, row.PreSurveyGeometryVersion);
        Assert.Equal(AlgorithmVersions.Current, row.AlgorithmVersion);
        Assert.Equal(simulation.Status, row.Status);
        Assert.Equal(simulation.Energy.Status, row.EnergyStatus);
        Assert.Equal(MountingTypes.Flush, row.MountingType);
        Assert.Equal(simulation.Product.Id, row.ProductId);
        Assert.Equal("PNL-550", row.ProductSku);
        Assert.Equal(simulation.Layout.PanelCount, row.PanelCount);
        Assert.Equal(simulation.Layout.InstalledCapacityKwp, row.InstalledCapacityKwp);
        Assert.Equal(simulation.Energy.AnnualEnergyKwh, row.AnnualEnergyKwh);
        Assert.Equal(simulation.Energy.SpecificYieldKwhPerKwpYear, row.SpecificYieldKwhPerKwpYear);
        Assert.Equal(simulation.Mounting.PanelTiltDegree, row.PanelTiltDegree);
        Assert.Equal(simulation.Mounting.PanelAzimuthDegree, row.PanelAzimuthDegree);
        Assert.Null(row.SnapshotObjectKey);
        Assert.Equal(simulation.SimulationId, (await ctx.Db.PreSurveys.AsNoTracking().SingleAsync()).SelectedSimulationId);
        Assert.Equal(SnapshotStatuses.None, selected.Selection!.SnapshotStatus);

        var read = await ctx.GetSimulationHandler().HandleAsync(Owner(userId), preSurveyId, simulation.SimulationId, CancellationToken.None);
        Assert.Equal(SimulationQueryOutcome.Found, read.Outcome);
        Assert.True(read.Simulation!.IsSelected);
        Assert.Equal(SimulationSources.Cache, read.Simulation.Source);
    }

    [Fact]
    public async Task Only_the_compact_summary_is_mapped_to_postgresql()
    {
        await using var ctx = new PreSurveyTestContext();
        var model = ctx.Db.Model;

        Assert.DoesNotContain(model.GetEntityTypes(), e => e.GetTableName() == "solar_simulation");
        var entity = model.FindEntityType(typeof(SelectedSimulation))!;
        Assert.Equal("selected_simulation", entity.GetTableName());
        var columns = entity.GetProperties().Select(p => p.GetColumnName()).OrderBy(c => c).ToList();
        Assert.Equal(new[]
        {
            "algorithm_version", "annual_energy_kwh", "created_at", "energy_status", "id", "installed_capacity_kwp", "mounting_type",
            "panel_azimuth_degree", "panel_count", "panel_tilt_degree", "pre_survey_geometry_version", "pre_survey_id", "product_id",
            "product_name", "product_sku", "selected_at", "selected_by_user_id", "simulation_id", "snapshot_bucket", "snapshot_object_key",
            "snapshot_sha256", "snapshot_size_bytes", "specific_yield_kwh_per_kwp_year", "status",
        }.OrderBy(c => c), columns);
        // No large documents (layout, energy, climate...) in PostgreSQL.
        Assert.DoesNotContain(entity.GetProperties(), p => p.GetColumnType() == "jsonb");
    }

    [Fact]
    public async Task Cache_hit_returns_the_full_result_and_a_miss_is_expired()
    {
        var (ctx, userId, preSurveyId, _, simulation) = await CreatedAsync();
        await using var _ = ctx;

        var hit = await ctx.GetSimulationHandler().HandleAsync(Owner(userId), preSurveyId, simulation.SimulationId, CancellationToken.None);
        var miss = await ctx.GetSimulationHandler().HandleAsync(Owner(userId), preSurveyId, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(SimulationQueryOutcome.Found, hit.Outcome);
        Assert.Equal(simulation.Layout.Details.Placements.Count, hit.Simulation!.Layout.Details.Placements.Count);
        Assert.Equal(simulation.Energy.AnnualEnergyKwh, hit.Simulation.Energy.AnnualEnergyKwh);
        Assert.Equal(simulation.ExpiresAt, hit.Simulation.ExpiresAt);
        Assert.Equal(SimulationQueryOutcome.Expired, miss.Outcome);
    }

    [Fact]
    public async Task Expired_result_is_gone_from_read_list_and_select_and_identical_input_recalculates()
    {
        var (ctx, userId, preSurveyId, productId, simulation) = await CreatedAsync();
        await using var _ = ctx;
        var listed = await ctx.ListSimulationsHandler().HandleAsync(Owner(userId), preSurveyId, CancellationToken.None);
        Assert.Single(listed.Simulations!.Items);

        Expire(ctx);

        var read = await ctx.GetSimulationHandler().HandleAsync(Owner(userId), preSurveyId, simulation.SimulationId, CancellationToken.None);
        var list = await ctx.ListSimulationsHandler().HandleAsync(Owner(userId), preSurveyId, CancellationToken.None);
        var select = await ctx.SelectSimulationAsync(userId, preSurveyId, simulation.SimulationId);
        Assert.Equal(SimulationQueryOutcome.Expired, read.Outcome);
        Assert.Empty(list.Simulations!.Items);
        Assert.True(list.Simulations.CacheAvailable);
        Assert.Null(list.Simulations.Selected);
        Assert.Equal(SelectSimulationResultOutcome.Expired, select.Outcome);
        Assert.Empty(await ctx.Db.SelectedSimulations.AsNoTracking().ToListAsync());

        var again = await ctx.CreateSimulationHandler().HandleAsync(Flush(userId, preSurveyId, productId), CancellationToken.None);
        Assert.Equal(CreateSimulationOutcome.Created, again.Outcome);
        Assert.NotEqual(simulation.SimulationId, again.Simulation!.SimulationId);
        Assert.Equal(4, ctx.PvEnergy.Requests.Count);
    }

    [Fact]
    public async Task Same_input_within_ttl_reuses_and_changed_input_creates_a_new_result()
    {
        var (ctx, userId, preSurveyId, productId, simulation) = await CreatedAsync();
        await using var _ = ctx;
        ctx.Time.Now = ctx.Time.Now.AddMinutes(ctx.SimulationOptions.ResultTtlMinutes - 1);

        var same = await ctx.CreateSimulationHandler().HandleAsync(Flush(userId, preSurveyId, productId), CancellationToken.None);
        var changed = await ctx.CreateSimulationHandler().HandleAsync(
            Flush(userId, preSurveyId, productId, installation: Defaults with { EdgeSetbackMm = 450m }), CancellationToken.None);

        Assert.Equal(CreateSimulationOutcome.Reused, same.Outcome);
        Assert.Equal(simulation.SimulationId, same.Simulation!.SimulationId);
        Assert.Equal(simulation.ExpiresAt, same.Simulation.ExpiresAt);
        Assert.Equal(CreateSimulationOutcome.Created, changed.Outcome);
        Assert.NotEqual(simulation.SimulationId, changed.Simulation!.SimulationId);
        var list = await ctx.ListSimulationsHandler().HandleAsync(Owner(userId), preSurveyId, CancellationToken.None);
        Assert.Equal(new[] { changed.Simulation.SimulationId, simulation.SimulationId }, list.Simulations!.Items.Select(i => i.SimulationId));
    }

    [Fact]
    public async Task Selected_summary_survives_cache_expiry_for_reads_list_and_sales_detail()
    {
        var (ctx, userId, preSurveyId, _, simulation) = await CreatedAsync();
        await using var _ = ctx;
        Assert.Equal(SelectSimulationResultOutcome.Selected, (await ctx.SelectSimulationAsync(userId, preSurveyId, simulation.SimulationId)).Outcome);
        var submitted = await ctx.SubmitPreSurveyHandler().HandleAsync(new SubmitPreSurveyCommand(userId, preSurveyId), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();

        Expire(ctx);

        var read = await ctx.GetSimulationHandler().HandleAsync(Owner(userId), preSurveyId, simulation.SimulationId, CancellationToken.None);
        Assert.Equal(SimulationQueryOutcome.SummaryOnly, read.Outcome);
        var summary = read.Summary!;
        Assert.Equal(SimulationSources.Summary, summary.Source);
        Assert.Equal(simulation.SimulationId, summary.SimulationId);
        Assert.Equal(simulation.Layout.PanelCount, summary.PanelCount);
        Assert.Equal(simulation.Energy.AnnualEnergyKwh, summary.AnnualEnergyKwh);
        Assert.False(summary.IsStale);

        var list = await ctx.ListSimulationsHandler().HandleAsync(Owner(userId), preSurveyId, CancellationToken.None);
        Assert.Empty(list.Simulations!.Items);
        Assert.Equal(simulation.SimulationId, list.Simulations.Selected!.SimulationId);

        var detail = await ctx.UnitOfWork.GetSurveyRequestDetailAsync(submitted.SurveyRequestId!.Value, CancellationToken.None);
        Assert.Equal(simulation.SimulationId, detail!.SelectedSimulation!.SimulationId);
        Assert.Equal(simulation.Layout.InstalledCapacityKwp, detail.SelectedSimulation.InstalledCapacityKwp);
        Assert.Equal(simulation.CreatedAt, detail.SelectedSimulation.CreatedAt);
    }

    [Fact]
    public async Task Reselecting_while_draft_replaces_the_summary_row_and_repeating_is_a_no_op()
    {
        var (ctx, userId, preSurveyId, productId, first) = await CreatedAsync();
        await using var _ = ctx;
        var second = (await ctx.CreateSimulationHandler().HandleAsync(
            Flush(userId, preSurveyId, productId, installation: Defaults with { PanelGapMm = 50m }), CancellationToken.None)).Simulation!;

        await ctx.SelectSimulationAsync(userId, preSurveyId, first.SimulationId);
        var firstRow = await ctx.Db.SelectedSimulations.AsNoTracking().SingleAsync();
        ctx.Time.Now = ctx.Time.Now.AddMinutes(5);
        var repeated = await ctx.SelectSimulationAsync(userId, preSurveyId, first.SimulationId);
        Assert.Equal(SelectSimulationResultOutcome.Selected, repeated.Outcome);
        Assert.Equal(firstRow.SelectedAt, (await ctx.Db.SelectedSimulations.AsNoTracking().SingleAsync()).SelectedAt);

        var replaced = await ctx.SelectSimulationAsync(userId, preSurveyId, second.SimulationId);

        Assert.Equal(SelectSimulationResultOutcome.Selected, replaced.Outcome);
        var row = await ctx.Db.SelectedSimulations.AsNoTracking().SingleAsync();
        Assert.Equal(second.SimulationId, row.SimulationId);
        Assert.NotEqual(firstRow.Id, row.Id);
        Assert.Equal(second.SimulationId, (await ctx.Db.PreSurveys.AsNoTracking().SingleAsync()).SelectedSimulationId);
        var list = await ctx.ListSimulationsHandler().HandleAsync(Owner(userId), preSurveyId, CancellationToken.None);
        Assert.Single(list.Simulations!.Items, i => i.IsSelected);
        Assert.True(list.Simulations.Items.Single(i => i.IsSelected).SimulationId == second.SimulationId);
    }

    [Fact]
    public async Task Selection_is_frozen_after_submit()
    {
        var (ctx, userId, preSurveyId, productId, first) = await CreatedAsync();
        await using var _ = ctx;
        var second = (await ctx.CreateSimulationHandler().HandleAsync(
            Flush(userId, preSurveyId, productId, installation: Defaults with { PanelGapMm = 50m }), CancellationToken.None)).Simulation!;
        await ctx.SelectSimulationAsync(userId, preSurveyId, first.SimulationId);
        await ctx.SubmitPreSurveyHandler().HandleAsync(new SubmitPreSurveyCommand(userId, preSurveyId), CancellationToken.None);
        ctx.Db.ChangeTracker.Clear();

        var reselect = await ctx.SelectSimulationAsync(userId, preSurveyId, second.SimulationId);
        var clear = await ctx.SelectSimulationHandler().ClearAsync(userId, preSurveyId, CancellationToken.None);

        Assert.Equal(SelectSimulationResultOutcome.NotEditable, reselect.Outcome);
        Assert.Equal(ClearSimulationSelectionOutcome.NotEditable, clear);
        Assert.Equal(first.SimulationId, (await ctx.Db.SelectedSimulations.AsNoTracking().SingleAsync()).SimulationId);
        Assert.Equal(first.SimulationId, (await ctx.Db.PreSurveys.AsNoTracking().SingleAsync()).SelectedSimulationId);
    }

    [Fact]
    public async Task Clearing_a_draft_selection_removes_the_row()
    {
        var (ctx, userId, preSurveyId, _, simulation) = await CreatedAsync();
        await using var _ = ctx;
        await ctx.SelectSimulationAsync(userId, preSurveyId, simulation.SimulationId);

        Assert.Equal(ClearSimulationSelectionOutcome.Cleared, await ctx.SelectSimulationHandler().ClearAsync(userId, preSurveyId, CancellationToken.None));
        Assert.Equal(ClearSimulationSelectionOutcome.Cleared, await ctx.SelectSimulationHandler().ClearAsync(userId, preSurveyId, CancellationToken.None));

        Assert.Empty(await ctx.Db.SelectedSimulations.AsNoTracking().ToListAsync());
        Assert.Null((await ctx.Db.PreSurveys.AsNoTracking().SingleAsync()).SelectedSimulationId);
    }

    [Fact]
    public async Task Select_requires_the_current_geometry_version_on_both_request_and_result()
    {
        var (ctx, userId, preSurveyId, _, simulation) = await CreatedAsync();
        await using var _ = ctx;

        var wrongExpected = await ctx.SelectSimulationAsync(userId, preSurveyId, simulation.SimulationId, geometryVersion: 0);
        Assert.Equal(SelectSimulationResultOutcome.SourceChanged, wrongExpected.Outcome);

        var preSurvey = (await ctx.UnitOfWork.FindPreSurveyByIdAsync(preSurveyId, CancellationToken.None))!;
        preSurvey.SurfaceWidthM = 9m;
        preSurvey.GeometryVersion++;
        preSurvey.Revision++;
        Assert.True(await ctx.UnitOfWork.TrySaveDraftChangesAsync(CancellationToken.None));
        ctx.Db.ChangeTracker.Clear();

        // The request matches the new version, but the cached result was calculated at version 1.
        var staleResult = await ctx.SelectSimulationAsync(userId, preSurveyId, simulation.SimulationId, geometryVersion: 2);
        Assert.Equal(SelectSimulationResultOutcome.SourceChanged, staleResult.Outcome);
        Assert.Empty(await ctx.Db.SelectedSimulations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Selected_summary_becomes_stale_after_a_geometry_edit()
    {
        var (ctx, userId, preSurveyId, _, simulation) = await CreatedAsync();
        await using var _ = ctx;
        await ctx.SelectSimulationAsync(userId, preSurveyId, simulation.SimulationId);
        var preSurvey = (await ctx.UnitOfWork.FindPreSurveyByIdAsync(preSurveyId, CancellationToken.None))!;
        preSurvey.SurfaceWidthM = 9m;
        preSurvey.GeometryVersion++;
        preSurvey.Revision++;
        Assert.True(await ctx.UnitOfWork.TrySaveDraftChangesAsync(CancellationToken.None));
        ctx.Db.ChangeTracker.Clear();

        var list = await ctx.ListSimulationsHandler().HandleAsync(Owner(userId), preSurveyId, CancellationToken.None);

        Assert.True(list.Simulations!.Selected!.IsStale);
    }

    [Fact]
    public async Task Only_the_owning_customer_can_select_and_unauthorized_viewers_cannot_read()
    {
        var (ctx, _, preSurveyId, _, simulation) = await CreatedAsync();
        await using var _ = ctx;
        var stranger = await ctx.SeedUserAsync();

        var select = await ctx.SelectSimulationAsync(stranger.Id, preSurveyId, simulation.SimulationId);
        var read = await ctx.GetSimulationHandler().HandleAsync(Owner(stranger.Id), preSurveyId, simulation.SimulationId, CancellationToken.None);
        var salesRead = await ctx.GetSimulationHandler().HandleAsync(new SimulationViewer(stranger.Id, false, true), preSurveyId, simulation.SimulationId, CancellationToken.None);
        var techRead = await ctx.GetSimulationHandler().HandleAsync(new SimulationViewer(stranger.Id, false, false, true), preSurveyId, simulation.SimulationId, CancellationToken.None);
        var clear = await ctx.SelectSimulationHandler().ClearAsync(stranger.Id, preSurveyId, CancellationToken.None);

        Assert.Equal(SelectSimulationResultOutcome.NotOwned, select.Outcome);
        Assert.Equal(SimulationQueryOutcome.Forbidden, read.Outcome);
        Assert.Equal(SimulationQueryOutcome.Forbidden, salesRead.Outcome);
        Assert.Equal(SimulationQueryOutcome.Forbidden, techRead.Outcome);
        Assert.Equal(ClearSimulationSelectionOutcome.NotOwned, clear);
        Assert.Empty(await ctx.Db.SelectedSimulations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_result_of_another_pre_survey_is_not_found_through_this_one()
    {
        var (ctx, _, _, productId, simulationA) = await CreatedAsync();
        await using var _ = ctx;
        var (userB, draftB) = await ctx.SeedDraftWithSurfaceAsync();

        var read = await ctx.GetSimulationHandler().HandleAsync(Owner(userB.Id), draftB.Id, simulationA.SimulationId, CancellationToken.None);

        Assert.Equal(SimulationQueryOutcome.SimulationNotFound, read.Outcome);
        Assert.Equal(productId, simulationA.Product.Id);
    }

    [Fact]
    public async Task Redis_outage_is_reported_as_unavailable_not_expired()
    {
        var (ctx, userId, preSurveyId, productId, simulation) = await CreatedAsync();
        await using var _ = ctx;
        var other = (await ctx.CreateSimulationHandler().HandleAsync(
            Flush(userId, preSurveyId, productId, installation: Defaults with { PanelGapMm = 50m }), CancellationToken.None)).Simulation!;
        await ctx.SelectSimulationAsync(userId, preSurveyId, simulation.SimulationId);
        var providerCalls = ctx.PvEnergy.Requests.Count;

        ctx.DistributedCache.Outage = true;

        var select = await ctx.SelectSimulationAsync(userId, preSurveyId, other.SimulationId);
        var readOther = await ctx.GetSimulationHandler().HandleAsync(Owner(userId), preSurveyId, other.SimulationId, CancellationToken.None);
        var readSelected = await ctx.GetSimulationHandler().HandleAsync(Owner(userId), preSurveyId, simulation.SimulationId, CancellationToken.None);
        var list = await ctx.ListSimulationsHandler().HandleAsync(Owner(userId), preSurveyId, CancellationToken.None);
        var create = await ctx.CreateSimulationHandler().HandleAsync(
            Flush(userId, preSurveyId, productId, installation: Defaults with { PanelGapMm = 70m }), CancellationToken.None);

        Assert.Equal(SelectSimulationResultOutcome.CacheUnavailable, select.Outcome);
        Assert.Equal(SimulationQueryOutcome.CacheUnavailable, readOther.Outcome);
        // The stored selection does not depend on the cache.
        Assert.Equal(SimulationQueryOutcome.SummaryOnly, readSelected.Outcome);
        Assert.False(list.Simulations!.CacheAvailable);
        Assert.Empty(list.Simulations.Items);
        Assert.Equal(simulation.SimulationId, list.Simulations.Selected!.SimulationId);
        Assert.Equal(CreateSimulationOutcome.CacheUnavailable, create.Outcome);
        Assert.Equal(SimulationErrorCodes.CacheUnavailable, create.ErrorCode);
        Assert.Equal(providerCalls, ctx.PvEnergy.Requests.Count);
        Assert.Equal(simulation.SimulationId, (await ctx.Db.SelectedSimulations.AsNoTracking().SingleAsync()).SimulationId);
    }

    [Fact]
    public async Task Enabled_snapshot_is_stored_on_select_and_served_verified_after_expiry()
    {
        var (ctx, userId, preSurveyId, _, simulation) = await CreatedAsync();
        await using var _ = ctx;
        ctx.Snapshots.IsEnabled = true;

        var selected = await ctx.SelectSimulationAsync(userId, preSurveyId, simulation.SimulationId);

        Assert.Equal(SnapshotStatuses.Verified, selected.Selection!.SnapshotStatus);
        var row = await ctx.Db.SelectedSimulations.AsNoTracking().SingleAsync();
        Assert.Equal(FakeSimulationSnapshotStore.Bucket, row.SnapshotBucket);
        Assert.Equal($"simulations/{preSurveyId}/{simulation.SimulationId}.json", row.SnapshotObjectKey);
        Assert.Equal(SnapshotIntegrity.Sha256Hex(ctx.Snapshots.Objects[row.SnapshotObjectKey!]), row.SnapshotSha256);
        Assert.Equal(ctx.Snapshots.Objects[row.SnapshotObjectKey!].LongLength, row.SnapshotSizeBytes);

        Expire(ctx);
        var read = await ctx.GetSimulationHandler().HandleAsync(Owner(userId), preSurveyId, simulation.SimulationId, CancellationToken.None);

        Assert.Equal(SimulationQueryOutcome.Found, read.Outcome);
        Assert.Equal(SimulationSources.Snapshot, read.Simulation!.Source);
        Assert.Null(read.Simulation.ExpiresAt);
        Assert.True(read.Simulation.IsSelected);
        Assert.Equal(simulation.Layout.Details.Placements.Count, read.Simulation.Layout.Details.Placements.Count);
        Assert.Equal(simulation.Energy.Monthly.Count, read.Simulation.Energy.Monthly.Count);
    }

    [Fact]
    public async Task Snapshot_integrity_mismatch_is_rejected_and_only_the_summary_is_served()
    {
        var (ctx, userId, preSurveyId, _, simulation) = await CreatedAsync();
        await using var _ = ctx;
        ctx.Snapshots.IsEnabled = true;
        await ctx.SelectSimulationAsync(userId, preSurveyId, simulation.SimulationId);
        var key = (await ctx.Db.SelectedSimulations.AsNoTracking().SingleAsync()).SnapshotObjectKey!;
        Expire(ctx);

        ctx.Snapshots.Tamper(key);
        var tampered = await ctx.GetSimulationHandler().HandleAsync(Owner(userId), preSurveyId, simulation.SimulationId, CancellationToken.None);

        Assert.Equal(SimulationQueryOutcome.SummaryOnly, tampered.Outcome);
        Assert.Null(tampered.Simulation);
        Assert.Equal(SnapshotStatuses.IntegrityFailed, tampered.Summary!.SnapshotStatus);

        ctx.Snapshots.Objects.Clear();
        var missing = await ctx.GetSimulationHandler().HandleAsync(Owner(userId), preSurveyId, simulation.SimulationId, CancellationToken.None);
        Assert.Equal(SnapshotStatuses.Missing, missing.Summary!.SnapshotStatus);
    }

    [Fact]
    public async Task Failed_snapshot_upload_still_selects_without_a_snapshot()
    {
        var (ctx, userId, preSurveyId, _, simulation) = await CreatedAsync();
        await using var _ = ctx;
        ctx.Snapshots.IsEnabled = true;
        ctx.Snapshots.FailWrites = true;

        var selected = await ctx.SelectSimulationAsync(userId, preSurveyId, simulation.SimulationId);

        Assert.Equal(SelectSimulationResultOutcome.Selected, selected.Outcome);
        Assert.False(selected.Selection!.SnapshotStored);
        var row = await ctx.Db.SelectedSimulations.AsNoTracking().SingleAsync();
        Assert.Null(row.SnapshotObjectKey);
        Assert.Null(row.SnapshotSha256);
        Assert.Equal(1, ctx.Snapshots.Puts);
    }

    [Fact]
    public async Task Snapshot_is_uploaded_before_the_transaction_and_left_orphaned_when_it_fails()
    {
        await using var ctx = PreSurveyTestContext.CreateFileBacked();
        var (user, draft) = await ctx.SeedDraftWithSurfaceAsync();
        var product = await ctx.SeedPanelProductAsync();
        var simulation = (await ctx.CreateSimulationHandler().HandleAsync(Flush(user.Id, draft.Id, product.Id), CancellationToken.None)).Simulation!;
        ctx.Snapshots.IsEnabled = true;

        // The pre-survey is submitted between the handler's checks and the transaction (simulated by
        // a handler whose unit of work sees a Draft source but whose transaction finds it submitted).
        var submitter = new SubmitPreSurveyHandler(ctx.CreateUnitOfWorkOnNewConnection());
        var racing = new SelectSimulationHandler(
            new SubmitBeforeSelectUnitOfWork(ctx.SimulationUnitOfWork, () => submitter.HandleAsync(new SubmitPreSurveyCommand(user.Id, draft.Id), CancellationToken.None)),
            ctx.SimulationCache, ctx.Snapshots, ctx.SimulationOptions, ctx.Time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SelectSimulationHandler>.Instance);

        var result = await racing.HandleAsync(new SelectSimulationCommand(user.Id, draft.Id, simulation.SimulationId, 1), CancellationToken.None);

        Assert.Equal(SelectSimulationResultOutcome.NotEditable, result.Outcome);
        Assert.Single(ctx.Snapshots.Objects);
        Assert.Empty(await ctx.Db.SelectedSimulations.AsNoTracking().ToListAsync());
        Assert.Null((await ctx.Db.PreSurveys.AsNoTracking().SingleAsync()).SelectedSimulationId);
    }

    private sealed class SubmitBeforeSelectUnitOfWork(
        Modules.SolarSimulation.Contracts.Persistence.ISolarSimulationUnitOfWork inner,
        Func<Task> beforeSelect) : Modules.SolarSimulation.Contracts.Persistence.ISolarSimulationUnitOfWork
    {
        public Task<Modules.SolarSimulation.Contracts.Persistence.SimulationSource?> GetSourceAsync(Guid preSurveyId, CancellationToken cancellationToken)
            => inner.GetSourceAsync(preSurveyId, cancellationToken);

        public Task<SelectedSimulation?> GetSelectionAsync(Guid preSurveyId, CancellationToken cancellationToken)
            => inner.GetSelectionAsync(preSurveyId, cancellationToken);

        public async Task<Modules.SolarSimulation.Contracts.Persistence.SelectSimulationOutcome> TrySelectAsync(SelectedSimulation selection, CancellationToken cancellationToken)
        {
            await beforeSelect();
            return await inner.TrySelectAsync(selection, cancellationToken);
        }

        public Task<Modules.SolarSimulation.Contracts.Persistence.ClearSelectionOutcome> TryClearSelectionAsync(Guid preSurveyId, CancellationToken cancellationToken)
            => inner.TryClearSelectionAsync(preSurveyId, cancellationToken);
    }
}
