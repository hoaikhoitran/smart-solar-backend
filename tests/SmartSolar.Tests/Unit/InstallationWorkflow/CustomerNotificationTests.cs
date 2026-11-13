using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Customer in-app notifications of the pre-installation flows (quotation, contract, installation): written in the transaction of the
/// business change, to the customer only, once per event; a refused or no-op transition writes none.
/// </summary>
public sealed class CustomerNotificationTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static Task<List<AfterSalesNotification>> OfAsync(InstallationWorkflowTestContext ctx, Guid user, string type)
        => ctx.Db.AfterSalesNotifications.AsNoTracking().Where(n => n.RecipientUserId == user && n.Type == type).ToListAsync();

    private static Task<int> CountAsync(InstallationWorkflowTestContext ctx) => ctx.Db.AfterSalesNotifications.AsNoTracking().CountAsync();

    private static readonly string[] ProjectTypes =
    [
        CustomerNotificationTypes.InstallationScheduled, CustomerNotificationTypes.InstallationStarted, CustomerNotificationTypes.InstallationAcceptanceRequired,
        CustomerNotificationTypes.InstallationReworkRequired, CustomerNotificationTypes.InstallationCancelled,
    ];

    [Fact]
    public async Task An_issued_quotation_notifies_its_customer_once_and_a_refused_second_issue_adds_nothing()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (s, issued) = await QuotationTests.IssuedAsync(ctx);
        var customer = s.Request.Customer.Id;

        var notification = Assert.Single(await OfAsync(ctx, customer, CustomerNotificationTypes.QuotationIssued));
        var stored = await ctx.Db.Quotations.AsNoTracking().SingleAsync(q => q.Id == issued.QuotationId);
        Assert.Contains(stored.QuoteNumber, notification.Message);
        Assert.Contains($"{stored.TotalAmount:N0}", notification.Message);
        Assert.Null(notification.ReadAt);
        Assert.Equal(ctx.Clock.GetUtcNow(), notification.CreatedAt);
        // Only the customer: the salesperson and the technician get nothing about it.
        Assert.Equal(1, await ctx.Db.AfterSalesNotifications.AsNoTracking().CountAsync(n => n.Type == CustomerNotificationTypes.QuotationIssued));

        var again = await ctx.Quotations().IssueAsync(s.Sales.Id, issued.QuotationId, issued.Revision, None);
        Assert.Equal(QuotationErrorCodes.NotDraft, again.ErrorCode);
        Assert.Single(await OfAsync(ctx, customer, CustomerNotificationTypes.QuotationIssued));
    }

    [Fact]
    public async Task An_automatically_issued_quotation_notifies_the_customer_like_a_manual_one()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var survey = await ctx.ConfirmedSurveyAsync();
        var catalog = await AutomaticQuotationFixture.SeedCatalogAsync(ctx);

        var completed = await AutomaticQuotationFixture.CompleteAsync(ctx, survey, catalog.StandardBill());

        Assert.True(completed.Succeeded, completed.Message);
        Assert.Equal(AutomaticQuotationOutcomes.Issued, completed.Value!.AutomaticQuotationOutcome);
        var stored = Assert.Single(await AutomaticQuotationFixture.QuotationsAsync(ctx, survey.Request.Request.Id));
        var notification = Assert.Single(await OfAsync(ctx, survey.Request.Customer.Id, CustomerNotificationTypes.QuotationIssued));
        Assert.Contains(stored.QuoteNumber, notification.Message);
        Assert.Equal(1, await ctx.Db.AfterSalesNotifications.AsNoTracking().CountAsync(n => n.Type == CustomerNotificationTypes.QuotationIssued));
    }

    [Fact]
    public async Task Publishing_a_contract_and_rejecting_the_signed_copy_notify_the_customer_while_issuing_does_not()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var r = await ContractTests.AcceptedQuotationAsync(ctx);
        var customer = r.Survey.Request.Customer.Id;
        var before = await CountAsync(ctx);

        var published = await ContractTests.PublishedAsync(ctx, r);                     // create → issue → attach PDF → publish
        var awaiting = Assert.Single(await OfAsync(ctx, customer, CustomerNotificationTypes.ContractAwaitingSignature));
        Assert.Contains("sign", awaiting.Message);
        Assert.Equal(before + 1, await CountAsync(ctx));                                   // issuing (internal step) notified nobody

        var under = await ContractTests.SubmitSignedAsync(ctx, r, published);
        Assert.Equal(before + 1, await CountAsync(ctx));                                   // the customer's own submission: no self-notification
        var rejected = await ctx.Contracts().ReviewAsync(r.Survey.Sales.Id, false, under.ContractId, under.Revision, ReviewDecision.Reject, "Page 2 not signed", None);
        Assert.True(rejected.Succeeded, rejected.Message);
        Assert.Contains("Page 2 not signed", Assert.Single(await OfAsync(ctx, customer, CustomerNotificationTypes.ContractSignatureRejected)).Message);
        Assert.Empty(await OfAsync(ctx, customer, CustomerNotificationTypes.ContractSigned));

        // A second review of the same submission is refused and notifies nobody.
        var total = await CountAsync(ctx);
        Assert.False((await ctx.Contracts().ReviewAsync(r.Survey.Sales.Id, false, under.ContractId, rejected.Value!.Revision, ReviewDecision.Reject, "late", None)).Succeeded);
        Assert.Equal(total, await CountAsync(ctx));
    }

    [Fact]
    public async Task An_approved_signed_copy_notifies_the_customer_that_the_contract_is_signed()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (r, contract) = await ContractTests.SignedAsync(ctx);
        var customer = r.Survey.Request.Customer.Id;
        Assert.Single(await OfAsync(ctx, customer, CustomerNotificationTypes.ContractAwaitingSignature));
        var signed = Assert.Single(await OfAsync(ctx, customer, CustomerNotificationTypes.ContractSigned));
        Assert.Contains(contract.ContractNumber, signed.Message);
        Assert.Empty(await OfAsync(ctx, customer, CustomerNotificationTypes.ContractSignatureRejected));
    }

    [Fact]
    public async Task A_confirmed_schedule_the_start_the_completion_report_and_a_rework_notify_the_customer()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, _) = await InstallationTests.ScheduledAsync(ctx);
        var customer = paid.CustomerId;
        var scheduled = Assert.Single(await OfAsync(ctx, customer, CustomerNotificationTypes.InstallationScheduled));
        Assert.Contains("08:00", scheduled.Message);                                        // Wed 08:00 in the business time zone
        // The Manager's assignment alone (not yet confirmed) notified nobody; the technician's acceptance did.
        Assert.Equal(1, await ctx.Db.AfterSalesNotifications.AsNoTracking().CountAsync(n => ProjectTypes.Contains(n.Type)));

        await InstallationTests.StartAsync(ctx, project.ProjectId, tech);
        Assert.Single(await OfAsync(ctx, customer, CustomerNotificationTypes.InstallationStarted));
        await InstallationTests.AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        Assert.True((await InstallationTests.RegisterPanelAndInverterAsync(ctx, project.ProjectId, tech)).Succeeded);
        var reported = (await ctx.Installations().ReportCompletionAsync(tech, project.ProjectId, "Done", null, None)).Value!;
        Assert.Contains("round 1", Assert.Single(await OfAsync(ctx, customer, CustomerNotificationTypes.InstallationAcceptanceRequired)).Message);

        var rework = await ctx.Installations().ReturnToReworkAsync(paid.ManagerId, project.ProjectId, reported.Revision, "Report incomplete", None);
        Assert.True(rework.Succeeded, rework.Message);
        Assert.Contains("Report incomplete", Assert.Single(await OfAsync(ctx, customer, CustomerNotificationTypes.InstallationReworkRequired)).Message);

        // A repeated acceptance (no-op) and a rework of a project no longer awaiting acceptance (refused) add nothing.
        var total = await CountAsync(ctx);
        Assert.True((await ctx.Installations().AcceptAssignmentAsync(tech, project.ProjectId, null, None)).Succeeded);
        Assert.False((await ctx.Installations().ReturnToReworkAsync(paid.ManagerId, project.ProjectId, rework.Value!.Revision, "again", None)).Succeeded);
        Assert.Equal(total, await CountAsync(ctx));
        Assert.All(await ctx.Db.AfterSalesNotifications.AsNoTracking().Where(n => ProjectTypes.Contains(n.Type)).ToListAsync(),
            n => Assert.Equal(customer, n.RecipientUserId));
    }

    [Fact]
    public async Task A_cancelled_project_notifies_the_customer_with_the_reason_and_a_repeated_cancel_adds_nothing()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var paid = await InstallationTests.PaidContractAsync(ctx);
        var project = (await ctx.Installations().CreateProjectAsync(paid.ManagerId, paid.ContractId, None)).Value!;
        Assert.Empty(await OfAsync(ctx, paid.CustomerId, CustomerNotificationTypes.InstallationCancelled));

        var cancelled = await ctx.Installations().CancelAsync(paid.ManagerId, project.ProjectId, project.Revision, "Customer moved abroad", None);
        Assert.True(cancelled.Succeeded, cancelled.Message);
        Assert.Contains("Customer moved abroad", Assert.Single(await OfAsync(ctx, paid.CustomerId, CustomerNotificationTypes.InstallationCancelled)).Message);

        Assert.True((await ctx.Installations().CancelAsync(paid.ManagerId, project.ProjectId, cancelled.Value!.Revision, "again", None)).Succeeded);   // no-op
        Assert.Single(await OfAsync(ctx, paid.CustomerId, CustomerNotificationTypes.InstallationCancelled));
    }

    [Fact]
    public async Task A_manager_approved_equipment_substitution_asks_the_customer_for_approval()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (paid, project, tech, _) = await InstallationTests.ScheduledAsync(ctx);
        var installs = ctx.Installations();
        await InstallationTests.StartAsync(ctx, project.ProjectId, tech);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, project.ProjectId, tech);
        var quotationId = (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == project.ContractId)).QuotationId;
        var lines = await ctx.Db.QuotationLines.AsNoTracking().Where(l => l.QuotationId == quotationId).ToListAsync();
        var panel = lines.Single(l => l.Quantity == 12m).ProductId!.Value;
        var otherInverter = await QuotationTests.InverterAsync(ctx, "SUN-OTHER", 14_000_000m);
        var assets = Enumerable.Range(1, 12).Select(i => new AssetInput("PANEL", panel, "Jinko", "Tiger Neo", $"SUB-PNL-{i:00}", null, null))
            .Append(new AssetInput("INVERTER", otherInverter.Id, "Huawei", "SUN-OTHER", "SUB-INV-01", null, null)).ToList();
        Assert.True((await installs.RegisterAssetsAsync(tech, project.ProjectId, assets, None)).Succeeded);
        var staff = (await installs.GetForManagerAsync(project.ProjectId, None)).Value!;
        Assert.Empty(await OfAsync(ctx, paid.CustomerId, CustomerNotificationTypes.EquipmentSubstitutionApprovalRequired));

        var approved = await installs.ApproveSubstitutionAsync(paid.ManagerId, project.ProjectId, staff.Revision, "Contracted model out of stock; same rating", None);
        Assert.True(approved.Succeeded, approved.Message);
        Assert.Contains("out of stock", Assert.Single(await OfAsync(ctx, paid.CustomerId, CustomerNotificationTypes.EquipmentSubstitutionApprovalRequired)).Message);
    }
}
