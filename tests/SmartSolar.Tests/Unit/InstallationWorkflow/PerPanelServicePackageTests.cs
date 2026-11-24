using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.PaymentPlanFlow;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Per-panel service packages sold with the installation (2026-11-25) on the production handlers: technician cart → issue snapshot → AGREED (ONE contract,
/// grand total) → 30/70 of the grand total → full payment + handover + panel reconciliation → PANEL-scope coverage; retail top-up after handover; claims.
/// Package and catalog prices are SYNTHETIC fixture values (the 700,000 VND/panel of the business example is an Admin catalog row created here, never a
/// constant of the code). TC numbers refer to guides/per-panel-packages/TEST_REPORT.md.
/// </summary>
public sealed class PerPanelServicePackageTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    internal sealed record World(AfterSalesTestContext After, Guid AdminId, ServicePackage Disaster, ServicePackage Maintenance, SimplifiedFlowFixture.Cart Cart)
    {
        public InstallationWorkflowTestContext Ctx => After.Installation;
        public Guid TechnicianId => Cart.TechnicianId;
        public Guid QuotationId => Cart.Quotation.QuotationId;
    }

    /// <summary>
    /// Per-panel catalog (disaster 700,000 / maintenance 300,000 VND per panel), installation catalog whose 10-panel cart is exactly 50,000,000 VND
    /// (10 × 3,000,000 panels + 10,000,000 inverter + 9,500,000 labor + 500,000 transport, tax 0 % unless given), and the technician's empty DRAFT.
    /// </summary>
    internal static async Task<World> WorldAsync(decimal taxPercent = 0m, decimal disasterPrice = 700_000m, decimal maintenancePrice = 300_000m,
        AfterSalesTestContext? on = null)
    {
        var after = on ?? AfterSalesTestContext.Create();
        var ctx = after.Installation;
        ctx.Clock.Now = SchedulingTestContext.DefaultNow.ToUniversalTime();
        ctx.QuotationOptions.DefaultTaxRatePercent = taxPercent;
        var admin = await ctx.Scheduling.Inner.SeedUserAsync("Admin");
        var disaster = await PerPanelPackageAsync(after, admin.Id, ServicePackageKinds.DisasterProtection, disasterPrice);
        var maintenance = await PerPanelPackageAsync(after, admin.Id, ServicePackageKinds.Maintenance, maintenancePrice);
        var catalog = await AutomaticQuotationFixture.SeedCatalogAsync(ctx, laborPrice: 9_500_000m);
        var survey = await ctx.QuotableSurveyAsync("Customer");
        var draft = await ctx.Quotations().CreateCartAsync(survey.Technician.Id, survey.Request.Request.Id, null, None);
        Assert.True(draft.Succeeded, draft.Message);
        return new World(after, admin.Id, disaster, maintenance, new SimplifiedFlowFixture.Cart(survey, catalog, draft.Value!));
    }

    internal static async Task<ServicePackage> PerPanelPackageAsync(AfterSalesTestContext after, Guid adminId, string kind, decimal unitPrice, string name = "")
    {
        var input = kind == ServicePackageKinds.Maintenance
            ? new PackageInput(kind, name == "" ? "Panel care 12" : name, "Two visits a year", unitPrice, 12, [6, 12], null, null, null, null, true, null, "PER_PANEL")
            : new PackageInput(kind, name == "" ? "Panel storm shield" : name, "Storm and flood support", unitPrice, 12, null, ["STORM", "FLOOD"], 70m, 5_000_000m,
                8_000_000m, true, null, "PER_PANEL");
        var created = await after.Packages().CreatePackageAsync(adminId, input, None);
        Assert.True(created.Succeeded, created.Message);
        Assert.Equal(PackagePricingUnits.PerPanel, created.Value!.PricingUnit);
        return await after.Db.ServicePackages.AsNoTracking().SingleAsync(p => p.Id == created.Value.PackageId);
    }

    private static async Task<QuotationView> CurrentAsync(World w) => (await w.Ctx.Quotations().GetForTechnicianAsync(w.TechnicianId, w.QuotationId, None)).Value!;

    private static async Task<QuotationView> AddAsync(World w, Guid productId, decimal quantity)
    {
        var view = await CurrentAsync(w);
        var added = await w.Ctx.Quotations().AddCartLineAsync(w.TechnicianId, w.QuotationId, view.Revision, productId, quantity, None);
        Assert.True(added.Succeeded, added.Message);
        return added.Value!;
    }

    /// <summary>The 50,000,000 VND installation of the business example (10 panels).</summary>
    private static async Task<QuotationView> TenPanelInstallationAsync(World w, decimal panels = 10m)
    {
        await AddAsync(w, w.Cart.Catalog.Panel.Id, panels);
        await AddAsync(w, w.Cart.Catalog.Inverter.Id, 1m);
        await AddAsync(w, w.Cart.Catalog.Labor.Id, 1m);
        return await AddAsync(w, w.Cart.Catalog.Transport.Id, 1m);
    }

    private static async Task<OperationResult<QuotationView>> SetAsync(World w, string kind, Guid packageId, int? revision = null)
        => await w.Ctx.Quotations().SetCartServicePackageAsync(w.TechnicianId, w.QuotationId, kind, packageId, revision ?? (await CurrentAsync(w)).Revision, None);

    private static async Task<QuotationView> IssueAsync(World w)
    {
        var issued = await w.Ctx.Quotations().IssueCartAsync(w.TechnicianId, w.QuotationId, (await CurrentAsync(w)).Revision, None);
        Assert.True(issued.Succeeded, issued.Message + " " + System.Text.Json.JsonSerializer.Serialize(issued.Details));
        return issued.Value!;
    }

    private static async Task<Agreed> AgreeAsync(World w)
    {
        await w.Ctx.EnsureInstallationWarrantyOptionAsync();
        var manager = await w.Ctx.Scheduling.Inner.SeedUserAsync("Manager");
        var view = await CurrentAsync(w);
        var agreed = await w.Ctx.QuotationsWithPaymentPlan().RecordOnSiteDecisionAsync(w.TechnicianId, w.QuotationId,
            new OnSiteDecisionInput(OnSiteDecisions.Agreed, view.Revision, "Agreed with the disaster package"), None);
        Assert.True(agreed.Succeeded, agreed.Message);
        return new Agreed(w.Cart with { Quotation = agreed.Value! }, manager, agreed.Value!.ContractId!.Value);
    }

    /// <summary>The 57,000,000 VND bundle of the business example, agreed: one 30/70 contract.</summary>
    internal static async Task<(World W, Agreed Flow)> AgreedBundleAsync(bool withMaintenance = false, AfterSalesTestContext? on = null)
    {
        var w = await WorldAsync(on: on);
        await TenPanelInstallationAsync(w);
        Assert.True((await SetAsync(w, ServicePackageKinds.DisasterProtection, w.Disaster.Id)).Succeeded);
        if (withMaintenance) Assert.True((await SetAsync(w, ServicePackageKinds.Maintenance, w.Maintenance.Id)).Succeeded);
        await IssueAsync(w);
        return (w, await AgreeAsync(w));
    }

    /// <summary>
    /// Deposit (cash, Manager-confirmed), paper signed, schedule confirmed, installation started, <paramref name="assets"/> registered (a Manager approves
    /// the substitution when they differ from the contract), completion reported. Returns the project id.
    /// </summary>
    internal static async Task<Guid> CompletedWithAssetsAsync(World w, Agreed flow, Func<Guid, Guid, IReadOnlyList<AssetInput>> assets)
    {
        var ctx = w.Ctx;
        await PayCashAsync(ctx, flow, 1);
        Assert.True((await ctx.Schedules().ProposeAsync(flow.TechnicianId, false, flow.ContractId, InstallationScheduleTests.Plan(note: "Agreed in person"), None)).Succeeded);
        await SignOnPaperAsync(ctx, flow);
        var projectId = (await ctx.Schedules().GetForCustomerAsync(flow.CustomerId, flow.ContractId, None)).Value!.First().ProjectId!.Value;
        await InstallationTests.SatisfyPrerequisitesAsync(ctx, projectId, flow.Manager.Id);
        Assert.True((await ctx.Installations().StartProjectAsync(flow.TechnicianId, projectId, None)).Succeeded);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, projectId, flow.TechnicianId);
        var registered = await ctx.Installations().RegisterAssetsAsync(flow.TechnicianId, projectId, assets(w.Cart.Catalog.Panel.Id, w.Cart.Catalog.Inverter.Id), None);
        Assert.True(registered.Succeeded, registered.Message);
        if (registered.Value!.Equipment?.Status == EquipmentComparisonStatuses.ApprovalRequired)
        {
            var revision = (await ctx.Installations().GetForManagerAsync(projectId, None)).Value!.Revision;
            Assert.True((await ctx.Installations().ApproveSubstitutionAsync(flow.Manager.Id, projectId, revision, "Fewer panels fit the roof than quoted", None)).Succeeded);
        }
        var reported = await ctx.Installations().ReportCompletionAsync(flow.TechnicianId, projectId, "Array and inverter commissioned.", null, None);
        Assert.True(reported.Succeeded, reported.Message);
        return projectId;
    }

    internal static IReadOnlyList<AssetInput> Serialised(Guid panel, Guid inverter, int panels)
        => Enumerable.Range(1, panels).Select(i => new AssetInput("PANEL", panel, "Jinko", "Tiger Neo", $"PP-PNL-{Guid.NewGuid():N}"[..20], $"R{i}", null))
            .Append(new AssetInput("INVERTER", inverter, "Huawei", "SUN2000", $"PP-INV-{Guid.NewGuid():N}"[..20], null, null)).ToList();

    private static async Task<ContractServicePackage> CommitmentAsync(World w, Guid contractId, string kind = ServicePackageKinds.DisasterProtection)
        => await w.Ctx.Db.ContractServicePackages.AsNoTracking().SingleAsync(c => c.ContractId == contractId && c.Kind == kind);

    // ================================================================== catalog (C)

    [Fact]
    public async Task TC27_C_existing_packages_stay_per_installation_and_the_technician_sees_only_active_per_panel_packages()
    {
        var w = await WorldAsync();
        await using var _ = w.After;
        var legacy = await w.After.PackageAsync(w.AdminId, ServicePackageKinds.DisasterProtection, 2_000_000m);   // created without a pricing unit
        Assert.Equal(PackagePricingUnits.PerInstallationLegacy, legacy.PricingUnit);
        var inactive = await PerPanelPackageAsync(w.After, w.AdminId, ServicePackageKinds.DisasterProtection, 650_000m, "Retired shield");
        var off = await w.After.Packages().UpdatePackageAsync(w.AdminId, inactive.Id, new PackageInput(inactive.Kind, inactive.Name, null, 650_000m, 12, null,
            ["STORM"], 70m, 5_000_000m, 8_000_000m, false), inactive.Revision, None);
        Assert.True(off.Succeeded, off.Message);

        var list = (await w.After.Packages().ListSellablePerPanelAsync(null, None)).Value!;
        Assert.Equal(new[] { w.Disaster.Id, w.Maintenance.Id }.Order(), list.Select(p => p.PackageId).Order());
        Assert.All(list, p => Assert.Equal(PackagePricingUnits.PerPanel, p.PricingUnit));
        Assert.Equal(w.Disaster.Id, Assert.Single((await w.After.Packages().ListSellablePerPanelAsync("DISASTER_PROTECTION", None)).Value!).PackageId);
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await w.After.Packages().ListSellablePerPanelAsync("SOLAR", None)).ErrorCode);

        // The pricing unit is fixed: an existing price is never reinterpreted (a new package is created instead).
        var reinterpret = await w.After.Packages().UpdatePackageAsync(w.AdminId, legacy.Id, new PackageInput(legacy.Kind, legacy.Name, null, legacy.Price, 12, null,
            ["STORM", "FLOOD"], 70m, 5_000_000m, 8_000_000m, true, null, "PER_PANEL"), legacy.Revision, None);
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, reinterpret.ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await w.After.Packages().CreatePackageAsync(w.AdminId, new PackageInput("MAINTENANCE", "X", null, 1m, 12,
            [6], null, null, null, null, true, null, "PER_KWP"), None)).ErrorCode);
    }

    // ================================================================== quotation (D)

    [Fact]
    public async Task TC01_TC26_a_quotation_without_packages_keeps_its_totals_and_its_contract_equals_the_installation()
    {
        var w = await WorldAsync(taxPercent: 8m);
        await using var _ = w.After;
        var view = await TenPanelInstallationAsync(w);
        Assert.Equal((10, 0m), (view.PanelQuantity, view.ServicePackagesTotal));
        Assert.Empty(view.ServicePackages!);
        Assert.Equal(view.TotalAmount, view.GrandTotal);
        Assert.Equal(view.TotalAmount, view.InstallationTotal);
        await IssueAsync(w);
        var flow = await AgreeAsync(w);
        var contract = await w.Ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId);
        Assert.Equal(54_000_000m, contract.TotalAmount);   // 50,000,000 + 8 % tax: unchanged rules
        Assert.Null(contract.InstallationTotal);
        Assert.Null(contract.ServicePackagesTotal);
        Assert.DoesNotContain("servicePackages", contract.PricingSnapshot);
        Assert.Equal((16_200_000m, 37_800_000m), ((await InstallmentAsync(w.Ctx, flow.ContractId, 1)).Amount, (await InstallmentAsync(w.Ctx, flow.ContractId, 2)).Amount));
        Assert.Empty(await w.Ctx.Db.ContractServicePackages.AsNoTracking().ToListAsync());
        Assert.Null((await StaffViewAsync(w.Ctx, flow.ContractId)).PaymentPlan!.Installments[0].Allocation);
    }

    [Fact]
    public async Task TC02_one_disaster_package_is_quantity_times_the_catalog_price_without_the_quotation_tax()
    {
        var w = await WorldAsync(taxPercent: 8m);
        await using var _ = w.After;
        await TenPanelInstallationAsync(w);
        var set = await SetAsync(w, ServicePackageKinds.DisasterProtection, w.Disaster.Id);
        Assert.True(set.Succeeded, set.Message);
        var view = set.Value!;
        var package = Assert.Single(view.ServicePackages!);
        Assert.Equal((ServicePackageKinds.DisasterProtection, "PER_PANEL", 700_000m, 10, 7_000_000m), (package.Kind, package.PricingUnit, package.UnitPrice, package.Quantity, package.TotalAmount));
        // totalAmount keeps its meaning (installation incl. its tax); packages are catalog final prices (no 8 % added again).
        Assert.Equal((54_000_000m, 54_000_000m, 7_000_000m, 0m, 7_000_000m, 61_000_000m),
            (view.TotalAmount, view.InstallationTotal!.Value, view.DisasterPackagesTotal, view.MaintenancePackagesTotal, view.ServicePackagesTotal, view.GrandTotal!.Value));
        Assert.Equal(10, view.PanelQuantity);
        Assert.Equal("VND", view.Currency);
    }

    [Fact]
    public async Task TC03_TC04_both_kinds_over_several_panel_models_are_itemized_and_counted_once()
    {
        var w = await WorldAsync();
        await using var _ = w.After;
        var secondPanel = await w.Ctx.ProductAsync($"PNL-B-{Guid.NewGuid():N}"[..20], 2_500_000m);   // another SOLAR_PANEL model
        await AddAsync(w, w.Cart.Catalog.Panel.Id, 6m);
        await AddAsync(w, secondPanel.Id, 4m);
        await AddAsync(w, w.Cart.Catalog.Inverter.Id, 1m);   // never counted as a panel
        await AddAsync(w, w.Cart.Catalog.Labor.Id, 2m);       // services never counted either
        Assert.True((await SetAsync(w, ServicePackageKinds.DisasterProtection, w.Disaster.Id)).Succeeded);
        var view = (await SetAsync(w, ServicePackageKinds.Maintenance, w.Maintenance.Id)).Value!;
        Assert.Equal(10, view.PanelQuantity);
        Assert.Equal(new[] { (ServicePackageKinds.DisasterProtection, 10, 7_000_000m), (ServicePackageKinds.Maintenance, 10, 3_000_000m) },
            view.ServicePackages!.OrderBy(p => p.Kind).Select(p => (p.Kind, p.Quantity, p.TotalAmount)));
        Assert.Equal((3_000_000m, 7_000_000m, 10_000_000m), (view.MaintenancePackagesTotal, view.DisasterPackagesTotal, view.ServicePackagesTotal));
        Assert.Equal(view.TotalAmount + 10_000_000m, view.GrandTotal);
        // At most one package per kind (unique per quotation and kind).
        Assert.Equal(2, await w.Ctx.Db.QuotationServicePackages.CountAsync(p => p.QuotationId == w.QuotationId));
    }

    [Fact]
    public async Task TC05_fractional_or_missing_panels_are_refused_for_a_per_panel_package()
    {
        var w = await WorldAsync();
        await using var _ = w.After;
        await AddAsync(w, w.Cart.Catalog.Inverter.Id, 1m);
        var noPanels = await SetAsync(w, ServicePackageKinds.DisasterProtection, w.Disaster.Id);
        Assert.Equal(QuotationErrorCodes.NoEligiblePanels, noPanels.ErrorCode);
        await AddAsync(w, w.Cart.Catalog.Panel.Id, 2.5m);
        var fractional = await SetAsync(w, ServicePackageKinds.DisasterProtection, w.Disaster.Id);
        Assert.Equal(QuotationErrorCodes.InvalidPanelQuantity, fractional.ErrorCode);
        Assert.Empty(await w.Ctx.Db.QuotationServicePackages.AsNoTracking().ToListAsync());
        // Without packages a fractional line stays possible (the existing cart rule is unchanged).
        Assert.Null((await CurrentAsync(w)).PanelQuantity);
    }

    [Fact]
    public async Task TC06_changing_the_panels_recomputes_the_packages_atomically_and_never_leaves_a_package_without_panels()
    {
        var w = await WorldAsync();
        await using var _ = w.After;
        var view = await TenPanelInstallationAsync(w);
        view = (await SetAsync(w, ServicePackageKinds.DisasterProtection, w.Disaster.Id)).Value!;
        var panelLine = view.Lines.Single(l => l.ProductId == w.Cart.Catalog.Panel.Id);

        var eight = await w.Ctx.Quotations().UpdateCartLineAsync(w.TechnicianId, w.QuotationId, panelLine.LineId, view.Revision, 8m, None);
        Assert.True(eight.Succeeded, eight.Message);
        Assert.Equal((8, 8, 5_600_000m, view.Revision + 1), (eight.Value!.PanelQuantity!.Value, eight.Value.ServicePackages![0].Quantity,
            eight.Value.ServicePackagesTotal, eight.Value.Revision));
        Assert.Equal(eight.Value.TotalAmount + 5_600_000m, eight.Value.GrandTotal);

        // A fractional panel count or removing every panel is refused while a per-panel package is selected (nothing is changed).
        Assert.Equal(QuotationErrorCodes.InvalidPanelQuantity, (await w.Ctx.Quotations().UpdateCartLineAsync(w.TechnicianId, w.QuotationId, panelLine.LineId,
            eight.Value.Revision, 7.5m, None)).ErrorCode);
        var removed = await w.Ctx.Quotations().RemoveCartLineAsync(w.TechnicianId, w.QuotationId, panelLine.LineId, eight.Value.Revision, None);
        Assert.Equal(QuotationErrorCodes.NoEligiblePanels, removed.ErrorCode);
        var unchanged = await CurrentAsync(w);
        Assert.Equal((8, 5_600_000m, eight.Value.Revision), (unchanged.PanelQuantity!.Value, unchanged.ServicePackagesTotal, unchanged.Revision));
    }

    [Fact]
    public async Task TC07_TC28_a_package_is_replaced_or_removed_with_revision_checks_and_stale_writes_are_refused()
    {
        var w = await WorldAsync();
        await using var _ = w.After;
        await TenPanelInstallationAsync(w);
        var cheaper = await PerPanelPackageAsync(w.After, w.AdminId, ServicePackageKinds.DisasterProtection, 500_000m, "Basic shield");
        var first = (await SetAsync(w, ServicePackageKinds.DisasterProtection, w.Disaster.Id)).Value!;

        // Two edits made from the same revision: the second one is refused (never silently overwrites the first).
        var replaced = await SetAsync(w, ServicePackageKinds.DisasterProtection, cheaper.Id, first.Revision);
        Assert.True(replaced.Succeeded, replaced.Message);
        Assert.Equal(QuotationErrorCodes.StaleRevision, (await SetAsync(w, ServicePackageKinds.Maintenance, w.Maintenance.Id, first.Revision)).ErrorCode);
        var only = Assert.Single(replaced.Value!.ServicePackages!);
        Assert.Equal((cheaper.Id, 500_000m, 5_000_000m), (only.PackageId, only.UnitPrice, only.TotalAmount));

        Assert.Equal(QuotationErrorCodes.ServicePackageNotFound, (await w.Ctx.Quotations().RemoveCartServicePackageAsync(w.TechnicianId, w.QuotationId,
            ServicePackageKinds.Maintenance, replaced.Value.Revision, None)).ErrorCode);
        var removed = await w.Ctx.Quotations().RemoveCartServicePackageAsync(w.TechnicianId, w.QuotationId, ServicePackageKinds.DisasterProtection, replaced.Value.Revision, None);
        Assert.True(removed.Succeeded, removed.Message);
        Assert.Equal((0m, removed.Value!.TotalAmount), (removed.Value.ServicePackagesTotal, removed.Value.GrandTotal!.Value));
        Assert.Contains((await w.Ctx.Db.QuotationEvents.AsNoTracking().Where(e => e.QuotationId == w.QuotationId).Select(e => e.Detail).ToListAsync()),
            d => d != null && d.Contains("replaces Panel storm shield"));
    }

    [Fact]
    public async Task Invalid_package_choices_are_refused_with_stable_codes()
    {
        var w = await WorldAsync();
        await using var _ = w.After;
        await TenPanelInstallationAsync(w);
        var legacy = await w.After.PackageAsync(w.AdminId, ServicePackageKinds.DisasterProtection, 2_000_000m);
        var q = w.Ctx.Quotations();
        var revision = (await CurrentAsync(w)).Revision;
        Assert.Equal(QuotationErrorCodes.ServicePackageIncompatible, (await SetAsync(w, ServicePackageKinds.DisasterProtection, legacy.Id)).ErrorCode);       // legacy unit
        Assert.Equal(QuotationErrorCodes.ServicePackageIncompatible, (await SetAsync(w, ServicePackageKinds.DisasterProtection, w.Maintenance.Id)).ErrorCode); // wrong kind
        Assert.Equal(QuotationErrorCodes.ServicePackageNotFound, (await SetAsync(w, ServicePackageKinds.DisasterProtection, Guid.NewGuid())).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await q.SetCartServicePackageAsync(w.TechnicianId, w.QuotationId, "WARRANTY", w.Disaster.Id, revision, None)).ErrorCode);
        // Another technician (not the one who surveyed) cannot see or change this cart.
        var other = await w.Ctx.Scheduling.SeedTechnicianAsync("Other technician", SchedulingTestContext.BinhTan);
        Assert.Equal(QuotationErrorCodes.NotFound, (await q.SetCartServicePackageAsync(other.Id, w.QuotationId, ServicePackageKinds.DisasterProtection, w.Disaster.Id,
            revision, None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.NotFound, (await q.RemoveCartServicePackageAsync(other.Id, w.QuotationId, ServicePackageKinds.DisasterProtection, revision, None)).ErrorCode);
        // Issued quotations are frozen.
        Assert.True((await SetAsync(w, ServicePackageKinds.DisasterProtection, w.Disaster.Id)).Succeeded);
        var issued = await IssueAsync(w);
        Assert.Equal(QuotationErrorCodes.NotDraft, (await SetAsync(w, ServicePackageKinds.Maintenance, w.Maintenance.Id, issued.Revision)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.NotDraft, (await q.RemoveCartServicePackageAsync(w.TechnicianId, w.QuotationId, ServicePackageKinds.DisasterProtection,
            issued.Revision, None)).ErrorCode);
    }

    [Fact]
    public async Task TC08_TC09_TC10_the_issue_reprices_from_the_catalog_then_freezes_and_an_inactive_package_blocks_the_issue()
    {
        var w = await WorldAsync();
        await using var _ = w.After;
        await TenPanelInstallationAsync(w);
        Assert.True((await SetAsync(w, ServicePackageKinds.DisasterProtection, w.Disaster.Id)).Succeeded);
        var packages = w.After.Packages();
        async Task ReviseAsync(decimal price, bool active)
        {
            var current = await w.Ctx.Db.ServicePackages.AsNoTracking().SingleAsync(p => p.Id == w.Disaster.Id);
            var updated = await packages.UpdatePackageAsync(w.AdminId, current.Id, new PackageInput(current.Kind, current.Name, current.Description, price, 12, null,
                ["STORM", "FLOOD"], 70m, 5_000_000m, 8_000_000m, active), current.Revision, None);
            Assert.True(updated.Succeeded, updated.Message);
        }

        // TC10: deactivated before the issue → the issue is refused and the draft stays a draft.
        await ReviseAsync(700_000m, active: false);
        var refused = await w.Ctx.Quotations().IssueCartAsync(w.TechnicianId, w.QuotationId, (await CurrentAsync(w)).Revision, None);
        Assert.Equal(QuotationErrorCodes.ServicePackageInactive, refused.ErrorCode);
        Assert.Equal(SmartSolar.Modules.Quotations.QuotationStatuses.Draft, (await CurrentAsync(w)).Status);

        // TC08: price changed before the issue → the issue snapshots today's catalog price (and revision).
        await ReviseAsync(800_000m, active: true);
        var issued = await IssueAsync(w);
        var frozen = Assert.Single(issued.ServicePackages!);
        Assert.Equal((800_000m, 8_000_000m, 58_000_000m), (frozen.UnitPrice, frozen.TotalAmount, issued.GrandTotal!.Value));
        Assert.Equal((await w.Ctx.Db.ServicePackages.AsNoTracking().SingleAsync(p => p.Id == w.Disaster.Id)).Revision, frozen.CatalogRevision);

        // TC09: price changed after the issue → the issued quotation and the contract keep the frozen amounts.
        await ReviseAsync(1_000_000m, active: true);
        Assert.Equal(800_000m, Assert.Single((await CurrentAsync(w)).ServicePackages!).UnitPrice);
        var flow = await AgreeAsync(w);
        var contract = await w.Ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId);
        Assert.Equal((58_000_000m, 50_000_000m, 8_000_000m), (contract.TotalAmount, contract.InstallationTotal!.Value, contract.ServicePackagesTotal!.Value));
        Assert.Equal(800_000m, (await CommitmentAsync(w, flow.ContractId)).UnitPrice);
    }

    [Fact]
    public async Task A_revision_carries_the_packages_and_its_issue_revalidates_them()
    {
        var w = await WorldAsync();
        await using var _ = w.After;
        await TenPanelInstallationAsync(w);
        Assert.True((await SetAsync(w, ServicePackageKinds.DisasterProtection, w.Disaster.Id)).Succeeded);
        var issued = await IssueAsync(w);
        var revision = await w.Ctx.Quotations().CreateCartRevisionAsync(w.TechnicianId, issued.QuotationId, None);
        Assert.True(revision.Succeeded, revision.Message);
        var copy = Assert.Single(revision.Value!.ServicePackages!);
        Assert.Equal((w.Disaster.Id, 10, 7_000_000m), (copy.PackageId, copy.Quantity, copy.TotalAmount));
        // The previous ISSUED version is untouched.
        Assert.Equal(7_000_000m, Assert.Single(await w.Ctx.Db.QuotationServicePackages.AsNoTracking().Where(p => p.QuotationId == issued.QuotationId).ToListAsync()).TotalAmount);
    }

    // ================================================================== contract and 30/70 (E, F)

    [Fact]
    public async Task TC11_TC12_the_contract_total_is_installation_plus_packages_and_30_70_applies_to_the_whole()
    {
        var (w, flow) = await AgreedBundleAsync();
        await using var _ = w.After;
        var contract = await w.Ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId);
        Assert.Equal((57_000_000m, 50_000_000m, 7_000_000m), (contract.TotalAmount, contract.InstallationTotal!.Value, contract.ServicePackagesTotal!.Value));
        var deposit = await InstallmentAsync(w.Ctx, flow.ContractId, 1);
        var balance = await InstallmentAsync(w.Ctx, flow.ContractId, 2);
        Assert.Equal((17_100_000m, 39_900_000m), (deposit.Amount, balance.Amount));
        Assert.Equal(contract.TotalAmount, deposit.Amount + balance.Amount);

        // The breakdown is part of the immutable snapshot and its canonical hash; the terms state it too.
        Assert.Contains("\"servicePackagesTotal\":7000000", contract.PricingSnapshot);
        Assert.Contains("\"grandTotal\":57000000", contract.PricingSnapshot);
        Assert.Equal(ContractHandlers.CanonicalHash(contract), contract.CanonicalHash);
        Assert.Contains("700.000 VND/tấm pin × 10 tấm", contract.Terms!.Replace(',', '.'));
        var commitment = await CommitmentAsync(w, flow.ContractId);
        Assert.Equal((ContractServicePackageStatuses.Committed, 10, 7_000_000m, 2_100_000m, 4_900_000m), (commitment.Status, commitment.Quantity, commitment.TotalAmount,
            commitment.DepositAllocation, commitment.BalanceAllocation));

        // Staff / customer view: grand total, breakdown and per-installment allocation (installation 30 % + package 30 %, then the 70 %s).
        var view = await StaffViewAsync(w.Ctx, flow.ContractId);
        Assert.Equal((57_000_000m, 50_000_000m, 7_000_000m), (view.TotalAmount, view.InstallationTotal!.Value, view.ServicePackagesTotal!.Value));
        Assert.Equal(new[] { (17_100_000m, 15_000_000m, 2_100_000m), (39_900_000m, 35_000_000m, 4_900_000m) },
            view.PaymentPlan!.Installments.Select(i => (i.Amount, i.Allocation!.Installation, i.Allocation.DisasterPackages)));
        Assert.Equal(57_000_000m, view.PaymentPlan.Total);
        Assert.Single(view.ServicePackages!);

        // TC12: the deposit checkout is exactly installment 1 (one line, no extra package line).
        var opened = await w.Ctx.InstallmentPayments().StartAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, "ONLINE", None);
        Assert.True(opened.Succeeded, opened.Message);
        Assert.Equal((17_100_000m, 17_100_000m, 2_100_000m), (opened.Value!.Amount, opened.Value.Current!.Amount, opened.Value.Allocation!.DisasterPackages));
        var lines = await w.Ctx.Db.PaymentAttemptItems.AsNoTracking().Where(l => l.PaymentAttemptId == opened.Value.Current.PaymentAttemptId).ToListAsync();
        Assert.Equal((PaymentItemTypes.ContractInstallment, 17_100_000m), (Assert.Single(lines).ItemType, lines[0].Amount));
    }

    [Fact]
    public async Task Rounding_remainders_are_absorbed_once_by_the_installation_share()
    {
        var split = PaymentPlanCalculator.Split(50_000_005m + 3_000_003m + 1_111_111m)!;
        var shares = PaymentPlanCalculator.Allocate(split, [(ServicePackageKindNames.Maintenance, 3_000_003m), (ServicePackageKindNames.DisasterProtection, 1_111_111m)])!;
        Assert.All(shares, s => Assert.Equal(s.Amount, s.Installation + s.MaintenancePackages + s.DisasterPackages));
        Assert.Equal(50_000_005m, shares.Sum(s => s.Installation));
        Assert.Equal(3_000_003m, shares.Sum(s => s.MaintenancePackages));
        Assert.Equal(1_111_111m, shares.Sum(s => s.DisasterPackages));
        Assert.Equal(split.Total, shares.Sum(s => s.Amount));
    }

    [Fact]
    public async Task A_withdrawal_before_the_deposit_discards_the_package_commitments()
    {
        var (w, flow) = await AgreedBundleAsync();
        await using var _ = w.After;
        var quotation = await w.Ctx.Db.Quotations.AsNoTracking().SingleAsync(q => q.Id == w.QuotationId);
        var withdrawn = await w.Ctx.QuotationsWithPaymentPlan().RecordOnSiteDecisionAsync(flow.TechnicianId, quotation.Id,
            new OnSiteDecisionInput(OnSiteDecisions.Declined, quotation.Revision, "Customer changed their mind"), None);
        Assert.True(withdrawn.Succeeded, withdrawn.Message);
        var commitment = await CommitmentAsync(w, flow.ContractId);
        Assert.Equal(ContractServicePackageStatuses.Discarded, commitment.Status);
        Assert.Equal(ContractStatuses.Discarded, (await w.Ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId)).Status);
        Assert.Empty(await w.Ctx.Db.ServiceSubscriptions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Legacy_contract_creation_paths_refuse_a_quotation_with_packages()
    {
        var w = await WorldAsync();
        await using var _ = w.After;
        await TenPanelInstallationAsync(w);
        Assert.True((await SetAsync(w, ServicePackageKinds.DisasterProtection, w.Disaster.Id)).Succeeded);
        await IssueAsync(w);
        await SimplifiedFlowFixture.AgreedAsync(w.Ctx, w.Cart with { Quotation = await CurrentAsync(w) });   // pre-30/70 wiring: no contract at agreement
        var manager = await w.Ctx.Scheduling.Inner.SeedUserAsync("Manager");
        Assert.Equal(PaymentPlanErrorCodes.NotApplicable, (await w.Ctx.Contracts().CreateAsync(manager.Id, w.QuotationId, "Terms", None)).ErrorCode);
    }

    // ================================================================== payments and activation (F, G, H)

    [Fact]
    public async Task TC13_TC14_TC16_TC17_TC25_deposit_grants_nothing_full_payment_and_handover_provision_once_and_visits_are_not_multiplied()
    {
        var (w, flow) = await AgreedBundleAsync(withMaintenance: true);
        await using var _ = w.After;
        var ctx = w.Ctx;
        var contract = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId);
        Assert.Equal((60_000_000m, 18_000_000m), (contract.TotalAmount, (await InstallmentAsync(ctx, flow.ContractId, 1)).Amount));

        // TC14: a cash deposit is recorded by a Manager with the exact amount; a different amount is refused.
        var opened = await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, "CASH", None);
        Assert.True(opened.Succeeded, opened.Message);
        var evidence = await ctx.VerifiedFileAsync(flow.Manager.Id, FilePurposes.PaymentEvidence);
        var wrong = await ctx.PaymentsWithCash().ConfirmCashAttemptAsync(flow.Manager.Id, opened.Value!.Current!.PaymentAttemptId,
            new CashConfirmation(17_999_999m, "PT-WRONG", evidence, null), None);
        Assert.False(wrong.Succeeded);
        var right = await ctx.PaymentsWithCash().ConfirmCashAttemptAsync(flow.Manager.Id, opened.Value.Current.PaymentAttemptId,
            new CashConfirmation(18_000_000m, $"PT-{Guid.NewGuid():N}"[..20], evidence, null), None);
        Assert.True(right.Succeeded, right.Message);

        // TC13: deposit verified → AWAITING_SIGNATURE; the packages stay commitments, nothing is active.
        Assert.Equal(ContractStatuses.AwaitingSignature, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId)).Status);
        Assert.Empty(await ctx.Db.ServiceSubscriptions.AsNoTracking().ToListAsync());
        Assert.All(await ctx.Db.ContractServicePackages.AsNoTracking().ToListAsync(), c => Assert.Equal(ContractServicePackageStatuses.Committed, c.Status));

        Assert.True((await ctx.Schedules().ProposeAsync(flow.TechnicianId, false, flow.ContractId, InstallationScheduleTests.Plan(note: "Agreed in person"), None)).Succeeded);
        await SignOnPaperAsync(ctx, flow);
        var projectId = (await ctx.Schedules().GetForCustomerAsync(flow.CustomerId, flow.ContractId, None)).Value!.First().ProjectId!.Value;
        await InstallationTests.SatisfyPrerequisitesAsync(ctx, projectId, flow.Manager.Id);
        Assert.True((await ctx.Installations().StartProjectAsync(flow.TechnicianId, projectId, None)).Succeeded);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, projectId, flow.TechnicianId);
        Assert.True((await InstallationTests.RegisterPanelAndInverterAsync(ctx, projectId, flow.TechnicianId)).Succeeded);
        Assert.True((await ctx.Installations().ReportCompletionAsync(flow.TechnicianId, projectId, "Done.", null, None)).Succeeded);
        await AcceptAsync(ctx, flow, projectId);
        Assert.Empty(await ctx.Db.ServiceSubscriptions.AsNoTracking().ToListAsync());   // accepted but the 70 % is not paid yet

        // TC16: verified balance (PayOS) → fully paid → each package provisioned once against the 10 real panels.
        var balance = await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Customer, flow.CustomerId, flow.ContractId, 2, "ONLINE", None);
        Assert.True(balance.Succeeded, balance.Message);
        Assert.Equal(42_000_000m, balance.Value!.Amount);
        var qr = balance.Value.Current!;
        var webhook = ctx.PayOs.Webhook(qr.OrderCode, (long)qr.Amount, $"FT-BAL-{qr.OrderCode}", transactionAt: ctx.Clock.GetUtcNow());
        Assert.Equal(WebhookOutcomes.Applied, (await ctx.PaymentsWithSchedule().HandleWebhookAsync(webhook, None)).Outcome);
        // TC17: the same provider event again changes nothing (no second settlement, no second subscription).
        Assert.Equal(WebhookOutcomes.Duplicate, (await ctx.PaymentsWithSchedule().HandleWebhookAsync(webhook, None)).Outcome);
        ctx.Db.ChangeTracker.Clear();

        Assert.Equal(ContractStatuses.Paid, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId)).Status);
        var subscriptions = await ctx.Db.ServiceSubscriptions.AsNoTracking().ToListAsync();
        Assert.Equal(2, subscriptions.Count);
        Assert.All(subscriptions, s => Assert.Equal((CoverageScopes.Panel, 10, SubscriptionStatuses.Active), (s.CoverageScope, s.CoveredUnits!.Value, s.Status)));
        var disaster = subscriptions.Single(s => s.Kind == ServicePackageKinds.DisasterProtection);
        Assert.Equal((7_000_000m, 700_000m, (Guid?)null), (disaster.Price, disaster.UnitPrice!.Value, disaster.CurrentPaymentAttemptId));
        var handoverAt = (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync(p => p.Id == projectId)).HandoverAt!.Value;
        Assert.True(disaster.StartsAt >= handoverAt && disaster.StartsAt >= disaster.PaidAt);
        Assert.Equal(disaster.StartsAt!.Value.AddDays(7), disaster.CoverageStartsAt);   // DisasterWaitingDays (default 7) kept
        Assert.Equal(10, (await ctx.Db.ServiceCoverageAllocations.AsNoTracking().Where(a => a.SubscriptionId == disaster.Id).ToListAsync()).Sum(a => a.UnitCount));
        Assert.All(await ctx.Db.ContractServicePackages.AsNoTracking().ToListAsync(), c => Assert.Equal(ContractServicePackageStatuses.Provisioned, c.Status));

        // TC12: verified money for the contract = 60,000,000 exactly (no package checkout was created).
        var paid = await ctx.Db.PaymentAttempts.AsNoTracking().Where(a => a.Status == PaymentStatuses.Paid && a.CustomerUserId == flow.CustomerId).ToListAsync();
        Assert.Equal(60_000_000m, paid.Sum(a => a.Amount));

        // TC25: maintenance for 10 panels = the package's 2 visits, not 20.
        var maintenance = subscriptions.Single(s => s.Kind == ServicePackageKinds.Maintenance);
        Assert.Equal(2, await ctx.Db.MaintenanceEntitlements.CountAsync(e => e.SubscriptionId == maintenance.Id));

        // Provisioning again (retry / repeated hook) is a no-op.
        await using (var tx = await ctx.Db.Database.BeginTransactionAsync())
        {
            await ctx.PanelCoverage().ContractFullyPaidAsync(flow.ContractId, ctx.Clock.GetUtcNow(), ctx.Clock.GetUtcNow(), None);
            await ctx.Db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        Assert.Equal(2, await ctx.Db.ServiceSubscriptions.CountAsync());

        // Customer view: the PANEL scope and the real panels it covers.
        var mine = await w.After.Packages().ListForCustomerAsync(flow.CustomerId, None);
        var view = mine.Single(s => s.Kind == ServicePackageKinds.DisasterProtection);
        Assert.Equal((CoverageScopes.Panel, 10, 10), (view.CoverageScope, view.CoveredUnits!.Value, view.CoveredPanels!.Sum(p => p.UnitCount)));
    }

    [Fact]
    public async Task TC15_TC29_rework_keeps_the_balance_closed_and_an_overdue_balance_opens_a_collection_case_for_the_full_70_percent()
    {
        var (w, flow) = await AgreedBundleAsync();
        await using var _ = w.After;
        var ctx = w.Ctx;
        var projectId = await CompletedWithAssetsAsync(w, flow, (panel, inverter) => Serialised(panel, inverter, 10));
        var photo = await ctx.VerifiedFileAsync(flow.CustomerId, FilePurposes.AcceptanceEvidence);
        Assert.True((await ctx.Installations().DecideAcceptanceAsync(flow.CustomerId, projectId,
            new CustomerAcceptanceInput(CustomerAcceptanceDecisions.RequestRework, "One panel is cracked", [photo], null), None)).Succeeded);
        Assert.Equal(InstallmentStatuses.Pending, (await InstallmentAsync(ctx, flow.ContractId, 2)).Status);
        Assert.Equal(PaymentPlanErrorCodes.InstallmentNotPayable,
            (await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Customer, flow.CustomerId, flow.ContractId, 2, "ONLINE", None)).ErrorCode);

        await InstallationTests.AddCompletionEvidenceAsync(ctx, projectId, flow.TechnicianId);
        Assert.True((await ctx.Installations().ReportCompletionAsync(flow.TechnicianId, projectId, "Panel replaced.", null, None)).Succeeded);
        await AcceptAsync(ctx, flow, projectId);
        var balance = await InstallmentAsync(ctx, flow.ContractId, 2);
        ctx.Clock.Now = balance.DueAt!.Value.AddMinutes(1);
        Assert.Equal(1, await ctx.Ledger().SweepOverdueAsync(50, None));
        Assert.Equal(39_900_000m, (await ctx.Db.ContractCollectionCases.AsNoTracking().SingleAsync()).AmountDue);
        Assert.Empty(await ctx.Db.ServiceSubscriptions.AsNoTracking().ToListAsync());   // nothing granted while the balance is unpaid
    }

    [Fact]
    public async Task TC19_fewer_installed_panels_than_contracted_need_reconciliation_without_coverage_or_money_change()
    {
        var (w, flow) = await AgreedBundleAsync();
        await using var _ = w.After;
        var ctx = w.Ctx;
        var manager = await ctx.Scheduling.SeedStaffAsync("Reconciliation manager", SmartSolar.Modules.Identity.Constants.RoleCodes.Manager);
        var projectId = await CompletedWithAssetsAsync(w, flow, (panel, inverter) => Serialised(panel, inverter, 9));
        await AcceptAsync(ctx, flow, projectId);
        await PayCashAsync(ctx, flow, 2);
        ctx.Db.ChangeTracker.Clear();

        var contract = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId);
        Assert.Equal((ContractStatuses.Paid, 57_000_000m), (contract.Status, contract.TotalAmount));
        var commitment = await CommitmentAsync(w, flow.ContractId);
        Assert.Equal(ContractServicePackageStatuses.ReconciliationRequired, commitment.Status);
        Assert.Contains("Contracted 10 panel unit(s) but 9", commitment.StatusReason);
        Assert.Empty(await ctx.Db.ServiceSubscriptions.AsNoTracking().ToListAsync());
        Assert.Empty(await ctx.Db.ServiceCoverageAllocations.AsNoTracking().ToListAsync());
        Assert.Equal((17_100_000m, 39_900_000m), ((await InstallmentAsync(ctx, flow.ContractId, 1)).PaidAmount!.Value, (await InstallmentAsync(ctx, flow.ContractId, 2)).PaidAmount!.Value));
        Assert.Contains(await ctx.Db.AfterSalesNotifications.AsNoTracking().Where(n => n.RecipientUserId == manager.Id).Select(n => n.Type).ToListAsync(),
            t => t == "PACKAGE_RECONCILIATION");

        // A retry while the record is still wrong changes nothing (no duplicate audit); the staff list exposes it.
        var events = await ctx.Db.AfterSalesEvents.CountAsync(e => e.SubjectId == commitment.Id);
        var retried = await ctx.PanelCoverage().RetryReconciliationAsync(manager.Id, commitment.Id, None);
        Assert.True(retried.Succeeded, retried.Message);
        Assert.Equal(ContractServicePackageStatuses.ReconciliationRequired, retried.Value!.Status);
        Assert.Equal(events, await ctx.Db.AfterSalesEvents.CountAsync(e => e.SubjectId == commitment.Id));
        var listed = await ctx.PanelCoverage().PageBundledAsync("RECONCILIATION_REQUIRED", new Modules.Common.Paging.PageRequest(1, 20), None);
        Assert.Equal(commitment.Id, Assert.Single(listed.Items).CommitmentId);

        // Retail purchase of the same kind is refused while the bundled package waits (never sold twice).
        var panelAsset = await ctx.Db.InstalledAssets.AsNoTracking().FirstAsync(a => a.ProjectId == projectId && a.AssetType == AssetTypes.Panel);
        var retail = await w.After.Packages().PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, null, w.Disaster.Id, false, null, 1,
            [new AssetUnitsInput(panelAsset.Id, 1)]), None);
        Assert.Equal(AfterSalesErrorCodes.TermOverlap, retail.ErrorCode);
    }

    [Fact]
    public async Task TC20_an_unserialised_lot_is_covered_by_unit_count_without_any_invented_serial()
    {
        var (w, flow) = await AgreedBundleAsync();
        await using var _ = w.After;
        var ctx = w.Ctx;
        var projectId = await CompletedWithAssetsAsync(w, flow, (panel, inverter) =>
            [new AssetInput("PANEL", panel, "Jinko", "Tiger Neo", null, "ROOF-A", null, 10), new AssetInput("INVERTER", inverter, "Huawei", "SUN2000", "LOT-INV-0001", null, null)]);
        await AcceptAsync(ctx, flow, projectId);
        await PayCashAsync(ctx, flow, 2);
        ctx.Db.ChangeTracker.Clear();
        var subscription = await ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync();
        var allocation = await ctx.Db.ServiceCoverageAllocations.AsNoTracking().SingleAsync();
        var lot = await ctx.Db.InstalledAssets.AsNoTracking().SingleAsync(a => a.Id == allocation.InstalledAssetId);
        Assert.Equal((10, 10, (string?)null), (allocation.UnitCount, lot.Quantity, lot.SerialNumber));
        Assert.Equal(subscription.Id, allocation.SubscriptionId);
        Assert.Equal(0, await ctx.Db.InstalledAssets.CountAsync(a => a.ProjectId == projectId && a.AssetType == AssetTypes.Panel && a.SerialNumber != null));
    }

    // ================================================================== retail, overlap, claims (I, G4, H4)

    /// <summary>A bundled 10-panel disaster package fully paid, handed over and provisioned (10 serialised panels).</summary>
    internal static async Task<(World W, Agreed Flow, Guid ProjectId, ServiceSubscription Bundled)> ProvisionedAsync(AfterSalesTestContext? on = null)
    {
        var (w, flow) = await AgreedBundleAsync(on: on);
        var projectId = await CompletedWithAssetsAsync(w, flow, (panel, inverter) => Serialised(panel, inverter, 10));
        await AcceptAsync(w.Ctx, flow, projectId);
        await PayCashAsync(w.Ctx, flow, 2);
        w.Ctx.Db.ChangeTracker.Clear();
        return (w, flow, projectId, await w.Ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync());
    }

    [Fact]
    public async Task TC21_TC22_retail_top_up_is_100_percent_upfront_for_eligible_units_and_covered_units_cannot_be_bought_again()
    {
        var (w, flow, projectId, bundled) = await ProvisionedAsync();
        await using var _ = w.After;
        var ctx = w.Ctx;
        var packages = w.After.Packages();
        var contractTotal = (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId)).TotalAmount;
        var panels = await ctx.Db.InstalledAssets.AsNoTracking().Where(a => a.ProjectId == projectId && a.AssetType == AssetTypes.Panel).OrderBy(a => a.PositionCode).ToListAsync();

        // Eligibility: every panel already has disaster coverage; maintenance is fully available.
        var eligibility = (await ctx.PanelCoverage().EligibilityAsync(flow.CustomerId, flow.ContractId, None)).Value!;
        Assert.True(eligibility.CanPurchase);
        Assert.Equal((10, 10, 0), Kind(eligibility, ServicePackageKinds.DisasterProtection));
        Assert.Equal((10, 0, 10), Kind(eligibility, ServicePackageKinds.Maintenance));
        Assert.Contains(bundled.Id, eligibility.Kinds.Single(k => k.Kind == ServicePackageKinds.DisasterProtection).RenewableSubscriptionIds);

        // TC22: already-covered disaster units cannot be paid for again in an overlapping period.
        var again = await packages.PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, null, w.Disaster.Id, false, null, 2,
            [new AssetUnitsInput(panels[0].Id, 1), new AssetUnitsInput(panels[1].Id, 1)]), None);
        Assert.Equal(AfterSalesErrorCodes.TermOverlap, again.ErrorCode);

        // TC21: 2 eligible uncovered units (maintenance) bought separately at 100 % upfront: 2 × 300,000.
        var bought = await packages.PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, w.Maintenance.Id, null, false, null, 2,
            [new AssetUnitsInput(panels[0].Id, 1), new AssetUnitsInput(panels[1].Id, 1)]), None);
        Assert.True(bought.Succeeded, bought.Message);
        Assert.Equal(600_000m, bought.Value!.Checkout.Amount);
        // While its checkout is open those units are reserved: the same units cannot be bought twice.
        Assert.Equal(AfterSalesErrorCodes.TermOverlap, (await packages.PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, w.Maintenance.Id, null, false,
            null, null, [new AssetUnitsInput(panels[1].Id, 1)]), None)).ErrorCode);
        await w.After.PayAsync(bought.Value.Checkout);
        var retail = await ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == bought.Value.SubscriptionIds[0]);
        Assert.Equal((CoverageScopes.Panel, 2, 300_000m, 600_000m, SubscriptionStatuses.Active), (retail.CoverageScope, retail.CoveredUnits!.Value, retail.UnitPrice!.Value,
            retail.Price, retail.Status));
        Assert.Equal(contractTotal, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId)).TotalAmount);
        Assert.Equal((10, 2, 8), Kind((await ctx.PanelCoverage().EligibilityAsync(flow.CustomerId, flow.ContractId, None)).Value!, ServicePackageKinds.Maintenance));

        // Other panels of the same kind remain purchasable; validation of the selection.
        Assert.Equal(AfterSalesErrorCodes.PanelSelectionInvalid, (await packages.PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, w.Maintenance.Id, null,
            false, null, 3, [new AssetUnitsInput(panels[2].Id, 1)]), None)).ErrorCode);   // quantity ≠ units
        Assert.Equal(AfterSalesErrorCodes.PanelSelectionInvalid, (await packages.PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, w.Maintenance.Id, null,
            false, null, null, [new AssetUnitsInput(panels[2].Id, 2)]), None)).ErrorCode);   // a serialised panel is one unit
        Assert.Equal(AfterSalesErrorCodes.PanelSelectionInvalid, (await packages.PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, w.Maintenance.Id, null,
            false, null, null, [new AssetUnitsInput(Guid.NewGuid(), 1)]), None)).ErrorCode);   // not an installed panel
        Assert.Equal(AfterSalesErrorCodes.PricingUnitMismatch, (await packages.PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, w.Maintenance.Id, null,
            false), None)).ErrorCode);   // a per-panel package needs its panels
        Assert.Equal(AfterSalesErrorCodes.ProjectNotFound, (await packages.PurchaseAsync(w.TechnicianId, new PurchaseInput(flow.ContractId, w.Maintenance.Id, null,
            false, null, null, [new AssetUnitsInput(panels[2].Id, 1)]), None)).ErrorCode);   // someone else's contract

        // Legacy installation-wide purchase of a kind with per-panel coverage in force is refused (it would overlap the same panels).
        var legacy = await w.After.PackageAsync(w.AdminId, ServicePackageKinds.DisasterProtection, 2_000_000m);
        Assert.Equal(AfterSalesErrorCodes.TermOverlap, (await packages.PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, null, legacy.Id, false), None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.PricingUnitMismatch, (await packages.PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, null, legacy.Id, false, null, 1,
            [new AssetUnitsInput(panels[3].Id, 1)]), None)).ErrorCode);
    }

    private static (int Active, int Held, int Available) Kind(PanelEligibilityView view, string kind)
    {
        var k = view.Kinds.Single(x => x.Kind == kind);
        return (k.ActivePanelUnits, k.HeldUnits, k.AvailableUnits);
    }

    [Fact]
    public async Task A_renewal_of_per_panel_coverage_starts_when_the_current_term_ends_without_a_new_waiting_period()
    {
        var (w, flow, _, bundled) = await ProvisionedAsync();
        await using var _ = w.After;
        var renewal = await w.After.Packages().PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, null, w.Disaster.Id, false, null, null, null, bundled.Id), None);
        Assert.True(renewal.Succeeded, renewal.Message);
        Assert.Equal(7_000_000m, renewal.Value!.Checkout.Amount);   // 10 units × today's catalog price
        // A second queued renewal of the same panels is refused.
        Assert.Equal(AfterSalesErrorCodes.TermOverlap, (await w.After.Packages().PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, null, w.Disaster.Id, false,
            null, null, null, bundled.Id), None)).ErrorCode);
        await w.After.PayAsync(renewal.Value.Checkout);
        var renewed = await w.Ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == renewal.Value.SubscriptionIds[0]);
        Assert.Equal((bundled.EndsAt, bundled.EndsAt, (Guid?)bundled.Id, SubscriptionStatuses.Paid), (renewed.StartsAt, renewed.CoverageStartsAt, renewed.RenewsSubscriptionId, renewed.Status));
    }

    [Fact]
    public async Task TC24_a_panel_claim_is_assessed_only_under_the_term_that_covers_that_panel()
    {
        var after = AfterSalesTestContext.Create();
        await using var _ = after;
        var h = await after.HandedOverAsync();
        var disaster = await PerPanelPackageAsync(after, h.AdminId, ServicePackageKinds.DisasterProtection, 700_000m);
        var panels = await after.Db.InstalledAssets.AsNoTracking().Where(a => a.ProjectId == h.ProjectId && a.AssetType == AssetTypes.Panel).ToListAsync();
        Assert.True(panels.Count >= 2, "the fixture installation has at least two panels");
        var bought = await after.Packages().PurchaseAsync(h.CustomerId, new PurchaseInput(h.ContractId, null, disaster.Id, false, null, 1,
            [new AssetUnitsInput(panels[0].Id, 1)]), None);
        Assert.True(bought.Succeeded, bought.Message);
        await after.PayAsync(bought.Value!.Checkout);
        var term = await after.Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == bought.Value.SubscriptionIds[0]);
        Assert.Equal(SubscriptionStatuses.Active, term.Status);
        after.Clock.Now = term.CoverageStartsAt!.Value.AddDays(2);
        var incident = term.CoverageStartsAt.Value.AddDays(1);

        var covered = await after.FaultAsync(h, "Storm broke the panel glass", panels[0].Id, "STORM", incident);
        Assert.Equal((ClaimStatuses.Submitted, (Guid?)term.Id), (covered.Claim!.Status, covered.Claim.SubscriptionId));
        Assert.NotNull(covered.Claim.CoverageAllocationId);
        Assert.Equal((70m, 5_000_000m), (covered.Claim.CoveragePercent, covered.Claim.PerIncidentLimit));   // limits are the term's, not × panels

        var otherPanel = await after.FaultAsync(h, "Storm broke another panel", panels[1].Id, "STORM", incident);
        Assert.Equal(ClaimStatuses.NotEligible, otherPanel.Claim!.Status);
        Assert.Null(otherPanel.Claim.SubscriptionId);
        var unnamed = await after.FaultAsync(h, "Storm damage somewhere", null, "STORM", incident);
        Assert.Equal(ClaimStatuses.NotEligible, unnamed.Claim!.Status);
        Assert.Contains("identify the damaged panel", unnamed.Claim.DecisionReason);
    }

    [Fact]
    public async Task TC23_a_replacement_panel_is_eligible_for_a_new_purchase_but_never_inherits_the_old_coverage()
    {
        var after = AfterSalesTestContext.Create();
        await using var _ = after;
        var h = await after.HandedOverAsync();
        var disaster = await PerPanelPackageAsync(after, h.AdminId, ServicePackageKinds.DisasterProtection, 700_000m);
        var old = await after.Db.InstalledAssets.AsNoTracking().FirstAsync(a => a.ProjectId == h.ProjectId && a.AssetType == AssetTypes.Panel);
        var bought = await after.Packages().PurchaseAsync(h.CustomerId, new PurchaseInput(h.ContractId, null, disaster.Id, false, null, 1, [new AssetUnitsInput(old.Id, 1)]), None);
        Assert.True(bought.Succeeded, bought.Message);
        await after.PayAsync(bought.Value!.Checkout);

        // The panel is replaced (lineage kept): the replacement is a new ACTIVE row, the old one REPLACED (synthetic repair outcome).
        var replacement = new InstalledAsset
        {
            Id = Guid.NewGuid(), ProjectId = h.ProjectId, AssetType = AssetTypes.Panel, ProductId = old.ProductId, Manufacturer = old.Manufacturer, Model = old.Model,
            SerialNumber = $"RPL-{Guid.NewGuid():N}"[..20], Quantity = 1, RegisteredByUserId = h.TechnicianId, RegisteredAt = after.Clock.GetUtcNow(),
            Status = InstalledAssetStatuses.Active, ReplacesAssetId = old.Id,
        };
        after.Db.InstalledAssets.Add(replacement);
        (await after.Db.InstalledAssets.SingleAsync(a => a.Id == old.Id)).Status = InstalledAssetStatuses.Replaced;
        await after.Db.SaveChangesAsync();
        after.Db.ChangeTracker.Clear();

        var allocation = await after.Db.ServiceCoverageAllocations.AsNoTracking().SingleAsync();
        Assert.Equal(old.Id, allocation.InstalledAssetId);   // history kept, nothing moved
        var eligibility = (await after.Installation.PanelCoverage().EligibilityAsync(h.CustomerId, h.ContractId, None)).Value!;
        var row = eligibility.Panels.Single(p => p.AssetId == replacement.Id);
        Assert.Equal(1, row.Coverage.Single(c => c.Kind == ServicePackageKinds.DisasterProtection).AvailableUnits);
        Assert.DoesNotContain(eligibility.Panels, p => p.AssetId == old.Id);

        var term = await after.Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == bought.Value.SubscriptionIds[0]);
        after.Clock.Now = term.CoverageStartsAt!.Value.AddDays(2);
        var claim = await after.FaultAsync(h, "Storm hit the new panel", replacement.Id, "STORM", term.CoverageStartsAt.Value.AddDays(1));
        Assert.Equal(ClaimStatuses.NotEligible, claim.Claim!.Status);
    }

    [Fact]
    public async Task TC26_a_legacy_installation_wide_package_keeps_its_price_and_meaning()
    {
        var after = AfterSalesTestContext.Create();
        await using var _ = after;
        var h = await after.HandedOverAsync();
        var legacy = await after.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance, 2_000_000m);
        var term = await after.ActiveTermAsync(h, legacy);
        Assert.Equal((CoverageScopes.Installation, 2_000_000m, (int?)null, SubscriptionStatuses.Active), (term.CoverageScope, term.Price, term.CoveredUnits, term.Status));
        // Per-panel coverage of the same kind would overlap the installation-wide term.
        var perPanel = await PerPanelPackageAsync(after, h.AdminId, ServicePackageKinds.Maintenance, 300_000m);
        var panel = await after.Db.InstalledAssets.AsNoTracking().FirstAsync(a => a.ProjectId == h.ProjectId && a.AssetType == AssetTypes.Panel);
        Assert.Equal(AfterSalesErrorCodes.TermOverlap, (await after.Packages().PurchaseAsync(h.CustomerId, new PurchaseInput(h.ContractId, perPanel.Id, null, false, null, 1,
            [new AssetUnitsInput(panel.Id, 1)]), None)).ErrorCode);
    }

    [Fact]
    public async Task TC30_finance_allocates_installments_between_installation_and_packages_without_double_counting()
    {
        var (w, flow, _, _) = await ProvisionedAsync();
        await using var _ = w.After;
        var today = w.Ctx.Scheduling.Calendar.LocalDateOf(w.Ctx.Clock.GetUtcNow());
        var overview = (await w.Ctx.Finance().GetAsync(today.AddDays(-60), today.AddDays(1), null, None)).Value!;
        Assert.Equal(17_100_000m, overview.DepositsCollected.Amount);
        Assert.Equal(39_900_000m, overview.BalancesCollected.Amount);
        var parts = overview.CollectedByComponent!.ToDictionary(b => b.Key, b => b.Amount);
        Assert.Equal((50_000_000m, 7_000_000m, 0m), (parts["INSTALLATION"], parts["BUNDLED_DISASTER_PROTECTION_PACKAGES"], parts["BUNDLED_MAINTENANCE_PACKAGES"]));
        Assert.Equal(overview.DepositsCollected.Amount + overview.BalancesCollected.Amount,
            parts["INSTALLATION"] + parts["BUNDLED_DISASTER_PROTECTION_PACKAGES"] + parts["BUNDLED_MAINTENANCE_PACKAGES"]);
        Assert.Equal(0m, parts["RETAIL_DISASTER_PROTECTION_PACKAGES"]);
    }
}
