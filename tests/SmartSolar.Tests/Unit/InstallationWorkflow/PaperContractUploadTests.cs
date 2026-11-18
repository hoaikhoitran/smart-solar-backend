using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// INSTALL-CONTRACT (2026-11-18): the contract is a physically signed paper. The customer signs the printed official PDF in person; the Sales
/// owner of the request (or a Manager/Admin) uploads the scan. The upload only moves the version to UNDER_REVIEW (never SIGNED by itself), it must
/// be the scan of the current official PDF, every upload is kept as history, and a Sales user of another request cannot see or upload anything.
/// </summary>
public sealed class PaperContractUploadTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed record Published(SimplifiedFlowFixture.Cart Cart, UserAccount Manager, UserAccount Sales, ContractView Contract);

    /// <summary>Issued cart → accepted → published (AWAITING_SIGNATURE), with a Sales user owning the survey request.</summary>
    private static async Task<Published> PublishedWithSalesOwnerAsync(InstallationWorkflowTestContext ctx)
    {
        var cart = await SimplifiedFlowFixture.IssuedAsync(ctx);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");
        var sales = await ctx.Scheduling.Inner.SeedUserAsync("Sales owner");
        var request = await ctx.Db.SurveyRequests.SingleAsync(r => r.Id == cart.RequestId);
        request.AssignedSaleId = sales.Id;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        var published = await SimplifiedFlowFixture.PublishedAsync(ctx, manager, await SimplifiedFlowFixture.AcceptAsync(ctx, cart));
        Assert.Equal(ContractStatuses.AwaitingSignature, published.Status);
        return new Published(cart, manager, sales, published);
    }

    [Fact]
    public async Task INSTALL_CONTRACT_001_Sales_owner_uploads_the_paper_scan_and_only_the_manager_review_makes_it_signed()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var p = await PublishedWithSalesOwnerAsync(ctx);
        var scan = await ctx.VerifiedFileAsync(p.Sales.Id, FilePurposes.SignedContract, "pdf");
        var signedOn = DateOnly.FromDateTime(ctx.Clock.GetUtcNow().UtcDateTime);

        var uploaded = await ctx.Contracts().SubmitPaperSignedDocumentAsync(p.Sales.Id, false, p.Contract.ContractId, scan, p.Contract.OfficialDocument!.Sha256!,
            signedOn, "Ký tại văn phòng", None);

        Assert.True(uploaded.Succeeded, uploaded.Message);
        Assert.Equal(ContractStatuses.UnderReview, uploaded.Value!.Status);
        Assert.Null(uploaded.Value.SignedAt);
        var submission = await ctx.Db.ContractSignatureSubmissions.AsNoTracking().SingleAsync(s => s.ContractId == p.Contract.ContractId);
        Assert.Equal((p.Sales.Id, scan, SignatureOutcomes.Pending), (submission.SubmittedByUserId, submission.FileId, submission.Outcome));
        var moved = await ctx.Db.ContractEvents.AsNoTracking()
            .SingleAsync(e => e.ContractId == p.Contract.ContractId && e.ToStatus == ContractStatuses.UnderReview);
        Assert.Equal(p.Sales.Id, moved.ActorUserId);
        Assert.Contains("paper contract scan uploaded by staff", moved.Detail);
        Assert.Contains($"signed on {signedOn:yyyy-MM-dd}", moved.Detail);

        var signed = await ctx.Contracts().ReviewAsync(p.Manager.Id, true, p.Contract.ContractId, uploaded.Value.Revision, ReviewDecision.Approve, null, None);
        Assert.True(signed.Succeeded, signed.Message);
        Assert.Equal(ContractStatuses.Signed, signed.Value!.Status);
        // The customer sees the same submission (and can download it) as history.
        var mine = (await ctx.Contracts().GetForCustomerAsync(p.Cart.CustomerId, p.Contract.ContractId, None)).Value!;
        Assert.Equal(submission.Id, Assert.Single(mine.Submissions!).SubmissionId);
    }

    [Fact]
    public async Task INSTALL_CONTRACT_002_The_scan_must_be_of_the_current_official_pdf_dated_today_or_earlier_and_a_verified_pdf_of_the_uploader()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var p = await PublishedWithSalesOwnerAsync(ctx);
        var c = ctx.Contracts();
        var sha = p.Contract.OfficialDocument!.Sha256!;
        var scan = await ctx.VerifiedFileAsync(p.Sales.Id, FilePurposes.SignedContract, "pdf");

        Assert.Equal(ContractErrorCodes.WrongDocumentVersion,
            (await c.SubmitPaperSignedDocumentAsync(p.Sales.Id, false, p.Contract.ContractId, scan, new string('0', 64), null, null, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed,
            (await c.SubmitPaperSignedDocumentAsync(p.Sales.Id, false, p.Contract.ContractId, scan, sha,
                DateOnly.FromDateTime(ctx.Clock.GetUtcNow().UtcDateTime.AddDays(3)), null, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed,
            (await c.SubmitPaperSignedDocumentAsync(p.Sales.Id, false, p.Contract.ContractId, scan, sha, null, new string('x', 301), None)).ErrorCode);
        // A file of another purpose, or a scan another user registered, is refused.
        var photo = await ctx.VerifiedFileAsync(p.Sales.Id, FilePurposes.ContractDocument, "pdf");
        Assert.False((await c.SubmitPaperSignedDocumentAsync(p.Sales.Id, false, p.Contract.ContractId, photo, sha, null, null, None)).Succeeded);
        var customersScan = await ctx.VerifiedFileAsync(p.Cart.CustomerId, FilePurposes.SignedContract, "pdf");
        Assert.False((await c.SubmitPaperSignedDocumentAsync(p.Sales.Id, false, p.Contract.ContractId, customersScan, sha, null, null, None)).Succeeded);

        // None of the refusals changed the contract or wrote a submission.
        Assert.Equal(ContractStatuses.AwaitingSignature, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(x => x.Id == p.Contract.ContractId)).Status);
        Assert.Equal(0, await ctx.Db.ContractSignatureSubmissions.CountAsync());
    }

    [Fact]
    public async Task INSTALL_CONTRACT_003_Sales_of_another_request_gets_404_and_a_draft_cannot_receive_a_signed_scan()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var p = await PublishedWithSalesOwnerAsync(ctx);
        var stranger = await ctx.Scheduling.Inner.SeedUserAsync("Other sales");
        var scan = await ctx.VerifiedFileAsync(stranger.Id, FilePurposes.SignedContract, "pdf");

        var refused = await ctx.Contracts().SubmitPaperSignedDocumentAsync(stranger.Id, false, p.Contract.ContractId, scan, p.Contract.OfficialDocument!.Sha256!, null, null, None);
        Assert.Equal(ContractErrorCodes.NotFound, refused.ErrorCode);
        Assert.Equal(ContractErrorCodes.NotFound,
            (await ctx.Contracts().SubmitPaperSignedDocumentAsync(p.Sales.Id, false, Guid.NewGuid(), scan, "x", null, null, None)).ErrorCode);

        // A DRAFT (generated, never printed and signed) is no evidence of a signature: no scan can be attached to it.
        await using var other = InstallationWorkflowTestContext.Create();
        var cart = await SimplifiedFlowFixture.IssuedAsync(other);
        var draft = await SimplifiedFlowFixture.AcceptAsync(other, cart);
        var manager = await other.Scheduling.Inner.SeedUserAsync("Manager");
        var managerScan = await other.VerifiedFileAsync(manager.Id, FilePurposes.SignedContract, "pdf");
        Assert.Equal(ContractErrorCodes.InvalidState,
            (await other.Contracts().SubmitPaperSignedDocumentAsync(manager.Id, true, draft.ContractId, managerScan, "", null, null, None)).ErrorCode);
    }

    [Fact]
    public async Task INSTALL_CONTRACT_004_A_scan_under_review_cannot_be_replaced_and_a_rejected_one_stays_as_history_when_a_new_scan_is_uploaded()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var p = await PublishedWithSalesOwnerAsync(ctx);
        var c = ctx.Contracts();
        var sha = p.Contract.OfficialDocument!.Sha256!;
        var first = await ctx.VerifiedFileAsync(p.Sales.Id, FilePurposes.SignedContract, "pdf");
        var second = await ctx.VerifiedFileAsync(p.Sales.Id, FilePurposes.SignedContract, "pdf");

        var underReview = await c.SubmitPaperSignedDocumentAsync(p.Sales.Id, false, p.Contract.ContractId, first, sha, null, null, None);
        Assert.True(underReview.Succeeded, underReview.Message);
        // Replacing a scan that waits for review is not possible (no silent replacement).
        Assert.Equal(ContractErrorCodes.InvalidState, (await c.SubmitPaperSignedDocumentAsync(p.Sales.Id, false, p.Contract.ContractId, second, sha, null, null, None)).ErrorCode);

        var rejected = await c.ReviewAsync(p.Manager.Id, true, p.Contract.ContractId, underReview.Value!.Revision, ReviewDecision.Reject, "Thiếu chữ ký trang 2", None);
        Assert.Equal(ContractStatuses.SignatureRejected, rejected.Value!.Status);
        // The same file twice is refused; a new scan is accepted and both stay as history.
        Assert.Equal(FileErrorCodes.AlreadyLinked, (await c.SubmitPaperSignedDocumentAsync(p.Sales.Id, false, p.Contract.ContractId, first, sha, null, null, None)).ErrorCode);
        var again = await c.SubmitPaperSignedDocumentAsync(p.Sales.Id, false, p.Contract.ContractId, second, sha, null, null, None);
        Assert.True(again.Succeeded, again.Message);
        Assert.Equal(2, again.Value!.Submissions.Count);
        Assert.Equal([SignatureOutcomes.Rejected, SignatureOutcomes.Pending], again.Value.Submissions.Select(s => s.Outcome).ToArray());
    }

    [Fact]
    public async Task INSTALL_CONTRACT_005_A_manager_may_upload_the_paper_scan_and_the_customer_upload_path_is_unchanged()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var p = await PublishedWithSalesOwnerAsync(ctx);
        var scan = await ctx.VerifiedFileAsync(p.Manager.Id, FilePurposes.SignedContract, "pdf");

        var uploaded = await ctx.Contracts().SubmitPaperSignedDocumentAsync(p.Manager.Id, true, p.Contract.ContractId, scan, p.Contract.OfficialDocument!.Sha256!, null, null, None);
        Assert.Equal(ContractStatuses.UnderReview, uploaded.Value!.Status);

        // Unchanged: the customer's own submission still works on another contract (fixture path).
        await using var other = InstallationWorkflowTestContext.Create();
        var signed = await SimplifiedFlowFixture.SignedAsync(other);
        Assert.Equal(ContractStatuses.Signed, signed.Contract.Status);
    }
}
