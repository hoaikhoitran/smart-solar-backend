namespace SmartSolar.Api.Contracts.InstallationWorkflow;

/// <summary>Amounts in VND. Totals are always computed by the server; any total sent by a client is ignored.</summary>
public sealed record QuotationHeaderRequest(int? Revision, decimal LaborCost, decimal TransportCost, decimal OtherServiceCost, decimal DiscountAmount,
    decimal? TaxRatePercent, string? ValidUntil, string? Terms, string? Notes);

public sealed record QuotationLineRequest(int Revision, Guid? ProductId, string? Name, string? Unit, decimal Quantity, decimal? UnitPrice, string? Description);

public sealed record QuotationLineUpdateRequest(int Revision, decimal Quantity, decimal? UnitPrice, string? Description);

public sealed record FileReferenceRequest(Guid FileId);

public sealed record ReasonRequest(string? Reason);

/// <summary>Issue a draft. <c>reviewNote</c>: only for an AUTOMATIC draft whose remaining pricing issues Sales accepts (see pricingReview).</summary>
public sealed record IssueQuotationRequest(int Revision, string? ReviewNote = null);

/// <summary>Re-base a draft on the current on-site survey version after reviewing its lines; the reason is recorded.</summary>
public sealed record ConfirmSurveyVersionRequest(int Revision, string? Reason);

/// <summary>Sales asks for a Manager approval of the draft's current pricing exceptions; the reason is recorded.</summary>
public sealed record PricingApprovalRequest(int Revision, string? Reason);

/// <summary>Manager decision on the pending pricing approval: approve = true / false, reason required either way.</summary>
public sealed record PricingApprovalDecisionRequest(int Revision, bool? Approve, string? Reason);

/// <summary>Sales closure: reasonCode NOT_FEASIBLE, CUSTOMER_DECLINED, NO_RESPONSE or OTHER (note required for OTHER).</summary>
public sealed record CloseSurveyRequestRequest(string? ReasonCode, string? Note);
