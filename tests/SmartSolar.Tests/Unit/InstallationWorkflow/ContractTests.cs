using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

public sealed class ContractTests
{
    public sealed record Ready(AcknowledgedSurvey Survey, Guid QuotationId, decimal Total);

    public static async Task<Ready> AcceptedQuotationAsync(InstallationWorkflowTestContext ctx, string customer = "Customer")
    {
        var (s, issued) = await QuotationTests.IssuedAsync(ctx, customer);
        Assert.True((await ctx.Quotations().AcceptAsync(s.Request.Customer.Id, issued.QuotationId, CancellationToken.None)).Succeeded);
        return new Ready(s, issued.QuotationId, issued.TotalAmount);
    }

    /// <summary>Created, issued, official PDF attached with the canonical hash, published: AWAITING_SIGNATURE.</summary>
    public static async Task<ContractView> PublishedAsync(InstallationWorkflowTestContext ctx, Ready r)
    {
        var c = ctx.Contracts();
        var draft = (await c.CreateAsync(r.Survey.Sales.Id, r.QuotationId, "Standard terms", CancellationToken.None)).Value!;
        var issued = (await c.IssueAsync(r.Survey.Sales.Id, draft.ContractId, draft.Revision, CancellationToken.None)).Value!;
        var pdf = await ctx.VerifiedFileAsync(r.Survey.Sales.Id, FilePurposes.ContractDocument, "pdf");
        var attached = await c.AttachOfficialDocumentAsync(r.Survey.Sales.Id, issued.ContractId, issued.Revision, pdf, issued.CanonicalHash!, CancellationToken.None);
        Assert.True(attached.Succeeded, attached.Message);
        var published = await c.PublishAsync(r.Survey.Sales.Id, issued.ContractId, attached.Value!.Revision, CancellationToken.None);
        Assert.True(published.Succeeded, published.Message);
        return published.Value!;
    }

    public static async Task<ContractView> SubmitSignedAsync(InstallationWorkflowTestContext ctx, Ready r, ContractView contract)
    {
        var scan = await ctx.VerifiedFileAsync(r.Survey.Request.Customer.Id, FilePurposes.SignedContract, "pdf");
        var submitted = await ctx.Contracts().SubmitSignedDocumentAsync(r.Survey.Request.Customer.Id, contract.ContractId, scan,
            contract.OfficialDocument!.Sha256!, CancellationToken.None);
        Assert.True(submitted.Succeeded, submitted.Message);
        return submitted.Value!;
    }

    /// <summary>All the way to SIGNED.</summary>
    public static async Task<(Ready Ready, ContractView Contract)> SignedAsync(InstallationWorkflowTestContext ctx, string customer = "Customer")
    {
        var r = await AcceptedQuotationAsync(ctx, customer);
        var under = await SubmitSignedAsync(ctx, r, await PublishedAsync(ctx, r));
        var signed = await ctx.Contracts().ReviewAsync(r.Survey.Sales.Id, false, under.ContractId, under.Revision, ReviewDecision.Approve, null, CancellationToken.None);
        Assert.True(signed.Succeeded, signed.Message);
        return (r, signed.Value!);
    }

    [Fact]
    public async Task Contract_goes_from_accepted_quotation_to_signed_with_snapshots_and_audit()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (r, signed) = await SignedAsync(ctx);

