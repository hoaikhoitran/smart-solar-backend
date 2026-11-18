namespace SmartSolar.Modules.CustomerContracts;

/// <summary>How an installation contract is paid. Null on <see cref="CustomerContract.PaymentPlan"/> = legacy full payment (100%, unchanged).</summary>
public static class PaymentPlans
{
    /// <summary>30% deposit before the paper signature, 70% balance after the customer accepts the installation (2026-11-19).</summary>
    public const string Deposit30Balance70 = "DEPOSIT_30_BALANCE_70";
    public static readonly IReadOnlyList<string> All = [Deposit30Balance70];
}

public static class InstallmentKinds
{
    /// <summary>First installment (30%). Called "deposit" in the product; NOT assumed to be a legal "đặt cọc" (BLDS 2015 Art. 328) — legal review.</summary>
    public const string Deposit = "DEPOSIT";
    /// <summary>Second installment (70%), payable after the customer's acceptance.</summary>
    public const string Balance = "BALANCE";
    public static readonly IReadOnlyList<string> All = [Deposit, Balance];
}

/// <summary>
/// Installment lifecycle (separate from the contract, installation and collection lifecycles). Overdue is derived (<c>DUE</c> and now &gt; due_at),
/// never stored as a payment state.
/// </summary>
public static class InstallmentStatuses
{
    /// <summary>Not payable yet (the balance before the customer accepted the installation).</summary>
    public const string Pending = "PENDING";
    /// <summary>Payable now (the deposit from the agreement on; the balance from the acceptance on).</summary>
    public const string Due = "DUE";
    /// <summary>Settled by exactly one verified payment line (webhook / reconciliation / Manager cash confirmation).</summary>
    public const string Paid = "PAID";
    /// <summary>The contract was discarded before anything was paid (customer withdrew the agreement). Kept as history.</summary>
    public const string Cancelled = "CANCELLED";
    public static readonly IReadOnlyList<string> All = [Pending, Due, Paid, Cancelled];
}

