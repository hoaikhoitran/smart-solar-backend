using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Catalog.Enums;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Quotations;

namespace SmartSolar.Infrastructure.Persistence.InstallationWorkflow;

public sealed class QuotationUnitOfWork(AppDbContext db) : TransactionalUnitOfWork(db), IQuotationUnitOfWork
{
    public async Task<QuotationSource?> GetSourceAsync(Guid surveyRequestId, CancellationToken cancellationToken)
    {
        var row = await Db.SurveyRequests.AsNoTracking()
            .Where(s => s.Id == surveyRequestId)
            .Select(s => new
            {
                s.Id, s.AssignedSaleId, s.Status, User = s.PreSurvey.Property.Customer.User, Property = s.PreSurvey.Property,
            })
            .Select(x => new
            {
                x.Id, x.AssignedSaleId, x.Status, UserId = x.User.Id, x.User.FullName, x.User.Phone, x.User.Email,
                PropertyName = x.Property.Name, x.Property.Province, x.Property.District, x.Property.StreetLine,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        var report = await Db.OnSiteSurveyReports.AsNoTracking().Where(r => r.SurveyRequestId == surveyRequestId)
            .Select(r => new { r.Id, r.Status, r.Version }).FirstOrDefaultAsync(cancellationToken);
        return new QuotationSource(row.Id, row.AssignedSaleId, row.Status.ToString(), row.UserId, row.FullName, row.Phone, row.Email,
            row.PropertyName, row.Province, row.District, row.StreetLine, report?.Id, report?.Status, report?.Version ?? 0);
    }

    public async Task<CatalogProductSnapshot?> GetProductAsync(Guid productId, CancellationToken cancellationToken)
        => await Db.Products.AsNoTracking().Where(p => p.Id == productId)
            .Select(p => new CatalogProductSnapshot(p.Id, p.Sku, p.Name, p.Brand, p.Model, p.Unit, p.UnitPrice, p.Currency,
                p.Status == ProductStatus.Active && p.DeletedAt == null, p.ProductType, p.RatedPowerW))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<QuotationSurveyBasis?> GetSurveyBasisAsync(Guid surveyRequestId, CancellationToken cancellationToken)
    {
        var report = await Db.OnSiteSurveyReports.AsNoTracking().Where(r => r.SurveyRequestId == surveyRequestId)
            .Select(r => new { r.Id, r.Status, r.Version, r.InstallationFeasibility, r.BillOfMaterialsJson, r.TechnicianUserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (report is null) return null;
        var simulationId = await Db.SurveyRequests.AsNoTracking().Where(s => s.Id == surveyRequestId)
            .Select(s => s.PreSurvey.SelectedSimulationId).FirstOrDefaultAsync(cancellationToken);
        return new QuotationSurveyBasis(report.Id, report.Status, report.Version, report.InstallationFeasibility, report.BillOfMaterialsJson,
            report.TechnicianUserId, simulationId);
    }

    public async Task<IReadOnlyList<Quotation>> ListAutomaticDraftsAsync(Guid? salesUserId, CancellationToken cancellationToken)
    {
        var query = Db.Quotations.AsNoTracking().Where(q => q.Origin == QuotationOrigins.Automatic && q.Status == QuotationStatuses.Draft);
        if (salesUserId is { } owner)
            query = query.Where(q => Db.SurveyRequests.Any(s => s.Id == q.SurveyRequestId && s.AssignedSaleId == owner));
        return (await query.ToListAsync(cancellationToken)).OrderBy(q => q.CreatedAt).ToList();
    }

    public async Task<IReadOnlyList<Quotation>> ListSurveyChangedAsync(Guid? salesUserId, CancellationToken cancellationToken)
    {
        // Terminal quotations (superseded, rejected, cancelled) are history, not work.
        var query = Db.Quotations.AsNoTracking().Where(q => q.Status != QuotationStatuses.Superseded && q.Status != QuotationStatuses.Rejected
            && q.Status != QuotationStatuses.Cancelled
            && Db.OnSiteSurveyReports.Any(r => r.SurveyRequestId == q.SurveyRequestId && r.Version > q.OnSiteSurveyVersion));
        if (salesUserId is { } owner)
            query = query.Where(q => Db.SurveyRequests.Any(s => s.Id == q.SurveyRequestId && s.AssignedSaleId == owner));
        return (await query.ToListAsync(cancellationToken)).OrderBy(q => q.CreatedAt).ToList();
    }

    public async Task<IReadOnlyList<Quotation>> ListDraftsWithPricingApprovalRequestsAsync(CancellationToken cancellationToken)
        => (await Db.Quotations.AsNoTracking()
                .Where(q => q.Status == QuotationStatuses.Draft
                            && Db.QuotationEvents.Any(e => e.QuotationId == q.Id && e.EventType == QuotationEventTypes.PricingApprovalRequested))
                .ToListAsync(cancellationToken))
            .OrderBy(q => q.CreatedAt).ToList();

    public async Task<int> GetSurveyVersionAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => await Db.OnSiteSurveyReports.AsNoTracking().Where(r => r.SurveyRequestId == surveyRequestId).Select(r => r.Version).FirstOrDefaultAsync(cancellationToken);

    public Task<bool> HasLiveContractAsync(Guid quotationId, CancellationToken cancellationToken)
        => Db.CustomerContracts.AsNoTracking().AnyAsync(c => c.QuotationId == quotationId && c.Status != SmartSolar.Modules.CustomerContracts.ContractStatuses.Superseded && c.Status != SmartSolar.Modules.CustomerContracts.ContractStatuses.Discarded, cancellationToken);

    public async Task<bool> TryAcceptAsync(Guid quotationId, DateTimeOffset now, CancellationToken cancellationToken)
        => await Db.Quotations
            .Where(q => q.Id == quotationId && q.Status == QuotationStatuses.Issued
                && Db.OnSiteSurveyReports.Any(r => r.SurveyRequestId == q.SurveyRequestId && r.Version == q.OnSiteSurveyVersion))
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, QuotationStatuses.Accepted)
                .SetProperty(q => q.UpdatedAt, now)
                .SetProperty(q => q.AcceptedAt, now)
                .SetProperty(q => q.Revision, q => q.Revision + 1), cancellationToken) == 1;

    /// <summary>
    /// Row lock (FOR UPDATE) on the survey request for the current transaction (PostgreSQL). Every path that creates or ends commercial
    /// records of a request (quotation create/revision/generation, contract creation, withdrawal, closure) takes it FIRST, so they run one
    /// after another per request: lock order survey_request → quotation → contract everywhere (no deadlock).
    /// </summary>
    public async Task LockSurveyRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken)
    {
        // SQLite serialises writers itself.
        if (Db.Database.IsNpgsql())
            await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM survey_request WHERE id = {surveyRequestId} FOR UPDATE", cancellationToken);
    }

    public async Task LockAsync(Guid quotationId, CancellationToken cancellationToken)
    {
        // SQLite serialises writers itself; the test database never needs the lock.
        if (Db.Database.IsNpgsql())
            await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM quotation WHERE id = {quotationId} FOR UPDATE", cancellationToken);
    }

    public Task<Quotation?> FindAsync(Guid quotationId, bool track, CancellationToken cancellationToken)
        => (track ? Db.Quotations : Db.Quotations.AsNoTracking()).FirstOrDefaultAsync(q => q.Id == quotationId, cancellationToken);

    public async Task<IReadOnlyList<Quotation>> ListBySurveyRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => await Db.Quotations.AsNoTracking().Where(q => q.SurveyRequestId == surveyRequestId).OrderBy(q => q.Version).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Quotation>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken)
        => (await Db.Quotations.AsNoTracking().Where(q => q.CustomerUserId == customerUserId && q.Status != QuotationStatuses.Draft).ToListAsync(cancellationToken))
            .OrderByDescending(q => q.IssuedAt).ThenByDescending(q => q.Version).ToList();

