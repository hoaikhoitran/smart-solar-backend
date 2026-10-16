using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Catalog.Enums;
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
            .Select(p => new CatalogProductSnapshot(p.Id, p.Sku, p.Name, p.Brand, p.Model, p.Unit, p.UnitPrice, p.Currency, p.Status == ProductStatus.Active))
            .FirstOrDefaultAsync(cancellationToken);

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
        => (await Db.QuotationEvents.AsNoTracking().Where(e => e.QuotationId == quotationId).ToListAsync(cancellationToken)).OrderBy(e => e.CreatedAt).ToList();

    public async Task<bool> TryTransitionAsync(Guid quotationId, string from, string to, DateTimeOffset now, CancellationToken cancellationToken)
        => await Db.Quotations.Where(q => q.Id == quotationId && q.Status == from)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, to)
                .SetProperty(q => q.UpdatedAt, now)
                .SetProperty(q => q.Revision, q => q.Revision + 1)
                .SetProperty(q => q.AcceptedAt, q => to == QuotationStatuses.Accepted ? now : q.AcceptedAt)
                .SetProperty(q => q.SupersededAt, q => to == QuotationStatuses.Superseded ? now : q.SupersededAt), cancellationToken) == 1;

    public void AddQuotation(Quotation quotation) => Db.Quotations.Add(quotation);
    public void AddLine(QuotationLine line) => Db.QuotationLines.Add(line);
    public void RemoveLine(QuotationLine line) => Db.QuotationLines.Remove(line);
    public void AddEvent(QuotationEvent quotationEvent) => Db.QuotationEvents.Add(quotationEvent);
}