/// <summary>One installment of a payment-plan contract (sequence 1 = deposit, 2 = balance). Amounts are frozen at creation from the contract total.</summary>
public sealed class ContractInstallment
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public int Sequence { get; set; }
    public string Kind { get; set; } = null!;
    public decimal Percent { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = null!;
    /// <summary>When it became payable (DUE).</summary>
    public DateTimeOffset? OpenedAt { get; set; }
    /// <summary>Payment deadline (UTC). Balance: acceptance time + configured hours (at most 72). Deposit: none (paid on site before signing).</summary>
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public decimal? PaidAmount { get; set; }
    /// <summary>Paid after <see cref="DueAt"/> (accepted, flagged; never refused).</summary>
    public bool PaidLate { get; set; }
    /// <summary>The live payment attempt holding this installment (one at a time), null when none.</summary>
    public Guid? CurrentPaymentAttemptId { get; set; }
    /// <summary>The verified attempt that settled it.</summary>
    public Guid? SettledPaymentAttemptId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

public static class SignatureSetStatuses
{
    /// <summary>The technician is uploading the photographed pages.</summary>
    public const string Uploading = "UPLOADING";
    /// <summary>All pages 1..N uploaded and verified; locked (immutable, audited). The contract is SIGNED.</summary>
    public const string Finalized = "FINALIZED";
    /// <summary>An unfinished upload restarted (e.g. wrong page count); kept as history.</summary>
    public const string Superseded = "SUPERSEDED";
    public static readonly IReadOnlyList<string> All = [Uploading, Finalized, Superseded];
}

/// <summary>
/// The photographed pages of the paper contract the customer signed on site after the deposit (2026-11-19). This is a record of a hand-signed paper
/// contract, not an electronic signature. No Manager review: the technician finalizes once every page 1..N is uploaded and verified.
/// </summary>
public sealed class ContractSignatureSet
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public int Version { get; set; }
    public int ExpectedPageCount { get; set; }
    public string Status { get; set; } = null!;
    /// <summary>Local date written on the paper (optional).</summary>
    public DateOnly? SignedOn { get; set; }
    public string? Note { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? FinalizedByUserId { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

/// <summary>One photographed page. A page replaced before finalization keeps its row (<see cref="ReplacedAt"/> set) as history.</summary>
public sealed class ContractSignedPage
{
    public Guid Id { get; set; }
    public Guid SignatureSetId { get; set; }
    public Guid ContractId { get; set; }
    public int PageNumber { get; set; }
    public Guid FileId { get; set; }
    public string Sha256 { get; set; } = null!;
    public string ContentType { get; set; } = null!;
    public Guid UploadedByUserId { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public DateTimeOffset? ReplacedAt { get; set; }
}

/// <summary>Collection follow-up of an overdue installment. Business workflow only: never extends the due date, never moves money or assets.</summary>
public static class CollectionCaseStatuses
{
    /// <summary>Detected overdue; derived state of the installment (kept for reporting completeness).</summary>
    public const string Overdue = "OVERDUE";
    /// <summary>Opened by the overdue sweep: Sales must contact the customer.</summary>
    public const string SalesContactRequired = "SALES_CONTACT_REQUIRED";
    public const string Contacted = "CONTACTED";
    public const string NegotiationInProgress = "NEGOTIATION_IN_PROGRESS";
    /// <summary>A payment agreement was recorded (promised date). The contractual due date is NOT changed.</summary>
    public const string AgreementRecorded = "AGREEMENT_RECORDED";
    public const string EscalatedToManager = "ESCALATED_TO_MANAGER";
    /// <summary>A Manager asked for legal review. The system never takes a legal decision.</summary>
    public const string LegalReviewRequired = "LEGAL_REVIEW_REQUIRED";
    public const string Resolved = "RESOLVED";
    public static readonly IReadOnlyList<string> All =
        [Overdue, SalesContactRequired, Contacted, NegotiationInProgress, AgreementRecorded, EscalatedToManager, LegalReviewRequired, Resolved];
    public static bool IsOpen(string status) => status != Resolved;
}

public static class CollectionActivityKinds
{
    public const string Opened = "OPENED";
    public const string Contact = "CONTACT";
    public const string Negotiation = "NEGOTIATION";
    public const string Agreement = "AGREEMENT";
    public const string Note = "NOTE";
    public const string Escalate = "ESCALATE";
    public const string LegalReview = "LEGAL_REVIEW";
    public const string Resolve = "RESOLVE";
    /// <summary>The installment was paid: the case closes automatically.</summary>
    public const string PaidResolved = "PAID";
    /// <summary>Kinds Sales / Manager / Admin record with <c>POST /api/collections/{id}/activities</c>.</summary>
    public static readonly IReadOnlyList<string> Recordable = [Contact, Negotiation, Agreement, Note];
    public static readonly IReadOnlyList<string> All = [Opened, Contact, Negotiation, Agreement, Note, Escalate, LegalReview, Resolve, PaidResolved];
}

public sealed class ContractCollectionCase
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public Guid InstallmentId { get; set; }
    public Guid CustomerUserId { get; set; }
    public string Status { get; set; } = null!;
    public DateTimeOffset OpenedAt { get; set; }
    /// <summary>Snapshot of the contractual due date when the case was opened (never changed by the case).</summary>
    public DateTimeOffset DueAt { get; set; }
    public decimal AmountDue { get; set; }
    /// <summary>The latest promised payment date recorded with an agreement (information only).</summary>
    public DateOnly? PromisedPaymentDate { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public string? Resolution { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

public sealed class ContractCollectionActivity
{
    public Guid Id { get; set; }
    public Guid CaseId { get; set; }
    public string Kind { get; set; } = null!;
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    /// <summary>The acting user; null for the system (overdue sweep, automatic resolution on payment).</summary>
    public Guid? ActorUserId { get; set; }
    public string? ActorRole { get; set; }
    public string? Channel { get; set; }
    public string? Summary { get; set; }
    public DateOnly? PromisedPaymentDate { get; set; }
    /// <summary>JSON array of VERIFIED COLLECTION_EVIDENCE file ids.</summary>
    public string? EvidenceFileIds { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>Read audit of contracts and their signed pages (who looked at what, when). Rows are never updated or deleted.</summary>
public sealed class ContractAccessAudit
{
    public Guid Id { get; set; }
    public Guid? ContractId { get; set; }
    public Guid ActorUserId { get; set; }
    public string ActorRole { get; set; } = null!;
    public string Action { get; set; } = null!;
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class ContractAccessActions
{
    public const string List = "LIST";
    public const string Detail = "DETAIL";
    public const string PageUrl = "PAGE_URL";
    public const string DocumentUrl = "DOCUMENT_URL";
}

public static class PaymentPlanErrorCodes
{
    /// <summary>The contract is a legacy full-payment contract (or vice versa): this operation does not apply (409).</summary>
    public const string NotApplicable = "CONTRACT_PAYMENT_PLAN_NOT_APPLICABLE";
    public const string InstallmentNotFound = "CONTRACT_INSTALLMENT_NOT_FOUND";
    /// <summary>The installment is not payable now (balance before acceptance, already paid, cancelled) (409).</summary>
    public const string InstallmentNotPayable = "CONTRACT_INSTALLMENT_NOT_PAYABLE";
    public const string InstallmentPaid = "CONTRACT_INSTALLMENT_ALREADY_PAID";
    /// <summary>The paper signature needs the verified deposit first (409).</summary>
    public const string DepositRequired = "CONTRACT_DEPOSIT_REQUIRED";
    public const string SignatureSetNotFound = "CONTRACT_SIGNATURE_SET_NOT_FOUND";
    /// <summary>The signed-page upload was not started (page count) (409).</summary>
    public const string SignatureNotStarted = "CONTRACT_SIGNATURE_NOT_STARTED";
    /// <summary>Pages missing / duplicated / beyond the expected count (409 / 400).</summary>
    public const string PagesIncomplete = "CONTRACT_SIGNED_PAGES_INCOMPLETE";
    public const string PageOutOfRange = "CONTRACT_SIGNED_PAGE_OUT_OF_RANGE";
    public const string SignatureFinalized = "CONTRACT_SIGNATURE_ALREADY_FINALIZED";
    public const string AmountNotSupported = "CONTRACT_PAYMENT_PLAN_AMOUNT_NOT_SUPPORTED";
    public const string CollectionNotFound = "COLLECTION_CASE_NOT_FOUND";
    public const string CollectionClosed = "COLLECTION_CASE_CLOSED";
    public const string CollectionInvalidTransition = "COLLECTION_INVALID_TRANSITION";
    public const string StaleRevision = "CONTRACT_STALE_REVISION";
}
