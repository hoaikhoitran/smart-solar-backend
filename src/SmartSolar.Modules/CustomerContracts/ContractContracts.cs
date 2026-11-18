using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Modules.CustomerContracts;

public sealed record AcceptedQuotationSource(
    Guid QuotationId, int QuotationVersion, string QuoteNumber, string Status, Guid SurveyRequestId, Guid CustomerUserId,
    Guid? AssignedSaleId, string Currency, decimal TotalAmount, string PricingSnapshotJson, string? CustomerSnapshotJson, string? Terms,
    int QuotationSurveyVersion = 0, int CurrentSurveyVersion = 0, DateTimeOffset? ValidUntil = null);

/// <summary>The verified payment that paid a contract before it existed (technician-led flow): attempt, when, how much.</summary>
public sealed record ContractPrepayment(Guid PaymentAttemptId, DateTimeOffset PaidAt, decimal Amount, string Method);

public interface IContractUnitOfWork : ITransactionalUnitOfWork, IInAppNotificationWriter
{
    Task<AcceptedQuotationSource?> GetQuotationAsync(Guid quotationId, CancellationToken cancellationToken);
    Task<Guid?> GetAssignedSaleAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    /// <summary>Row lock on the survey request (PostgreSQL); the same lock closures take, always before any quotation or contract row.</summary>
    Task LockSurveyRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    /// <summary>True when the request was withdrawn (Cancelled) or Closed.</summary>
    Task<bool> IsSurveyRequestClosedAsync(Guid surveyRequestId, CancellationToken cancellationToken);
    Task<CustomerContract?> FindAsync(Guid contractId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerContract>> ListByQuotationAsync(Guid quotationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerContract>> ListBySurveyRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerContract>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContractEvent>> GetEventsAsync(Guid contractId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ContractSignatureSubmission>> GetSubmissionsAsync(Guid contractId, bool track, CancellationToken cancellationToken);

    /// <summary>Single conditional UPDATE of status (and revision); false when another request changed it first.</summary>
    Task<bool> TryTransitionAsync(Guid contractId, string from, string to, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Active installation warranty durations (months) offered by the Admin, and the default one (null when none is set).</summary>
    Task<(IReadOnlyList<int> Active, int? Default)> GetInstallationWarrantyOptionsAsync(CancellationToken cancellationToken);

    /// <summary>The PAID payment attempt linked to the contract (an installation quotation paid before the contract was created); null when none.</summary>
    Task<ContractPrepayment?> GetPrepaymentAsync(Guid contractId, CancellationToken cancellationToken);

    /// <summary>True when a verified payment of the installation quotation was settled (the only way a contract may be created since 2026-11-18).</summary>
    Task<bool> HasSettledQuotationPaymentAsync(Guid quotationId, CancellationToken cancellationToken);

    void AddContract(CustomerContract contract);
    void AddSubmission(ContractSignatureSubmission submission);
    void AddEvent(ContractEvent contractEvent);
}
