using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Infrastructure.Persistence.InstallationWorkflow;

/// <summary>Persistence of the 30/70 payment plan (installments, paper signature, collection, contract read-all and access audit).</summary>
public sealed partial class ContractUnitOfWork
{
    public async Task LockContractAsync(Guid contractId, CancellationToken cancellationToken)
    {
        // SQLite serialises writers itself.
        if (Db.Database.IsNpgsql())
            await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM customer_contract WHERE id = {contractId} FOR UPDATE", cancellationToken);
    }

    public async Task<IReadOnlyList<ContractInstallment>> GetInstallmentsAsync(Guid contractId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.ContractInstallments : Db.ContractInstallments.AsNoTracking()).Where(i => i.ContractId == contractId).OrderBy(i => i.Sequence)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ContractInstallment>> GetInstallmentsForContractsAsync(IReadOnlyCollection<Guid> contractIds, CancellationToken cancellationToken)
        => contractIds.Count == 0 ? []
            : await Db.ContractInstallments.AsNoTracking().Where(i => contractIds.Contains(i.ContractId)).OrderBy(i => i.Sequence).ToListAsync(cancellationToken);

    public Task<ContractInstallment?> FindInstallmentAsync(Guid installmentId, bool track, CancellationToken cancellationToken)
        => (track ? Db.ContractInstallments : Db.ContractInstallments.AsNoTracking()).FirstOrDefaultAsync(i => i.Id == installmentId, cancellationToken);

    public Task<bool> HasPayableInstallmentAttemptAsync(Guid installmentId, CancellationToken cancellationToken)
        => Db.PaymentAttemptItems.AsNoTracking().Where(i => i.ContractInstallmentId == installmentId)
            .Join(Db.PaymentAttempts, i => i.PaymentAttemptId, a => a.Id, (i, a) => a.Status)
            .AnyAsync(s => s == PaymentStatuses.Pending || s == PaymentStatuses.Processing, cancellationToken);

    public void AddInstallment(ContractInstallment installment) => Db.ContractInstallments.Add(installment);

    public async Task<IReadOnlyList<ContractSignatureSet>> GetSignatureSetsAsync(Guid contractId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.ContractSignatureSets : Db.ContractSignatureSets.AsNoTracking()).Where(s => s.ContractId == contractId).OrderBy(s => s.Version)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ContractSignedPage>> GetSignedPagesAsync(Guid contractId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.ContractSignedPages : Db.ContractSignedPages.AsNoTracking()).Where(p => p.ContractId == contractId)
            .OrderBy(p => p.PageNumber).ThenBy(p => p.UploadedAt).ToListAsync(cancellationToken);

    public void AddSignatureSet(ContractSignatureSet set) => Db.ContractSignatureSets.Add(set);
    public void AddSignedPage(ContractSignedPage page) => Db.ContractSignedPages.Add(page);

    public async Task<Guid?> GetSurveyTechnicianAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => await Db.OnSiteSurveyReports.AsNoTracking()
            .Where(r => r.SurveyRequestId == surveyRequestId && r.Status == SmartSolar.Modules.OnSiteSurveys.OnSiteSurveyStatuses.Completed)
            .OrderByDescending(r => r.CompletedAt).Select(r => (Guid?)r.TechnicianUserId).FirstOrDefaultAsync(cancellationToken);

