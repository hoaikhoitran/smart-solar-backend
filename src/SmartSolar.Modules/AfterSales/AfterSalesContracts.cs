using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Modules.AfterSales;

/// <summary>
/// The facts of an installation that after-sales rules need: owner, site, handover and the warranty months copied into the
/// issued contract (null for contracts created before Flow 3).
/// </summary>
public sealed record InstallationFacts(
    Guid ProjectId, Guid ContractId, Guid CustomerUserId, Guid PropertySiteId, string ProjectStatus, DateTimeOffset? HandoverAt,
    Guid? OperationalZoneId, int? WarrantyMonths);

/// <summary>A customer contract as seen by package purchases (the project may not exist yet).</summary>
public sealed record ContractFacts(Guid ContractId, Guid CustomerUserId, string Status, Guid? ProjectId, string? ProjectStatus, DateTimeOffset? HandoverAt);

public sealed record RequestFilter(Guid? CustomerUserId = null, Guid? ProjectId = null, string? Status = null, string? Kind = null);

/// <summary>Persistence of Flow 3 (implemented in Infrastructure). Writes happen inside <see cref="ITransactionalUnitOfWork.ExecuteInTransactionAsync{T}"/>.</summary>
public interface IAfterSalesUnitOfWork : ITransactionalUnitOfWork
{
    // ---- installations and assets
    Task<InstallationFacts?> GetInstallationAsync(Guid projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<InstallationFacts>> ListInstallationsForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken);
    Task<ContractFacts?> GetContractAsync(Guid contractId, CancellationToken cancellationToken);
    /// <summary>Street, ward, district and province of a site, for the technician's visit.</summary>
    Task<string?> GetSiteAddressAsync(Guid propertySiteId, CancellationToken cancellationToken);

    Task<IReadOnlyList<InstalledAsset>> ListAssetsAsync(Guid projectId, bool track, CancellationToken cancellationToken);
    Task<InstalledAsset?> FindAssetAsync(Guid assetId, bool track, CancellationToken cancellationToken);
    Task<bool> SerialExistsAsync(string manufacturer, string serialNumber, Guid? exceptAssetId, CancellationToken cancellationToken);

