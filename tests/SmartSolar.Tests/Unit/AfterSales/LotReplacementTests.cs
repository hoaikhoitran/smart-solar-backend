using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Installations;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>
/// After-sales on an installation handed over with a quantity lot (12 panels confirmed by count, no serial numbers): warranty, fault reports and
/// equipment replacement keep working. Replacing one panel of the lot splits that unit off as its own REPLACED row, so the installed count of
/// the handover never changes and the replacement's lineage points at the exact unit. Data is SYNTHETIC.
/// </summary>
public sealed class LotReplacementTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Like <see cref="AfterSalesTestContext.HandOverAsync"/>, but the technician confirms the 12 panels as one lot and only the inverter by serial.</summary>
    private static async Task<(HandedOver H, Guid LotId)> HandedOverWithLotAsync(AfterSalesTestContext ctx)
    {
        var admin = await ctx.Installation.Scheduling.Inner.SeedUserAsync("Admin");
        Assert.True((await ctx.Packages().SaveWarrantyOptionAsync(admin.Id, WarrantyOptionKinds.Installation, InstallationWarrantyMonths, true, true, None)).Succeeded);
        Assert.True((await ctx.Packages().SaveWarrantyOptionAsync(admin.Id, WarrantyOptionKinds.Repair, RepairWarrantyMonths, true, true, None)).Succeeded);
        var paid = await InstallationTests.PaidContractAsync(ctx.Installation);
        var installs = ctx.Installation.Installations();
        var technician = await ctx.Installation.Scheduling.SeedTechnicianAsync("Installer", BinhTan);
        var project = (await installs.CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        var wed = Day.AddDays(1);
        Assert.True((await installs.AssignAsync(paid.ManagerId, project.ProjectId, new AssignTechnicianInput(technician.Id, At(8, 0, wed), At(17, 0, wed), null, project.Revision), None)).Succeeded);
        Assert.True((await installs.AcceptAssignmentAsync(technician.Id, project.ProjectId, null, None)).Succeeded);
        await InstallationTests.StartAsync(ctx.Installation, project.ProjectId, technician.Id);
        await InstallationTests.AddCompletionEvidenceAsync(ctx.Installation, project.ProjectId, technician.Id);
        var (panel, inverter) = await InstallationLotRegistrationTests.ContractedAsync(ctx.Installation, paid.ContractId);
        // The catalog panel carries a manufacturer warranty, copied onto the lot at registration (the only source of a duration).
        var catalogPanel = await ctx.Db.Products.SingleAsync(p => p.Id == panel.Id);
        catalogPanel.WarrantyMonth = 144;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        var registered = await installs.RegisterAssetsAsync(technician.Id, project.ProjectId,
            [InstallationLotRegistrationTests.Lot(panel, "PANEL", 12), InstallationLotRegistrationTests.Serialised(inverter, "INVERTER", "HW-LOT-INV")], None);
        Assert.True(registered.Succeeded, registered.Message);
        Assert.True((await installs.ReportCompletionAsync(technician.Id, project.ProjectId, "Done", null, None)).Succeeded);
        var accepted = await InternalAcceptance.AcceptAsync(ctx.Installation, project.ProjectId, paid.ManagerId);
        Assert.True(accepted.Succeeded, accepted.Message);
        var sales = await ctx.Installation.Scheduling.SeedSalesAsync("After-sales Sales");
        var assets = await ctx.Db.InstalledAssets.AsNoTracking().Where(a => a.ProjectId == project.ProjectId).ToListAsync();
        ctx.Db.ChangeTracker.Clear();
        var h = new HandedOver(paid.CustomerId, paid.ContractId, project.ProjectId, technician.Id, paid.ManagerId, sales.Id, admin.Id,
            accepted.Value!.HandoverAt!.Value, assets.Single(a => a.AssetType == "INVERTER").Id);
        return (h, assets.Single(a => a.AssetType == "PANEL").Id);
    }

    [Fact]
    public async Task Replacing_one_panel_of_a_lot_splits_that_unit_off_and_keeps_the_handover_count()
    {
        await using var ctx = AfterSalesTestContext.Create();
        var (h, lotId) = await HandedOverWithLotAsync(ctx);

        // The customer reports a fault on the lot (the suspected equipment can be the lot itself), the technician replaces one panel.
        var warranty = (await ctx.Customer().GetWarrantyAsync(h.CustomerId, h.ProjectId, None)).Value!;
        var lotView = Assert.Single(warranty.Assets, a => a.AssetId == lotId);
        Assert.Equal((12, (string?)null), (lotView.Quantity, lotView.SerialNumber));
        var request = await ctx.FaultAsync(h, "One panel cracked", lotId);
        var items = await ctx.InspectedAsync(h, request.RequestId, Later(3), new DiagnosedItemInput(lotId, "Cracked panel", "Hail damage", null));
        await ctx.DecideAsync(h, request.RequestId, (items[0], ItemCoverages.InstallationWarranty));
        var repair = await ctx.VisitStartedAsync(h, request.RequestId, Later(6));
        var reported = await ctx.Technician().ReportWorkAsync(h.TechnicianId, repair, "Panel swapped", [new ItemResultInput(items[0], "replaced", null)],
            [new ReplacementInput(lotId, "Jinko", "Tiger Neo 550", "JK-NEW-01", "550 W, same class")], null, None);
        Assert.True(reported.Succeeded, reported.Message);
        var pending = await ctx.Db.InstalledAssets.AsNoTracking().SingleAsync(a => a.ServiceRequestId == request.RequestId);
        Assert.Equal((InstalledAssetStatuses.PendingAcceptance, (Guid?)lotId, 1), (pending.Status, pending.ReplacesAssetId, pending.Quantity));
        Assert.Contains("one of 12 PANEL units without serial", (await ctx.Db.AfterSalesEvents.AsNoTracking().SingleAsync(e => e.EventType == "ASSET_REPLACEMENT_RECORDED")).Detail);

        Assert.True((await ctx.Customer().DecideCompletionAsync(h.CustomerId, request.RequestId, true, null, null, None)).Succeeded);
        ctx.Db.ChangeTracker.Clear();

        var rows = await ctx.Db.InstalledAssets.AsNoTracking().Where(a => a.ProjectId == h.ProjectId && a.AssetType == "PANEL").ToListAsync();
        var lot = rows.Single(a => a.Id == lotId);
        var replacement = rows.Single(a => a.Id == pending.Id);
        var unit = rows.Single(a => a.Id != lotId && a.Id != pending.Id);
        Assert.Equal((11, InstalledAssetStatuses.Active, (string?)null), (lot.Quantity, lot.Status, lot.SerialNumber));
        Assert.Equal((1, InstalledAssetStatuses.Replaced, (string?)null, (Guid?)null, lot.ProductId, lot.RegisteredAt), (unit.Quantity, unit.Status, unit.SerialNumber, unit.ServiceRequestId, unit.ProductId, unit.RegisteredAt));
        Assert.Equal((InstalledAssetStatuses.Active, (Guid?)unit.Id, "JK-NEW-01"), (replacement.Status, replacement.ReplacesAssetId, replacement.SerialNumber));
        // 11 + 1 active units for the customer; the installation's own 12 units (lot + split-off unit) still match the contract.
        Assert.Equal(12, rows.Where(a => a.Status == InstalledAssetStatuses.Active).Sum(a => a.Quantity));
        Assert.Equal(12, rows.Where(a => a.ServiceRequestId == null).Sum(a => a.Quantity));
        Assert.Equal(EquipmentComparisonStatuses.Matches, (await ctx.Installation.Installations().GetForManagerAsync(h.ProjectId, None)).Value!.Equipment!.Status);
    }

    [Fact]
    public async Task A_lot_cannot_be_given_one_serial_number_but_its_position_can_be_corrected_and_its_warranty_activated()
    {
        await using var ctx = AfterSalesTestContext.Create();
        var (h, lotId) = await HandedOverWithLotAsync(ctx);

        var serial = await ctx.Manager().CorrectAssetAsync(h.ManagerId, lotId, new AssetCorrection(null, null, "JK-ONE", null, null, "typo"), None);
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, serial.ErrorCode);
        var moved = await ctx.Manager().CorrectAssetAsync(h.ManagerId, lotId, new AssetCorrection(null, null, null, "Roof B", null, "surveyed again"), None);
        Assert.True(moved.Succeeded, moved.Message);
        Assert.Equal((12, (string?)null, "Roof B"), (moved.Value!.Quantity, moved.Value.SerialNumber, moved.Value.PositionCode));

        // One activation date covers the whole lot (the panels were installed together).
        var document = await ctx.Installation.VerifiedFileAsync(h.ManagerId, SmartSolar.Modules.Files.FilePurposes.ProjectDocument, "pdf");
        var activated = await ctx.Manager().RecordWarrantyActivationAsync(h.ManagerId, lotId, h.HandoverAt, document, "Manufacturer confirmation", None);
        Assert.True(activated.Succeeded, activated.Message);
        Assert.Equal((144, 12), (activated.Value!.WarrantyMonths, activated.Value.Quantity));
        Assert.NotNull(activated.Value.WarrantyEndsAt);
    }
}
