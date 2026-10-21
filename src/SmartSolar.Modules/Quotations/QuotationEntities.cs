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

    public static readonly IReadOnlyList<string> All = [Draft, Issued, Accepted, RevisionRequested, Superseded];
}

public static class QuotationEventTypes
{
    public const string Created = "CREATED";
    public const string Updated = "UPDATED";
    public const string Issued = "ISSUED";
    public const string Accepted = "ACCEPTED";
    public const string RevisionRequested = "REVISION_REQUESTED";
    public const string RevisionCreated = "REVISION_CREATED";
    public const string Superseded = "SUPERSEDED";
    public const string DocumentAttached = "DOCUMENT_ATTACHED";
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
}
