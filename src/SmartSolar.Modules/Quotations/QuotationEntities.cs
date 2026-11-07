namespace SmartSolar.Modules.Quotations;

/// <summary>
/// One version of a quotation for a survey request. Issued versions are immutable: a customer's
/// revision request leaves the issued version untouched and Sales issues a new version
/// (<see cref="PreviousQuotationId"/>), which the customer must accept again. Amounts are server
/// computed from immutable line snapshots, so later catalog changes never alter a quotation.
/// </summary>
public sealed class Quotation
{
    public Guid Id { get; set; }
    public string QuoteNumber { get; set; } = null!;
    public Guid SurveyRequestId { get; set; }
    public Guid OnSiteSurveyReportId { get; set; }
    public int OnSiteSurveyVersion { get; set; }
    public int Version { get; set; }
    public Guid? PreviousQuotationId { get; set; }
    public string Status { get; set; } = null!;
    public Guid CustomerUserId { get; set; }
    public Guid SalesUserId { get; set; }
    public string Currency { get; set; } = null!;
    public decimal EquipmentSubtotal { get; set; }
    public decimal LaborCost { get; set; }
    public decimal TransportCost { get; set; }
    public decimal OtherServiceCost { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxRatePercent { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public string? Terms { get; set; }
    public string? Notes { get; set; }
    /// <summary>Customer name/contact/site captured when issued (JSON).</summary>
    public string? CustomerSnapshot { get; set; }
    public Guid? DocumentFileId { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevisionRequestedAt { get; set; }
    public string? RevisionReason { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    /// <summary>REJECTED (by the customer) or CANCELLED (the request was withdrawn or closed): when, by whom and why.</summary>
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public string? CloseReason { get; set; }
    /// <summary>SALES (prepared by Sales) or AUTOMATIC (priced by the backend from the technician's bill of materials).</summary>
    public string Origin { get; set; } = QuotationOrigins.Sales;
    /// <summary>
    /// AUTOMATIC quotations: JSON <see cref="PricingBasis"/> — the survey version, the priced material and service items with their
    /// catalog snapshots, and the pricing issues that kept the quotation from being issued automatically. Never changed after issue.
    /// </summary>
    public string? PricingBasisJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

/// <summary>Immutable product snapshot: what was sold, at which price, regardless of later catalog changes.</summary>
public sealed class QuotationLine
{
    public Guid Id { get; set; }
    public Guid QuotationId { get; set; }
    public int LineNumber { get; set; }
    public Guid? ProductId { get; set; }
    public string? Sku { get; set; }
    public string Name { get; set; } = null!;
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string Unit { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    /// <summary>Catalog price at the moment the line was added (for audit when Sales overrides the price).</summary>
    public decimal? CatalogUnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public string? Description { get; set; }
}

public sealed class QuotationEvent
{
    public Guid Id { get; set; }
    public Guid QuotationId { get; set; }
    public string EventType { get; set; } = null!;
    public Guid ActorUserId { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class QuotationStatuses
{
    public const string Draft = "DRAFT";
    public const string Issued = "ISSUED";
    public const string Accepted = "ACCEPTED";
    public const string RevisionRequested = "REVISION_REQUESTED";
    public const string Superseded = "SUPERSEDED";
    /// <summary>The customer rejected the issued offer. Terminal: never issued or accepted again (Sales may make a new version).</summary>
    public const string Rejected = "REJECTED";
    /// <summary>The survey request was withdrawn or closed while this quotation was open. Terminal.</summary>
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlyList<string> All = [Draft, Issued, Accepted, RevisionRequested, Superseded, Rejected, Cancelled];

    public static bool IsTerminal(string status) => status is Superseded or Rejected or Cancelled;
}

public static class QuotationOrigins
{
    public const string Sales = "SALES";
    public const string Automatic = "AUTOMATIC";

    public static readonly IReadOnlyList<string> All = [Sales, Automatic];
}

public static class QuotationEventTypes
{
    /// <summary>The backend priced the technician's bill of materials (issued automatically, or left as a draft for Sales).</summary>
    public const string AutomaticallyGenerated = "AUTOMATICALLY_GENERATED";
    /// <summary>The on-site survey this quotation was based on was amended (detail "survey vN"); written once per new survey version.</summary>
    public const string SurveyAmended = "SURVEY_AMENDED";
    /// <summary>Sales confirmed a draft against the current survey version after reviewing its lines (detail: old → new and the reason).</summary>
    public const string SurveyVersionConfirmed = "SURVEY_VERSION_CONFIRMED";
    public const string Created = "CREATED";
    public const string Updated = "UPDATED";
    public const string Issued = "ISSUED";
    public const string Accepted = "ACCEPTED";
    public const string RevisionRequested = "REVISION_REQUESTED";
    public const string RevisionCreated = "REVISION_CREATED";
    public const string Superseded = "SUPERSEDED";
    public const string DocumentAttached = "DOCUMENT_ATTACHED";
    /// <summary>The customer rejected the issued quotation (detail: the reason, if any).</summary>
    public const string Rejected = "REJECTED";
    /// <summary>The request was withdrawn or closed while the quotation was open (detail: "CODE: note").</summary>
    public const string Cancelled = "CANCELLED";
    /// <summary>Sales asked a Manager to approve the draft's discount / reduced prices (detail: "fp:{pricing fingerprint}; reason").</summary>
    public const string PricingApprovalRequested = "PRICING_APPROVAL_REQUESTED";
    /// <summary>A Manager approved exactly the pricing named by the fingerprint (detail: "fp:{fingerprint}; reason").</summary>
    public const string PricingApproved = "PRICING_APPROVED";
    /// <summary>A Manager rejected the requested pricing (detail: "fp:{fingerprint}; reason").</summary>
    public const string PricingRejected = "PRICING_REJECTED";

    /// <summary>
    /// Tie-breaker for events written in one transaction with the same timestamp (e.g. AUTOMATICALLY_GENERATED + ISSUED): their
    /// lifecycle order, so the history always reads the same way. Unknown types sort last.
    /// </summary>
    public static int Rank(string eventType) => Array.IndexOf(LifecycleOrder, eventType) is var i and >= 0 ? i : LifecycleOrder.Length;

    private static readonly string[] LifecycleOrder =
        [AutomaticallyGenerated, Created, RevisionCreated, Updated, SurveyAmended, SurveyVersionConfirmed, PricingApprovalRequested, PricingRejected, PricingApproved,
         DocumentAttached, Superseded, Issued, RevisionRequested, Accepted, Rejected, Cancelled];
}

public static class QuotationErrorCodes
{
    public const string NotFound = "QUOTATION_NOT_FOUND";
    public const string SurveyNotCompleted = "QUOTATION_SURVEY_NOT_COMPLETED";
    public const string DraftExists = "QUOTATION_DRAFT_EXISTS";
    public const string ActiveExists = "QUOTATION_ACTIVE_EXISTS";
    public const string NotDraft = "QUOTATION_NOT_DRAFT";
    public const string NotIssued = "QUOTATION_NOT_ISSUED";
    public const string Expired = "QUOTATION_EXPIRED";
    public const string InvalidLine = "QUOTATION_INVALID_LINE";
    public const string Empty = "QUOTATION_EMPTY";
    public const string StaleRevision = "QUOTATION_STALE_REVISION";
    public const string ProductUnavailable = "QUOTATION_PRODUCT_UNAVAILABLE";
    public const string AlreadyAccepted = "QUOTATION_ALREADY_ACCEPTED";
    public const string CannotRevise = "QUOTATION_CANNOT_REVISE";
    public const string SalesOwnerRequired = "QUOTATION_SALES_OWNER_REQUIRED";
    /// <summary>The on-site survey was amended after this quotation was based on it: it can no longer be issued or accepted as is (409).</summary>
    public const string SurveyChanged = "QUOTATION_SURVEY_CHANGED";
    /// <summary>An automatic draft still has unresolved pricing issues (or needs a review note) and cannot be issued (409).</summary>
    public const string PricingReviewRequired = "QUOTATION_PRICING_REVIEW_REQUIRED";
    /// <summary>The draft has a discount, a unit price below the catalog price or a zero price without a Manager approval of exactly this pricing (409).</summary>
    public const string PricingApprovalRequired = "QUOTATION_PRICING_APPROVAL_REQUIRED";
    /// <summary>An approval request for exactly this pricing is already waiting for a Manager (409).</summary>
    public const string PricingApprovalPending = "QUOTATION_PRICING_APPROVAL_PENDING";
    /// <summary>The draft's pricing needs no approval (no discount, no reduced or zero price) (409).</summary>
    public const string PricingApprovalNotRequired = "QUOTATION_PRICING_APPROVAL_NOT_REQUIRED";
    /// <summary>No approval request for the draft's current pricing is waiting (none made, already decided, or the pricing changed since) (409).</summary>
    public const string PricingApprovalNotPending = "QUOTATION_PRICING_APPROVAL_NOT_PENDING";
    /// <summary>The approver is the Sales owner of the quotation; a pricing exception is never self-approved (409).</summary>
    public const string SelfApprovalNotAllowed = "QUOTATION_SELF_APPROVAL_NOT_ALLOWED";
    /// <summary>A catalog product without a catalog price was added without an explicit unit price; a missing price is never free (400).</summary>
    public const string PriceMissing = "QUOTATION_PRICE_MISSING";
    /// <summary>
    /// The quotation's equipment lines do not match the current on-site survey's bill of materials (quantity of a listed product, or a panel /
    /// inverter not in it). The survey is the technical specification: amend it, or correct the lines (409).
    /// </summary>
    public const string BomMismatch = "QUOTATION_BOM_MISMATCH";
    /// <summary>The on-site survey concluded NOT_FEASIBLE: no installation quotation can be made for it (409).</summary>
    public const string SurveyNotFeasible = "QUOTATION_SURVEY_NOT_FEASIBLE";
    /// <summary>The survey request was withdrawn or closed: no quotation work on it any more (409).</summary>
    public const string RequestClosed = "QUOTATION_REQUEST_CLOSED";
}
