using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Modules.Quotations;

/// <summary>What quotation creation needs from Flow 1 and the on-site survey.</summary>
public sealed record QuotationSource(
    Guid SurveyRequestId, Guid? AssignedSaleId, string SurveyRequestStatus, Guid CustomerUserId, string CustomerName,
    string? CustomerPhone, string CustomerEmail, string PropertyName, string Province, string? District, string? StreetLine,
    Guid? OnSiteSurveyReportId, string? OnSiteSurveyStatus, int OnSiteSurveyVersion);

public sealed record CatalogProductSnapshot(Guid ProductId, string Sku, string Name, string Brand, string? Model, string Unit, decimal UnitPrice, string Currency, bool IsActive);

public interface IQuotationUnitOfWork : ITransactionalUnitOfWork
{
    Task<QuotationSource?> GetSourceAsync(Guid surveyRequestId, CancellationToken cancellationToken);
    Task<CatalogProductSnapshot?> GetProductAsync(Guid productId, CancellationToken cancellationToken);
    Task<Quotation?> FindAsync(Guid quotationId, bool track, CancellationToken cancellationToken);

    /// <summary>Row lock on the quotation for the current transaction (PostgreSQL), so concurrent edits are serialised.</summary>
    Task LockAsync(Guid quotationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Quotation>> ListBySurveyRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Quotation>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<QuotationLine>> GetLinesAsync(Guid quotationId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<QuotationEvent>> GetEventsAsync(Guid quotationId, CancellationToken cancellationToken);

    /// <summary>Conditional status change (single UPDATE … WHERE status = from); false when another request changed it first.</summary>
    Task<bool> TryTransitionAsync(Guid quotationId, string from, string to, DateTimeOffset now, CancellationToken cancellationToken);

    void AddQuotation(Quotation quotation);
    void AddLine(QuotationLine line);
    void RemoveLine(QuotationLine line);
    void AddEvent(QuotationEvent quotationEvent);
}
