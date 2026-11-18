namespace SmartSolar.Modules.CustomerContracts;

/// <summary>Filters of the staff contract list (read-all for Sales / Technician / Manager / Admin). All optional; combined with AND.</summary>
/// <param name="Search">Contract number, customer name, phone or e-mail (contains, case-insensitive).</param>
/// <param name="Overdue">true = only contracts with an overdue installment.</param>
public sealed record ContractListFilter(Guid? CustomerUserId = null, string? Status = null, string? PaymentPlan = null, Guid? SurveyRequestId = null,
    Guid? PropertySiteId = null, Guid? QuotationId = null, Guid? ProjectId = null, string? Search = null, bool? Overdue = null, DateTimeOffset? Now = null);

/// <summary>One row of the staff contract list (joined data; money figures from installments for payment-plan contracts).</summary>
public sealed record ContractListRow(Guid ContractId, string ContractNumber, int Version, string Status, string? PaymentPlan, Guid CustomerUserId, string CustomerName,
    string? CustomerPhone, Guid SurveyRequestId, Guid? PropertySiteId, string? PropertyName, Guid QuotationId, decimal TotalAmount, Guid? ProjectId,
    string? ProjectStatus, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt);

/// <summary>Filters of the collection case list.</summary>
public sealed record CollectionCaseFilter(string? Status = null, bool OpenOnly = false, Guid? CustomerUserId = null, Guid? ContractId = null);

/// <summary>Persistence of the 30/70 plan (installments, paper signature, collection, access audit) — part of <see cref="IContractUnitOfWork"/>.</summary>
public interface IPaymentPlanStore
{
    /// <summary>Row lock (FOR UPDATE) on the contract (PostgreSQL). Taken before installment / signature / collection rows of the contract.</summary>
    Task LockContractAsync(Guid contractId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContractInstallment>> GetInstallmentsAsync(Guid contractId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContractInstallment>> GetInstallmentsForContractsAsync(IReadOnlyCollection<Guid> contractIds, CancellationToken cancellationToken);
    Task<ContractInstallment?> FindInstallmentAsync(Guid installmentId, bool track, CancellationToken cancellationToken);
    /// <summary>True when an attempt with a payable (PENDING / PROCESSING) status holds a line of the installment.</summary>
    Task<bool> HasPayableInstallmentAttemptAsync(Guid installmentId, CancellationToken cancellationToken);
    void AddInstallment(ContractInstallment installment);

    Task<IReadOnlyList<ContractSignatureSet>> GetSignatureSetsAsync(Guid contractId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContractSignedPage>> GetSignedPagesAsync(Guid contractId, bool track, CancellationToken cancellationToken);
    void AddSignatureSet(ContractSignatureSet set);
    void AddSignedPage(ContractSignedPage page);
    /// <summary>The technician of the request's latest COMPLETED on-site survey (the technician assigned to the on-site sale); null when none.</summary>
    Task<Guid?> GetSurveyTechnicianAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    Task<ContractCollectionCase?> FindCollectionCaseAsync(Guid caseId, bool track, CancellationToken cancellationToken);
    Task<ContractCollectionCase?> FindOpenCollectionCaseAsync(Guid installmentId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContractCollectionCase>> GetCollectionCasesAsync(Guid contractId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContractCollectionActivity>> GetCollectionActivitiesAsync(Guid caseId, CancellationToken cancellationToken);
    Task<(IReadOnlyList<ContractCollectionCase> Rows, int Total)> PageCollectionCasesAsync(CollectionCaseFilter filter, int skip, int take, CancellationToken cancellationToken);
    /// <summary>DUE installments whose deadline passed (strictly before <paramref name="now"/>) and that have no open collection case.</summary>
    Task<IReadOnlyList<Guid>> ListOverdueInstallmentsWithoutCaseAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
    void AddCollectionCase(ContractCollectionCase collectionCase);
    void AddCollectionActivity(ContractCollectionActivity activity);

    void AddAccessAudit(ContractAccessAudit audit);
    Task<(IReadOnlyList<ContractListRow> Rows, int Total)> PageContractsAsync(ContractListFilter filter, int skip, int take, CancellationToken cancellationToken);
    Task<ContractListRow?> GetContractRowAsync(Guid contractId, CancellationToken cancellationToken);
}
