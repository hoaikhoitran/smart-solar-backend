using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Api.Controllers;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.InstallationWorkflow;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// DS-02b: the official PDF and the signed scans of every contract version the caller may read are downloadable through short-lived presigned
/// URLs (older, SUPERSEDED versions included as history). Downloading changes nothing: paying still requires the SIGNED status. Access follows
/// the contract views (customer owner from AWAITING_SIGNATURE on; Sales owner or Manager for any version); anyone else gets 404.
/// </summary>
public sealed class ContractDocumentUrlTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static (ContractDocumentHandlers Handlers, FakeStorageUrlSigner Signer) Handlers(InstallationWorkflowTestContext ctx)
    {
        var signer = new FakeStorageUrlSigner(ctx.Clock);
        var urls = new FileReadUrlService(signer, ctx.Storage, ctx.StorageOptions, ctx.Clock, NullLogger<FileReadUrlService>.Instance);
        return (new ContractDocumentHandlers(new ContractUnitOfWork(ctx.Db), new FileStore(ctx.Db), urls, NullLogger<ContractDocumentHandlers>.Instance), signer);
    }

    [Fact]
    public async Task The_customer_and_every_staff_reader_download_the_official_pdf_other_customers_are_not_found()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var r = await ContractTests.AcceptedQuotationAsync(ctx);
        var published = await ContractTests.PublishedAsync(ctx, r);
        var customer = r.Survey.Request.Customer.Id;
        var sales = r.Survey.Sales.Id;
        var otherSales = (await ctx.Scheduling.SeedSalesAsync("Other sales")).Id;
        var manager = (await ctx.Scheduling.Inner.SeedUserAsync("Manager")).Id;
        var stranger = (await ctx.Scheduling.Inner.SeedUserAsync("Stranger")).Id;
        var (handlers, signer) = Handlers(ctx);

        var inline = await handlers.OfficialDocumentUrlAsync(ContractViewer.Customer, customer, false, published.ContractId, StorageUrlDisposition.Inline, None);
        var attachment = await handlers.OfficialDocumentUrlAsync(ContractViewer.Staff, sales, false, published.ContractId, StorageUrlDisposition.Attachment, None);
        var byManager = await handlers.OfficialDocumentUrlAsync(ContractViewer.Staff, manager, true, published.ContractId, StorageUrlDisposition.Inline, None);

        Assert.True(inline.Succeeded, inline.Message);
        Assert.Equal((published.OfficialDocument!.FileId, "inline", FilePolicies.Pdf), (inline.Value!.Id, inline.Value.Disposition, inline.Value.ContentType));
        Assert.StartsWith(FakeStorageUrlSigner.Host, inline.Value.Url);
        Assert.EndsWith(".pdf", inline.Value.FileName);
        Assert.Equal("attachment", attachment.Value!.Disposition);
        Assert.True(byManager.Succeeded, byManager.Message);
        // Signed from the stored reference, pinned to the verified object version; the key stays under the purpose prefix.
        Assert.All(signer.Calls, c => Assert.StartsWith("contracts/official/", c.Location.ObjectKey));
        Assert.All(signer.Calls, c => Assert.NotNull(c.Location.VersionId));

        // Staff read-all (2026-11-19): Sales of another request (and Technicians, through the API roles) read every contract document.
        Assert.True((await handlers.OfficialDocumentUrlAsync(ContractViewer.Staff, otherSales, false, published.ContractId, StorageUrlDisposition.Inline, None)).Succeeded);
        foreach (var refused in new[]
        {
            await handlers.OfficialDocumentUrlAsync(ContractViewer.Customer, stranger, false, published.ContractId, StorageUrlDisposition.Inline, None),
            await handlers.OfficialDocumentUrlAsync(ContractViewer.Customer, customer, false, Guid.NewGuid(), StorageUrlDisposition.Inline, None),
            await handlers.OfficialDocumentUrlAsync(ContractViewer.Staff, otherSales, false, Guid.NewGuid(), StorageUrlDisposition.Inline, None),
        })
        {
            Assert.Equal(ContractErrorCodes.NotFound, refused.ErrorCode);
            Assert.Equal(404, OperationResultControllerBase.StatusFor(refused.ErrorCode!));
        }
    }

    [Fact]
    public async Task A_superseded_version_stays_downloadable_for_the_customer_a_draft_is_invisible_and_payment_still_needs_signed()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (r, signed) = await ContractTests.SignedAsync(ctx);
        var customer = r.Survey.Request.Customer.Id;
        var (handlers, _) = Handlers(ctx);

        // An amendment draft is made from the signed version (a draft is not customer-visible and has no PDF yet), then the signed version is
        // superseded: its PDF remains the customer's history.
        var draft = (await ctx.Contracts().CreateAmendmentAsync(r.Survey.Sales.Id, signed.ContractId, "Amended terms", None)).Value!;
        var old = await ctx.Db.CustomerContracts.SingleAsync(c => c.Id == signed.ContractId);
        old.Status = ContractStatuses.Superseded;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        var oldPdf = await handlers.OfficialDocumentUrlAsync(ContractViewer.Customer, customer, false, signed.ContractId, StorageUrlDisposition.Attachment, None);
        Assert.True(oldPdf.Succeeded, oldPdf.Message);
        Assert.Equal(signed.OfficialDocument!.FileId, oldPdf.Value!.Id);
        // Downloading is read-only: the superseded version cannot be paid (payment is SIGNED-only).
        var pay = await ctx.Payments().CreateOrGetAttemptAsync(customer, signed.ContractId, None);
        Assert.False(pay.Succeeded);
        Assert.Equal(ContractStatuses.Superseded, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == signed.ContractId)).Status);

        // The draft is not customer-visible (404) and has no document yet for staff (409).
        Assert.Equal(ContractErrorCodes.NotFound, (await handlers.OfficialDocumentUrlAsync(ContractViewer.Customer, customer, false, draft.ContractId, StorageUrlDisposition.Inline, None)).ErrorCode);
        var missing = await handlers.OfficialDocumentUrlAsync(ContractViewer.Staff, r.Survey.Sales.Id, false, draft.ContractId, StorageUrlDisposition.Inline, None);
        Assert.Equal(ContractErrorCodes.DocumentMissing, missing.ErrorCode);
        Assert.Equal(409, OperationResultControllerBase.StatusFor(missing.ErrorCode!));
    }

    [Fact]
    public async Task Signed_scans_are_downloadable_per_submission_and_an_unknown_submission_is_not_found()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var r = await ContractTests.AcceptedQuotationAsync(ctx);
        var under = await ContractTests.SubmitSignedAsync(ctx, r, await ContractTests.PublishedAsync(ctx, r));
        var customer = r.Survey.Request.Customer.Id;
        var submission = Assert.Single(under.Submissions!);
        var (handlers, signer) = Handlers(ctx);

        var scan = await handlers.SubmissionUrlAsync(ContractViewer.Customer, customer, false, under.ContractId, submission.SubmissionId, StorageUrlDisposition.Inline, None);
        var staff = await handlers.SubmissionUrlAsync(ContractViewer.Staff, r.Survey.Sales.Id, false, under.ContractId, submission.SubmissionId, StorageUrlDisposition.Attachment, None);
        var unknown = await handlers.SubmissionUrlAsync(ContractViewer.Customer, customer, false, under.ContractId, Guid.NewGuid(), StorageUrlDisposition.Inline, None);

        Assert.True(scan.Succeeded, scan.Message);
        Assert.Equal(submission.FileId, scan.Value!.Id);
        Assert.True(staff.Succeeded, staff.Message);
        Assert.All(signer.Calls, c => Assert.StartsWith("contracts/signed/", c.Location.ObjectKey));
        Assert.Equal(ContractErrorCodes.SubmissionNotFound, unknown.ErrorCode);
        Assert.Equal(404, OperationResultControllerBase.StatusFor(unknown.ErrorCode!));
    }
}
