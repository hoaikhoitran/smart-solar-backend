using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Quotations;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// The 2026-11-17 simplified commercial flow, step by step on the production handlers: the surveying technician builds and issues a cart
/// quotation, the customer accepts (contract DRAFT created in the same transaction), a Manager prepares, issues and publishes the contract,
/// the customer submits the signed scan and the Manager confirms it (SIGNED). Catalog prices are SYNTHETIC fixture values.
/// </summary>
public static class SimplifiedFlowFixture
{
    private static readonly CancellationToken None = CancellationToken.None;

    public sealed record Cart(ConfirmedSurvey Survey, QuotationCatalog Catalog, QuotationView Quotation)
    {
        public Guid TechnicianId => Survey.Technician.Id;
        public Guid CustomerId => Survey.Request.Customer.Id;
        public Guid RequestId => Survey.Request.Request.Id;
    }

    public sealed record Signed(Cart Cart, UserAccount Manager, ContractView Contract);

    /// <summary>Completed survey + synthetic catalog + the technician's DRAFT cart (empty).</summary>
    public static async Task<Cart> DraftAsync(InstallationWorkflowTestContext ctx, string customer = "Customer")
    {
        var catalog = await AutomaticQuotationFixture.SeedCatalogAsync(ctx);
        var survey = await ctx.QuotableSurveyAsync(customer);
        var draft = await ctx.Quotations().CreateCartAsync(survey.Technician.Id, survey.Request.Request.Id, "Two rows facing south", None);
        Assert.True(draft.Succeeded, draft.Message);
        return new Cart(survey, catalog, draft.Value!);
    }

    /// <summary>2 panels, 1 inverter, 1 labor job, 1 transport trip, issued: 16,000,000 equipment + 2,500,000 services = 18,500,000 VND before tax.</summary>
    public static async Task<Cart> IssuedAsync(InstallationWorkflowTestContext ctx, string customer = "Customer")
    {
        var cart = await DraftAsync(ctx, customer);
        var q = ctx.Quotations();
        var view = cart.Quotation;
        foreach (var (product, quantity) in new[] { (cart.Catalog.Panel.Id, 2m), (cart.Catalog.Inverter.Id, 1m), (cart.Catalog.Labor.Id, 1m), (cart.Catalog.Transport.Id, 1m) })
        {
            var added = await q.AddCartLineAsync(cart.TechnicianId, view.QuotationId, view.Revision, product, quantity, None);
            Assert.True(added.Succeeded, added.Message);
            view = added.Value!;
        }
        var issued = await q.IssueCartAsync(cart.TechnicianId, view.QuotationId, view.Revision, None);
        Assert.True(issued.Succeeded, issued.Message + " " + System.Text.Json.JsonSerializer.Serialize(issued.Details));
        return cart with { Quotation = issued.Value! };
    }

    /// <summary>Accepts with the production wiring; returns the contract DRAFT that the acceptance created.</summary>
    public static async Task<ContractView> AcceptAsync(InstallationWorkflowTestContext ctx, Cart cart)
    {
        await ctx.EnsureInstallationWarrantyOptionAsync();
        var accepted = await ctx.QuotationsWithContracts().AcceptAsync(cart.CustomerId, cart.Quotation.QuotationId, None);
        Assert.True(accepted.Succeeded, accepted.Message);
        var contract = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.QuotationId == cart.Quotation.QuotationId);
        return (await ctx.Contracts().GetForStaffAsync(Guid.Empty, true, contract.Id, None)).Value!;
    }

    /// <summary>Manager: terms → issue → official PDF → publish (AWAITING_SIGNATURE).</summary>
    public static async Task<ContractView> PublishedAsync(InstallationWorkflowTestContext ctx, UserAccount manager, ContractView draft)
    {
        var c = ctx.Contracts();
        var terms = await c.UpdateTermsAsync(manager.Id, draft.ContractId, draft.Revision, "Standard installation terms", None);
        Assert.True(terms.Succeeded, terms.Message);
        var issued = await c.IssueAsync(manager.Id, draft.ContractId, terms.Value!.Revision, None);
        Assert.True(issued.Succeeded, issued.Message);
        var pdf = await ctx.VerifiedFileAsync(manager.Id, FilePurposes.ContractDocument, "pdf");
        var attached = await c.AttachOfficialDocumentAsync(manager.Id, draft.ContractId, issued.Value!.Revision, pdf, issued.Value.CanonicalHash!, None);
        Assert.True(attached.Succeeded, attached.Message);
        var published = await c.PublishAsync(manager.Id, draft.ContractId, attached.Value!.Revision, None);
        Assert.True(published.Succeeded, published.Message);
        return published.Value!;
    }

    /// <summary>Issued cart → accepted → published → signed scan → Manager confirms the signature (SIGNED).</summary>
    public static async Task<Signed> SignedAsync(InstallationWorkflowTestContext ctx, string customer = "Customer")
    {
        var cart = await IssuedAsync(ctx, customer);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync($"Manager of {customer}");
        var published = await PublishedAsync(ctx, manager, await AcceptAsync(ctx, cart));
        var scan = await ctx.VerifiedFileAsync(cart.CustomerId, FilePurposes.SignedContract, "pdf");
        var submitted = await ctx.Contracts().SubmitSignedDocumentAsync(cart.CustomerId, published.ContractId, scan, published.OfficialDocument!.Sha256!, None);
        Assert.True(submitted.Succeeded, submitted.Message);
        var signed = await ctx.Contracts().ReviewAsync(manager.Id, true, published.ContractId, submitted.Value!.Revision, ReviewDecision.Approve, null, None);
        Assert.True(signed.Succeeded, signed.Message);
        Assert.Equal(ContractStatuses.Signed, signed.Value!.Status);
        return new Signed(cart, manager, signed.Value);
    }
}
