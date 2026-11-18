using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.Scheduling.Constants;

namespace SmartSolar.Infrastructure.Persistence.AfterSales;

/// <summary>
/// The Admin operational overview: every distribution is a SQL GROUP BY / COUNT; money is summed over the selected amounts only (SQLite, used by the
/// tests, cannot SUM decimals). No row is invented: a metric without data is an empty list or zero.
/// </summary>
public sealed class AdminOverviewQueries(AppDbContext db) : IAdminOverviewQueries
{
    public async Task<AdminOverviewView> GetAsync(DateOnly from, DateOnly to, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset nowUtc, CancellationToken ct)
    {
        // ---- accounts per role
        var accountRows = await (from ur in db.UserRoles.AsNoTracking()
                                 join u in db.UserAccounts.AsNoTracking() on ur.UserId equals u.Id
                                 join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                                 where u.DeletedAt == null
                                 group u by new { r.Code, u.Status } into g
                                 select new { g.Key.Code, g.Key.Status, Count = g.Count() }).ToListAsync(ct);
        var accounts = accountRows.GroupBy(r => r.Code).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new OverviewRoleAccounts(g.Key, g.Sum(x => x.Count),
            g.Where(x => x.Status == UserStatus.Active).Sum(x => x.Count), g.Where(x => x.Status == UserStatus.PendingVerification).Sum(x => x.Count),
            g.Where(x => x.Status is UserStatus.Suspended or UserStatus.Disabled).Sum(x => x.Count))).ToList();

        // ---- pre-sales and installation (snapshot)
        var surveyRequests = Buckets((await db.SurveyRequests.AsNoTracking().GroupBy(s => s.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct))
            .Select(x => (x.Key.ToString(), x.Count)));
        var outstandingAppointments = await db.TechnicianAppointments.AsNoTracking()
            .CountAsync(a => a.Status == AppointmentStatuses.PendingAcceptance || a.Status == AppointmentStatuses.Escalated, ct);
        var quotations = await CountByAsync(db.Quotations.AsNoTracking().Select(q => q.Status), ct);
        var contracts = await CountByAsync(db.CustomerContracts.AsNoTracking().Select(c => c.Status), ct);
        var withPdf = await db.CustomerContracts.AsNoTracking().CountAsync(c => c.OfficialDocumentFileId != null, ct);
        var withScan = await db.CustomerContracts.AsNoTracking().CountAsync(c => c.SignedDocumentFileId != null, ct);
        var staffScans = await db.ContractSignatureSubmissions.AsNoTracking()
            .CountAsync(s => db.CustomerContracts.Any(c => c.Id == s.ContractId && c.CustomerUserId != s.SubmittedByUserId), ct);
        var projects = await CountByAsync(db.InstallationProjects.AsNoTracking().Select(p => p.Status), ct);
        var schedules = await CountByAsync(db.InstallationScheduleProposals.AsNoTracking().Select(p => p.Status), ct);
        var openOffers = await db.InstallationSlotOffers.AsNoTracking().CountAsync(o => o.Status == InstallationSlotOfferStatuses.Open, ct);

        // ---- after-sales (snapshot)
        var serviceRequests = Buckets((await db.ServiceRequests.AsNoTracking().GroupBy(r => new { r.Kind, r.Status })
            .Select(g => new { g.Key.Kind, g.Key.Status, Count = g.Count() }).ToListAsync(ct)).Select(x => ($"{x.Kind}:{x.Status}", x.Count)));
        var serviceQuotations = await CountByAsync(db.ServiceQuotations.AsNoTracking().Select(q => q.Status), ct);
        var byIssuer = await CountByAsync(db.ServiceQuotations.AsNoTracking().Select(q => q.IssuedByRole), ct);
        var claims = await CountByAsync(db.DisasterClaims.AsNoTracking().Select(c => c.Status), ct);
        var subscriptions = Buckets((await db.ServiceSubscriptions.AsNoTracking().GroupBy(s => new { s.Kind, s.Status })
            .Select(g => new { g.Key.Kind, g.Key.Status, Count = g.Count() }).ToListAsync(ct)).Select(x => ($"{x.Kind}:{x.Status}", x.Count)));
        var openTasks = await CountByAsync(db.SalesTasks.AsNoTracking()
            .Where(t => t.Status == SalesTaskStatuses.Open || t.Status == SalesTaskStatuses.Claimed || t.Status == SalesTaskStatuses.InProgress).Select(t => t.TaskType), ct);
        var openVisits = await CountByAsync(db.ServiceVisits.AsNoTracking()
            .Where(v => v.Status == VisitStatuses.Scheduled || v.Status == VisitStatuses.InProgress).Select(v => v.Status), ct);

