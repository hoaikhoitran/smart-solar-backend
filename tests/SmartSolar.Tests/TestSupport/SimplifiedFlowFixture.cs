using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Quotations;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// The commercial flow step by step on the production handlers. Technician-led flow (2026-11-18): the surveying technician builds and finalizes a
/// cart quotation, records the customer's on-site agreement, opens the payment (cash confirmed by a Manager here), the verified payment creates the
/// contract, a Manager prepares / issues / publishes it, the signed scan is reviewed and the prepaid contract becomes PAID. LEGACY helpers build
/// the previous flow's data (customer acceptance in the app + contract DRAFT, signed then paid by the customer), which production still holds.
/// Catalog prices are SYNTHETIC fixture values.
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

    /// <summary>LEGACY data: the customer accepted in the app and the contract DRAFT was created (previous flow; see <see cref="LegacyFlowData"/>).</summary>
    public static async Task<ContractView> LegacyAcceptAsync(InstallationWorkflowTestContext ctx, Cart cart)
    {
        await ctx.EnsureInstallationWarrantyOptionAsync();
        var contractId = await LegacyFlowData.CustomerAcceptedWithDraftAsync(ctx.Db, cart.Quotation.QuotationId, ctx.Clock.GetUtcNow());
        return (await ctx.Contracts().GetForStaffAsync(Guid.Empty, true, contractId, None)).Value!;
    }

    /// <summary>The technician records the customer's on-site agreement (actor = technician).</summary>
    public static async Task<QuotationView> AgreedAsync(InstallationWorkflowTestContext ctx, Cart cart, string? note = null)
    {
        var agreed = await ctx.Quotations().RecordOnSiteDecisionAsync(cart.TechnicianId, cart.Quotation.QuotationId,
            new OnSiteDecisionInput(OnSiteDecisions.Agreed, cart.Quotation.Revision, note), None);
        Assert.True(agreed.Succeeded, agreed.Message);
        Assert.Equal(QuotationStatuses.Accepted, agreed.Value!.Status);
        return agreed.Value;
    }

    /// <summary>
    /// Agreed on site → the technician opens CASH → a Manager records the receipt → the verified payment creates the contract DRAFT (paid). Returns the
    /// contract as staff see it.
    /// </summary>
    public static async Task<ContractView> PaidOnSiteAsync(InstallationWorkflowTestContext ctx, Cart cart, UserAccount manager)
    {
        await ctx.EnsureInstallationWarrantyOptionAsync();
        await AgreedAsync(ctx, cart);
        var opened = await ctx.OnSitePayments().StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "CASH", None);
        Assert.True(opened.Succeeded, opened.Message);
        var evidence = await ctx.VerifiedFileAsync(manager.Id, FilePurposes.PaymentEvidence);
        var confirmed = await ctx.PaymentsWithCash().ConfirmCashAttemptAsync(manager.Id, opened.Value!.Current!.PaymentAttemptId,
            new SmartSolar.Modules.Payments.CashConfirmation(opened.Value.Amount, $"PT-{cart.Quotation.QuoteNumber}", evidence, null), None);
        Assert.True(confirmed.Succeeded, confirmed.Message);
        var contract = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.QuotationId == cart.Quotation.QuotationId);
        return (await ctx.Contracts().GetForStaffAsync(Guid.Empty, true, contract.Id, None)).Value!;
    }

    /// <summary>Technician-led flow end to end up to the contract: issued → agreed → paid (cash) → published → signed scan → approved → PAID.</summary>
    public static async Task<Signed> PrepaidAsync(InstallationWorkflowTestContext ctx, string customer = "Customer")
    {
        var cart = await IssuedAsync(ctx, customer);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync($"Manager of {customer}");
        var published = await PublishedAsync(ctx, manager, await PaidOnSiteAsync(ctx, cart, manager));
        var scan = await ctx.VerifiedFileAsync(manager.Id, FilePurposes.SignedContract, "pdf");
        var submitted = await ctx.Contracts().SubmitPaperSignedDocumentAsync(manager.Id, true, published.ContractId, scan, published.OfficialDocument!.Sha256!, null,
            "signed on site", None);
        Assert.True(submitted.Succeeded, submitted.Message);
        var reviewed = await ctx.ContractsWithFollowUp().ReviewAsync(manager.Id, true, published.ContractId, submitted.Value!.Revision, ReviewDecision.Approve, null, None);
        Assert.True(reviewed.Succeeded, reviewed.Message);
        Assert.Equal(ContractStatuses.Paid, reviewed.Value!.Status);
        return new Signed(cart, manager, reviewed.Value);
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

    /// <summary>LEGACY flow: issued cart → accepted in the app (legacy data) → published → signed scan → Manager confirms the signature (SIGNED, unpaid).</summary>
    public static async Task<Signed> SignedAsync(InstallationWorkflowTestContext ctx, string customer = "Customer")
    {
        var cart = await IssuedAsync(ctx, customer);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync($"Manager of {customer}");
        var published = await PublishedAsync(ctx, manager, await LegacyAcceptAsync(ctx, cart));
        var scan = await ctx.VerifiedFileAsync(cart.CustomerId, FilePurposes.SignedContract, "pdf");
        var submitted = await ctx.Contracts().SubmitSignedDocumentAsync(cart.CustomerId, published.ContractId, scan, published.OfficialDocument!.Sha256!, None);
        Assert.True(submitted.Succeeded, submitted.Message);
        var signed = await ctx.Contracts().ReviewAsync(manager.Id, true, published.ContractId, submitted.Value!.Revision, ReviewDecision.Approve, null, None);
        Assert.True(signed.Succeeded, signed.Message);
        Assert.Equal(ContractStatuses.Signed, signed.Value!.Status);
        return new Signed(cart, manager, signed.Value);
    }
}
