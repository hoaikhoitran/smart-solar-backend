using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Flow 2 — technician physical cash receipt (2026-11-27): the technician who took the cash photographs the receipt and sends it with its number;
/// nothing is paid until a Manager/Admin accepts it (or rejects it with a reason and the technician sends again). 30/70 amounts, PayOS and bundled
/// packages unchanged. Production handlers on SQLite; files are synthetic VERIFIED objects.
/// </summary>
public sealed class TechnicianCashReceiptTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    internal static async Task<Guid> CashChosenAsync(InstallationWorkflowTestContext ctx, PaymentPlanFlow.Agreed flow, int sequence)
    {
        var opened = await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, sequence, "CASH", None);
        Assert.True(opened.Succeeded, opened.Message);
        Assert.Equal(InstallmentPaymentStates.AwaitingCashConfirmation, opened.Value!.PaymentState);
        return opened.Value.Current!.PaymentAttemptId;
    }

    internal static Task<OperationResult<InstallmentPaymentView>> SendAsync(InstallationWorkflowTestContext ctx, PaymentPlanFlow.Agreed flow, int sequence, Guid attemptId,
        string? number, Guid fileId, DateTimeOffset? collectedAt = null, Guid? technician = null)
        => ctx.InstallmentPayments().SubmitCashReceiptAsync(technician ?? flow.TechnicianId, flow.ContractId, sequence,
            new CashReceiptInput(attemptId, number, fileId, collectedAt, "Khách trả đủ tiền mặt"), None);

    internal static async Task<CashReceiptView> SentAsync(InstallationWorkflowTestContext ctx, PaymentPlanFlow.Agreed flow, int sequence, Guid attemptId, string number)
    {
        var photo = await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.PaymentEvidence);
        var sent = await SendAsync(ctx, flow, sequence, attemptId, number, photo);
        Assert.True(sent.Succeeded, sent.ErrorCode + " " + sent.Message);
        return sent.Value!.CurrentCashReceipt!;
    }

    private static string Number() => $"PT-{Guid.NewGuid():N}"[..20];

    // 1 + 9: valid receipt for the 30 % deposit; accepted by a Manager → paid with the technician's photo, no re-upload.
    [Fact]
    public async Task TC01_TC06_TC09_a_valid_deposit_receipt_waits_for_review_then_the_manager_accepts_it_and_the_30_percent_is_paid()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        var attemptId = await CashChosenAsync(ctx, flow, 1);
        var deposit = await PaymentPlanFlow.InstallmentAsync(ctx, flow.ContractId, 1);
        var contract = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId);
        Assert.Equal(Math.Round(contract.TotalAmount * 0.30m, 0), deposit.Amount);   // 30 % of the full total, unchanged

        ctx.Clock.Now = ctx.Clock.GetUtcNow().AddMinutes(10);
        var collectedAt = ctx.Clock.GetUtcNow().AddMinutes(-3);
        var photo = await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.PaymentEvidence);
        var number = Number();
        var sent = await SendAsync(ctx, flow, 1, attemptId, $"  {number} ", photo, collectedAt);
        Assert.True(sent.Succeeded, sent.Message);
        // Sending is not paying: still DUE, still awaiting the cash confirmation; the receipt is SUBMITTED.
        Assert.Equal(InstallmentStatuses.Due, sent.Value!.InstallmentStatus);
        Assert.Equal(InstallmentPaymentStates.AwaitingCashConfirmation, sent.Value.PaymentState);
        Assert.Null(sent.Value.PaidAt);
        var receipt = sent.Value.CurrentCashReceipt!;
        Assert.Equal(CashReceiptStatuses.Submitted, receipt.Status);
        Assert.Equal(number, receipt.ReceiptNumber);
        Assert.Equal(attemptId, receipt.PaymentAttemptId);
        Assert.Equal(flow.ContractId, receipt.ContractId);
        Assert.Equal(deposit.Id, receipt.ContractInstallmentId);
        Assert.Equal(deposit.Amount, receipt.Amount);
        Assert.Equal(flow.TechnicianId, receipt.CollectedByUserId);
        Assert.Equal(collectedAt, receipt.CollectedAt);
        Assert.Equal(photo, receipt.EvidenceFileId);
        Assert.Single(sent.Value.CashReceipts!);
        var attempt = await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId);
        Assert.Equal(PaymentStatuses.Pending, attempt.Status);
        Assert.Null(attempt.ConfirmedByUserId);
        Assert.Contains(await ctx.Db.PaymentEvents.AsNoTracking().Where(e => e.PaymentAttemptId == attemptId).Select(e => e.EventType).ToListAsync(), t => t == "CASH_RECEIPT_SUBMITTED");
        // The customer reads the installment without the internal review data.
        var customerView = (await ctx.InstallmentPayments().GetAsync(InstallmentPayer.Customer, flow.CustomerId, flow.ContractId, 1, None)).Value!;
        Assert.Null(customerView.CashReceipts);
        Assert.Null(customerView.CurrentCashReceipt);
        // The paper signature still waits for the verified deposit.
        Assert.Equal(PaymentPlanErrorCodes.DepositRequired, (await ctx.PaperSignatures().StartAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId,
            new StartSignatureSetInput(2, null, null), None)).ErrorCode);

        // The Manager's queue shows it with its context; accepting it pays the deposit with the technician's photo.
        var queue = await ctx.PaymentsWithCash().PageCashReceiptsAsync(CashReceiptStatuses.Submitted, new PageRequest(1, 20), None);
        var row = Assert.Single(queue.Items);
        Assert.Equal(1, row.InstallmentSequence);
        Assert.Equal(InstallmentKinds.Deposit, row.InstallmentKind);
        Assert.Equal(flow.CustomerId, row.CustomerUserId);
        Assert.False(string.IsNullOrEmpty(row.ContractNumber));
        Assert.False(string.IsNullOrEmpty(row.CollectedByName));
        Assert.Equal(PaymentStatuses.Pending, row.PaymentStatus);
        var accepted = await ctx.PaymentsWithCash().AcceptCashReceiptAsync(flow.Manager.Id, receipt.CashReceiptId, receipt.Revision, "Đã đối chiếu phiếu thu", None);
        Assert.True(accepted.Succeeded, accepted.ErrorCode + " " + accepted.Message);
        Assert.Equal(PaymentStatuses.Paid, accepted.Value!.Status);
        attempt = await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId);
        Assert.Equal(number, attempt.ProviderReference);
        Assert.Equal(photo, attempt.EvidenceFileId);
        Assert.Equal(flow.Manager.Id, attempt.ConfirmedByUserId);
        Assert.Equal(deposit.Amount, attempt.AmountPaid);
        Assert.Equal(collectedAt, attempt.ProviderTransactionAt);
        deposit = await PaymentPlanFlow.InstallmentAsync(ctx, flow.ContractId, 1);
        Assert.Equal(InstallmentStatuses.Paid, deposit.Status);
        Assert.Equal(attemptId, deposit.SettledPaymentAttemptId);
        var stored = await ctx.Db.CashReceiptSubmissions.AsNoTracking().SingleAsync();
        Assert.Equal(CashReceiptStatuses.Accepted, stored.Status);
        Assert.Equal(flow.Manager.Id, stored.ReviewedByUserId);
        Assert.Equal("Đã đối chiếu phiếu thu", stored.ReviewReason);
        var plan = (await PaymentPlanFlow.StaffViewAsync(ctx, flow.ContractId)).PaymentPlan!;
        Assert.True(plan.DepositPaid);
        Assert.False(plan.FullyPaid);
        Assert.Equal(InstallmentStatuses.Pending, plan.Installments.Single(i => i.Sequence == 2).Status);   // 70 % still closed until acceptance
        Assert.Equal(PaymentPlanStages.DepositVerified, plan.Stage);
        // Now the paper can be signed.
        Assert.True((await ctx.PaperSignatures().StartAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, new StartSignatureSetInput(2, null, null), None)).Succeeded);
        var technicianView = (await ctx.InstallmentPayments().GetAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, None)).Value!;
        Assert.Equal(InstallmentPaymentStates.Paid, technicianView.PaymentState);
        Assert.Equal(CashReceiptStatuses.Accepted, technicianView.CurrentCashReceipt!.Status);
    }

    // 2: missing photo / number / attempt; bad collection time — 400, nothing stored.
    [Fact]
    public async Task TC02_a_receipt_without_photo_number_or_attempt_or_with_an_impossible_time_is_refused_with_field_errors()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        var attemptId = await CashChosenAsync(ctx, flow, 1);
        var photo = await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.PaymentEvidence);

        async Task FieldAsync(string field, Task<OperationResult<InstallmentPaymentView>> call)
        {
            var result = await call;
            Assert.Equal("VALIDATION_FAILED", result.ErrorCode);
            Assert.Contains(field, ((IDictionary<string, string[]>)result.Details!).Keys);
        }
        await FieldAsync("evidenceFileId", SendAsync(ctx, flow, 1, attemptId, Number(), Guid.Empty));
        await FieldAsync("receiptNumber", SendAsync(ctx, flow, 1, attemptId, null, photo));
        await FieldAsync("receiptNumber", SendAsync(ctx, flow, 1, attemptId, "   ", photo));
        await FieldAsync("receiptNumber", SendAsync(ctx, flow, 1, attemptId, new string('9', 101), photo));
        await FieldAsync("paymentAttemptId", SendAsync(ctx, flow, 1, Guid.Empty, Number(), photo));
        await FieldAsync("collectedAt", SendAsync(ctx, flow, 1, attemptId, Number(), photo, ctx.Clock.GetUtcNow().AddHours(1)));
        await FieldAsync("collectedAt", SendAsync(ctx, flow, 1, attemptId, Number(), photo, ctx.Clock.GetUtcNow().AddDays(-1)));   // before cash was chosen
        Assert.Equal(0, await ctx.Db.CashReceiptSubmissions.CountAsync());
        Assert.Equal(PaymentStatuses.Pending, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId)).Status);
    }

    // 3: the upload failed or the photo is not usable — FILE_* codes, nothing stored.
    [Fact]
    public async Task TC03_a_failed_or_foreign_upload_is_refused()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        var attemptId = await CashChosenAsync(ctx, flow, 1);
        Assert.Equal(FileErrorCodes.NotVerified, (await SendAsync(ctx, flow, 1, attemptId, Number(), await ctx.UnverifiedFileAsync(flow.TechnicianId, FilePurposes.PaymentEvidence))).ErrorCode);
        Assert.Equal(FileErrorCodes.NotFound, (await SendAsync(ctx, flow, 1, attemptId, Number(), Guid.NewGuid())).ErrorCode);
        Assert.Equal(FileErrorCodes.NotOwned, (await SendAsync(ctx, flow, 1, attemptId, Number(), await ctx.VerifiedFileAsync(flow.Manager.Id, FilePurposes.PaymentEvidence))).ErrorCode);
        Assert.Equal(FileErrorCodes.WrongPurpose, (await SendAsync(ctx, flow, 1, attemptId, Number(), await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.InstallationEvidence))).ErrorCode);
        Assert.Equal(0, await ctx.Db.CashReceiptSubmissions.CountAsync());
        // After a successful upload the same request goes through.
        await SentAsync(ctx, flow, 1, attemptId, Number());
        // The upload policy accepts camera formats up to the purpose limit and refuses others (checked at the upload-URL step).
        var policy = FilePolicies.ByPurpose[FilePurposes.PaymentEvidence];
        Assert.Null(FilePolicies.CheckTypeAndSize(policy, FilePolicies.Jpeg, "receipt.jpg", 2_000_000));
        Assert.Equal(FileErrorCodes.TooLarge, FilePolicies.CheckTypeAndSize(policy, FilePolicies.Jpeg, "receipt.jpg", policy.MaxSizeBytes + 1)!.Value.Code);
        Assert.Equal(FileErrorCodes.TypeNotAllowed, FilePolicies.CheckTypeAndSize(policy, "image/gif", "receipt.gif", 1000)!.Value.Code);
        Assert.Contains(Modules.Identity.Constants.RoleCodes.Technician, FilePolicies.UploaderRoles[FilePurposes.PaymentEvidence]);
    }

    // 4: technician outside their scope; wrong attempt; no cash chosen.
    [Fact]
    public async Task TC04_another_technician_a_wrong_attempt_or_a_payment_without_cash_is_refused()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        // No cash chosen yet.
        var photo = await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.PaymentEvidence);
        Assert.Equal(PaymentErrorCodes.CashNotSelected, (await SendAsync(ctx, flow, 1, Guid.NewGuid(), Number(), photo)).ErrorCode);
        var attemptId = await CashChosenAsync(ctx, flow, 1);

        var stranger = await ctx.Scheduling.SeedTechnicianAsync("Stranger", BinhTan);
        var strangerPhoto = await ctx.VerifiedFileAsync(stranger.Id, FilePurposes.PaymentEvidence);
        Assert.Equal(ContractErrorCodes.NotFound, (await SendAsync(ctx, flow, 1, attemptId, Number(), strangerPhoto, technician: stranger.Id)).ErrorCode);
        Assert.Equal(ContractErrorCodes.NotFound, (await ctx.InstallmentPayments().FindCashReceiptForTechnicianAsync(stranger.Id, flow.ContractId, 1, Guid.NewGuid(), None)).ErrorCode);
        // An attempt id of another payment (e.g. stale screen) is never attached here.
        Assert.Equal(CashReceiptErrorCodes.AttemptMismatch, (await SendAsync(ctx, flow, 1, Guid.NewGuid(), Number(), photo)).ErrorCode);
        // The balance (still PENDING, no payment) cannot get a receipt.
        Assert.Equal(PaymentErrorCodes.CashNotSelected, (await SendAsync(ctx, flow, 2, attemptId, Number(), photo)).ErrorCode);
        Assert.Equal(PaymentPlanErrorCodes.InstallmentNotFound, (await SendAsync(ctx, flow, 3, attemptId, Number(), photo)).ErrorCode);
        Assert.Equal(0, await ctx.Db.CashReceiptSubmissions.CountAsync());
        // The receipt of the assigned technician is readable by them, not through another installment.
        var receipt = await SentAsync(ctx, flow, 1, attemptId, Number());
        Assert.True((await ctx.InstallmentPayments().FindCashReceiptForTechnicianAsync(flow.TechnicianId, flow.ContractId, 1, receipt.CashReceiptId, None)).Succeeded);
        Assert.Equal(CashReceiptErrorCodes.NotFound, (await ctx.InstallmentPayments().FindCashReceiptForTechnicianAsync(flow.TechnicianId, flow.ContractId, 2, receipt.CashReceiptId, None)).ErrorCode);
    }

    // 7: rejection needs a reason; the technician sends again; the history keeps both.
    [Fact]
    public async Task TC07_a_rejected_receipt_keeps_the_payment_open_and_the_technician_sends_a_new_one()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        var attemptId = await CashChosenAsync(ctx, flow, 1);
        var number = Number();
        var first = await SentAsync(ctx, flow, 1, attemptId, number);
        var payments = ctx.PaymentsWithCash();

        foreach (var reason in new[] { null, "  ", new string('x', 501) })
            Assert.Equal("VALIDATION_FAILED", (await payments.RejectCashReceiptAsync(flow.Manager.Id, first.CashReceiptId, first.Revision, reason, None)).ErrorCode);
        var rejected = await payments.RejectCashReceiptAsync(flow.Manager.Id, first.CashReceiptId, first.Revision, "Ảnh bị mờ, chụp lại số phiếu", None);
        Assert.True(rejected.Succeeded, rejected.Message);
        Assert.Equal(CashReceiptStatuses.Rejected, rejected.Value!.Status);
        Assert.Equal("Ảnh bị mờ, chụp lại số phiếu", rejected.Value.ReviewReason);
        Assert.Equal(flow.Manager.Id, rejected.Value.ReviewedByUserId);
        Assert.Equal(PaymentStatuses.Pending, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId)).Status);
        Assert.Equal(InstallmentStatuses.Due, (await PaymentPlanFlow.InstallmentAsync(ctx, flow.ContractId, 1)).Status);
        var view = (await ctx.InstallmentPayments().GetAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, None)).Value!;
        Assert.Equal(CashReceiptStatuses.Rejected, view.CurrentCashReceipt!.Status);
        Assert.Equal(InstallmentPaymentStates.AwaitingCashConfirmation, view.PaymentState);
        // A rejected receipt can no longer be accepted or rejected again.
        Assert.Equal(CashReceiptErrorCodes.NotReviewable, (await payments.AcceptCashReceiptAsync(flow.Manager.Id, first.CashReceiptId, rejected.Value.Revision, null, None)).ErrorCode);
        Assert.Equal(CashReceiptErrorCodes.NotReviewable, (await payments.RejectCashReceiptAsync(flow.Manager.Id, first.CashReceiptId, rejected.Value.Revision, "again", None)).ErrorCode);

        // Same physical receipt number with a better photo is accepted for the same payment.
        var second = await SentAsync(ctx, flow, 1, attemptId, number);
        Assert.NotEqual(first.CashReceiptId, second.CashReceiptId);
        view = (await ctx.InstallmentPayments().GetAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, None)).Value!;
        Assert.Equal([CashReceiptStatuses.Rejected, CashReceiptStatuses.Submitted], view.CashReceipts!.Select(r => r.Status));
        Assert.Equal(second.CashReceiptId, view.CurrentCashReceipt!.CashReceiptId);
        var detail = (await payments.GetCashReceiptForStaffAsync(second.CashReceiptId, None)).Value!;
        Assert.Equal([first.CashReceiptId, second.CashReceiptId], detail.History.Select(r => r.CashReceiptId));
        Assert.True((await payments.AcceptCashReceiptAsync(flow.Manager.Id, second.CashReceiptId, second.Revision, null, None)).Succeeded);
        var events = await ctx.Db.PaymentEvents.AsNoTracking().Where(e => e.PaymentAttemptId == attemptId).OrderBy(e => e.CreatedAt).Select(e => e.EventType).ToListAsync();
        Assert.Equal(2, events.Count(e => e == "CASH_RECEIPT_SUBMITTED"));
        Assert.Single(events, e => e == "CASH_RECEIPT_REJECTED");
        Assert.Single(events, e => e == "CASH_RECEIPT_ACCEPTED");
        Assert.Single(events, e => e == "CASH_CONFIRMED");
    }

    // 8: duplicate submission / confirmation, stale review, number or photo reused, direct confirmation and cancellation while under review.
    [Fact]
    public async Task TC08_duplicates_are_refused_and_nothing_is_paid_twice()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await PaymentPlanFlow.AgreedAsync(ctx, "First customer");
        var other = await PaymentPlanFlow.AgreedAsync(ctx, "Second customer");
        var attemptId = await CashChosenAsync(ctx, flow, 1);
        var otherAttempt = await CashChosenAsync(ctx, other, 1);
        var payments = ctx.PaymentsWithCash();
        var number = Number();
        var photo = await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.PaymentEvidence);
        var sent = await SendAsync(ctx, flow, 1, attemptId, number, photo);
        Assert.True(sent.Succeeded, sent.Message);
        var receipt = sent.Value!.CurrentCashReceipt!;

        // A second receipt while one is under review.
        Assert.Equal(CashReceiptErrorCodes.AlreadySubmitted, (await SendAsync(ctx, flow, 1, attemptId, Number(), await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.PaymentEvidence))).ErrorCode);
        // The same number or the same photo on another payment.
        var otherPhoto = await ctx.VerifiedFileAsync(other.TechnicianId, FilePurposes.PaymentEvidence);
        Assert.Equal(PaymentErrorCodes.CashReceiptDuplicate, (await SendAsync(ctx, other, 1, otherAttempt, number, otherPhoto)).ErrorCode);
        // While under review the Manager cannot bypass it with a direct confirmation, nor can the cash choice be cancelled.
        var managerEvidence = await ctx.VerifiedFileAsync(flow.Manager.Id, FilePurposes.PaymentEvidence);
        Assert.Equal(CashReceiptErrorCodes.UnderReview, (await payments.ConfirmCashAttemptAsync(flow.Manager.Id, attemptId,
            new CashConfirmation(receipt.Amount, Number(), managerEvidence, null), None)).ErrorCode);
        Assert.Equal(CashReceiptErrorCodes.UnderReview, (await ctx.InstallmentPayments().CancelAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, None)).ErrorCode);
        Assert.Equal(CashReceiptErrorCodes.UnderReview, (await ctx.InstallmentPayments().CancelAsync(InstallmentPayer.Customer, flow.CustomerId, flow.ContractId, 1, None)).ErrorCode);
        Assert.Equal(PaymentStatuses.Pending, (await payments.CancelAttemptAsync(attemptId, "test", None))!.Status);   // service-level guard too
        // A stale review is refused; then exactly one acceptance wins.
        Assert.Equal(CashReceiptErrorCodes.Stale, (await payments.AcceptCashReceiptAsync(flow.Manager.Id, receipt.CashReceiptId, receipt.Revision + 1, null, None)).ErrorCode);
        Assert.Equal(CashReceiptErrorCodes.NotFound, (await payments.AcceptCashReceiptAsync(flow.Manager.Id, Guid.NewGuid(), 1, null, None)).ErrorCode);
        Assert.True((await payments.AcceptCashReceiptAsync(flow.Manager.Id, receipt.CashReceiptId, receipt.Revision, null, None)).Succeeded);
        Assert.Equal(CashReceiptErrorCodes.NotReviewable, (await payments.AcceptCashReceiptAsync(flow.Manager.Id, receipt.CashReceiptId, receipt.Revision, null, None)).ErrorCode);
        Assert.Equal(CashReceiptErrorCodes.NotReviewable, (await payments.RejectCashReceiptAsync(flow.Manager.Id, receipt.CashReceiptId, receipt.Revision + 1, "late", None)).ErrorCode);
        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await payments.ConfirmCashAttemptAsync(flow.Manager.Id, attemptId,
            new CashConfirmation(receipt.Amount, Number(), managerEvidence, null), None)).ErrorCode);
        Assert.Equal(PaymentPlanErrorCodes.InstallmentPaid, (await SendAsync(ctx, flow, 1, attemptId, Number(), photo)).ErrorCode);
        // The accepted number (now the payment's receipt reference) and photo can never be reused elsewhere.
        Assert.Equal(PaymentErrorCodes.CashReceiptDuplicate, (await SendAsync(ctx, other, 1, otherAttempt, number, otherPhoto)).ErrorCode);
        Assert.Equal(1, await ctx.Db.PaymentAttempts.CountAsync(a => a.Status == PaymentStatuses.Paid));
        Assert.Equal(1, await ctx.Db.ContractInstallments.CountAsync(i => i.Status == InstallmentStatuses.Paid));
    }

    // 8 (evidence reuse by the same technician across two of their payments).
    [Fact]
    public async Task TC08_the_same_photo_cannot_prove_two_payments()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        var attemptId = await CashChosenAsync(ctx, flow, 1);
        var photo = await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.PaymentEvidence);
        Assert.True((await SendAsync(ctx, flow, 1, attemptId, Number(), photo)).Succeeded);
        // Same technician, same file, another payment of theirs (a second contract on a request they surveyed: simulated through the service).
        var second = await PaymentPlanFlow.AgreedAsync(ctx, "Second");
        var secondAttempt = await CashChosenAsync(ctx, second, 1);
        var installment = await PaymentPlanFlow.InstallmentAsync(ctx, second.ContractId, 1);
        var reused = await ctx.PaymentsWithCash().SubmitCashReceiptAsync(flow.TechnicianId, second.ContractId, installment.Id, secondAttempt,
            new CashReceiptInput(secondAttempt, Number(), photo, null, null), None);
        Assert.Equal(CashReceiptErrorCodes.EvidenceReused, reused.ErrorCode);
    }

    // 10: the 70 % balance in cash after the customer's acceptance; the contract becomes fully paid.
    [Fact]
    public async Task TC10_the_70_percent_balance_is_paid_through_an_accepted_technician_receipt()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        var projectId = await PaymentPlanFlow.CompletionReportedAsync(ctx, flow);   // deposit paid (Manager direct cash confirmation, unchanged path)
        // Before the customer's acceptance the balance cannot be taken in cash.
        Assert.Equal(PaymentPlanErrorCodes.InstallmentNotPayable, (await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 2, "CASH", None)).ErrorCode);
        await PaymentPlanFlow.AcceptAsync(ctx, flow, projectId);
        var balance = await PaymentPlanFlow.InstallmentAsync(ctx, flow.ContractId, 2);
        var contract = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId);
        Assert.Equal(contract.TotalAmount - (await PaymentPlanFlow.InstallmentAsync(ctx, flow.ContractId, 1)).Amount, balance.Amount);
        var attemptId = await CashChosenAsync(ctx, flow, 2);
        var receipt = await SentAsync(ctx, flow, 2, attemptId, Number());
        Assert.Equal(balance.Amount, receipt.Amount);
        Assert.Equal(InstallmentStatuses.Due, (await PaymentPlanFlow.InstallmentAsync(ctx, flow.ContractId, 2)).Status);
        var accepted = await ctx.PaymentsWithSchedule().AcceptCashReceiptAsync(flow.Manager.Id, receipt.CashReceiptId, receipt.Revision, null, None);
        Assert.True(accepted.Succeeded, accepted.ErrorCode + " " + accepted.Message);
        var plan = (await PaymentPlanFlow.StaffViewAsync(ctx, flow.ContractId)).PaymentPlan!;
        Assert.True(plan.FullyPaid);
        Assert.Equal(0m, plan.Outstanding);
        Assert.Equal(PaymentPlanStages.FullyPaid, plan.Stage);
        Assert.Equal(ContractStatuses.Paid, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId)).Status);
    }

    // 11: PayOS unchanged; a receipt is never attached to an online payment.
    [Fact]
    public async Task TC11_payos_payments_are_unchanged_and_never_take_a_cash_receipt()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await PaymentPlanFlow.AgreedAsync(ctx);
        var opened = await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, "ONLINE", None);
        Assert.True(opened.Succeeded, opened.Message);
        var photo = await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.PaymentEvidence);
        Assert.Equal(PaymentErrorCodes.CashNotSelected, (await SendAsync(ctx, flow, 1, opened.Value!.Current!.PaymentAttemptId, Number(), photo)).ErrorCode);
        Assert.True((await ctx.InstallmentPayments().CancelAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, None)).Succeeded);
        var paid = await PaymentPlanFlow.PayOnlineAsync(ctx, flow, 1);
        Assert.Equal(PaymentStatuses.Paid, paid.Status);
        Assert.Equal(InstallmentStatuses.Paid, (await PaymentPlanFlow.InstallmentAsync(ctx, flow.ContractId, 1)).Status);
        var view = (await ctx.InstallmentPayments().GetAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, None)).Value!;
        Assert.Empty(view.CashReceipts!);
        Assert.Null(view.CurrentCashReceipt);
        Assert.Equal(0, await ctx.Db.CashReceiptSubmissions.CountAsync());
    }

    // 12: bundled per-panel packages — the receipt covers the installment total (installation + packages share) and settles the package shares too.
    [Fact]
    public async Task TC12_a_bundled_contract_deposit_paid_by_technician_receipt_keeps_the_package_allocation()
    {
        var (w, flow) = await PerPanelServicePackageTests.AgreedBundleAsync(withMaintenance: true);
        await using var _ = w.After;
        var ctx = w.Ctx;
        var attemptId = await CashChosenAsync(ctx, flow, 1);
        var receipt = await SentAsync(ctx, flow, 1, attemptId, Number());
        var deposit = await PaymentPlanFlow.InstallmentAsync(ctx, flow.ContractId, 1);
        var before = (await ctx.InstallmentPayments().GetAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, None)).Value!;
        Assert.Equal(deposit.Amount, receipt.Amount);
        Assert.Equal(before.Amount, before.Allocation!.Installation + before.Allocation.MaintenancePackages + before.Allocation.DisasterPackages);
        var accepted = await ctx.PaymentsWithCash().AcceptCashReceiptAsync(flow.Manager.Id, receipt.CashReceiptId, receipt.Revision, null, None);
        Assert.True(accepted.Succeeded, accepted.ErrorCode + " " + accepted.Message);
        var view = await PaymentPlanFlow.StaffViewAsync(ctx, flow.ContractId);
        Assert.True(view.PaymentPlan!.DepositPaid);
        Assert.Equal(view.TotalAmount, view.InstallationTotal + view.ServicePackagesTotal);
        var after = (await ctx.InstallmentPayments().GetAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, None)).Value!;
        Assert.Equal(before.Allocation, after.Allocation);
        Assert.Equal(InstallmentPaymentStates.Paid, after.PaymentState);
    }
}
