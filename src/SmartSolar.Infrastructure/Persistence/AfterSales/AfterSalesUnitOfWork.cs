using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Infrastructure.Persistence.AfterSales;

/// <summary>Flow 3 persistence: plain EF queries over the shared <see cref="AppDbContext"/> (one context per request).</summary>
public sealed class AfterSalesUnitOfWork(AppDbContext db) : TransactionalUnitOfWork(db), IAfterSalesUnitOfWork
{
    // ------------------------------------------------------------------ installations and assets

    /// <summary>Projects (filtered on the entity first: a filter on the projected record cannot be translated to SQL) joined to their contract.</summary>
    private IQueryable<InstallationFacts> Installations(System.Linq.Expressions.Expression<Func<InstallationProject, bool>> filter)
        => Db.InstallationProjects.AsNoTracking().Where(filter)
            .Join(Db.CustomerContracts, p => p.ContractId, c => c.Id, (p, c) => new InstallationFacts(
                p.Id, p.ContractId, p.CustomerUserId, p.PropertySiteId, p.Status, p.HandoverAt, p.OperationalZoneId, c.WarrantyMonths));

    public Task<InstallationFacts?> GetInstallationAsync(Guid projectId, CancellationToken cancellationToken)
        => Installations(p => p.Id == projectId).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<InstallationFacts>> ListInstallationsForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken)
        => await Installations(p => p.CustomerUserId == customerUserId).ToListAsync(cancellationToken);

    public async Task<ContractFacts?> GetContractAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await Db.CustomerContracts.AsNoTracking().Where(c => c.Id == contractId)
            .Select(c => new { c.Id, c.CustomerUserId, c.Status }).FirstOrDefaultAsync(cancellationToken);
        if (contract is null) return null;
        var project = await Db.InstallationProjects.AsNoTracking().Where(p => p.ContractId == contractId)
            .Select(p => new { p.Id, p.Status, p.HandoverAt }).FirstOrDefaultAsync(cancellationToken);
        return new ContractFacts(contract.Id, contract.CustomerUserId, contract.Status, project?.Id, project?.Status, project?.HandoverAt);
    }

    public async Task<string?> GetSiteAddressAsync(Guid propertySiteId, CancellationToken cancellationToken)
    {
        var site = await Db.PropertySites.AsNoTracking().Where(p => p.Id == propertySiteId)
            .Select(p => new { p.StreetLine, p.Ward, p.District, p.Province }).FirstOrDefaultAsync(cancellationToken);
        return site is null ? null : string.Join(", ", new[] { site.StreetLine, site.Ward, site.District, site.Province }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    public async Task<IReadOnlyList<InstalledAsset>> ListAssetsAsync(Guid projectId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.InstalledAssets : Db.InstalledAssets.AsNoTracking()).Where(a => a.ProjectId == projectId)
            .OrderBy(a => a.AssetType).ThenBy(a => a.SerialNumber).ToListAsync(cancellationToken);

    public Task<InstalledAsset?> FindAssetAsync(Guid assetId, bool track, CancellationToken cancellationToken)
        => (track ? Db.InstalledAssets : Db.InstalledAssets.AsNoTracking()).FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken);

    public Task<bool> SerialExistsAsync(string manufacturer, string serialNumber, Guid? exceptAssetId, CancellationToken cancellationToken)
        => Db.InstalledAssets.AsNoTracking().AnyAsync(a => a.Manufacturer == manufacturer && a.SerialNumber == serialNumber
            && (exceptAssetId == null || a.Id != exceptAssetId), cancellationToken);

    // ------------------------------------------------------------------ requests, items, visits, evidence

    public Task<ServiceRequest?> FindRequestAsync(Guid requestId, bool track, CancellationToken cancellationToken)
        => (track ? Db.ServiceRequests : Db.ServiceRequests.AsNoTracking()).FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken);

    public async Task<IReadOnlyList<ServiceRequest>> ListRequestsAsync(RequestFilter filter, CancellationToken cancellationToken)
    {
        var query = Db.ServiceRequests.AsNoTracking().AsQueryable();
        if (filter.CustomerUserId is { } customer) query = query.Where(r => r.CustomerUserId == customer);
        if (filter.ProjectId is { } project) query = query.Where(r => r.ProjectId == project);
        if (filter.Status is { } status) query = query.Where(r => r.Status == status);
        if (filter.Kind is { } kind) query = query.Where(r => r.Kind == kind);
        return (await query.ToListAsync(cancellationToken)).OrderByDescending(r => r.CreatedAt).ToList();
    }

    public async Task<IReadOnlyList<ServiceItem>> ListItemsAsync(Guid requestId, bool track, CancellationToken cancellationToken)
        => (await (track ? Db.ServiceItems : Db.ServiceItems.AsNoTracking()).Where(i => i.RequestId == requestId).ToListAsync(cancellationToken))
            .OrderBy(i => i.CreatedAt).ToList();

    public async Task<IReadOnlyList<ServiceVisit>> ListVisitsAsync(Guid requestId, bool track, CancellationToken cancellationToken)
        => (await (track ? Db.ServiceVisits : Db.ServiceVisits.AsNoTracking()).Where(v => v.RequestId == requestId).ToListAsync(cancellationToken))
            .OrderBy(v => v.CreatedAt).ToList();

    public Task<ServiceVisit?> FindVisitAsync(Guid visitId, bool track, CancellationToken cancellationToken)
        => (track ? Db.ServiceVisits : Db.ServiceVisits.AsNoTracking()).FirstOrDefaultAsync(v => v.Id == visitId, cancellationToken);

    public async Task<IReadOnlyList<ServiceVisit>> ListVisitsForTechnicianAsync(Guid technicianUserId, CancellationToken cancellationToken)
        => (await Db.ServiceVisits.AsNoTracking().Where(v => v.TechnicianUserId == technicianUserId).ToListAsync(cancellationToken))
            .OrderBy(v => v.StartsAt).ToList();

    public async Task<IReadOnlyList<ServiceVisit>> ListOnSiteWaitsEndedAsync(DateTimeOffset now, CancellationToken cancellationToken)
        => await Db.ServiceVisits.AsNoTracking()
            .Where(v => v.Purpose == VisitPurposes.Inspection && v.Status == VisitStatuses.InProgress && v.Report != null && v.EndsAt <= now)
            .ToListAsync(cancellationToken);

    public Task<InstallationAssignment?> FindCurrentAssignmentAsync(Guid visitId, bool track, CancellationToken cancellationToken)
        => (track ? Db.InstallationAssignments : Db.InstallationAssignments.AsNoTracking())
            .FirstOrDefaultAsync(a => a.ServiceVisitId == visitId && (a.Status == AssignmentStatuses.Pending || a.Status == AssignmentStatuses.Accepted), cancellationToken);

    public async Task<IReadOnlyList<ServiceEvidence>> ListEvidenceAsync(Guid requestId, CancellationToken cancellationToken)
        => (await Db.ServiceEvidence.AsNoTracking().Where(e => e.RequestId == requestId).ToListAsync(cancellationToken)).OrderBy(e => e.AddedAt).ToList();

    public Task<bool> IsEvidenceFileUsedAsync(Guid fileId, CancellationToken cancellationToken)
        => Db.ServiceEvidence.AsNoTracking().AnyAsync(e => e.FileId == fileId, cancellationToken);

    // ------------------------------------------------------------------ quotations and claims

    public async Task<IReadOnlyList<ServiceQuotation>> ListQuotationsAsync(Guid requestId, bool track, CancellationToken cancellationToken)
        => (await (track ? Db.ServiceQuotations : Db.ServiceQuotations.AsNoTracking()).Where(q => q.RequestId == requestId).ToListAsync(cancellationToken))
            .OrderBy(q => q.Version).ToList();

    public Task<ServiceQuotation?> FindQuotationAsync(Guid quotationId, bool track, CancellationToken cancellationToken)
        => (track ? Db.ServiceQuotations : Db.ServiceQuotations.AsNoTracking()).FirstOrDefaultAsync(q => q.Id == quotationId, cancellationToken);

    public async Task<IReadOnlyList<ServiceQuotation>> ListExpiredOpenQuotationsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
        => (await Db.ServiceQuotations.AsNoTracking().Where(q => q.Status == QuotationStatuses.Issued || q.Status == QuotationStatuses.Accepted).ToListAsync(cancellationToken))
            .Where(q => q.ExpiresAt <= nowUtc).ToList();   // timestamp comparison in memory (SQLite cannot compare DateTimeOffset)

    public Task<DisasterClaim?> FindClaimForRequestAsync(Guid requestId, bool track, CancellationToken cancellationToken)
        => (track ? Db.DisasterClaims : Db.DisasterClaims.AsNoTracking()).FirstOrDefaultAsync(c => c.RequestId == requestId, cancellationToken);

    // ------------------------------------------------------------------ catalog and purchases

    public async Task<IReadOnlyList<WarrantyOption>> ListWarrantyOptionsAsync(string? kind, bool track, CancellationToken cancellationToken)
        => (await (track ? Db.WarrantyOptions : Db.WarrantyOptions.AsNoTracking()).Where(o => kind == null || o.Kind == kind).ToListAsync(cancellationToken))
            .OrderBy(o => o.Kind).ThenBy(o => o.Months).ToList();

    public async Task<IReadOnlyList<ServicePackage>> ListPackagesAsync(string? kind, bool activeOnly, CancellationToken cancellationToken)
        => (await Db.ServicePackages.AsNoTracking().Where(p => (kind == null || p.Kind == kind) && (!activeOnly || p.IsActive)).ToListAsync(cancellationToken))
            .OrderBy(p => p.Kind).ThenBy(p => p.Price).ToList();

    public Task<ServicePackage?> FindPackageAsync(Guid packageId, bool track, CancellationToken cancellationToken)
        => (track ? Db.ServicePackages : Db.ServicePackages.AsNoTracking()).FirstOrDefaultAsync(p => p.Id == packageId, cancellationToken);

    public Task<ServiceSubscription?> FindSubscriptionAsync(Guid subscriptionId, bool track, CancellationToken cancellationToken)
        => (track ? Db.ServiceSubscriptions : Db.ServiceSubscriptions.AsNoTracking()).FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken);

    public async Task<IReadOnlyList<ServiceSubscription>> ListSubscriptionsForContractAsync(Guid contractId, bool track, CancellationToken cancellationToken)
        => (await (track ? Db.ServiceSubscriptions : Db.ServiceSubscriptions.AsNoTracking()).Where(s => s.ContractId == contractId).ToListAsync(cancellationToken))
            .OrderBy(s => s.CreatedAt).ToList();

    public async Task<IReadOnlyList<ServiceSubscription>> ListSubscriptionsForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken)
        => (await Db.ServiceSubscriptions.AsNoTracking().Where(s => s.CustomerUserId == customerUserId).ToListAsync(cancellationToken))
            .OrderByDescending(s => s.CreatedAt).ToList();

    public async Task<IReadOnlyList<ServiceSubscription>> ListSubscriptionsByStatusAsync(IReadOnlyCollection<string> statuses, CancellationToken cancellationToken)
    {
        var list = statuses.ToList();
        return await Db.ServiceSubscriptions.AsNoTracking().Where(s => list.Contains(s.Status)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MaintenanceEntitlement>> ListEntitlementsAsync(Guid subscriptionId, bool track, CancellationToken cancellationToken)
        => (await (track ? Db.MaintenanceEntitlements : Db.MaintenanceEntitlements.AsNoTracking()).Where(e => e.SubscriptionId == subscriptionId)
            .ToListAsync(cancellationToken)).OrderBy(e => e.PlanMonth).ToList();

    public Task<MaintenanceEntitlement?> FindEntitlementAsync(Guid entitlementId, bool track, CancellationToken cancellationToken)
        => (track ? Db.MaintenanceEntitlements : Db.MaintenanceEntitlements.AsNoTracking()).FirstOrDefaultAsync(e => e.Id == entitlementId, cancellationToken);

    public async Task<IReadOnlyList<MaintenanceEntitlement>> ListEntitlementsByStatusAsync(IReadOnlyCollection<string> statuses, CancellationToken cancellationToken)
    {
        var list = statuses.ToList();
        return await Db.MaintenanceEntitlements.AsNoTracking().Where(e => list.Contains(e.Status)).ToListAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ sales queue, refunds, history

    public async Task<IReadOnlyList<SalesTask>> ListTasksAsync(string? status, Guid? assignedSalesUserId, CancellationToken cancellationToken)
        => (await Db.SalesTasks.AsNoTracking()
                .Where(t => (status == null || t.Status == status) && (assignedSalesUserId == null || t.AssignedSalesUserId == assignedSalesUserId))
                .ToListAsync(cancellationToken))
            .OrderBy(t => t.CreatedAt).ToList();

    public Task<SalesTask?> FindTaskAsync(Guid taskId, bool track, CancellationToken cancellationToken)
        => (track ? Db.SalesTasks : Db.SalesTasks.AsNoTracking()).FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

    public Task<SalesTask?> FindOpenTaskAsync(Guid? requestId, Guid? entitlementId, string taskType, bool track, CancellationToken cancellationToken)
        => (track ? Db.SalesTasks : Db.SalesTasks.AsNoTracking()).FirstOrDefaultAsync(t => t.TaskType == taskType
            && (requestId == null || t.RequestId == requestId) && (entitlementId == null || t.EntitlementId == entitlementId)
            && (t.Status == SalesTaskStatuses.Open || t.Status == SalesTaskStatuses.Claimed || t.Status == SalesTaskStatuses.InProgress), cancellationToken);

    public async Task<IReadOnlyList<RefundRecord>> ListRefundsAsync(string? status, CancellationToken cancellationToken)
        => (await Db.RefundRecords.AsNoTracking().Where(r => status == null || r.Status == status).ToListAsync(cancellationToken))
            .OrderByDescending(r => r.CreatedAt).ToList();

    public Task<RefundRecord?> FindRefundAsync(Guid refundId, bool track, CancellationToken cancellationToken)
        => (track ? Db.RefundRecords : Db.RefundRecords.AsNoTracking()).FirstOrDefaultAsync(r => r.Id == refundId, cancellationToken);

    public async Task<IReadOnlyList<AfterSalesEvent>> ListEventsAsync(string subjectType, Guid subjectId, CancellationToken cancellationToken)
        => (await Db.AfterSalesEvents.AsNoTracking().Where(e => e.SubjectType == subjectType && e.SubjectId == subjectId).ToListAsync(cancellationToken))
            .OrderBy(e => e.CreatedAt).ToList();

    public async Task<IReadOnlyList<AfterSalesNotification>> ListNotificationsAsync(Guid recipientUserId, CancellationToken cancellationToken)
        => (await Db.AfterSalesNotifications.AsNoTracking().Where(n => n.RecipientUserId == recipientUserId).ToListAsync(cancellationToken))
            .OrderByDescending(n => n.CreatedAt).Take(100).ToList();

    public Task<PaymentAttempt?> FindPaymentAttemptAsync(Guid attemptId, CancellationToken cancellationToken)
        => Db.PaymentAttempts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);

    // ------------------------------------------------------------------ users and locks

    public Task<bool> IsActiveUserWithRoleAsync(Guid userId, string roleCode, CancellationToken cancellationToken)
        => Db.UserAccounts.AsNoTracking().AnyAsync(u => u.Id == userId && u.DeletedAt == null && u.Status == UserStatus.Active
            && u.UserRoles.Any(r => r.Role.Code == roleCode), cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListActiveUserIdsWithRoleAsync(string roleCode, CancellationToken cancellationToken)
        => await Db.UserAccounts.AsNoTracking()
            .Where(u => u.DeletedAt == null && u.Status == UserStatus.Active && u.UserRoles.Any(r => r.Role.Code == roleCode))
            .OrderBy(u => u.Id).Select(u => u.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();
        return ids.Count == 0
            ? new Dictionary<Guid, string>()
            : await Db.UserAccounts.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
    }

    public Task LockTechniciansAsync(IEnumerable<Guid> technicianUserIds, CancellationToken cancellationToken) => LockTechnicianProfilesAsync(technicianUserIds, cancellationToken);

    public async Task LockContractAsync(Guid contractId, CancellationToken cancellationToken)
    {
        if (!Db.Database.IsNpgsql()) return;   // SQLite serialises writers
        await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM customer_contract WHERE id = {contractId} FOR UPDATE", cancellationToken);
    }

    public void Add<TEntity>(TEntity entity) where TEntity : class => Db.Set<TEntity>().Add(entity);
}
