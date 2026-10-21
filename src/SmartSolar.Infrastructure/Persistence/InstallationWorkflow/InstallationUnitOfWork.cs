using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Installations;

namespace SmartSolar.Infrastructure.Persistence.InstallationWorkflow;

/// <summary>Installation persistence. One project per contract (unique index), one current assignment per project (partial unique index;
/// after-sales visit assignments, which share the table, are excluded from every query here),
/// non-overlapping current assignments per technician (exclusion constraint, PostgreSQL manual SQL), revision token on the project.</summary>
public sealed class InstallationUnitOfWork(AppDbContext db) : TransactionalUnitOfWork(db), IInstallationUnitOfWork
{
    private async Task<List<PaidContractSource>> ContractsAsync(IQueryable<CustomerContract> contracts, CancellationToken cancellationToken)
        => (await contracts.AsNoTracking()
                .Join(Db.SurveyRequests, c => c.SurveyRequestId, s => s.Id, (c, s) => new { c, s.PreSurvey.PropertyId })
                .Join(Db.PropertySites, x => x.PropertyId, p => p.Id, (x, p) => new
                {
                    x.c.Id, x.c.ContractNumber, x.c.Status, x.c.SurveyRequestId, x.c.CustomerUserId, SiteId = p.Id, p.Latitude, p.Longitude, x.c.TotalAmount, x.c.PaidAt,
                })
                .ToListAsync(cancellationToken))
            .Select(x => new PaidContractSource(x.Id, x.ContractNumber, x.Status, x.SurveyRequestId, x.CustomerUserId, x.SiteId, x.Latitude, x.Longitude, x.TotalAmount, x.PaidAt))
            .ToList();

    public async Task<PaidContractSource?> GetContractAsync(Guid contractId, CancellationToken cancellationToken)
        => (await ContractsAsync(Db.CustomerContracts.Where(c => c.Id == contractId), cancellationToken)).FirstOrDefault();

    public async Task<IReadOnlyList<PaidContractSource>> ListPaidContractsWithoutProjectAsync(CancellationToken cancellationToken)
        => (await ContractsAsync(Db.CustomerContracts.Where(c => c.Status == ContractStatuses.Paid && !Db.InstallationProjects.Any(p => p.ContractId == c.Id)), cancellationToken))
            .OrderBy(c => c.PaidAt).ToList();

    public Task<InstallationProject?> FindProjectAsync(Guid projectId, bool track, CancellationToken cancellationToken)
        => (track ? Db.InstallationProjects : Db.InstallationProjects.AsNoTracking()).FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

    public async Task<IReadOnlyList<InstallationProject>> ListProjectsAsync(string? status, CancellationToken cancellationToken)
        => (await Db.InstallationProjects.AsNoTracking().Where(p => status == null || p.Status == status).ToListAsync(cancellationToken)).OrderByDescending(p => p.CreatedAt).ToList();

    public async Task<IReadOnlyList<InstallationProject>> ListForTechnicianAsync(Guid technicianUserId, CancellationToken cancellationToken)
        => (await Db.InstallationProjects.AsNoTracking()
                .Where(p => Db.InstallationAssignments.Any(a => a.ProjectId == p.Id && a.TechnicianUserId == technicianUserId && a.ServiceVisitId == null
                    && (a.Status == AssignmentStatuses.Pending || a.Status == AssignmentStatuses.Accepted || a.Status == AssignmentStatuses.Completed)))
                .ToListAsync(cancellationToken)).OrderBy(p => p.ScheduledStartsAt).ToList();