        Assert.Equal(ContractStatuses.Signed, signed.Status);
        Assert.Equal(r.Total, signed.TotalAmount);
        Assert.Equal(2, signed.PricingSnapshot.GetProperty("lines").GetArrayLength());
        Assert.StartsWith("HD-", signed.ContractNumber);
        Assert.Equal(64, signed.CanonicalHash!.Length);
        Assert.Equal(SignatureOutcomes.Approved, Assert.Single(signed.Submissions!).Outcome);
        Assert.Contains(signed.Events!, e => e.ToStatus == ContractStatuses.Signed);
        Assert.Contains(signed.Events!, e => e.ToStatus == ContractStatuses.AwaitingSignature);
    }

    [Fact]
    public async Task Contracts_need_an_accepted_quotation_and_only_one_live_contract_exists()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (s, issued) = await QuotationTests.IssuedAsync(ctx);
        var c = ctx.Contracts();

        Assert.Equal(ContractErrorCodes.QuotationNotAccepted, (await c.CreateAsync(s.Sales.Id, issued.QuotationId, null, CancellationToken.None)).ErrorCode);
        await ctx.Quotations().AcceptAsync(s.Request.Customer.Id, issued.QuotationId, CancellationToken.None);
        Assert.Equal(ContractErrorCodes.NotFound, (await c.CreateAsync(Guid.NewGuid(), issued.QuotationId, null, CancellationToken.None)).ErrorCode);
        Assert.True((await c.CreateAsync(s.Sales.Id, issued.QuotationId, null, CancellationToken.None)).Succeeded);
        Assert.Equal(ContractErrorCodes.AlreadyExists, (await c.CreateAsync(s.Sales.Id, issued.QuotationId, null, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Official_pdf_must_match_the_issued_canonical_version_before_publishing()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var r = await AcceptedQuotationAsync(ctx);
        var c = ctx.Contracts();
        var draft = (await c.CreateAsync(r.Survey.Sales.Id, r.QuotationId, "Terms", CancellationToken.None)).Value!;
        var pdf = await ctx.VerifiedFileAsync(r.Survey.Sales.Id, FilePurposes.ContractDocument, "pdf");

        Assert.Equal(ContractErrorCodes.InvalidState, (await c.AttachOfficialDocumentAsync(r.Survey.Sales.Id, draft.ContractId, draft.Revision, pdf, "x", CancellationToken.None)).ErrorCode);
        var issued = (await c.IssueAsync(r.Survey.Sales.Id, draft.ContractId, draft.Revision, CancellationToken.None)).Value!;
        Assert.Equal(ContractErrorCodes.InvalidState, (await c.UpdateTermsAsync(r.Survey.Sales.Id, issued.ContractId, issued.Revision, "changed", CancellationToken.None)).ErrorCode);
        Assert.Equal(ContractErrorCodes.DocumentMissing, (await c.PublishAsync(r.Survey.Sales.Id, issued.ContractId, issued.Revision, CancellationToken.None)).ErrorCode);
        Assert.Equal(ContractErrorCodes.CanonicalHashMismatch,
            (await c.AttachOfficialDocumentAsync(r.Survey.Sales.Id, issued.ContractId, issued.Revision, pdf, new string('0', 64), CancellationToken.None)).ErrorCode);
        // Customers never see drafts or unpublished contracts.
        Assert.Empty(await c.ListForCustomerAsync(r.Survey.Request.Customer.Id, CancellationToken.None));
        Assert.Equal(ContractErrorCodes.NotFound, (await c.GetForCustomerAsync(r.Survey.Request.Customer.Id, issued.ContractId, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Signed_scan_must_reference_the_published_pdf_and_belong_to_the_customer()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var r = await AcceptedQuotationAsync(ctx);
        var published = await PublishedAsync(ctx, r);
        var c = ctx.Contracts();
        var customer = r.Survey.Request.Customer.Id;
        var scan = await ctx.VerifiedFileAsync(customer, FilePurposes.SignedContract, "pdf");
        var foreign = await ctx.VerifiedFileAsync(r.Survey.Sales.Id, FilePurposes.SignedContract, "pdf");

        Assert.Equal(ContractErrorCodes.WrongDocumentVersion,
            (await c.SubmitSignedDocumentAsync(customer, published.ContractId, scan, new string('a', 64), CancellationToken.None)).ErrorCode);
        Assert.Equal(FileErrorCodes.NotOwned,
            (await c.SubmitSignedDocumentAsync(customer, published.ContractId, foreign, published.OfficialDocument!.Sha256!, CancellationToken.None)).ErrorCode);
        Assert.Equal(ContractErrorCodes.NotFound,
            (await c.SubmitSignedDocumentAsync(Guid.NewGuid(), published.ContractId, scan, published.OfficialDocument.Sha256!, CancellationToken.None)).ErrorCode);
        var under = await c.SubmitSignedDocumentAsync(customer, published.ContractId, scan, published.OfficialDocument.Sha256!, CancellationToken.None);
        Assert.Equal(ContractStatuses.UnderReview, under.Value!.Status);
    }

    [Fact]
    public async Task Rejected_signatures_keep_history_and_the_customer_can_resubmit()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var r = await AcceptedQuotationAsync(ctx);
        var under = await SubmitSignedAsync(ctx, r, await PublishedAsync(ctx, r));
        var c = ctx.Contracts();

        Assert.Equal(ContractErrorCodes.ReasonRequired,
            (await c.ReviewAsync(r.Survey.Sales.Id, false, under.ContractId, under.Revision, ReviewDecision.Reject, " ", CancellationToken.None)).ErrorCode);
        var rejected = (await c.ReviewAsync(r.Survey.Sales.Id, false, under.ContractId, under.Revision, ReviewDecision.Reject, "Page 2 not signed", CancellationToken.None)).Value!;
        Assert.Equal(ContractStatuses.SignatureRejected, rejected.Status);
        Assert.Equal("Page 2 not signed", rejected.RejectionReason);

        var again = await SubmitSignedAsync(ctx, r, rejected);
        Assert.Equal(ContractStatuses.UnderReview, again.Status);
        Assert.Equal(2, again.Submissions!.Count);
        Assert.Equal(SignatureOutcomes.Rejected, again.Submissions[0].Outcome);
        Assert.Equal(SignatureOutcomes.Pending, again.Submissions[1].Outcome);
    }

    [Fact]
    public async Task Only_the_owning_sales_or_a_manager_reviews_and_double_approval_is_rejected()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var r = await AcceptedQuotationAsync(ctx);
        var under = await SubmitSignedAsync(ctx, r, await PublishedAsync(ctx, r));
        var c = ctx.Contracts();

        Assert.Equal(ContractErrorCodes.NotFound,
            (await c.ReviewAsync(Guid.NewGuid(), false, under.ContractId, under.Revision, ReviewDecision.Approve, null, CancellationToken.None)).ErrorCode);
        Assert.Equal(ContractErrorCodes.NotFound,
            (await c.ReviewAsync(r.Survey.Request.Customer.Id, false, under.ContractId, under.Revision, ReviewDecision.Approve, null, CancellationToken.None)).ErrorCode);
        var managerUser = await ctx.Scheduling.Inner.SeedUserAsync("Manager");
        var manager = await c.ReviewAsync(managerUser.Id, true, under.ContractId, under.Revision, ReviewDecision.Approve, null, CancellationToken.None);
        Assert.Equal(ContractStatuses.Signed, manager.Value!.Status);

        var second = await c.ReviewAsync(r.Survey.Sales.Id, false, under.ContractId, under.Revision, ReviewDecision.Reject, "late", CancellationToken.None);
        Assert.Equal(ContractErrorCodes.StaleRevision, second.ErrorCode);
        Assert.Equal(ContractStatuses.Signed, (await c.GetForStaffAsync(r.Survey.Sales.Id, false, under.ContractId, CancellationToken.None)).Value!.Status);
    }

    [Fact]
    public async Task Amendments_create_a_new_version_that_supersedes_the_previous_one_on_issue()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var r = await AcceptedQuotationAsync(ctx);
        var v1 = await PublishedAsync(ctx, r);
        var c = ctx.Contracts();

        var v2 = (await c.CreateAmendmentAsync(r.Survey.Sales.Id, v1.ContractId, "Installation window changed", CancellationToken.None)).Value!;
        Assert.Equal(2, v2.Version);
        Assert.Equal(v1.ContractNumber, v2.ContractNumber);
        Assert.Equal(v1.TotalAmount, v2.TotalAmount);
        Assert.Equal(ContractErrorCodes.AlreadyExists, (await c.CreateAmendmentAsync(r.Survey.Sales.Id, v1.ContractId, "again", CancellationToken.None)).ErrorCode);

        var issued = (await c.IssueAsync(r.Survey.Sales.Id, v2.ContractId, v2.Revision, CancellationToken.None)).Value!;
        Assert.NotEqual(v1.CanonicalHash, issued.CanonicalHash);
        Assert.Equal(ContractStatuses.Superseded, (await c.GetForStaffAsync(r.Survey.Sales.Id, false, v1.ContractId, CancellationToken.None)).Value!.Status);
        // A scan of the superseded PDF can no longer be submitted.
        var scan = await ctx.VerifiedFileAsync(r.Survey.Request.Customer.Id, FilePurposes.SignedContract, "pdf");
        Assert.Equal(ContractErrorCodes.InvalidState, (await c.SubmitSignedDocumentAsync(r.Survey.Request.Customer.Id, v1.ContractId, scan,
            v1.OfficialDocument!.Sha256!, CancellationToken.None)).ErrorCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Approval_is_refused_when_a_signed_or_official_document_changed_after_verification(bool signedScan)
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        ctx.Storage.Versioned = false;                       // a bucket where an upload replaces bytes in place
        ctx.StorageOptions.S3.RequireObjectVersioning = false;
        var ready = await AcceptedQuotationAsync(ctx);
        var published = await PublishedAsync(ctx, ready);
        var submitted = await SubmitSignedAsync(ctx, ready, published);

        var scanId = (await ctx.Db.ContractSignatureSubmissions.AsNoTracking().SingleAsync(x => x.ContractId == submitted.ContractId)).FileId;
        var target = await ctx.Db.StoredFiles.FindAsync(signedScan ? scanId : published.OfficialDocument!.FileId);
        ctx.Db.ChangeTracker.Clear();
        ctx.Storage.Put(target!.Bucket, target.ObjectKey, "%PDF-1.7\n% forged after verification!\n"u8.ToArray()[..FakeStorageObjectVerifier.PdfBytes.Length]);

        var review = await ctx.Contracts().ReviewAsync(ready.Survey.Sales.Id, false, submitted.ContractId, submitted.Revision, ReviewDecision.Approve, null, CancellationToken.None);

        Assert.Equal(FileErrorCodes.ContentChanged, review.ErrorCode);
        Assert.Equal(ContractStatuses.UnderReview, (await ctx.Contracts().GetForStaffAsync(ready.Survey.Sales.Id, false, submitted.ContractId, CancellationToken.None)).Value!.Status);
        // Rejection does not need the documents to be intact.
        Assert.True((await ctx.Contracts().ReviewAsync(ready.Survey.Sales.Id, false, submitted.ContractId, submitted.Revision, ReviewDecision.Reject,
            "Document changed", CancellationToken.None)).Succeeded);
    }
}
