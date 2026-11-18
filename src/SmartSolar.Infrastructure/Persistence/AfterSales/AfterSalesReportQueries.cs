using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Infrastructure.Persistence.AfterSales;

/// <summary>
/// Dashboard numbers. Every filter (period, zone, service type, status) runs in the database; counts are SQL COUNTs. Money sums are
/// taken over the selected amounts only (SQLite, used by the tests, cannot SUM decimals). The zone filter follows each row to its
/// installation project: a contract line through the contract, a package through its contract, a quotation through its request.
/// </summary>
public sealed class AfterSalesReportQueries(AppDbContext db) : IAfterSalesReportQueries
{
    public async Task<DashboardNumbers> GetNumbersAsync(DashboardFilter filter, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset nowUtc,
        TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var zoneId = filter.OperationalZoneId;
        // "At the end of the period" for a period that is still running means now.
        var endOfPeriod = toUtc < nowUtc ? toUtc : nowUtc;

        // ---- installations of the zone (every installation-related number follows the zone filter)
        var projects = db.InstallationProjects.AsNoTracking().Where(p => zoneId == null || p.OperationalZoneId == zoneId);
        var newProjects = await projects.CountAsync(p => p.CreatedAt >= fromUtc && p.CreatedAt < toUtc, cancellationToken);
        var handedOver = await projects.CountAsync(p => p.HandoverAt >= fromUtc && p.HandoverAt < toUtc, cancellationToken);
        var activeWarranties = await CountActiveWarrantiesAsync(projects, endOfPeriod, zone, cancellationToken);

        // ---- customers (not zone specific)
        var customers = db.UserAccounts.AsNoTracking().Where(u => u.DeletedAt == null && u.UserRoles.Any(r => r.Role.Code == RoleCodes.Customer));
        var totalCustomers = await customers.CountAsync(u => u.CreatedAt < endOfPeriod, cancellationToken);
        var newCustomers = await customers.CountAsync(u => u.CreatedAt >= fromUtc && u.CreatedAt < toUtc, cancellationToken);

        // ---- requests (service type and status filters apply here)
        var requests = db.ServiceRequests.AsNoTracking()
            .Where(r => filter.ServiceType == null || r.Kind == filter.ServiceType)
            .Where(r => filter.Status == null || r.Status == filter.Status)
            .Where(r => zoneId == null || projects.Any(p => p.Id == r.ProjectId));
        var faultsReported = await requests.CountAsync(r => r.Kind == ServiceRequestKinds.Fault && r.CreatedAt >= fromUtc && r.CreatedAt < toUtc, cancellationToken);
        var completed = requests.Where(r => r.Status == ServiceRequestStatuses.Completed && r.CompletedAt >= fromUtc && r.CompletedAt < toUtc);
        var repairsCompleted = await completed.CountAsync(r => r.Kind == ServiceRequestKinds.Fault, cancellationToken);
        var maintenanceCompleted = await completed.CountAsync(r => r.Kind != ServiceRequestKinds.Fault, cancellationToken);

        // ---- package terms covering the end of the period, and purchases verified in the period
        var subscriptions = db.ServiceSubscriptions.AsNoTracking()
            .Where(s => zoneId == null || projects.Any(p => p.ContractId == s.ContractId));
        var covering = subscriptions.Where(s => s.Status != SubscriptionStatuses.Cancelled && s.StartsAt <= endOfPeriod && s.EndsAt > endOfPeriod);
        var activeMaintenance = await covering.CountAsync(s => s.Kind == ServicePackageKinds.Maintenance, cancellationToken);
        var activeDisaster = await covering.CountAsync(s => s.Kind == ServicePackageKinds.DisasterProtection, cancellationToken);
        var purchases = await subscriptions.CountAsync(s => s.PaidAt >= fromUtc && s.PaidAt < toUtc, cancellationToken);

        // ---- technician activity (installation and after-sales intervals share installation_assignment)
        var activeTechnicians = await db.InstallationAssignments.AsNoTracking()
            .Where(a => a.Status != AssignmentStatuses.Declined && a.Status != AssignmentStatuses.Cancelled && a.Status != AssignmentStatuses.Replaced)
            .Where(a => a.StartsAt >= fromUtc && a.StartsAt < toUtc && projects.Any(p => p.Id == a.ProjectId))
            .Select(a => a.TechnicianUserId).Distinct().CountAsync(cancellationToken);
        var reportedVisits = await db.ServiceVisits.AsNoTracking()
            .Where(v => v.Status == VisitStatuses.Reported && v.ReportedAt >= fromUtc && v.ReportedAt < toUtc)
            .CountAsync(v => db.ServiceRequests.Any(r => r.Id == v.RequestId && projects.Any(p => p.Id == r.ProjectId)), cancellationToken);

        var (contracts, packages, services, unapplied) = await CollectedAsync(projects, zoneId, fromUtc, toUtc, cancellationToken);
        var refunded = await RefundedAsync(projects, zoneId, fromUtc, toUtc, cancellationToken);

        return new DashboardNumbers(totalCustomers, newCustomers, newProjects, handedOver, activeWarranties, faultsReported, repairsCompleted,
            maintenanceCompleted, activeMaintenance, activeDisaster, purchases, activeTechnicians, reportedVisits, contracts, packages, services, refunded, unapplied);
    }

    /// <summary>Handed-over installations whose warranty (handover + contract months, local calendar) covers the end of the period.</summary>
    private async Task<int> CountActiveWarrantiesAsync(IQueryable<InstallationProject> projects, DateTimeOffset endOfPeriod, TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        var handovers = await projects.Where(p => p.HandoverAt != null && p.HandoverAt < endOfPeriod)
            .Join(db.CustomerContracts.Where(c => c.WarrantyMonths != null), p => p.ContractId, c => c.Id, (p, c) => new { p.HandoverAt, c.WarrantyMonths })
            .ToListAsync(cancellationToken);
        return handovers.Count(h => AfterSalesRules.AddMonths(h.HandoverAt!.Value, h.WarrantyMonths!.Value, zone) > endOfPeriod);
    }

