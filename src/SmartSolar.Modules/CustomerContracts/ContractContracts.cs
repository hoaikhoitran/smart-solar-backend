using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Modules.CustomerContracts;

public sealed record AcceptedQuotationSource(
    Guid QuotationId, int QuotationVersion, string QuoteNumber, string Status, Guid SurveyRequestId, Guid CustomerUserId,
    Guid? AssignedSaleId, string Currency, decimal TotalAmount, string PricingSnapshotJson, string? CustomerSnapshotJson, string? Terms,
    int QuotationSurveyVersion = 0, int CurrentSurveyVersion = 0, DateTimeOffset? ValidUntil = null, decimal? InstallationTotal = null,
    IReadOnlyList<Quotations.QuotationServicePackage>? ServicePackages = null)
{
    /// <summary>True when the quotation bundles per-panel service packages: <see cref="TotalAmount"/> is then the grand total (installation + packages).</summary>
    public bool HasServicePackages => ServicePackages is { Count: > 0 };
}

/// <summary>The verified payment that paid a contract before it existed (technician-led flow): attempt, when, how much.</summary>
public sealed record ContractPrepayment(Guid PaymentAttemptId, DateTimeOffset PaidAt, decimal Amount, string Method);

public interface IContractUnitOfWork : ITransactionalUnitOfWork, IInAppNotificationWriter, IPaymentPlanStore
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

    /// <summary>The bundled package commitments of a contract (empty for contracts without packages).</summary>
    Task<IReadOnlyList<ContractServicePackage>> GetServicePackagesAsync(Guid contractId, bool track, CancellationToken cancellationToken);
    void AddServicePackage(ContractServicePackage servicePackage);

    void AddContract(CustomerContract contract);
    void AddSubmission(ContractSignatureSubmission submission);
    void AddEvent(ContractEvent contractEvent);
}

/// <summary>
/// Provisions the per-panel packages bundled into a contract (implemented by the after-sales module). Called inside the payment confirmation transaction
/// right after the balance made the contract PAID, and by staff to retry a reconciliation. Idempotent: a commitment is provisioned at most once.
/// </summary>
public interface IBundledPackageProvisioner
{
    Task ContractFullyPaidAsync(Guid contractId, DateTimeOffset paidAt, DateTimeOffset now, CancellationToken cancellationToken);
}
