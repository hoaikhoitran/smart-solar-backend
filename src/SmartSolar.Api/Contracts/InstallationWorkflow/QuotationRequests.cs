namespace SmartSolar.Api.Contracts.InstallationWorkflow;

/// <summary>Amounts in VND. Totals are always computed by the server; any total sent by a client is ignored.</summary>
public sealed record QuotationHeaderRequest(int? Revision, decimal LaborCost, decimal TransportCost, decimal OtherServiceCost, decimal DiscountAmount,
    decimal? TaxRatePercent, string? ValidUntil, string? Terms, string? Notes);

public sealed record QuotationLineRequest(int Revision, Guid? ProductId, string? Name, string? Unit, decimal Quantity, decimal? UnitPrice, string? Description);

public sealed record QuotationLineUpdateRequest(int Revision, decimal Quantity, decimal? UnitPrice, string? Description);

public sealed record FileReferenceRequest(Guid FileId);

public sealed record ReasonRequest(string? Reason);
