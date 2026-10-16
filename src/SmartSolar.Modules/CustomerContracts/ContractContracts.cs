using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Modules.CustomerContracts;

public sealed record AcceptedQuotationSource(
    Guid QuotationId, int QuotationVersion, string QuoteNumber, string Status, Guid SurveyRequestId, Guid CustomerUserId,
    Guid? AssignedSaleId, string Currency, decimal TotalAmount, string PricingSnapshotJson, string? CustomerSnapshotJson, string? Terms);

public interface IContractUnitOfWork : ITransactionalUnitOfWork
{
    Task<AcceptedQuotationSource?> GetQuotationAsync(Guid quotationId, CancellationToken cancellationToken);
    Task<Guid?> GetAssignedSaleAsync(Guid surveyRequestId, CancellationToken cancellationToken);
    Task<CustomerContract?> FindAsync(Guid contractId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerContract>> ListByQuotationAsync(Guid quotationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerContract>> ListBySurveyRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerContract>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContractEvent>> GetEventsAsync(Guid contractId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContractSignatureSubmission>> GetSubmissionsAsync(Guid contractId, bool track, CancellationToken cancellationToken);

    /// <summary>Single conditional UPDATE of status (and revision); false when another request changed it first.</summary>
    Task<bool> TryTransitionAsync(Guid contractId, string from, string to, DateTimeOffset now, CancellationToken cancellationToken);

    void AddContract(CustomerContract contract);
    void AddSubmission(ContractSignatureSubmission submission);
    void AddEvent(ContractEvent contractEvent);
}
