namespace SmartSolar.Modules.CustomerContracts;

/// <summary>
/// A customer contract created from an ACCEPTED quotation version. Pricing and customer data are
/// immutable snapshots; once issued, the content is fixed and identified by <see cref="CanonicalHash"/>
/// (SHA-256 of the canonical content). The official PDF is generated and uploaded by the frontend,
/// verified by the backend and confirmed by Sales against that hash before the customer sees it.
/// Signing is a hand-signed scan reviewed internally by Sales/Manager — not an electronic signature.
/// </summary>
public sealed class CustomerContract
{
    public Guid Id { get; set; }
    public string ContractNumber { get; set; } = null!;
    public int Version { get; set; }
    public Guid? PreviousContractId { get; set; }
    public Guid QuotationId { get; set; }
    public int QuotationVersion { get; set; }
    public Guid SurveyRequestId { get; set; }
    public Guid CustomerUserId { get; set; }
    public Guid SalesUserId { get; set; }
    public string Status { get; set; } = null!;
    public string Currency { get; set; } = null!;
    public decimal TotalAmount { get; set; }
    /// <summary>Quotation lines and totals (JSON) copied when the contract was created.</summary>
    public string PricingSnapshot { get; set; } = null!;
    public string CustomerSnapshot { get; set; } = null!;
    public string? Terms { get; set; }

    /// <summary>
    /// Installation warranty chosen by Sales from the Admin's options (Flow 3). Part of the issued snapshot and the canonical hash,
    /// so later option changes never apply retroactively. Null for contracts created before the option existed.
    /// </summary>
    public int? WarrantyMonths { get; set; }

    public string? CanonicalHash { get; set; }
    public Guid? OfficialDocumentFileId { get; set; }
    public string? OfficialDocumentSha256 { get; set; }
    public Guid? OfficialDocumentConfirmedByUserId { get; set; }
    public DateTimeOffset? OfficialDocumentConfirmedAt { get; set; }
    public Guid? SignedDocumentFileId { get; set; }
    public string? SignedDocumentSha256 { get; set; }
    public DateTimeOffset? SignedSubmittedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? RejectionReason { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? SignedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }

    /// <summary>DISCARDED: when, by which Sales owner and why the never-issued draft was discarded (kept as history, never reactivated).</summary>
    public DateTimeOffset? DiscardedAt { get; set; }
    public Guid? DiscardedByUserId { get; set; }
    public string? DiscardReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

/// <summary>Every signed scan the customer submitted and how it was reviewed (rejected scans are kept).</summary>
public sealed class ContractSignatureSubmission
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public Guid FileId { get; set; }
    public string FileSha256 { get; set; } = null!;
    public string OfficialDocumentSha256 { get; set; } = null!;
    public Guid SubmittedByUserId { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public string Outcome { get; set; } = null!;
    public Guid? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? RejectionReason { get; set; }
}

public sealed class ContractEvent
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public string EventType { get; set; } = null!;
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public Guid ActorUserId { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class ContractStatuses
{
    public const string Draft = "DRAFT";
    public const string Issued = "ISSUED";
    public const string AwaitingSignature = "AWAITING_SIGNATURE";
    public const string UnderReview = "UNDER_REVIEW";
    public const string SignatureRejected = "SIGNATURE_REJECTED";
    public const string Signed = "SIGNED";
    public const string Paid = "PAID";
    public const string Superseded = "SUPERSEDED";
    /// <summary>An initial draft the Sales owner explicitly discarded before it was issued. Terminal: never issued, signed, paid or reactivated.</summary>
    public const string Discarded = "DISCARDED";

    public static readonly IReadOnlyList<string> All = [Draft, Issued, AwaitingSignature, UnderReview, SignatureRejected, Signed, Paid, Superseded, Discarded];

    /// <summary>A contract that still binds or may still bind the request (anything but superseded or discarded).</summary>
    public static bool IsLive(string status) => status is not (Superseded or Discarded);

    /// <summary>States the customer can see.</summary>
    public static readonly IReadOnlyList<string> CustomerVisible = [AwaitingSignature, UnderReview, SignatureRejected, Signed, Paid, Superseded];
}

public static class SignatureOutcomes
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
}

public static class ContractErrorCodes
{
    public const string NotFound = "CONTRACT_NOT_FOUND";
    public const string QuotationNotAccepted = "CONTRACT_QUOTATION_NOT_ACCEPTED";
    /// <summary>The on-site survey was amended after the quotation was made: the quotation no longer describes the surveyed installation (409).</summary>
    public const string SurveyChanged = "CONTRACT_SURVEY_CHANGED";
    /// <summary>The survey request was withdrawn or closed: no contract can be made for it (409).</summary>
    public const string RequestClosed = "CONTRACT_REQUEST_CLOSED";
    /// <summary>Only an initial DRAFT can be discarded (not an amendment draft, not an issued or later contract) (409).</summary>
    public const string NotDiscardable = "CONTRACT_NOT_DISCARDABLE";
    /// <summary>The quotation's contract draft was discarded; a new contract for the same quotation is not a defined process (409).</summary>
    public const string DraftDiscarded = "CONTRACT_DRAFT_DISCARDED";
    /// <summary>A non-empty discard reason (≤ 1000) is required (400).</summary>
    public const string DiscardReasonRequired = "CONTRACT_DISCARD_REASON_REQUIRED";
    /// <summary>A contract cannot be issued without its contractual terms (400). Contracts issued earlier keep their state.</summary>
    public const string TermsRequired = "CONTRACT_TERMS_REQUIRED";
    public const string AlreadyExists = "CONTRACT_ALREADY_EXISTS";
    public const string InvalidState = "CONTRACT_INVALID_STATE";
    public const string StaleRevision = "CONTRACT_STALE_REVISION";
    public const string CanonicalHashMismatch = "CONTRACT_CANONICAL_HASH_MISMATCH";
    public const string DocumentMissing = "CONTRACT_DOCUMENT_MISSING";
    public const string WrongDocumentVersion = "CONTRACT_WRONG_DOCUMENT_VERSION";
    public const string ReasonRequired = "CONTRACT_REJECTION_REASON_REQUIRED";
    public const string InvalidWarranty = "CONTRACT_WARRANTY_VALIDATION_FAILED";
    public const string WarrantyRequired = "CONTRACT_WARRANTY_REQUIRED";
}
