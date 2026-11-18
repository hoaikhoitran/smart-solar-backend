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
                .Join(Db.SurveyRequests, c => c.SurveyRequestId, s => s.Id, (c, s) => new { c, PropertyId = s.PropertySiteId })
                .Join(Db.PropertySites, x => x.PropertyId, p => p.Id, (x, p) => new
                {
                    x.c.Id, x.c.ContractNumber, x.c.Status, x.c.SurveyRequestId, x.c.CustomerUserId, SiteId = p.Id, p.Latitude, p.Longitude, x.c.TotalAmount, x.c.PaidAt,
                })
                .ToListAsync(cancellationToken))
            .Select(x => new PaidContractSource(x.Id, x.ContractNumber, x.Status, x.SurveyRequestId, x.CustomerUserId, x.SiteId, x.Latitude, x.Longitude, x.TotalAmount, x.PaidAt))
            .ToList();

    private IQueryable<ScheduleContractInfo> ScheduleContracts(IQueryable<CustomerContract> contracts)
        => contracts.Join(Db.SurveyRequests, c => c.SurveyRequestId, s => s.Id, (c, s) => new { c, s.PropertySiteId })
            .Join(Db.PropertySites, x => x.PropertySiteId, p => p.Id, (x, p) => new ScheduleContractInfo(
                x.c.Id, x.c.ContractNumber, x.c.Status, x.c.SurveyRequestId, x.c.CustomerUserId, x.PropertySiteId,
                Db.OnSiteSurveyReports.Where(r => r.SurveyRequestId == x.c.SurveyRequestId && r.Status == SmartSolar.Modules.OnSiteSurveys.OnSiteSurveyStatuses.Completed)
                    .OrderByDescending(r => r.CompletedAt).Select(r => (Guid?)r.TechnicianUserId).FirstOrDefault(),
                p.Name,
                Db.UserAccounts.Where(u => u.Id == x.c.CustomerUserId).Select(u => u.FullName).FirstOrDefault() ?? "",
                Db.PaymentAttempts.Any(a => a.ContractId == x.c.Id && a.Status == SmartSolar.Modules.Payments.PaymentStatuses.Paid)));

    public Task<ScheduleContractInfo?> GetScheduleContractAsync(Guid contractId, CancellationToken cancellationToken)
        => ScheduleContracts(Db.CustomerContracts.AsNoTracking().Where(c => c.Id == contractId)).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ScheduleContractInfo>> ListScheduleContractsForTechnicianAsync(Guid technicianUserId, CancellationToken cancellationToken)
        => (await ScheduleContracts(Db.CustomerContracts.AsNoTracking().Where(c => (c.Status == ContractStatuses.Signed || c.Status == ContractStatuses.Paid
                        || (c.Status != ContractStatuses.Superseded && c.Status != ContractStatuses.Discarded
                            && Db.PaymentAttempts.Any(a => a.ContractId == c.Id && a.Status == SmartSolar.Modules.Payments.PaymentStatuses.Paid)))
                    && Db.OnSiteSurveyReports.Any(r => r.SurveyRequestId == c.SurveyRequestId && r.TechnicianUserId == technicianUserId
                        && r.Status == SmartSolar.Modules.OnSiteSurveys.OnSiteSurveyStatuses.Completed)))
                .ToListAsync(cancellationToken))
            // The responsible technician is the one of the LATEST completed report.
            .Where(c => c.SurveyTechnicianUserId == technicianUserId)
            .OrderBy(c => c.ContractNumber, StringComparer.Ordinal).ToList();

    public Task<InstallationProject?> FindProjectByContractAsync(Guid contractId, CancellationToken cancellationToken)
        => Db.InstallationProjects.AsNoTracking().FirstOrDefaultAsync(p => p.ContractId == contractId, cancellationToken);

    public Task<InstallationScheduleProposal?> GetLiveProposalAsync(Guid contractId, bool track, CancellationToken cancellationToken)
        => (track ? Db.InstallationScheduleProposals : Db.InstallationScheduleProposals.AsNoTracking())
            .FirstOrDefaultAsync(p => p.ContractId == contractId && (p.Status == InstallationScheduleStatuses.Planned
                || p.Status == InstallationScheduleStatuses.Confirmed || p.Status == InstallationScheduleStatuses.RescheduleRequired), cancellationToken);

    public Task<InstallationScheduleProposal?> FindProposalAsync(Guid proposalId, bool track, CancellationToken cancellationToken)
        => (track ? Db.InstallationScheduleProposals : Db.InstallationScheduleProposals.AsNoTracking()).FirstOrDefaultAsync(p => p.Id == proposalId, cancellationToken);

    public async Task<IReadOnlyList<InstallationScheduleProposal>> ListProposalsAsync(Guid? contractId, Guid? technicianUserId, Guid? customerUserId, string? status,
        CancellationToken cancellationToken)
        => (await Db.InstallationScheduleProposals.AsNoTracking()
                .Where(p => (contractId == null || p.ContractId == contractId) && (technicianUserId == null || p.TechnicianUserId == technicianUserId)
                            && (customerUserId == null || p.CustomerUserId == customerUserId) && (status == null || p.Status == status))
                .ToListAsync(cancellationToken))
            .OrderByDescending(p => p.CreatedAt).ToList();

    public async Task<IReadOnlyList<Guid>> ListContractsWithPlannedProposalAndPaidAsync(int limit, CancellationToken cancellationToken)
        => await Db.InstallationScheduleProposals.AsNoTracking()
            .Where(p => p.Status == InstallationScheduleStatuses.Planned
                        && Db.CustomerContracts.Any(c => c.Id == p.ContractId && c.Status == ContractStatuses.Paid))
            .OrderBy(p => p.CreatedAt).Select(p => p.ContractId).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListExpiredPlannedProposalIdsAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken)
        => await Db.InstallationScheduleProposals.AsNoTracking()
            .Where(p => p.Status == InstallationScheduleStatuses.Planned && p.StartsAt <= nowUtc
                        && !Db.CustomerContracts.Any(c => c.Id == p.ContractId && c.Status == ContractStatuses.Paid))
            .OrderBy(p => p.StartsAt).Select(p => p.Id).Take(limit).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<InstallationSlotOffer>> ListSlotOffersAsync(Guid contractId, string? status, bool track, CancellationToken cancellationToken)
        => await (track ? Db.InstallationSlotOffers : Db.InstallationSlotOffers.AsNoTracking())
            .Where(o => o.ContractId == contractId && (status == null || o.Status == status))
            .OrderBy(o => o.StartsAt).ThenBy(o => o.Id).ToListAsync(cancellationToken);

    public Task<InstallationSlotOffer?> FindSlotOfferAsync(Guid offerId, bool track, CancellationToken cancellationToken)
        => (track ? Db.InstallationSlotOffers : Db.InstallationSlotOffers.AsNoTracking()).FirstOrDefaultAsync(o => o.Id == offerId, cancellationToken);

    public async Task<IReadOnlyList<InstallationScheduleProposal>> ListOverlappingLiveProposalsAsync(Guid technicianUserId, Guid exceptContractId,
        DateTimeOffset startsAt, DateTimeOffset endsAt, CancellationToken cancellationToken)
        => (await Db.InstallationScheduleProposals.AsNoTracking()
                .Where(p => p.TechnicianUserId == technicianUserId && p.ContractId != exceptContractId
                            && (p.Status == InstallationScheduleStatuses.Planned || p.Status == InstallationScheduleStatuses.Confirmed
                                || p.Status == InstallationScheduleStatuses.RescheduleRequired))
                .ToListAsync(cancellationToken))
            // Interval overlap evaluated in memory: SQLite (tests) cannot compare DateTimeOffset columns; the candidate set is one technician's live plans.
            .Where(p => p.StartsAt < endsAt && startsAt < p.EndsAt).ToList();

    public async Task LockContractAsync(Guid contractId, CancellationToken cancellationToken)
    {
        if (!Db.Database.IsNpgsql()) return;
        await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM customer_contract WHERE id = {contractId} FOR UPDATE", cancellationToken);
    }

    public async Task<PaidContractSource?> GetContractAsync(Guid contractId, CancellationToken cancellationToken)
        => (await ContractsAsync(Db.CustomerContracts.Where(c => c.Id == contractId), cancellationToken)).FirstOrDefault();

    public async Task<IReadOnlyList<PaidContractSource>> ListPaidContractsWithoutProjectAsync(CancellationToken cancellationToken)
        => (await ContractsAsync(Db.CustomerContracts.Where(c => c.Status == ContractStatuses.Paid && !Db.InstallationProjects.Any(p => p.ContractId == c.Id)), cancellationToken))
            .OrderBy(c => c.PaidAt).ToList();

    public Task<InstallationProject?> FindProjectAsync(Guid projectId, bool track, CancellationToken cancellationToken)
        => (track ? Db.InstallationProjects : Db.InstallationProjects.AsNoTracking()).FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);

    public async Task<IReadOnlyList<InstallationProject>> ListProjectsAsync(string? status, CancellationToken cancellationToken)
        => (await Db.InstallationProjects.AsNoTracking().Where(p => status == null || p.Status == status).ToListAsync(cancellationToken)).OrderByDescending(p => p.CreatedAt).ToList();

    public async Task<(IReadOnlyList<InstallationProject> Rows, int Total)> PageProjectsAsync(string? status, int skip, int take, CancellationToken cancellationToken)
    {
        var query = Db.InstallationProjects.AsNoTracking().Where(p => status == null || p.Status == status);
        var total = await query.CountAsync(cancellationToken);
        return (await query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).Skip(skip).Take(take).ToListAsync(cancellationToken), total);
    }

    public async Task<(IReadOnlyList<InstallationScheduleProposal> Rows, int Total)> PageProposalsAsync(string? status, int skip, int take,
        CancellationToken cancellationToken)
    {
        var query = Db.InstallationScheduleProposals.AsNoTracking().Where(p => status == null || p.Status == status);
        var total = await query.CountAsync(cancellationToken);
        return (await query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).Skip(skip).Take(take).ToListAsync(cancellationToken), total);
    }

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

    public async Task<SimulatedProductionView?> GetSelectedSimulationAsync(Guid surveyRequestId, CancellationToken cancellationToken)
    {
        var simulationId = await Db.SurveyRequests.AsNoTracking().Where(s => s.Id == surveyRequestId).Select(s => s.PreSurvey!.SelectedSimulationId).FirstOrDefaultAsync(cancellationToken);
        if (simulationId is null) return null;
        return await Db.SelectedSimulations.AsNoTracking().Where(s => s.SimulationId == simulationId)
            .Select(s => new SimulatedProductionView(s.SimulationId, s.SelectedAt, s.AlgorithmVersion, s.EnergyStatus, s.PanelCount, s.InstalledCapacityKwp,
                s.AnnualEnergyKwh, s.SpecificYieldKwhPerKwpYear))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<Guid> GetContractQuotationIdAsync(Guid contractId, CancellationToken cancellationToken)
        => Db.CustomerContracts.AsNoTracking().Where(c => c.Id == contractId).Select(c => c.QuotationId).FirstAsync(cancellationToken);

    public Task<EquipmentSubstitution?> GetActiveSubstitutionAsync(Guid projectId, bool track, CancellationToken cancellationToken)
        => (track ? Db.EquipmentSubstitutions : Db.EquipmentSubstitutions.AsNoTracking())
            .FirstOrDefaultAsync(s => s.ProjectId == projectId
                && (s.Status == EquipmentSubstitutionStatuses.CustomerApprovalPending || s.Status == EquipmentSubstitutionStatuses.Approved
                    || s.Status == EquipmentSubstitutionStatuses.InternallyApproved), cancellationToken);

    public async Task<IReadOnlyList<EquipmentSubstitution>> ListSubstitutionsAsync(Guid projectId, CancellationToken cancellationToken)
        => (await Db.EquipmentSubstitutions.AsNoTracking().Where(s => s.ProjectId == projectId).ToListAsync(cancellationToken)).OrderBy(s => s.CreatedAt).ToList();

    public async Task<IReadOnlyList<EquipmentSubstitutionItem>> GetSubstitutionItemsAsync(IReadOnlyCollection<Guid> substitutionIds, CancellationToken cancellationToken)
        => await Db.EquipmentSubstitutionItems.AsNoTracking().Where(i => substitutionIds.Contains(i.SubstitutionId)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ContractedEquipment>> GetContractedEquipmentAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var quotationId = await Db.CustomerContracts.AsNoTracking().Where(c => c.Id == contractId).Select(c => (Guid?)c.QuotationId).FirstOrDefaultAsync(cancellationToken);
        if (quotationId is null) return [];
        var lines = await Db.QuotationLines.AsNoTracking().Where(l => l.QuotationId == quotationId && l.ProductId != null)
            .Join(Db.Products.AsNoTracking(), l => l.ProductId, p => p.Id, (l, p) => new { ProductId = p.Id, p.ProductType, l.Name, l.Quantity })
            .ToListAsync(cancellationToken);
        return lines.Where(l => SmartSolar.Modules.Catalog.Constants.ProductTypes.Normalize(l.ProductType)
                is SmartSolar.Modules.Catalog.Constants.ProductTypes.SolarPanel or SmartSolar.Modules.Catalog.Constants.ProductTypes.Inverter)
            .GroupBy(l => l.ProductId)
            .Select(g => new ContractedEquipment(g.Key, SmartSolar.Modules.Catalog.Constants.ProductTypes.Normalize(g.First().ProductType), g.First().Name, g.Sum(l => l.Quantity)))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, AssetCatalogProduct>> GetAssetProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken)
        => await Db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id) && p.DeletedAt == null)
            .Select(p => new AssetCatalogProduct(p.Id, p.ProductType, p.WarrantyMonth))
            .ToDictionaryAsync(p => p.ProductId, cancellationToken);

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
