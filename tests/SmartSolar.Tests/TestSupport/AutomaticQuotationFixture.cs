using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.OnSiteSurveys;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// SYNTHETIC catalog for the automatic quotation (test fixture values only, not production prices): a 550 W panel at 3,000,000 VND,
/// an inverter at 10,000,000 VND, installation labor at 2,000,000 VND per job and transport at 500,000 VND per trip.
/// </summary>
public sealed record QuotationCatalog(Product Panel, Product Inverter, Product Labor, Product Transport)
{
    /// <summary>2 panels + 1 inverter + 1 labor job + 1 transport trip: equipment 16,000,000, subtotal 18,500,000 VND.</summary>
    public BillOfMaterialsInput StandardBill(decimal panels = 2m) => new(
        [new BillOfMaterialsItem(Panel.Id, panels), new BillOfMaterialsItem(Inverter.Id, 1m)],
        [new BillOfMaterialsItem(Labor.Id, 1m), new BillOfMaterialsItem(Transport.Id, 1m)],
        "Standard rooftop installation");
}

public static class AutomaticQuotationFixture
{
    public static async Task<QuotationCatalog> SeedCatalogAsync(InstallationWorkflowTestContext ctx, decimal laborPrice = 2_000_000m)
    {
        var panel = PreSurveyTestContext.NewPanelProduct($"PNL-{Guid.NewGuid():N}"[..20]);
        panel.UnitPrice = 3_000_000m;
        var inverter = Product(ProductTypes.Inverter, "Hybrid inverter 5 kW", "PCS", 10_000_000m);
        var labor = Product(ProductTypes.InstallationLabor, "Rooftop installation labor", "JOB", laborPrice);
        var transport = Product(ProductTypes.Transport, "Delivery within the city", "TRIP", 500_000m);
        ctx.Db.Products.AddRange(panel, inverter, labor, transport);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        return new QuotationCatalog(panel, inverter, labor, transport);
    }

    private static Product Product(string type, string name, string unit, decimal price)
    {
        var product = PreSurveyTestContext.NewPanelProduct($"{type[..3]}-{Guid.NewGuid():N}"[..20], ratedPowerW: null, widthMm: null, heightMm: null, productType: type);
        product.Name = name;
        product.Unit = unit;
        product.UnitPrice = price;
        return product;
    }

    /// <summary>Full survey data (as the existing tests) with a feasibility and an optional bill of materials.</summary>
    public static OnSiteSurveyData Data(BillOfMaterialsInput? bom, string feasibility = "FEASIBLE")
        => Unit.InstallationWorkflow.OnSiteSurveyTests.FullData() with { InstallationFeasibility = feasibility, BillOfMaterials = bom };

    /// <summary>Technician on site: start → save (with the bill of materials) → roof photo → complete, with the production wiring.</summary>
    public static async Task<Modules.Common.Results.OperationResult<OnSiteSurveyView>> CompleteAsync(InstallationWorkflowTestContext ctx, ConfirmedSurvey s,
        BillOfMaterialsInput? bom, string feasibility = "FEASIBLE", DateTimeOffset? at = null)
    {
        ctx.Clock.Now = (at ?? At(10)).ToUniversalTime();
        var onSite = ctx.OnSiteWithAutomaticQuotation();
        var started = await onSite.StartAsync(s.Technician.Id, s.AppointmentId, CancellationToken.None);
        Assert.True(started.Succeeded, started.Message);
        var saved = await onSite.SaveAsync(new SaveOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, started.Value!.Revision, Data(bom, feasibility)), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Message + " " + System.Text.Json.JsonSerializer.Serialize(saved.Details));
        var photo = await ctx.VerifiedFileAsync(s.Technician.Id, FilePurposes.OnSiteSurveyEvidence);
        var attached = await onSite.AddAttachmentAsync(new AddOnSiteSurveyAttachmentCommand(s.Technician.Id, s.AppointmentId, photo, "ROOF", "South side"), CancellationToken.None);
        Assert.True(attached.Succeeded, attached.Message);
        return await onSite.CompleteAsync(new CompleteOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, attached.Value!.Revision), CancellationToken.None);
    }

    public static async Task<List<Modules.Quotations.Quotation>> QuotationsAsync(InstallationWorkflowTestContext ctx, Guid surveyRequestId)
        => await ctx.Db.Quotations.AsNoTracking().Where(q => q.SurveyRequestId == surveyRequestId).OrderBy(q => q.Version).ToListAsync();
}