    // ---- requests, items, visits, evidence
    Task<ServiceRequest?> FindRequestAsync(Guid requestId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<ServiceRequest>> ListRequestsAsync(RequestFilter filter, CancellationToken cancellationToken);
    Task<IReadOnlyList<ServiceItem>> ListItemsAsync(Guid requestId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<ServiceVisit>> ListVisitsAsync(Guid requestId, bool track, CancellationToken cancellationToken);
    Task<ServiceVisit?> FindVisitAsync(Guid visitId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<ServiceVisit>> ListVisitsForTechnicianAsync(Guid technicianUserId, CancellationToken cancellationToken);
    /// <summary>Inspection visits where the technician reported the diagnosis and stayed on site, whose reserved interval ended before <paramref name="now"/>.</summary>
    Task<IReadOnlyList<ServiceVisit>> ListOnSiteWaitsEndedAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<InstallationAssignment?> FindCurrentAssignmentAsync(Guid visitId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<ServiceEvidence>> ListEvidenceAsync(Guid requestId, CancellationToken cancellationToken);
    Task<bool> IsEvidenceFileUsedAsync(Guid fileId, CancellationToken cancellationToken);

    // ---- quotations and disaster claims
    Task<IReadOnlyList<ServiceQuotation>> ListQuotationsAsync(Guid requestId, bool track, CancellationToken cancellationToken);
    Task<ServiceQuotation?> FindQuotationAsync(Guid quotationId, bool track, CancellationToken cancellationToken);
    /// <summary>ISSUED / ACCEPTED quotations whose validity ended before <paramref name="nowUtc"/> (for the worker).</summary>
    Task<IReadOnlyList<ServiceQuotation>> ListExpiredOpenQuotationsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<DisasterClaim?> FindClaimForRequestAsync(Guid requestId, bool track, CancellationToken cancellationToken);

    // ---- catalog and purchases
    Task<IReadOnlyList<WarrantyOption>> ListWarrantyOptionsAsync(string? kind, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<ServicePackage>> ListPackagesAsync(string? kind, bool activeOnly, CancellationToken cancellationToken);
    Task<ServicePackage?> FindPackageAsync(Guid packageId, bool track, CancellationToken cancellationToken);
    Task<ServiceSubscription?> FindSubscriptionAsync(Guid subscriptionId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<ServiceSubscription>> ListSubscriptionsForContractAsync(Guid contractId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<ServiceSubscription>> ListSubscriptionsForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ServiceSubscription>> ListSubscriptionsByStatusAsync(IReadOnlyCollection<string> statuses, CancellationToken cancellationToken);
    Task<IReadOnlyList<MaintenanceEntitlement>> ListEntitlementsAsync(Guid subscriptionId, bool track, CancellationToken cancellationToken);
    Task<MaintenanceEntitlement?> FindEntitlementAsync(Guid entitlementId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<MaintenanceEntitlement>> ListEntitlementsByStatusAsync(IReadOnlyCollection<string> statuses, CancellationToken cancellationToken);

    // ---- sales queue, refunds, history
    Task<IReadOnlyList<SalesTask>> ListTasksAsync(string? status, Guid? assignedSalesUserId, CancellationToken cancellationToken);
    Task<SalesTask?> FindTaskAsync(Guid taskId, bool track, CancellationToken cancellationToken);
    Task<SalesTask?> FindOpenTaskAsync(Guid? requestId, Guid? entitlementId, string taskType, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<RefundRecord>> ListRefundsAsync(string? status, CancellationToken cancellationToken);
    Task<RefundRecord?> FindRefundAsync(Guid refundId, bool track, CancellationToken cancellationToken);
    Task<IReadOnlyList<AfterSalesEvent>> ListEventsAsync(string subjectType, Guid subjectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AfterSalesNotification>> ListNotificationsAsync(Guid recipientUserId, CancellationToken cancellationToken);

    // ---- payments seen from after-sales
    Task<PaymentAttempt?> FindPaymentAttemptAsync(Guid attemptId, CancellationToken cancellationToken);

    // ---- users
    Task<bool> IsActiveUserWithRoleAsync(Guid userId, string roleCode, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> ListActiveUserIdsWithRoleAsync(string roleCode, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>Row locks on technician profiles (same order and table as installation and survey scheduling; PostgreSQL only).</summary>
    Task LockTechniciansAsync(IEnumerable<Guid> technicianUserIds, CancellationToken cancellationToken);

    /// <summary>Row lock on the contract, so two terms of one installation are never computed at the same time (PostgreSQL only).</summary>
    Task LockContractAsync(Guid contractId, CancellationToken cancellationToken);

    void Add<TEntity>(TEntity entity) where TEntity : class;
}

/// <summary>Raw numbers for one dashboard period (implemented in Infrastructure with plain EF queries).</summary>
public interface IAfterSalesReportQueries
{
    /// <param name="zone">Business time zone (warranty end dates are calendar months in local time).</param>
    Task<DashboardNumbers> GetNumbersAsync(DashboardFilter filter, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset nowUtc, TimeZoneInfo zone,
        CancellationToken cancellationToken);
}

public sealed record DashboardFilter(Guid? OperationalZoneId, string? ServiceType, string? Status);

/// <summary>
/// Metrics of one period [from, to). Collections are verified PayOS money by provider transaction time, split per item category so a
/// combined checkout is never counted twice; refunds are money actually returned (refund date), so net = collected − refunded.
/// </summary>
public sealed record DashboardNumbers(
    int TotalCustomers, int NewCustomers, int InstallationProjects, int CompletedInstallations, int ActiveInstallationWarranties,
    int ReportedFaultRequests, int CompletedRepairs, int CompletedMaintenanceVisits, int ActiveMaintenanceSubscriptions,
    int ActiveDisasterSubscriptions, int PackagePurchases, int ActiveTechnicians, int TechnicianServiceVisits,
    decimal CollectedContracts, decimal CollectedPackages, decimal CollectedServices, decimal Refunded)
{
    public decimal CollectedTotal => CollectedContracts + CollectedPackages + CollectedServices;
    public decimal NetCollected => CollectedTotal - Refunded;
}