    public async Task<IReadOnlyList<QuotationLine>> GetLinesAsync(Guid quotationId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.QuotationLines : Db.QuotationLines.AsNoTracking()).Where(l => l.QuotationId == quotationId).OrderBy(l => l.LineNumber).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<QuotationEvent>> GetEventsAsync(Guid quotationId, CancellationToken cancellationToken)
        => (await Db.QuotationEvents.AsNoTracking().Where(e => e.QuotationId == quotationId).ToListAsync(cancellationToken))
            .OrderBy(e => e.CreatedAt).ThenBy(e => QuotationEventTypes.Rank(e.EventType)).ToList();

    public async Task<bool> TryTransitionAsync(Guid quotationId, string from, string to, DateTimeOffset now, CancellationToken cancellationToken)
        => await Db.Quotations.Where(q => q.Id == quotationId && q.Status == from)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, to)
                .SetProperty(q => q.UpdatedAt, now)
                .SetProperty(q => q.Revision, q => q.Revision + 1)
                .SetProperty(q => q.AcceptedAt, q => to == QuotationStatuses.Accepted ? now : q.AcceptedAt)
                .SetProperty(q => q.SupersededAt, q => to == QuotationStatuses.Superseded ? now : q.SupersededAt), cancellationToken) == 1;

    public async Task<SalesRequestState?> GetRequestStateAsync(Guid surveyRequestId, CancellationToken cancellationToken)
    {
        var row = await Db.SurveyRequests.AsNoTracking().Where(s => s.Id == surveyRequestId)
            .Select(s => new { s.Id, CustomerUserId = s.PreSurvey.Property.Customer.User.Id, s.AssignedSaleId, s.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        var feasibility = await Db.OnSiteSurveyReports.AsNoTracking().Where(r => r.SurveyRequestId == surveyRequestId)
            .Select(r => r.InstallationFeasibility).FirstOrDefaultAsync(cancellationToken);
        var contract = await Db.CustomerContracts.AsNoTracking()
            // A discarded draft no longer binds the request (M-7); superseded versions never did.
            .Where(c => c.SurveyRequestId == surveyRequestId && c.Status != SmartSolar.Modules.CustomerContracts.ContractStatuses.Superseded && c.Status != SmartSolar.Modules.CustomerContracts.ContractStatuses.Discarded)
            .Select(c => c.Status).FirstOrDefaultAsync(cancellationToken);
        var appointment = await Db.TechnicianAppointments.AsNoTracking()
            .AnyAsync(a => a.SurveyRequestId == surveyRequestId
                && a.Status != SmartSolar.Modules.Scheduling.Constants.AppointmentStatuses.Completed
                && a.Status != SmartSolar.Modules.Scheduling.Constants.AppointmentStatuses.Cancelled, cancellationToken);
        return new SalesRequestState(row.Id, row.CustomerUserId, row.AssignedSaleId, row.Status.ToString(), feasibility, contract is not null, appointment, contract);
    }

    public async Task<bool> TryCloseRequestAsync(Guid surveyRequestId, SurveyRequestStatus status, Guid actorUserId, string reasonCode, string? note,
        DateTimeOffset now, CancellationToken cancellationToken)
        => await Db.SurveyRequests
            .Where(s => s.Id == surveyRequestId && s.Status != SurveyRequestStatus.Cancelled && s.Status != SurveyRequestStatus.Closed)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, status)
                .SetProperty(s => s.ClosedAt, now)
                .SetProperty(s => s.ClosedByUserId, actorUserId)
                .SetProperty(s => s.ClosureReasonCode, reasonCode)
                .SetProperty(s => s.ClosureNote, note), cancellationToken) == 1;

    public async Task<bool> TryCloseQuotationAsync(Guid quotationId, string from, string to, Guid actorUserId, string? reason, DateTimeOffset now,
        CancellationToken cancellationToken)
        => await Db.Quotations.Where(q => q.Id == quotationId && q.Status == from)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, to)
                .SetProperty(q => q.ClosedAt, now)
                .SetProperty(q => q.ClosedByUserId, actorUserId)
                .SetProperty(q => q.CloseReason, reason)
                .SetProperty(q => q.UpdatedAt, now)
                .SetProperty(q => q.Revision, q => q.Revision + 1), cancellationToken) == 1;

    public Task<RequestClosure?> GetClosureAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => Db.SurveyRequests.AsNoTracking().Where(s => s.Id == surveyRequestId)
            .Select(s => new RequestClosure(s.ClosedAt, s.ClosedByUserId, s.ClosureReasonCode, s.ClosureNote)).FirstOrDefaultAsync(cancellationToken);

    public void AddRequestEvent(SmartSolar.Modules.PreSurvey.Entities.SurveyRequestEvent requestEvent) => Db.SurveyRequestEvents.Add(requestEvent);

    public void AddQuotation(Quotation quotation) => Db.Quotations.Add(quotation);
    public void AddLine(QuotationLine line) => Db.QuotationLines.Add(line);
    public void RemoveLine(QuotationLine line) => Db.QuotationLines.Remove(line);
    public void AddEvent(QuotationEvent quotationEvent) => Db.QuotationEvents.Add(quotationEvent);
}