    public async Task<IReadOnlyList<InstallationProject>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken)
        => (await Db.InstallationProjects.AsNoTracking().Where(p => p.CustomerUserId == customerUserId).ToListAsync(cancellationToken)).OrderByDescending(p => p.CreatedAt).ToList();

    public async Task<InstallationSiteInfo?> GetSiteAsync(Guid propertySiteId, Guid customerUserId, CancellationToken cancellationToken)
    {
        var site = await Db.PropertySites.AsNoTracking().Where(p => p.Id == propertySiteId)
            .Select(p => new { p.Id, p.Name, p.Province, p.District, p.Ward, p.StreetLine, p.Latitude, p.Longitude })
            .FirstOrDefaultAsync(cancellationToken);
        if (site is null) return null;
        var customer = await Db.UserAccounts.AsNoTracking().Where(u => u.Id == customerUserId).Select(u => new { u.FullName, u.Phone }).FirstOrDefaultAsync(cancellationToken);
        return new InstallationSiteInfo(site.Id, site.Name, site.Province, site.District, site.Ward, site.StreetLine, site.Latitude, site.Longitude,
            customer?.FullName ?? "", customer?.Phone);
    }

    public async Task<IReadOnlyList<InstallationAssignment>> GetAssignmentsAsync(Guid projectId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.InstallationAssignments : Db.InstallationAssignments.AsNoTracking()).Where(a => a.ProjectId == projectId && a.ServiceVisitId == null)
            .ToListAsync(cancellationToken);

    public Task<InstallationAssignment?> GetCurrentAssignmentAsync(Guid projectId, bool track, CancellationToken cancellationToken)
        => (track ? Db.InstallationAssignments : Db.InstallationAssignments.AsNoTracking())
            .FirstOrDefaultAsync(a => a.ProjectId == projectId && a.ServiceVisitId == null
                && (a.Status == AssignmentStatuses.Pending || a.Status == AssignmentStatuses.Accepted), cancellationToken);

    public async Task<IReadOnlyList<InstallationEvidence>> GetEvidenceAsync(Guid projectId, CancellationToken cancellationToken)
        => await Db.InstallationEvidence.AsNoTracking().Where(e => e.ProjectId == projectId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InstallationAcceptance>> GetAcceptancesAsync(Guid projectId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.InstallationAcceptances : Db.InstallationAcceptances.AsNoTracking()).Where(a => a.ProjectId == projectId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InstalledAsset>> GetAssetsAsync(Guid projectId, CancellationToken cancellationToken)
        => await Db.InstalledAssets.AsNoTracking().Where(a => a.ProjectId == projectId).OrderBy(a => a.AssetType).ThenBy(a => a.SerialNumber).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InstallationChecklistItem>> GetChecklistAsync(Guid projectId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.InstallationChecklistItems : Db.InstallationChecklistItems.AsNoTracking()).Where(i => i.ProjectId == projectId).OrderBy(i => i.Code).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InstallationEvent>> GetEventsAsync(Guid projectId, CancellationToken cancellationToken)
        => (await Db.InstallationEvents.AsNoTracking().Where(e => e.ProjectId == projectId).ToListAsync(cancellationToken)).OrderBy(e => e.CreatedAt).ToList();

    public Task<bool> IsEvidenceFileUsedAsync(Guid fileId, CancellationToken cancellationToken)
        => Db.InstallationEvidence.AsNoTracking().AnyAsync(e => e.FileId == fileId, cancellationToken);

    public Task<bool> SerialExistsAsync(string manufacturer, string serialNumber, CancellationToken cancellationToken)
        => Db.InstalledAssets.AsNoTracking().AnyAsync(a => a.Manufacturer == manufacturer && a.SerialNumber == serialNumber, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.ToList();
        return await Db.UserAccounts.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
    }

    public Task<ZoneSummary?> GetZoneSummaryAsync(Guid zoneId, CancellationToken cancellationToken)
        => Db.OperationalZones.AsNoTracking().Where(z => z.Id == zoneId).Select(z => new ZoneSummary(z.Id, z.Code, z.Name)).FirstOrDefaultAsync(cancellationToken);

    public Task LockTechniciansAsync(IEnumerable<Guid> technicianUserIds, CancellationToken cancellationToken) => LockTechnicianProfilesAsync(technicianUserIds, cancellationToken);

    public void Add<TEntity>(TEntity entity) where TEntity : class => Db.Set<TEntity>().Add(entity);
}