        // ---- money (snapshot)
        var pendingCash = await db.PaymentAttempts.AsNoTracking().Where(a => a.Provider == PaymentProviders.Cash && a.Status == PaymentStatuses.Pending)
            .Select(a => a.Amount).ToListAsync(ct);
        var refunds = (await db.RefundRecords.AsNoTracking().Select(r => new { r.Status, r.OriginalAmount }).ToListAsync(ct))
            .GroupBy(r => r.Status).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new OverviewBucket(g.Key, g.Count(), g.Sum(x => x.OriginalAmount))).ToList();

        // ---- period: attempts created, money collected, combined checkouts, package channels, quotation values
        var created = (await db.PaymentAttempts.AsNoTracking().Where(a => a.CreatedAt >= fromUtc && a.CreatedAt < toUtc)
                .Select(a => new { Cash = a.Provider == PaymentProviders.Cash, a.Status, a.Amount }).ToListAsync(ct))
            .GroupBy(a => $"{(a.Cash ? PaymentMethods.Cash : PaymentMethods.Online)}:{a.Status}").OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new OverviewBucket(g.Key, g.Count(), g.Sum(x => x.Amount))).ToList();
        var paid = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.Status == PaymentStatuses.Paid && a.AmountPaid != null)
            .Where(a => (a.ProviderTransactionAt ?? a.PaidAt) >= fromUtc && (a.ProviderTransactionAt ?? a.PaidAt) < toUtc)
            .Select(a => new { a.Id, Cash = a.Provider == PaymentProviders.Cash, Paid = a.AmountPaid!.Value }).ToListAsync(ct);
        var collected = paid.GroupBy(a => a.Cash ? PaymentMethods.Cash : PaymentMethods.Online).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new OverviewBucket(g.Key, g.Count(), g.Sum(x => (decimal)x.Paid))).ToList();
        var paidIds = paid.Select(p => p.Id).ToList();
        var lines = await db.PaymentAttemptItems.AsNoTracking().Where(l => paidIds.Contains(l.PaymentAttemptId) && l.Status == PaymentItemStatuses.Settled)
            .Select(l => new { l.PaymentAttemptId, l.ItemType, l.Amount }).ToListAsync(ct);
        var linesByAttempt = lines.ToLookup(l => l.PaymentAttemptId);
        bool IsPackage(string type) => type is PaymentItemTypes.MaintenancePackage or PaymentItemTypes.DisasterPackage;
        var combined = paidIds.Where(id => linesByAttempt[id].Any(l => l.ItemType == PaymentItemTypes.Contract) && linesByAttempt[id].Any(l => IsPackage(l.ItemType))).ToList();
        var combinedCheckouts = new OverviewBucket("COMBINED_INSTALLATION_AND_PACKAGES", combined.Count, combined.Sum(id => linesByAttempt[id].Sum(l => l.Amount)));
        var packageLines = lines.Where(l => IsPackage(l.ItemType))
            .Select(l => (Channel: linesByAttempt[l.PaymentAttemptId].Any(x => x.ItemType == PaymentItemTypes.Contract) ? "WITH_INSTALLATION" : "SEPARATE", l.Amount)).ToList();
        var packageChannels = packageLines.GroupBy(l => l.Channel).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new OverviewBucket(g.Key, g.Count(), g.Sum(x => x.Amount))).ToList();
        var installationOffers = await db.Quotations.AsNoTracking().Where(q => q.IssuedAt >= fromUtc && q.IssuedAt < toUtc).Select(q => q.TotalAmount).ToListAsync(ct);
        var serviceOffers = await db.ServiceQuotations.AsNoTracking().Where(q => q.IssuedAt >= fromUtc && q.IssuedAt < toUtc).Select(q => q.CustomerAmount).ToListAsync(ct);

        return new AdminOverviewView(from, to, nowUtc, accounts, surveyRequests, outstandingAppointments, quotations, contracts, withPdf, withScan, staffScans,
            projects, schedules, openOffers, serviceRequests, serviceQuotations, byIssuer, claims, subscriptions, openTasks, openVisits,
            new OverviewBucket("CASH_PENDING", pendingCash.Count, pendingCash.Sum()), refunds, created, collected, combinedCheckouts, packageChannels,
            new OverviewBucket("INSTALLATION_QUOTATIONS_ISSUED", installationOffers.Count, installationOffers.Sum()),
            new OverviewBucket("SERVICE_QUOTATIONS_ISSUED", serviceOffers.Count, serviceOffers.Sum()));
    }

    private static async Task<IReadOnlyList<OverviewBucket>> CountByAsync(IQueryable<string> keys, CancellationToken ct)
        => Buckets((await keys.GroupBy(k => k).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct)).Select(x => (x.Key, x.Count)));

    private static IReadOnlyList<OverviewBucket> Buckets(IEnumerable<(string Key, int Count)> rows)
        => rows.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => new OverviewBucket(r.Key, r.Count)).ToList();
}
