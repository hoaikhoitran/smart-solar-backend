using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.Unit.InstallationWorkflow;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// The 30/70 flow (2026-11-19) step by step on the production handlers: issued cart → AGREED (contract DRAFT + installments) → deposit 30% (cash
/// confirmed by a Manager, or PayOS webhook) → paper signed on site (photographed pages, finalized) → schedule confirmed → installation → customer
/// ACCEPT (balance due ≤ 72 h) → balance 70% → FULLY PAID. Catalog prices are SYNTHETIC fixture values.
/// </summary>
public static class PaymentPlanFlow
{
    private static readonly CancellationToken None = CancellationToken.None;

    public sealed record Agreed(SimplifiedFlowFixture.Cart Cart, UserAccount Manager, Guid ContractId)
    {
        public Guid TechnicianId => Cart.TechnicianId;
        public Guid CustomerId => Cart.CustomerId;
    }

    /// <summary>Issued cart, warranty option, then the technician records AGREED: one 30/70 contract (DRAFT) with its two installments.</summary>
    public static async Task<Agreed> AgreedAsync(InstallationWorkflowTestContext ctx, string customer = "Customer")
    {
        ctx.Clock.Now = SchedulingTestContext.DefaultNow.ToUniversalTime();   // each journey starts at the fixture's "now" (survey booking horizon)
        var cart = await SimplifiedFlowFixture.IssuedAsync(ctx, customer);
        await ctx.EnsureInstallationWarrantyOptionAsync();
        var manager = await ctx.Scheduling.Inner.SeedUserAsync($"Manager of {customer}");
        var agreed = await ctx.QuotationsWithPaymentPlan().RecordOnSiteDecisionAsync(cart.TechnicianId, cart.Quotation.QuotationId,
            new OnSiteDecisionInput(OnSiteDecisions.Agreed, cart.Quotation.Revision, "Agreed at the customer's home"), None);
        Assert.True(agreed.Succeeded, agreed.Message);
        Assert.NotNull(agreed.Value!.ContractId);
        return new Agreed(cart, manager, agreed.Value.ContractId!.Value);
    }

    public static async Task<ContractInstallment> InstallmentAsync(InstallationWorkflowTestContext ctx, Guid contractId, int sequence)
        => await ctx.Db.ContractInstallments.AsNoTracking().SingleAsync(i => i.ContractId == contractId && i.Sequence == sequence);

    /// <summary>The technician opens CASH for the installment; a Manager records the receipt (unique reference, verified evidence).</summary>
    public static async Task<PaymentAttemptView> PayCashAsync(InstallationWorkflowTestContext ctx, Agreed flow, int sequence, InstallmentPayer payer = InstallmentPayer.Technician)
    {
        var actor = payer == InstallmentPayer.Technician ? flow.TechnicianId : flow.CustomerId;
        var opened = await ctx.InstallmentPayments().StartAsync(payer, actor, flow.ContractId, sequence, "CASH", None);
        Assert.True(opened.Succeeded, opened.Message);
        var evidence = await ctx.VerifiedFileAsync(flow.Manager.Id, FilePurposes.PaymentEvidence);
        var confirmed = await ctx.PaymentsWithCash().ConfirmCashAttemptAsync(flow.Manager.Id, opened.Value!.Current!.PaymentAttemptId,
            new CashConfirmation(opened.Value.Amount, $"PT-{flow.ContractId:N}-{sequence}-{Guid.NewGuid():N}"[..40], evidence, null), None);
        Assert.True(confirmed.Succeeded, confirmed.Message);
        return confirmed.Value!;
    }

