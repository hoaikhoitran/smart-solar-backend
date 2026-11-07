using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Modules.Quotations;

/// <summary>What quotation creation needs from Flow 1 and the on-site survey.</summary>
public sealed record QuotationSource(
    Guid SurveyRequestId, Guid? AssignedSaleId, string SurveyRequestStatus, Guid CustomerUserId, string CustomerName,
    string? CustomerPhone, string CustomerEmail, string PropertyName, string Province, string? District, string? StreetLine,
    Guid? OnSiteSurveyReportId, string? OnSiteSurveyStatus, int OnSiteSurveyVersion);

public sealed record CatalogProductSnapshot(Guid ProductId, string Sku, string Name, string Brand, string? Model, string Unit, decimal UnitPrice, string Currency, bool IsActive,
    string ProductType = "", decimal? RatedPowerW = null);

/// <summary>The completed on-site survey facts the automatic quotation is based on.</summary>
public sealed record QuotationSurveyBasis(Guid ReportId, string Status, int Version, string? InstallationFeasibility, string? BillOfMaterialsJson,
    Guid TechnicianUserId, Guid? SelectedSimulationId);

/// <summary>What closing a survey request depends on: its owners, status and the downstream commitments that forbid closing it.</summary>
public sealed record RequestClosure(DateTimeOffset? ClosedAt, Guid? ClosedByUserId, string? ReasonCode, string? Note);

public sealed record SalesRequestState(Guid SurveyRequestId, Guid CustomerUserId, Guid? AssignedSaleId, string Status, string? InstallationFeasibility,
    bool HasLiveContract, bool HasOpenAppointment, string? LiveContractStatus = null);

public interface IQuotationUnitOfWork : ITransactionalUnitOfWork
{
    Task<SalesRequestState?> GetRequestStateAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    /// <summary>
    /// Open request → <paramref name="status"/> (Cancelled or Closed) with the closure metadata, in one conditional UPDATE: false when the
    /// request was already terminal (a concurrent closure won).
    /// </summary>
    Task<bool> TryCloseRequestAsync(Guid surveyRequestId, PreSurvey.Enums.SurveyRequestStatus status, Guid actorUserId, string reasonCode, string? note,
        DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary><paramref name="from"/> → REJECTED or CANCELLED with actor, time and reason, conditionally; false when the status changed first.</summary>
    Task<bool> TryCloseQuotationAsync(Guid quotationId, string from, string to, Guid actorUserId, string? reason, DateTimeOffset now, CancellationToken cancellationToken);

    void AddRequestEvent(PreSurvey.Entities.SurveyRequestEvent requestEvent);

    /// <summary>The request's closure metadata (all null while it is open).</summary>
    Task<RequestClosure?> GetClosureAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    Task<QuotationSource?> GetSourceAsync(Guid surveyRequestId, CancellationToken cancellationToken);
    Task<CatalogProductSnapshot?> GetProductAsync(Guid productId, CancellationToken cancellationToken);
    Task<QuotationSurveyBasis?> GetSurveyBasisAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    /// <summary>AUTOMATIC drafts waiting for Sales (pricing exceptions), of requests owned by <paramref name="salesUserId"/> (null = all).</summary>
    Task<IReadOnlyList<Quotation>> ListAutomaticDraftsAsync(Guid? salesUserId, CancellationToken cancellationToken);

    /// <summary>Non-superseded quotations whose survey was amended after they were made, of requests owned by <paramref name="salesUserId"/> (null = all).</summary>
    Task<IReadOnlyList<Quotation>> ListSurveyChangedAsync(Guid? salesUserId, CancellationToken cancellationToken);

    /// <summary>DRAFT quotations with at least one pricing-approval request (the Manager queue filters them by their current approval state).</summary>
    Task<IReadOnlyList<Quotation>> ListDraftsWithPricingApprovalRequestsAsync(CancellationToken cancellationToken);

    /// <summary>Current version of the request's on-site survey report (0 when none).</summary>
    Task<int> GetSurveyVersionAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    /// <summary>True when a contract that is not SUPERSEDED exists for the quotation.</summary>
    Task<bool> HasLiveContractAsync(Guid quotationId, CancellationToken cancellationToken);

    /// <summary>
    /// ISSUED → ACCEPTED in one conditional UPDATE that also requires the quotation's survey version to still be the report's
    /// current version, so a concurrent amendment cannot slip between the check and the acceptance.
    /// </summary>
    Task<bool> TryAcceptAsync(Guid quotationId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<Quotation?> FindAsync(Guid quotationId, bool track, CancellationToken cancellationToken);

    /// <summary>
    /// Row lock on the survey request (PostgreSQL) taken first by every path that creates or ends commercial records of the request, so
    /// a closure and a contract or quotation creation for the same request never interleave. Lock order: survey_request → quotation → contract.
    /// </summary>
    Task LockSurveyRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken);

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