    public Task<ContractCollectionCase?> FindCollectionCaseAsync(Guid caseId, bool track, CancellationToken cancellationToken)
        => (track ? Db.ContractCollectionCases : Db.ContractCollectionCases.AsNoTracking()).FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken);

    public Task<ContractCollectionCase?> FindOpenCollectionCaseAsync(Guid installmentId, bool track, CancellationToken cancellationToken)
        => (track ? Db.ContractCollectionCases : Db.ContractCollectionCases.AsNoTracking())
            .FirstOrDefaultAsync(c => c.InstallmentId == installmentId && c.Status != CollectionCaseStatuses.Resolved, cancellationToken);

    public async Task<IReadOnlyList<ContractCollectionCase>> GetCollectionCasesAsync(Guid contractId, CancellationToken cancellationToken)
        => await Db.ContractCollectionCases.AsNoTracking().Where(c => c.ContractId == contractId).OrderByDescending(c => c.OpenedAt).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ContractCollectionActivity>> GetCollectionActivitiesAsync(Guid caseId, CancellationToken cancellationToken)
        => await Db.ContractCollectionActivities.AsNoTracking().Where(a => a.CaseId == caseId).OrderBy(a => a.OccurredAt).ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<ContractCollectionCase> Rows, int Total)> PageCollectionCasesAsync(CollectionCaseFilter filter, int skip, int take,
        CancellationToken cancellationToken)
    {
        var query = Db.ContractCollectionCases.AsNoTracking().AsQueryable();
        if (filter.Status is { } status) query = query.Where(c => c.Status == status);
        if (filter.OpenOnly) query = query.Where(c => c.Status != CollectionCaseStatuses.Resolved);
        if (filter.CustomerUserId is { } customer) query = query.Where(c => c.CustomerUserId == customer);
        if (filter.ContractId is { } contract) query = query.Where(c => c.ContractId == contract);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(c => c.DueAt).ThenBy(c => c.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (rows, total);
    }

    public async Task<IReadOnlyList<Guid>> ListOverdueInstallmentsWithoutCaseAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
        => await Db.ContractInstallments.AsNoTracking()
            .Where(i => i.Status == InstallmentStatuses.Due && i.DueAt != null && i.DueAt < now
                        && !Db.ContractCollectionCases.Any(c => c.InstallmentId == i.Id && c.Status != CollectionCaseStatuses.Resolved))
            .OrderBy(i => i.DueAt).Select(i => i.Id).Take(limit).ToListAsync(cancellationToken);

    public void AddCollectionCase(ContractCollectionCase collectionCase) => Db.ContractCollectionCases.Add(collectionCase);
    public void AddCollectionActivity(ContractCollectionActivity activity) => Db.ContractCollectionActivities.Add(activity);
    public void AddAccessAudit(ContractAccessAudit audit) => Db.ContractAccessAudits.Add(audit);

    public async Task<(IReadOnlyList<ContractListRow> Rows, int Total)> PageContractsAsync(ContractListFilter filter, int skip, int take,
        CancellationToken cancellationToken)
    {
        var contracts = Db.CustomerContracts.AsNoTracking().AsQueryable();
        if (filter.CustomerUserId is { } customer) contracts = contracts.Where(c => c.CustomerUserId == customer);
        if (filter.Status is { } status) contracts = contracts.Where(c => c.Status == status);
        if (filter.PaymentPlan is { } plan)
            contracts = plan == "LEGACY" ? contracts.Where(c => c.PaymentPlan == null) : contracts.Where(c => c.PaymentPlan == plan);
        if (filter.SurveyRequestId is { } request) contracts = contracts.Where(c => c.SurveyRequestId == request);
        if (filter.QuotationId is { } quotation) contracts = contracts.Where(c => c.QuotationId == quotation);
        if (filter.PropertySiteId is { } site)
            contracts = contracts.Where(c => Db.SurveyRequests.Any(s => s.Id == c.SurveyRequestId && s.PropertySiteId == site));
        if (filter.ProjectId is { } project)
            contracts = contracts.Where(c => Db.InstallationProjects.Any(p => p.Id == project && p.ContractId == c.Id));
        if (filter.Overdue == true && filter.Now is { } now)
            contracts = contracts.Where(c => Db.ContractInstallments.Any(i => i.ContractId == c.Id && i.Status == InstallmentStatuses.Due && i.DueAt != null && i.DueAt < now));
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            contracts = contracts.Where(c => c.ContractNumber.ToLower().Contains(term)
                || Db.UserAccounts.Any(u => u.Id == c.CustomerUserId && (u.FullName.ToLower().Contains(term) || u.Email.ToLower().Contains(term)
                    || (u.Phone != null && u.Phone.Contains(term)))));
        }
        var total = await contracts.CountAsync(cancellationToken);
        var rows = await Rows(contracts.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Skip(skip).Take(take)).ToListAsync(cancellationToken);
        return (rows, total);
    }

    public Task<ContractListRow?> GetContractRowAsync(Guid contractId, CancellationToken cancellationToken)
        => Rows(Db.CustomerContracts.AsNoTracking().Where(c => c.Id == contractId)).FirstOrDefaultAsync(cancellationToken);

    private IQueryable<ContractListRow> Rows(IQueryable<CustomerContract> contracts)
        => contracts.Select(c => new ContractListRow(c.Id, c.ContractNumber, c.Version, c.Status, c.PaymentPlan, c.CustomerUserId,
            Db.UserAccounts.Where(u => u.Id == c.CustomerUserId).Select(u => u.FullName).FirstOrDefault() ?? "",
            Db.UserAccounts.Where(u => u.Id == c.CustomerUserId).Select(u => u.Phone).FirstOrDefault(),
            c.SurveyRequestId,
            Db.SurveyRequests.Where(s => s.Id == c.SurveyRequestId).Select(s => (Guid?)s.PropertySiteId).FirstOrDefault(),
            Db.SurveyRequests.Where(s => s.Id == c.SurveyRequestId).Join(Db.PropertySites, s => s.PropertySiteId, p => p.Id, (s, p) => p.Name).FirstOrDefault(),
            c.QuotationId, c.TotalAmount,
            Db.InstallationProjects.Where(p => p.ContractId == c.Id).Select(p => (Guid?)p.Id).FirstOrDefault(),
            Db.InstallationProjects.Where(p => p.ContractId == c.Id).Select(p => p.Status).FirstOrDefault(),
            c.CreatedAt, c.PaidAt));
}