    /// <summary>
    /// Verified money of the period (PayOS by provider transaction time, CASH by its confirmation time) received by attempts in PAID,
    /// REFUND_REQUIRED or REVIEW_REQUIRED. Applied money per category: SETTLED lines of PAID attempts (a combined checkout is split by its
    /// lines, never counted as a whole), plus PAID contract-only attempts without lines (whole amount = the contract). Everything else that was
    /// received is unapplied: gross per attempt is its amount paid, so applied + unapplied = gross exactly. The zone filter follows each attempt
    /// to its installation (through its contract or any of its lines).
    /// </summary>
    private async Task<(decimal Contracts, decimal Packages, decimal Services, decimal Unapplied)> CollectedAsync(IQueryable<InstallationProject> projects,
        Guid? zoneId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        var received = db.PaymentAttempts.AsNoTracking()
            .Where(a => a.AmountPaid != null && (a.Status == PaymentStatuses.Paid || a.Status == PaymentStatuses.RefundRequired || a.Status == PaymentStatuses.ReviewRequired))
            .Where(a => (a.ProviderTransactionAt ?? a.PaidAt ?? a.UpdatedAt) >= fromUtc && (a.ProviderTransactionAt ?? a.PaidAt ?? a.UpdatedAt) < toUtc);
        var lines = db.PaymentAttemptItems.AsNoTracking();
        var inZone = zoneId == null
            ? received
            : received.Where(a => (a.ContractId != null && projects.Any(p => p.ContractId == a.ContractId))
                                  || lines.Any(l => l.PaymentAttemptId == a.Id
                                                    && ((l.SubscriptionId != null && db.ServiceSubscriptions.Any(s => s.Id == l.SubscriptionId && projects.Any(p => p.ContractId == s.ContractId)))
                                                        || (l.QuotationId != null && db.ServiceQuotations.Any(q => q.Id == l.QuotationId
                                                            && db.ServiceRequests.Any(r => r.Id == q.RequestId && projects.Any(p => p.Id == r.ProjectId))))
                                                        // 30/70 installment lines follow their contract to its installation.
                                                        || (l.ContractInstallmentId != null && db.ContractInstallments.Any(i => i.Id == l.ContractInstallmentId
                                                            && projects.Any(p => p.ContractId == i.ContractId))))));

        var attempts = await inZone.Select(a => new { a.Id, a.Status, a.AmountPaid, a.ContractId }).ToListAsync(cancellationToken);
        var ids = attempts.Select(a => a.Id).ToList();
        var attemptLines = await lines.Where(l => ids.Contains(l.PaymentAttemptId))
            .Select(l => new { l.PaymentAttemptId, l.ItemType, l.Amount, l.Status })
            .ToListAsync(cancellationToken);
        var byAttempt = attemptLines.ToLookup(l => l.PaymentAttemptId);

        decimal contracts = 0, packages = 0, services = 0, unapplied = 0;
        foreach (var a in attempts)
        {
            var gross = (decimal)a.AmountPaid!.Value;
            var mine = byAttempt[a.Id].ToList();
            if (a.Status != PaymentStatuses.Paid)
            {
                unapplied += gross;
                continue;
            }
            if (mine.Count == 0)
            {
                contracts += gross;   // contract-only attempt (online or cash) without lines
                continue;
            }
            var settled = mine.Where(l => l.Status == PaymentItemStatuses.Settled).ToList();
            // An installation quotation paid before its contract (technician-led flow) is contract revenue once settled (its contract was created).
            // 30/70 installments (deposit / balance) are contract revenue once settled, each counted once (one settled line per installment).
            contracts += settled.Where(l => l.ItemType is PaymentItemTypes.Contract or PaymentItemTypes.InstallationQuotation or PaymentItemTypes.ContractInstallment)
                .Sum(l => l.Amount);
            packages += settled.Where(l => l.ItemType is PaymentItemTypes.MaintenancePackage or PaymentItemTypes.DisasterPackage).Sum(l => l.Amount);
            services += settled.Where(l => l.ItemType is PaymentItemTypes.RepairQuotation or PaymentItemTypes.MaintenanceQuotation).Sum(l => l.Amount);
            unapplied += gross - settled.Sum(l => l.Amount);
        }
        return (contracts, packages, services, unapplied);
    }

    /// <summary>Manual refunds actually returned in the period (refund date), not approvals.</summary>
    private async Task<decimal> RefundedAsync(IQueryable<InstallationProject> projects, Guid? zoneId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var amounts = await db.RefundRecords.AsNoTracking()
            .Where(r => r.Status == RefundStatuses.Refunded && r.RefundedAt >= fromUtc && r.RefundedAt < toUtc)
            .Where(r => zoneId == null
                        || (r.SubscriptionId != null && db.ServiceSubscriptions.Any(s => s.Id == r.SubscriptionId && projects.Any(p => p.ContractId == s.ContractId)))
                        || (r.RequestId != null && db.ServiceRequests.Any(q => q.Id == r.RequestId && projects.Any(p => p.Id == q.ProjectId)))
                        || (r.QuotationId != null && db.ServiceQuotations.Any(q => q.Id == r.QuotationId
                            && db.ServiceRequests.Any(x => x.Id == q.RequestId && projects.Any(p => p.Id == x.ProjectId)))))
            .Select(r => r.ApprovedAmount ?? 0)
            .ToListAsync(cancellationToken);
        return amounts.Sum();
    }
}