    /// <summary>The technician shows the QR; the verified PayOS webhook settles it.</summary>
    public static async Task<PaymentAttemptView> PayOnlineAsync(InstallationWorkflowTestContext ctx, Agreed flow, int sequence, InstallmentPayer payer = InstallmentPayer.Technician,
        string reference = "FT-3070")
    {
        var actor = payer == InstallmentPayer.Technician ? flow.TechnicianId : flow.CustomerId;
        var opened = await ctx.InstallmentPayments().StartAsync(payer, actor, flow.ContractId, sequence, "ONLINE", None);
        Assert.True(opened.Succeeded, opened.Message);
        var qr = opened.Value!.Current!;
        // The provider reports the transaction at the current (test) time: lateness is judged on the provider's transaction time.
        var outcome = await ctx.PaymentsWithSchedule().HandleWebhookAsync(ctx.PayOs.Webhook(qr.OrderCode, (long)qr.Amount, $"{reference}-{qr.OrderCode}",
            transactionAt: ctx.Clock.GetUtcNow()), None);
        Assert.Equal(WebhookOutcomes.Applied, outcome.Outcome);
        return (await ctx.PaymentsWithCash().GetAttemptForCustomerAsync(flow.CustomerId, qr.PaymentAttemptId, false, None))!;
    }

    /// <summary>The technician photographs <paramref name="pages"/> pages, uploads them in order and finalizes → contract SIGNED.</summary>
    public static async Task<SignatureSetView> SignOnPaperAsync(InstallationWorkflowTestContext ctx, Agreed flow, int pages = 3)
    {
        var signatures = ctx.PaperSignatures();
        var started = await signatures.StartAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, new StartSignatureSetInput(pages, null, "signed at home"), None);
        Assert.True(started.Succeeded, started.Message);
        for (var page = 1; page <= pages; page++)
        {
            var file = await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.SignedContractPage);
            var put = await signatures.PutPageAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, page, file, None);
            Assert.True(put.Succeeded, put.Message);
        }
        var finalized = await signatures.FinalizeAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, null, None);
        Assert.True(finalized.Succeeded, finalized.Message);
        return finalized.Value!;
    }

    /// <summary>
    /// Deposit paid, paper signed, the technician-agreed date confirmed (project + assignment), prerequisites satisfied, installation started,
    /// evidence + equipment registered and completion reported: PENDING_CUSTOMER_ACCEPTANCE. Returns the project id.
    /// </summary>
    public static async Task<Guid> CompletionReportedAsync(InstallationWorkflowTestContext ctx, Agreed flow, bool payOnline = false)
    {
        if (payOnline) await PayOnlineAsync(ctx, flow, 1);
        else await PayCashAsync(ctx, flow, 1);
        var planned = await ctx.Schedules().ProposeAsync(flow.TechnicianId, false, flow.ContractId, InstallationScheduleTests.Plan(note: "Agreed in person"), None);
        Assert.True(planned.Succeeded, planned.Message);
        await SignOnPaperAsync(ctx, flow);
        var schedule = (await ctx.Schedules().GetForCustomerAsync(flow.CustomerId, flow.ContractId, None)).Value!.First();
        Assert.Equal(InstallationScheduleStatuses.Confirmed, schedule.Status);
        var projectId = schedule.ProjectId!.Value;
        await InstallationTests.SatisfyPrerequisitesAsync(ctx, projectId, flow.Manager.Id);
        var started = await ctx.Installations().StartProjectAsync(flow.TechnicianId, projectId, None);
        Assert.True(started.Succeeded, started.Message);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, projectId, flow.TechnicianId);
        Assert.True((await InstallationTests.RegisterPanelAndInverterAsync(ctx, projectId, flow.TechnicianId)).Succeeded);
        var reported = await ctx.Installations().ReportCompletionAsync(flow.TechnicianId, projectId, "Array and inverter commissioned.", null, None);
        Assert.True(reported.Succeeded, reported.Message);
        return projectId;
    }

    public static async Task<InstallationCustomerView> AcceptAsync(InstallationWorkflowTestContext ctx, Agreed flow, Guid projectId)
    {
        var revision = (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync(p => p.Id == projectId)).Revision;
        var accepted = await ctx.Installations().DecideAcceptanceAsync(flow.CustomerId, projectId,
            new CustomerAcceptanceInput(CustomerAcceptanceDecisions.Accept, null, null, revision), None);
        Assert.True(accepted.Succeeded, accepted.Message);
        return accepted.Value!;
    }

    public static async Task<ContractView> StaffViewAsync(InstallationWorkflowTestContext ctx, Guid contractId)
        => (await ctx.Contracts().GetForStaffAsync(Guid.Empty, true, contractId, None)).Value!;
}
