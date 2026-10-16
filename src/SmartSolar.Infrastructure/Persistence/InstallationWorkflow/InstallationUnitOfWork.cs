using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Installations;

namespace SmartSolar.Infrastructure.Persistence.InstallationWorkflow;

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
                .Where(p => Db.InstallationTeamMembers.Any(m => m.ProjectId == p.Id && m.TechnicianUserId == technicianUserId && m.RemovedAt == null))
                .ToListAsync(cancellationToken)).OrderBy(p => p.PlannedStartDate).ToList();

    public async Task<IReadOnlyList<InstallationProject>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken)
        => (await Db.InstallationProjects.AsNoTracking().Where(p => p.CustomerUserId == customerUserId).ToListAsync(cancellationToken)).OrderByDescending(p => p.CreatedAt).ToList();

    public async Task<(decimal? Latitude, decimal? Longitude)> GetSiteLocationAsync(Guid propertySiteId, CancellationToken cancellationToken)
    {
        var site = await Db.PropertySites.AsNoTracking().Where(p => p.Id == propertySiteId).Select(p => new { p.Latitude, p.Longitude }).FirstOrDefaultAsync(cancellationToken);
        return (site?.Latitude, site?.Longitude);
    }

    public async Task<IReadOnlyList<InstallationTeamMember>> GetTeamAsync(Guid projectId, bool activeOnly, bool track, CancellationToken cancellationToken)
        => await (track ? Db.InstallationTeamMembers : Db.InstallationTeamMembers.AsNoTracking())
            .Where(m => m.ProjectId == projectId && (!activeOnly || m.RemovedAt == null)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InstallationShift>> GetShiftsAsync(Guid projectId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.InstallationShifts : Db.InstallationShifts.AsNoTracking()).Where(s => s.ProjectId == projectId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InstallationShiftAssignment>> GetAssignmentsAsync(Guid projectId, bool activeOnly, bool track, CancellationToken cancellationToken)
        => await (track ? Db.InstallationShiftAssignments : Db.InstallationShiftAssignments.AsNoTracking())
            .Where(a => a.ProjectId == projectId && (!activeOnly || a.Status == "ACTIVE")).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InstallationMilestone>> GetMilestonesAsync(Guid projectId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.InstallationMilestones : Db.InstallationMilestones.AsNoTracking()).Where(m => m.ProjectId == projectId).OrderBy(m => m.SortOrder).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MilestoneEvidence>> GetEvidenceAsync(Guid projectId, CancellationToken cancellationToken)
        => await Db.MilestoneEvidence.AsNoTracking().Where(e => e.ProjectId == projectId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InstallationAcceptance>> GetAcceptancesAsync(Guid projectId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.InstallationAcceptances : Db.InstallationAcceptances.AsNoTracking()).Where(a => a.ProjectId == projectId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InstalledAsset>> GetAssetsAsync(Guid projectId, CancellationToken cancellationToken)
        => await Db.InstalledAssets.AsNoTracking().Where(a => a.ProjectId == projectId).OrderBy(a => a.AssetType).ThenBy(a => a.SerialNumber).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InstallationChecklistItem>> GetChecklistAsync(Guid projectId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.InstallationChecklistItems : Db.InstallationChecklistItems.AsNoTracking()).Where(i => i.ProjectId == projectId).OrderBy(i => i.Code).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InstallationEvent>> GetEventsAsync(Guid projectId, CancellationToken cancellationToken)
        => (await Db.InstallationEvents.AsNoTracking().Where(e => e.ProjectId == projectId).ToListAsync(cancellationToken)).OrderBy(e => e.CreatedAt).ToList();

    public Task<bool> IsEvidenceFileUsedAsync(Guid fileId, CancellationToken cancellationToken)
        => Db.MilestoneEvidence.AsNoTracking().AnyAsync(e => e.FileId == fileId, cancellationToken);

    public Task<bool> SerialExistsAsync(string manufacturer, string serialNumber, CancellationToken cancellationToken)
        => Db.InstalledAssets.AsNoTracking().AnyAsync(a => a.Manufacturer == manufacturer && a.SerialNumber == serialNumber, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.ToList();
        return await Db.UserAccounts.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
    }

    public Task LockTechniciansAsync(IEnumerable<Guid> technicianUserIds, CancellationToken cancellationToken) => LockTechnicianProfilesAsync(technicianUserIds, cancellationToken);

    public void Add<TEntity>(TEntity entity) where TEntity : class => Db.Set<TEntity>().Add(entity);
    public void Remove<TEntity>(TEntity entity) where TEntity : class => Db.Set<TEntity>().Remove(entity);
}
