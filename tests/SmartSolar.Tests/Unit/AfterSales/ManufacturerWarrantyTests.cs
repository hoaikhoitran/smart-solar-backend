using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Installations;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>
/// M-4 / D-06: manufacturer warranty is its own coverage category, but its start rule and claim handling are not approved, so it is never
/// decided, never dated by a guess and never paid automatically. Equipment warranty facts are snapshotted per item; installation-warranty
/// decisions are unchanged. SQLite check constraints only (PostgreSQL constraint and backfill tests NOT EXECUTED).
/// </summary>
public sealed class ManufacturerWarrantyTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static DiagnosedItemInput Item(string description, Guid? assetId = null) => new(assetId, description, $"{description}: diagnosis", null);

    [Fact]
    public async Task Manufacturer_warranty_is_a_separate_category_that_cannot_be_decided_before_its_rules_are_approved()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var panel = await ctx.Db.InstalledAssets.AsNoTracking().FirstAsync(a => a.ProjectId == h.ProjectId && a.AssetType == AssetTypes.Panel);
        var request = await ctx.FaultAsync(h);
        var items = await ctx.InspectedAsync(h, request.RequestId, Later(3), Item("Inverter fan", h.InverterId), Item("Panel hot spot", panel.Id));

        // Asset warranty sources: the inverter's duration comes from its catalog product; the panel product has none (nothing is invented).
        var inverter = await ctx.Db.InstalledAssets.AsNoTracking().SingleAsync(a => a.Id == h.InverterId);
        Assert.Equal(((int?)120, EquipmentWarrantySources.CatalogProduct, (DateTimeOffset?)null, (DateTimeOffset?)null),
            (inverter.WarrantyMonths, inverter.WarrantySource, inverter.WarrantyStartsAt, inverter.WarrantyEndsAt));
        Assert.Equal(((int?)null, (string?)null), (panel.WarrantyMonths, panel.WarrantySource));

        var refused = await ctx.Manager().DecideCoverageAsync(h.ManagerId, request.RequestId,
            [new CoverageDecision(items[0], ItemCoverages.ManufacturerWarranty, "Inverter fault within the manufacturer term")], None);
        Assert.Equal(AfterSalesErrorCodes.ManufacturerWarrantyPolicyPending, refused.ErrorCode);
        Assert.Equal(409, SmartSolar.Api.Controllers.OperationResultControllerBase.StatusFor(refused.ErrorCode!));
        var untouched = await ctx.Db.ServiceItems.AsNoTracking().SingleAsync(i => i.Id == items[0]);
        Assert.Equal((ItemCoverages.Pending, ItemStatuses.Open, (int?)null), (untouched.Coverage, untouched.Status, untouched.EquipmentWarrantyMonths));
        Assert.Empty(await ctx.Db.ServiceQuotations.AsNoTracking().Where(q => q.RequestId == request.RequestId).ToListAsync());

        // Other categories keep working, and each item keeps its own equipment-warranty snapshot.
        await ctx.DecideAsync(h, request.RequestId, (items[0], ItemCoverages.InstallationWarranty), (items[1], ItemCoverages.Chargeable));
        var decided = await ctx.Db.ServiceItems.AsNoTracking().Where(i => i.RequestId == request.RequestId).ToListAsync();
        var fan = decided.Single(i => i.Id == items[0]);
        var hotSpot = decided.Single(i => i.Id == items[1]);
        Assert.Equal((ItemCoverages.InstallationWarranty, ItemStatuses.Authorized, (int?)120, EquipmentWarrantySources.CatalogProduct),
            (fan.Coverage, fan.Status, fan.EquipmentWarrantyMonths, fan.EquipmentWarrantySource));
        Assert.Equal((ItemCoverages.Chargeable, (int?)null, (string?)null), (hotSpot.Coverage, hotSpot.EquipmentWarrantyMonths, hotSpot.EquipmentWarrantySource));
        Assert.Null(fan.EquipmentWarrantyEndsAt);
    }

    [Fact]
    public async Task The_database_refuses_manufacturer_coverage_without_a_computed_end_and_a_duration_without_its_source()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var request = await ctx.FaultAsync(h);
        var items = await ctx.InspectedAsync(h, request.RequestId, Later(3), Item("Inverter fan", h.InverterId));

        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE service_item SET coverage = 'MANUFACTURER_WARRANTY' WHERE id = {items[0]}"));
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE service_item SET equipment_warranty_source = 'TECHNICIAN' WHERE id = {items[0]}"));
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE installed_asset SET warranty_source = NULL WHERE id = {h.InverterId}"));
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE installed_asset SET warranty_ends_at = '2036-01-01T00:00:00+00:00' WHERE id = {h.InverterId}"));
        // Where an end date exists (only once a start rule is approved), the category itself is a valid stored value.
        Assert.Equal(1, await ctx.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE service_item SET coverage = 'MANUFACTURER_WARRANTY', equipment_warranty_ends_at = '2036-01-01T00:00:00+00:00' WHERE id = {items[0]}"));
        Assert.Equal(1, await ctx.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE installed_asset SET warranty_source = 'LEGACY_UNVERIFIED' WHERE id = {h.InverterId}"));
    }

    [Fact]
    public async Task A_verified_activation_date_gives_a_catalog_warranty_its_period_once_but_coverage_stays_refused()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var manager = ctx.Manager();
        var document = await ctx.Installation.VerifiedFileAsync(h.ManagerId, SmartSolar.Modules.Files.FilePurposes.ProjectDocument, "pdf");
        var activatedAt = h.HandoverAt.AddDays(-1);

        Assert.Equal(AfterSalesErrorCodes.ReasonRequired, (await manager.RecordWarrantyActivationAsync(h.ManagerId, h.InverterId, activatedAt, document, " ", None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed,
            (await manager.RecordWarrantyActivationAsync(h.ManagerId, h.InverterId, ctx.Clock.GetUtcNow().AddDays(1), document, "invoice", None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await manager.RecordWarrantyActivationAsync(h.ManagerId, h.InverterId, activatedAt, null, "invoice", None)).ErrorCode);
        // A panel whose catalog product has no warranty, and a legacy (unverified) duration, never get dates.
        var panel = await ctx.Db.InstalledAssets.AsNoTracking().FirstAsync(a => a.ProjectId == h.ProjectId && a.AssetType == AssetTypes.Panel);
        Assert.Equal(AfterSalesErrorCodes.WarrantySourceUnverified, (await manager.RecordWarrantyActivationAsync(h.ManagerId, panel.Id, activatedAt, document, "invoice", None)).ErrorCode);

        var recorded = (await manager.RecordWarrantyActivationAsync(h.ManagerId, h.InverterId, activatedAt, document, "Manufacturer activation certificate #A-77", None)).Value!;
        Assert.Equal((activatedAt.ToUniversalTime(), AfterSalesRules.AddMonths(activatedAt, 120, ctx.Installation.Scheduling.Calendar.TimeZone)),
            (recorded.WarrantyStartsAt!.Value, recorded.WarrantyEndsAt!.Value));
        Assert.Equal(AfterSalesErrorCodes.WarrantyActivationRecorded,
            (await manager.RecordWarrantyActivationAsync(h.ManagerId, h.InverterId, activatedAt.AddDays(-30), document, "different date", None)).ErrorCode);
        Assert.Single(await ctx.Db.AfterSalesEvents.AsNoTracking().Where(e => e.SubjectId == h.InverterId && e.EventType == "WARRANTY_ACTIVATION_RECORDED").ToListAsync());

        // The coverage snapshot now carries the verified end, but manufacturer coverage is still not decidable (payer and claim handling pending).
        var request = await ctx.FaultAsync(h);
        var items = await ctx.InspectedAsync(h, request.RequestId, Later(3), Item("Inverter fan", h.InverterId));
        Assert.Equal(AfterSalesErrorCodes.ManufacturerWarrantyPolicyPending, (await manager.DecideCoverageAsync(h.ManagerId, request.RequestId,
            [new CoverageDecision(items[0], ItemCoverages.ManufacturerWarranty, "within term")], None)).ErrorCode);
        await ctx.DecideAsync(h, request.RequestId, (items[0], ItemCoverages.InstallationWarranty));
        Assert.Equal(recorded.WarrantyEndsAt, (await ctx.Db.ServiceItems.AsNoTracking().SingleAsync(i => i.Id == items[0])).EquipmentWarrantyEndsAt);

        // A legacy duration (backfilled LEGACY_UNVERIFIED) is refused too.
        var legacyRequest = panel.Id;
        await ctx.Db.Database.ExecuteSqlInterpolatedAsync($"UPDATE installed_asset SET warranty_months = 60, warranty_source = 'LEGACY_UNVERIFIED' WHERE id = {legacyRequest}");
        Assert.Equal(AfterSalesErrorCodes.WarrantySourceUnverified, (await manager.RecordWarrantyActivationAsync(h.ManagerId, panel.Id, activatedAt, document, "invoice", None)).ErrorCode);
    }
}
