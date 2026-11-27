namespace SmartSolar.Modules.Payments;

/// <summary>
/// The physical cash receipt a technician photographed after taking cash from the customer for one CASH payment attempt (2026-11-27): a 30/70
/// installment of an installation contract, or a chargeable after-sales quotation (2026-11-27, unified: repairs, warranty / disaster remainders,
/// one-time maintenance, supplementary package-visit costs). <see cref="Kind"/> names the context; the payment attempt is the common identity. Submitting it never pays anything: the attempt stays PENDING until a Manager/Admin accepts the receipt (attempt → PAID, installment
/// settled, in one transaction) or rejects it with a reason (the technician sends a new one). Every submission is kept as the review history; at most
/// one is SUBMITTED and at most one ACCEPTED per attempt; a receipt number is used by one attempt only.
/// </summary>
public sealed class CashReceiptSubmission
{
    public Guid Id { get; set; }
    public Guid PaymentAttemptId { get; set; }
    /// <summary><see cref="CashReceiptKinds"/>: INSTALLMENT (contract + installment set) or SERVICE_QUOTATION (quotation + request set).</summary>
    public string Kind { get; set; } = CashReceiptKinds.Installment;
    /// <summary>INSTALLMENT: the 30/70 contract the installment belongs to.</summary>
    public Guid? ContractId { get; set; }
    public Guid? ContractInstallmentId { get; set; }
    /// <summary>SERVICE_QUOTATION: the after-sales quotation the payment line pays, and its service request.</summary>
    public Guid? ServiceQuotationId { get; set; }
    public Guid? ServiceRequestId { get; set; }
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

public static class CashReceiptKinds
{
    public const string Installment = "INSTALLMENT";
    public const string ServiceQuotation = "SERVICE_QUOTATION";
    public static readonly IReadOnlyList<string> All = [Installment, ServiceQuotation];
}

/// <summary>What the payment line of a receipt must pay: one installment of a contract, or one after-sales quotation of a request.</summary>
public sealed record CashReceiptContext(string Kind, Guid? ContractId, Guid? ContractInstallmentId, Guid? ServiceQuotationId, Guid? ServiceRequestId)
{
    public static CashReceiptContext ForInstallment(Guid contractId, Guid installmentId) => new(CashReceiptKinds.Installment, contractId, installmentId, null, null);
    public static CashReceiptContext ForServiceQuotation(Guid quotationId, Guid requestId) => new(CashReceiptKinds.ServiceQuotation, null, null, quotationId, requestId);
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
    /// <summary>The payment's lines do not pay the installment / quotation the receipt is sent for (409).</summary>
    public const string ContextMismatch = "PAYMENT_CASH_RECEIPT_CONTEXT_MISMATCH";
}

/// <summary>What the technician sends for one CASH payment.</summary>
public sealed record CashReceiptInput(Guid PaymentAttemptId, string? ReceiptNumber, Guid EvidenceFileId, DateTimeOffset? CollectedAt, string? Note);

/// <summary>One receipt submission (technician and staff views). <see cref="EvidenceFileId"/> is read through the evidence URL endpoints.</summary>
public sealed record CashReceiptView(Guid CashReceiptId, Guid PaymentAttemptId, Guid? ContractId, Guid? ContractInstallmentId, decimal Amount, string ReceiptNumber,
    Guid CollectedByUserId, DateTimeOffset CollectedAt, Guid EvidenceFileId, string? Note, string Status, DateTimeOffset SubmittedAt, Guid? ReviewedByUserId,
    DateTimeOffset? ReviewedAt, string? ReviewReason, int Revision, string Kind = CashReceiptKinds.Installment, Guid? ServiceQuotationId = null,
    Guid? ServiceRequestId = null)
{
    public static CashReceiptView From(CashReceiptSubmission s) => new(s.Id, s.PaymentAttemptId, s.ContractId, s.ContractInstallmentId, s.Amount, s.ReceiptNumber,
        s.CollectedByUserId, s.CollectedAt, s.EvidenceFileId, s.Note, s.Status, s.SubmittedAt, s.ReviewedByUserId, s.ReviewedAt, s.ReviewReason, s.Revision,
        s.Kind, s.ServiceQuotationId, s.ServiceRequestId);
}

/// <summary>
/// Manager/Admin queue row: the receipt with what is needed to review it without opening other screens. INSTALLMENT rows carry the contract number and
/// installment (DEPOSIT 30 % / BALANCE 70 %); SERVICE_QUOTATION rows the service request (number, kind FAULT / ONE_TIME_MAINTENANCE / MAINTENANCE) and
/// the quotation version. <see cref="Context"/> is INSTALLATION_DEPOSIT, INSTALLATION_BALANCE or AFTER_SALES_QUOTATION.
/// </summary>
public sealed record StaffCashReceiptView(CashReceiptView Receipt, string ContractNumber, int? InstallmentSequence, string? InstallmentKind, Guid CustomerUserId,
    string CustomerName, string CollectedByName, string PaymentStatus, string Context = CashReceiptContexts.InstallationDeposit, Guid? ServiceRequestId = null,
    string? ServiceRequestNumber = null, string? ServiceRequestKind = null, Guid? ServiceQuotationId = null, int? QuotationVersion = null);

/// <summary>Business context label of a receipt for the review queue.</summary>
public static class CashReceiptContexts
{
    public const string InstallationDeposit = "INSTALLATION_DEPOSIT";
    public const string InstallationBalance = "INSTALLATION_BALANCE";
    public const string AfterSalesQuotation = "AFTER_SALES_QUOTATION";
}

/// <summary>
/// Detail for the review screen: the receipt, its context, every submission of the same payment (oldest first) and the payment's lines (after an
/// acceptance a line may be REVIEW_REQUIRED: the money was recorded but could not be applied — see the refund / unapplied-money queue).
/// </summary>
public sealed record StaffCashReceiptDetail(StaffCashReceiptView Current, IReadOnlyList<CashReceiptView> History, IReadOnlyList<PaymentLineView>? Lines = null);

/// <summary>A receipt row with its review context (persistence projection).</summary>
public sealed record CashReceiptRow(CashReceiptSubmission Receipt, string ContractNumber, int? InstallmentSequence, string? InstallmentKind, Guid CustomerUserId,
    string CustomerName, string CollectedByName, string PaymentStatus, string? ServiceRequestNumber = null, string? ServiceRequestKind = null, int? QuotationVersion = null);
