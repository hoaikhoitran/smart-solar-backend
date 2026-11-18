using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Quotations;

namespace SmartSolar.Tests.TestSupport;

/// <summary>The technician records the customer's on-site decision on an installation quotation at its CURRENT revision (what the device shows).</summary>
public static class OnSiteDecision
{
    public static async Task<OperationResult<QuotationView>> RecordAsync(InstallationWorkflowTestContext ctx, Guid technicianUserId, Guid quotationId, string decision,
        string? note = null)
    {
        var revision = (await ctx.Db.Quotations.AsNoTracking().SingleAsync(q => q.Id == quotationId)).Revision;
        return await ctx.Quotations().RecordOnSiteDecisionAsync(technicianUserId, quotationId, new OnSiteDecisionInput(decision, revision, note), CancellationToken.None);
    }

    public static Task<OperationResult<QuotationView>> AgreeAsync(InstallationWorkflowTestContext ctx, Guid technicianUserId, Guid quotationId)
        => RecordAsync(ctx, technicianUserId, quotationId, OnSiteDecisions.Agreed);

    public static Task<OperationResult<QuotationView>> DeclineAsync(InstallationWorkflowTestContext ctx, Guid technicianUserId, Guid quotationId, string note = "Not now")
        => RecordAsync(ctx, technicianUserId, quotationId, OnSiteDecisions.Declined, note);

    /// <summary>
    /// Pays an AGREED quotation on site in cash (the technician opens it, the Manager records the receipt with verified evidence) and returns the id
    /// of the contract the verified payment created.
    /// </summary>
    public static async Task<Guid> PayInCashAsync(InstallationWorkflowTestContext ctx, Guid technicianUserId, Guid quotationId, Guid managerUserId)
    {
        await ctx.EnsureInstallationWarrantyOptionAsync();
        var opened = await ctx.OnSitePayments().StartAsync(technicianUserId, quotationId, "CASH", CancellationToken.None);
        Assert.True(opened.Succeeded, opened.Message);
        var evidence = await ctx.VerifiedFileAsync(managerUserId, SmartSolar.Modules.Files.FilePurposes.PaymentEvidence);
        var confirmed = await ctx.PaymentsWithCash().ConfirmCashAttemptAsync(managerUserId, opened.Value!.Current!.PaymentAttemptId,
            new SmartSolar.Modules.Payments.CashConfirmation(opened.Value.Amount, $"PT-{quotationId:N}"[..20], evidence, null), CancellationToken.None);
        Assert.True(confirmed.Succeeded, confirmed.Message);
        return (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.QuotationId == quotationId)).Id;
    }

    public static Task<OperationResult<QuotationView>> RevisionAsync(InstallationWorkflowTestContext ctx, Guid technicianUserId, Guid quotationId, string note)
        => RecordAsync(ctx, technicianUserId, quotationId, OnSiteDecisions.RevisionNeeded, note);
}
