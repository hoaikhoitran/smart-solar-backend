using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Api.Controllers;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Installations;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.Unit.InstallationWorkflow.InstallationTests;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Quantity-based handover: a technician confirms the installed count of a contracted catalog product in one entry (a "lot" without serial
/// numbers) instead of typing one serial per panel, while single devices (the inverter) keep their serial. Lots count as units in the
/// contract comparison, are audited, appear in every view and in the handover; no serial number is ever invented. Data is SYNTHETIC.
/// </summary>
public sealed class InstallationLotRegistrationTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The contracted panel (12) and inverter (1) products of the scheduled test project.</summary>
    internal static async Task<(Product Panel, Product Inverter)> ContractedAsync(InstallationWorkflowTestContext ctx, Guid contractId)
    {
        var quotationId = (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == contractId)).QuotationId;
        var lines = await ctx.Db.QuotationLines.AsNoTracking().Where(l => l.QuotationId == quotationId && l.ProductId != null).ToListAsync();
        var panelId = lines.Single(l => l.Quantity == 12m).ProductId!.Value;
        var inverterId = lines.Single(l => l.Quantity == 1m).ProductId!.Value;
        var panel = await ctx.Db.Products.AsNoTracking().SingleAsync(p => p.Id == panelId);
        var inverter = await ctx.Db.Products.AsNoTracking().SingleAsync(p => p.Id == inverterId);
        return (panel, inverter);
    }

    internal static AssetInput Lot(Product product, string type, int quantity, string? position = null)
        => new(type, product.Id, product.Brand, product.Model ?? "Model", null, position, null, quantity);

    internal static AssetInput Serialised(Product product, string type, string serial)
        => new(type, product.Id, product.Brand, product.Model ?? "Model", serial, null, null);

    private static async Task<(InstallationWorkflowTestContext Ctx, Paid Paid, Guid ProjectId, Guid ContractId, Guid Tech, Guid Other)> StartedAsync()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        var (paid, project, tech, other) = await ScheduledAsync(ctx);
        await StartAsync(ctx, project.ProjectId, tech);
        await AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        return (ctx, paid, project.ProjectId, project.ContractId, tech, other);
    }

    [Fact]
    public async Task One_lot_confirms_the_contracted_panels_the_inverter_keeps_its_serial_and_the_handover_lists_both()
    {
        var (ctx, paid, projectId, contractId, tech, _) = await StartedAsync();
        await using var _ = ctx;
        var installs = ctx.Installations();
        var (panel, inverter) = await ContractedAsync(ctx, contractId);

        var registered = await installs.RegisterAssetsAsync(tech, projectId, [Lot(panel, "PANEL", 12, "Roof A"), Serialised(inverter, "INVERTER", "HW-LOT-INV-01")], None);

        Assert.True(registered.Succeeded, registered.Message);
        var view = registered.Value!;
        var lot = Assert.Single(view.Assets, a => a.AssetType == "PANEL");
        Assert.Equal((12, (string?)null, (Guid?)panel.Id, "Roof A", panel.WarrantyMonth), (lot.Quantity, lot.SerialNumber, lot.ProductId, lot.PositionCode, lot.WarrantyMonths));
        Assert.NotNull(lot.RegisteredAt);
        var single = Assert.Single(view.Assets, a => a.AssetType == "INVERTER");
        Assert.Equal((1, "HW-LOT-INV-01"), (single.Quantity, single.SerialNumber));
        // 12 units by count + 1 serialised unit = exactly the accepted quotation: no substitution to approve.
        Assert.Equal(EquipmentComparisonStatuses.Matches, view.Equipment!.Status);
        Assert.True(view.RequiresPanelAndInverterAssets);
        // History: one audited registration naming the counts and telling lots from serials; the stored lot carries no serial.
        var audit = Assert.Single(await ctx.Db.InstallationEvents.AsNoTracking().Where(e => e.ProjectId == projectId && e.EventType == InstallationEventTypes.AssetsRegistered).ToListAsync());
        Assert.Equal("13 units in 2 entries: PANEL ×12 (12 by count), INVERTER ×1 (1 serial)", audit.Detail);
        Assert.Equal(tech, audit.ActorUserId);
        var stored = await ctx.Db.InstalledAssets.AsNoTracking().SingleAsync(a => a.ProjectId == projectId && a.AssetType == "PANEL");
        Assert.Equal((12, (string?)null, InstalledAssetStatuses.Active), (stored.Quantity, stored.SerialNumber, stored.Status));

        Assert.True((await installs.ReportCompletionAsync(tech, projectId, "All 12 panels and the inverter installed and commissioned.", null, None)).Succeeded);
        var accepted = await installs.DecideAcceptanceAsync(paid.CustomerId, projectId, true, null, None);
        Assert.True(accepted.Succeeded, accepted.Message);
        var handover = (await installs.GetHandoverAsync(paid.CustomerId, false, projectId, None)).Value!;
        var handedLot = Assert.Single(handover.Assets, a => a.AssetType == "PANEL");
        Assert.Equal((12, (string?)null, panel.WarrantyMonth), (handedLot.Quantity, handedLot.SerialNumber, handedLot.WarrantyMonths));
        Assert.Equal(1, Assert.Single(handover.Assets, a => a.AssetType == "INVERTER").Quantity);
        var customer = (await installs.GetForCustomerAsync(paid.CustomerId, projectId, None)).Value!;
        Assert.Equal(13, customer.Assets.Sum(a => a.Quantity));
    }

    [Fact]
    public async Task Quantity_entries_are_validated_and_never_mixed_with_serial_numbers()
    {
        var (ctx, _, projectId, contractId, tech, _) = await StartedAsync();
        await using var _ = ctx;
        var installs = ctx.Installations();
        var (panel, _) = await ContractedAsync(ctx, contractId);
        static string Keys(OperationResult<InstallationTechnicianView> result) => string.Join(",", JsonSerializer.Deserialize<Dictionary<string, string[]>>(JsonSerializer.Serialize(result.Details))!.Keys);

        var noProduct = await installs.RegisterAssetsAsync(tech, projectId, [new AssetInput("BATTERY", null, "BYD", "LVS 4.0", null, null, null, 2)], None);
        var missingQuantity = await installs.RegisterAssetsAsync(tech, projectId, [new AssetInput("PANEL", panel.Id, "Jinko", "Tiger Neo", null, null, null, null)], None);
        var zero = await installs.RegisterAssetsAsync(tech, projectId, [Lot(panel, "PANEL", 0)], None);
        var tooMany = await installs.RegisterAssetsAsync(tech, projectId, [Lot(panel, "PANEL", InstallationLimits.MaxLotQuantity + 1)], None);
        var serialWithQuantity = await installs.RegisterAssetsAsync(tech, projectId, [Serialised(panel, "PANEL", "JK-Q-1") with { Quantity = 2 }], None);
        var serialWithOne = await installs.RegisterAssetsAsync(tech, projectId, [Serialised(panel, "PANEL", "JK-Q-1") with { Quantity = 1 }], None);

        foreach (var invalid in new[] { noProduct, missingQuantity, zero, tooMany, serialWithQuantity })
        {
            Assert.Equal(InstallationErrorCodes.AssetQuantityInvalid, invalid.ErrorCode);
            Assert.Equal(400, OperationResultControllerBase.StatusFor(invalid.ErrorCode!));
        }
        Assert.Equal("assets[0].productId", Keys(noProduct));
        Assert.Equal("assets[0].quantity", Keys(missingQuantity));
        Assert.Equal("assets[0].quantity", Keys(zero));
        Assert.Equal("assets[0].quantity", Keys(tooMany));
        Assert.Equal("assets[0].quantity", Keys(serialWithQuantity));
        Assert.True(serialWithOne.Succeeded, serialWithOne.Message);
        Assert.Equal(1, Assert.Single(serialWithOne.Value!.Assets).Quantity);
        Assert.Equal(1, await ctx.Db.InstalledAssets.CountAsync(a => a.ProjectId == projectId));
        // No serial number was invented for a lot and lots do not collide with each other.
        Assert.True((await installs.RegisterAssetsAsync(tech, projectId, [Lot(panel, "PANEL", 5), Lot(panel, "PANEL", 6)], None)).Succeeded);
        Assert.Equal(2, await ctx.Db.InstalledAssets.CountAsync(a => a.ProjectId == projectId && a.SerialNumber == null));
    }

    [Fact]
    public async Task A_short_lot_differs_from_the_contract_until_the_remaining_units_are_confirmed_by_count_or_serial()
    {
        var (ctx, paid, projectId, contractId, tech, _) = await StartedAsync();
        await using var _ = ctx;
        var installs = ctx.Installations();
        var (panel, inverter) = await ContractedAsync(ctx, contractId);

        // 11 of 12 panels confirmed: the comparison shows the shortfall in units and the report is blocked exactly as for serialised assets.
        var registered = (await installs.RegisterAssetsAsync(tech, projectId, [Lot(panel, "PANEL", 11), Serialised(inverter, "INVERTER", "HW-SHORT-01")], None)).Value!;
        Assert.Equal(EquipmentComparisonStatuses.ApprovalRequired, registered.Equipment!.Status);
        var difference = Assert.Single(registered.Equipment.Differences);
        Assert.Equal((panel.Id, 12m, 11), (difference.ProductId, difference.ContractedQuantity, difference.InstalledQuantity));
        var blocked = await installs.ReportCompletionAsync(tech, projectId, "Installed", null, None);
        Assert.Equal(InstallationErrorCodes.SubstitutionApprovalRequired, blocked.ErrorCode);
        Assert.Contains("\"InstalledQuantity\":11", JsonSerializer.Serialize(blocked.Details));

        // A Manager approval of the 11/12 comparison is superseded when the last panel is registered (here with its serial): the count then matches.
        var revision = (await installs.GetForManagerAsync(projectId, None)).Value!.Revision;
        Assert.True((await installs.ApproveSubstitutionAsync(paid.ManagerId, projectId, revision, "Panel damaged in transport", None)).Succeeded);
        var completed = (await installs.RegisterAssetsAsync(tech, projectId, [Serialised(panel, "PANEL", "JK-LAST-12")], None)).Value!;
        Assert.Equal(EquipmentComparisonStatuses.Matches, completed.Equipment!.Status);
        Assert.Equal(12, completed.Assets.Where(a => a.AssetType == "PANEL").Sum(a => a.Quantity));
        Assert.Equal(EquipmentSubstitutionStatuses.Superseded, (await ctx.Db.EquipmentSubstitutions.AsNoTracking().SingleAsync(s => s.ProjectId == projectId)).Status);
        Assert.True((await installs.ReportCompletionAsync(tech, projectId, "Installed", null, None)).Succeeded);

        // Too many units (a 13th panel by count) is a substitution again, for the Manager and then the customer.
        var ctx2 = InstallationWorkflowTestContext.Create();
        await using var __ = ctx2;
        var (_, project2, tech2, _) = await ScheduledAsync(ctx2);
        await StartAsync(ctx2, project2.ProjectId, tech2);
        var (panel2, inverter2) = await ContractedAsync(ctx2, project2.ContractId);
        var over = (await ctx2.Installations().RegisterAssetsAsync(tech2, project2.ProjectId, [Lot(panel2, "PANEL", 13), Serialised(inverter2, "INVERTER", "HW-OVER-01")], None)).Value!;
        Assert.Equal(EquipmentComparisonStatuses.ApprovalRequired, over.Equipment!.Status);
        Assert.Equal((12m, 13), (Assert.Single(over.Equipment.Differences).ContractedQuantity, over.Equipment.Differences[0].InstalledQuantity));
    }

    [Fact]
    public async Task Lots_are_registered_only_by_the_accepted_technician_of_a_started_project()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, other) = await ScheduledAsync(ctx);
        var installs = ctx.Installations();
        var (panel, inverter) = await ContractedAsync(ctx, project.ContractId);
        var entries = new[] { Lot(panel, "PANEL", 12), Serialised(inverter, "INVERTER", "HW-AUTH-01") };

        // Not started yet: the state machine refuses; another technician sees no project at all.
        Assert.Equal(InstallationErrorCodes.InvalidState, (await installs.RegisterAssetsAsync(tech, project.ProjectId, entries, None)).ErrorCode);
        await StartAsync(ctx, project.ProjectId, tech);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.RegisterAssetsAsync(other, project.ProjectId, entries, None)).ErrorCode);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await installs.RegisterAssetsAsync(paid.CustomerId, project.ProjectId, entries, None)).ErrorCode);
        Assert.Equal(0, await ctx.Db.InstalledAssets.CountAsync(a => a.ProjectId == project.ProjectId));

        Assert.True((await installs.RegisterAssetsAsync(tech, project.ProjectId, entries, None)).Succeeded);
        await AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        Assert.True((await installs.ReportCompletionAsync(tech, project.ProjectId, "Done", null, None)).Succeeded);
        // Waiting for the customer: no more registrations (the report is what the customer judges).
        Assert.Equal(InstallationErrorCodes.InvalidState, (await installs.RegisterAssetsAsync(tech, project.ProjectId, [Lot(panel, "PANEL", 1)], None)).ErrorCode);
    }

    [Fact]
    public async Task The_completion_report_names_the_missing_equipment_types_and_the_view_tells_the_client_whether_they_are_required()
    {
        var (ctx, _, projectId, contractId, tech, _) = await StartedAsync();
        await using var _ = ctx;
        var installs = ctx.Installations();
        var (panel, _) = await ContractedAsync(ctx, contractId);

        var nothing = await installs.ReportCompletionAsync(tech, projectId, "Done", null, None);
        Assert.Equal(InstallationErrorCodes.AssetsRequired, nothing.ErrorCode);
        Assert.Equal(409, OperationResultControllerBase.StatusFor(nothing.ErrorCode!));
        Assert.Contains("\"missing\":[\"PANEL\",\"INVERTER\"]", JsonSerializer.Serialize(nothing.Details));

        Assert.True((await installs.RegisterAssetsAsync(tech, projectId, [Lot(panel, "PANEL", 12)], None)).Succeeded);
        var noInverter = await installs.ReportCompletionAsync(tech, projectId, "Done", null, None);
        Assert.Equal(InstallationErrorCodes.AssetsRequired, noInverter.ErrorCode);
        Assert.Contains("\"missing\":[\"INVERTER\"]", JsonSerializer.Serialize(noInverter.Details));
        Assert.True((await installs.GetForTechnicianAsync(tech, projectId, None)).Value!.RequiresPanelAndInverterAssets);

        // The requirement is a configuration: switched off, the view says so and the report (12 panels, contracted inverter still missing →
        // a substitution) proceeds to the substitution rule instead.
        ctx.InstallationOptions.RequirePanelAndInverterAssets = false;
        var relaxed = ctx.Installations();
        Assert.False((await relaxed.GetForTechnicianAsync(tech, projectId, None)).Value!.RequiresPanelAndInverterAssets);
        Assert.Equal(InstallationErrorCodes.SubstitutionApprovalRequired, (await relaxed.ReportCompletionAsync(tech, projectId, "Done", null, None)).ErrorCode);
    }
}
