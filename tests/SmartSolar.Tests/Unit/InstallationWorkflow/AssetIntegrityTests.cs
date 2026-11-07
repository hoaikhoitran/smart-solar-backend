using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// BL-08: an installed asset that references the catalog must reference an existing product of its kind (answered as a 400, not a
/// database foreign-key failure), and an omitted warranty duration comes from the product's catalog warranty. Data is SYNTHETIC.
/// </summary>
public sealed class AssetIntegrityTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Assets_must_reference_existing_products_of_their_kind_and_inherit_the_catalog_warranty()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (_, project, tech, _) = await InstallationTests.ScheduledAsync(ctx);
        await InstallationTests.StartAsync(ctx, project.ProjectId, tech);
        var panel = PreSurveyTestContext.NewPanelProduct("PNL-AST-1");
        panel.WarrantyMonth = 144;
        var inverter = PreSurveyTestContext.NewPanelProduct("INV-AST-1", ratedPowerW: null, widthMm: null, heightMm: null, productType: ProductTypes.Inverter);
        inverter.WarrantyMonth = 120;
        ctx.Db.Products.AddRange(panel, inverter);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        var installs = ctx.Installations();

        var unknown = await installs.RegisterAssetsAsync(tech, project.ProjectId,
            [new AssetInput("PANEL", Guid.NewGuid(), "Jinko", "Tiger Neo 550", "JK-X-1", null, null)], None);
        var wrongKind = await installs.RegisterAssetsAsync(tech, project.ProjectId,
            [new AssetInput("PANEL", inverter.Id, "Huawei", "SUN2000", "HW-X-1", null, null)], None);
        // D-05/D-06: panels and inverters reference their catalog product; the equipment warranty comes from the catalog, never the technician.
        var noProduct = await installs.RegisterAssetsAsync(tech, project.ProjectId,
            [new AssetInput("INVERTER", null, "Huawei", "SUN2000", "HW-X-0", null, null)], None);
        var typedWarranty = await installs.RegisterAssetsAsync(tech, project.ProjectId,
            [new AssetInput("INVERTER", inverter.Id, "Huawei", "SUN2000", "HW-X-3", null, 60)], None);
        var meterWarranty = await installs.RegisterAssetsAsync(tech, project.ProjectId,
            [new AssetInput("METER", null, "EVN", "M1", "M-X-1", null, 24)], None);
        var registered = await installs.RegisterAssetsAsync(tech, project.ProjectId,
            [new AssetInput("PANEL", panel.Id, "Jinko", "Tiger Neo 550", "JK-X-2", "R1-C1", null), new AssetInput("INVERTER", inverter.Id, "Huawei", "SUN2000", "HW-X-2", null, 120)], None);

        Assert.Equal(InstallationErrorCodes.AssetProductInvalid, unknown.ErrorCode);
        Assert.Equal(InstallationErrorCodes.AssetProductInvalid, wrongKind.ErrorCode);
        Assert.Contains("assets[0].productId", System.Text.Json.JsonSerializer.Serialize(wrongKind.Details));
        Assert.Equal(InstallationErrorCodes.AssetProductInvalid, noProduct.ErrorCode);
        Assert.Contains("assets[0].productId", System.Text.Json.JsonSerializer.Serialize(noProduct.Details));
        Assert.Equal(InstallationErrorCodes.AssetProductInvalid, typedWarranty.ErrorCode);
        Assert.Contains("assets[0].warrantyMonths", System.Text.Json.JsonSerializer.Serialize(typedWarranty.Details));
        Assert.Equal(InstallationErrorCodes.AssetProductInvalid, meterWarranty.ErrorCode);
        Assert.True(registered.Succeeded, registered.Message);
        var stored = await ctx.Db.InstalledAssets.AsNoTracking().Where(a => a.ProjectId == project.ProjectId).OrderBy(a => a.AssetType).ToListAsync();
        Assert.Equal(new[] { ("INVERTER", (int?)120), ("PANEL", (int?)144) }, stored.Select(a => (a.AssetType, a.WarrantyMonths)));
    }
}
