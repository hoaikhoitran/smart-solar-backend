namespace SmartSolar.Modules.Payments;

/// <summary>
/// The physical cash receipt a technician photographed after taking cash from the customer for one CASH payment attempt of a 30/70 installment
/// (2026-11-27). Submitting it never pays anything: the attempt stays PENDING until a Manager/Admin accepts the receipt (attempt → PAID, installment
/// settled, in one transaction) or rejects it with a reason (the technician sends a new one). Every submission is kept as the review history; at most
/// one is SUBMITTED and at most one ACCEPTED per attempt; a receipt number is used by one attempt only.
/// </summary>
public sealed class CashReceiptSubmission
{
    public Guid Id { get; set; }
    public Guid PaymentAttemptId { get; set; }
    /// <summary>The 30/70 contract the installment belongs to.</summary>
    public Guid ContractId { get; set; }
    public Guid ContractInstallmentId { get; set; }
    /// <summary>Snapshot of the attempt amount (the exact amount due) when the receipt was sent.</summary>
    public decimal Amount { get; set; }
    /// <summary>Number printed / written on the physical receipt (trimmed, at most 100 characters).</summary>
    public string ReceiptNumber { get; set; } = null!;
    /// <summary>Who took the cash: the submitting technician.</summary>
    public Guid CollectedByUserId { get; set; }
    /// <summary>When the cash was taken (reported by the technician; never in the future, never before the cash choice).</summary>
    public DateTimeOffset CollectedAt { get; set; }
    /// <summary>VERIFIED PAYMENT_EVIDENCE photo registered by the submitting technician.</summary>
    public Guid EvidenceFileId { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = null!;
    public DateTimeOffset SubmittedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    /// <summary>Required when REJECTED (what to fix); optional note when ACCEPTED; the reason when WITHDRAWN.</summary>
    public string? ReviewReason { get; set; }
    public int Revision { get; set; }
}

public static class CashReceiptStatuses
{
    /// <summary>Sent by the technician, waiting for a Manager/Admin. The payment is NOT paid yet.</summary>
    public const string Submitted = "SUBMITTED";
    /// <summary>A Manager/Admin accepted it: the payment is PAID with this receipt number and photo.</summary>
    public const string Accepted = "ACCEPTED";
    /// <summary>A Manager/Admin asked for a new receipt (reason required); the payment is still awaiting cash confirmation.</summary>
    public const string Rejected = "REJECTED";
    /// <summary>Closed without review because the payment ended another way (reserved for history; never set by a technician).</summary>
    public const string Withdrawn = "WITHDRAWN";

    public static readonly IReadOnlyList<string> All = [Submitted, Accepted, Rejected, Withdrawn];
}

public static class CashReceiptErrorCodes
{
    public const string NotFound = "PAYMENT_CASH_RECEIPT_NOT_FOUND";
    /// <summary>A receipt of this payment is already waiting for review (409).</summary>
    public const string AlreadySubmitted = "PAYMENT_CASH_RECEIPT_ALREADY_SUBMITTED";
    /// <summary>The paymentAttemptId sent is not the open CASH payment of this installment (409).</summary>
    public const string AttemptMismatch = "PAYMENT_CASH_RECEIPT_ATTEMPT_MISMATCH";
    /// <summary>A technician receipt is waiting for review: review it (accept / reject) instead of cancelling or confirming directly (409).</summary>
    public const string UnderReview = "PAYMENT_CASH_RECEIPT_UNDER_REVIEW";
    /// <summary>The receipt was already accepted / rejected / withdrawn (409).</summary>
    public const string NotReviewable = "PAYMENT_CASH_RECEIPT_NOT_REVIEWABLE";
    /// <summary>The receipt changed since it was read (stale revision) (409).</summary>
    public const string Stale = "PAYMENT_CASH_RECEIPT_STALE";
    /// <summary>The photo is already the evidence of another payment (409).</summary>
    public const string EvidenceReused = "PAYMENT_CASH_RECEIPT_EVIDENCE_REUSED";
}

/// <summary>What the technician sends for one CASH payment.</summary>
public sealed record CashReceiptInput(Guid PaymentAttemptId, string? ReceiptNumber, Guid EvidenceFileId, DateTimeOffset? CollectedAt, string? Note);

/// <summary>One receipt submission (technician and staff views). <see cref="EvidenceFileId"/> is read through the evidence URL endpoints.</summary>
public sealed record CashReceiptView(Guid CashReceiptId, Guid PaymentAttemptId, Guid ContractId, Guid ContractInstallmentId, decimal Amount, string ReceiptNumber,
    Guid CollectedByUserId, DateTimeOffset CollectedAt, Guid EvidenceFileId, string? Note, string Status, DateTimeOffset SubmittedAt, Guid? ReviewedByUserId,
    DateTimeOffset? ReviewedAt, string? ReviewReason, int Revision)
{
    public static CashReceiptView From(CashReceiptSubmission s) => new(s.Id, s.PaymentAttemptId, s.ContractId, s.ContractInstallmentId, s.Amount, s.ReceiptNumber,
        s.CollectedByUserId, s.CollectedAt, s.EvidenceFileId, s.Note, s.Status, s.SubmittedAt, s.ReviewedByUserId, s.ReviewedAt, s.ReviewReason, s.Revision);
}

/// <summary>Manager/Admin queue row: the receipt with what is needed to review it without opening other screens.</summary>
public sealed record StaffCashReceiptView(CashReceiptView Receipt, string ContractNumber, int InstallmentSequence, string InstallmentKind, Guid CustomerUserId,
    string CustomerName, string CollectedByName, string PaymentStatus);

/// <summary>Detail for the review screen: the receipt, its context and every submission of the same payment (oldest first).</summary>
public sealed record StaffCashReceiptDetail(StaffCashReceiptView Current, IReadOnlyList<CashReceiptView> History);

/// <summary>A receipt row with its review context (persistence projection).</summary>
public sealed record CashReceiptRow(CashReceiptSubmission Receipt, string ContractNumber, int InstallmentSequence, string InstallmentKind, Guid CustomerUserId,
    string CustomerName, string CollectedByName, string PaymentStatus);
