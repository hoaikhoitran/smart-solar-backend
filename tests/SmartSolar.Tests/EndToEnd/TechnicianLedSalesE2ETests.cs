using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;

namespace SmartSolar.Tests.EndToEnd;

/// <summary>
/// E2E-ONSITE (2026-11-18): the whole technician-led installation on the production handlers, one database. The surveying technician builds and
/// finalizes the quotation, records the customer's on-site agreement, shows the PayOS QR (paid with a banking app, verified by the signed webhook);
/// the verified payment creates the contract; the technician records the installation date agreed in person; the Manager prepares the contract and
/// confirms the paper signature (the paid contract becomes PAID and the schedule is confirmed by the system); the installation runs; the Manager
/// accepts it internally; the customer reads progress and the handover. At no step does the customer approve anything in the app.
/// </summary>
public sealed class TechnicianLedSalesE2ETests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Notification types that would ask the customer to act in the app (all retired 2026-11-18).</summary>
    private static readonly string[] ApprovalRequests =
        ["INSTALLATION_QUOTATION_ISSUED", "INSTALLATION_SLOTS_OFFERED", "INSTALLATION_ACCEPTANCE_REQUIRED", "EQUIPMENT_SUBSTITUTION_APPROVAL_REQUIRED"];

    [Fact]
    public async Task E2E_ONSITE_01_Quotation_agreed_on_site_paid_by_QR_contract_schedule_installation_and_internal_acceptance()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var cart = await SimplifiedFlowFixture.IssuedAsync(ctx);
        await ctx.EnsureInstallationWarrantyOptionAsync();
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");
        var customer = cart.CustomerId;

        // 1. On site: the technician records the agreement and shows the QR; the customer pays with a banking app (no SmartSolar login).
        await SimplifiedFlowFixture.AgreedAsync(ctx, cart, "Agreed at the customer's home");
        var qr = (await ctx.OnSitePayments().StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "ONLINE", None)).Value!.Current!;
        Assert.Equal(0, await ctx.Db.CustomerContracts.CountAsync());   // payment comes first
        Assert.Equal(WebhookOutcomes.Applied,
            (await ctx.PaymentsWithSchedule().HandleWebhookAsync(ctx.PayOs.Webhook(qr.OrderCode, (long)qr.Amount, "FT-E2E-ONSITE"), None)).Outcome);
        var payment = (await ctx.OnSitePayments().GetAsync(cart.TechnicianId, cart.Quotation.QuotationId, None)).Value!;
        Assert.Equal(QuotationPaymentStates.Paid, payment.PaymentState);
        var contractId = payment.ContractId!.Value;

        // 2. The contract exists (paid, DRAFT); the technician records the installation date agreed in person.
        var draft = (await ctx.Contracts().GetForStaffAsync(manager.Id, true, contractId, None)).Value!;
        Assert.Equal((ContractStatuses.Draft, true, cart.Quotation.TotalAmount), (draft.Status, draft.PaymentCompleted, draft.TotalAmount));
        var planned = await ctx.Schedules().ProposeAsync(cart.TechnicianId, false, contractId, InstallationScheduleTests.Plan(note: "Agreed in person on 19/10"), None);
        Assert.True(planned.Succeeded, planned.Message);
        Assert.True(planned.Value!.AwaitingContractSignature);

        // 3. The Manager prepares and publishes the contract, records the paper signed on site and confirms it: PAID at once, schedule confirmed.
        var published = await SimplifiedFlowFixture.PublishedAsync(ctx, manager, draft);
        var scan = await ctx.VerifiedFileAsync(manager.Id, FilePurposes.SignedContract, "pdf");
        var under = (await ctx.Contracts().SubmitPaperSignedDocumentAsync(manager.Id, true, contractId, scan, published.OfficialDocument!.Sha256!, null, "signed on site", None)).Value!;
        var completedContract = (await ctx.ContractsWithFollowUp().ReviewAsync(manager.Id, true, contractId, under.Revision, ReviewDecision.Approve, null, None)).Value!;
        Assert.Equal(ContractStatuses.Paid, completedContract.Status);
        var schedule = (await ctx.Schedules().GetForCustomerAsync(customer, contractId, None)).Value!.First();
        Assert.Equal((InstallationScheduleStatuses.Confirmed, true), (schedule.Status, schedule.IsConfirmed));
        var projectId = schedule.ProjectId!.Value;

        // 4. Installation by the technician (prerequisites satisfied by staff), completion report.
        await InstallationTests.SatisfyPrerequisitesAsync(ctx, projectId, manager.Id);   // EVN / PCCC / structural documents verified by staff
        var started = await ctx.Installations().StartProjectAsync(cart.TechnicianId, projectId, None);
        Assert.True(started.Succeeded, started.Message);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, projectId, cart.TechnicianId);
        Assert.True((await InstallationTests.RegisterPanelAndInverterAsync(ctx, projectId, cart.TechnicianId)).Succeeded);
        var reported = await ctx.Installations().ReportCompletionAsync(cart.TechnicianId, projectId, "Array and inverter commissioned, handed over on site.", null, None);
        Assert.True(reported.Succeeded, reported.Message);

        // 5. Internal acceptance by the Manager → COMPLETED; the customer reads the handover.
        Assert.True((await InternalAcceptance.AcceptAsync(ctx, projectId, manager.Id, "Handover minutes signed with the customer on site")).Succeeded);
        var view = (await ctx.Installations().GetForCustomerAsync(customer, projectId, None)).Value!;
        Assert.Equal(InstallationStatuses.Completed, view.Status);
        Assert.True((await ctx.Installations().GetHandoverAsync(customer, false, projectId, None)).Succeeded);

        // The customer was informed along the way and never asked to approve anything.
        var notifications = await ctx.Db.AfterSalesNotifications.AsNoTracking().Where(n => n.RecipientUserId == customer).Select(n => n.Type).ToListAsync();
        Assert.Contains(CustomerNotificationTypes.ContractCreatedAfterPayment, notifications);
        Assert.Contains(CustomerNotificationTypes.InstallationCompleted, notifications);
        Assert.DoesNotContain(notifications, ApprovalRequests.Contains);
        // Every decision on the way was recorded by staff, never under the customer's identity.
        Assert.DoesNotContain(await ctx.Db.QuotationEvents.AsNoTracking().Where(e => e.QuotationId == cart.Quotation.QuotationId).Select(e => e.ActorUserId).ToListAsync(),
            actor => actor == customer);
        Assert.DoesNotContain(await ctx.Db.InstallationAcceptances.AsNoTracking().Where(a => a.ProjectId == projectId).Select(a => a.DecidedByUserId).ToListAsync(),
            decider => decider == customer);
        Assert.Equal(1, await ctx.Db.PaymentAttempts.CountAsync(a => a.Status == PaymentStatuses.Paid));
    }

    [Fact]
    public async Task E2E_ONSITE_02_Changes_asked_on_site_give_a_new_version_and_only_the_agreed_version_is_paid_and_contracted()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var cart = await SimplifiedFlowFixture.IssuedAsync(ctx);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");
        var q = ctx.Quotations();

        Assert.True((await OnSiteDecision.RevisionAsync(ctx, cart.TechnicianId, cart.Quotation.QuotationId, "Only one transport trip")).Succeeded);
        Assert.Equal(QuotationErrorCodes.NotAgreed, (await ctx.OnSitePayments().StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "CASH", None)).ErrorCode);
        var draft = (await q.CreateCartRevisionAsync(cart.TechnicianId, cart.Quotation.QuotationId, None)).Value!;
        var transport = draft.Lines.Single(l => l.ProductId == cart.Catalog.Transport.Id);
        var trimmed = (await q.RemoveCartLineAsync(cart.TechnicianId, draft.QuotationId, transport.LineId, draft.Revision, None)).Value!;
        var v2 = (await q.IssueCartAsync(cart.TechnicianId, trimmed.QuotationId, trimmed.Revision, None)).Value!;
        Assert.True((await OnSiteDecision.AgreeAsync(ctx, cart.TechnicianId, v2.QuotationId)).Succeeded);

        var contractId = await OnSiteDecision.PayInCashAsync(ctx, cart.TechnicianId, v2.QuotationId, manager.Id);
        var contract = (await ctx.Contracts().GetForStaffAsync(manager.Id, true, contractId, None)).Value!;
        Assert.Equal((v2.QuotationId, 2, v2.TotalAmount), (contract.QuotationId, contract.QuotationVersion, contract.TotalAmount));
        Assert.Equal(QuotationStatuses.Superseded, (await ctx.Db.Quotations.AsNoTracking().SingleAsync(x => x.Id == cart.Quotation.QuotationId)).Status);
        Assert.Equal(1, await ctx.Db.CustomerContracts.CountAsync());
    }
}
