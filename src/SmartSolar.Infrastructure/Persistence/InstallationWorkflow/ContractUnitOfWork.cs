using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.CustomerContracts;

namespace SmartSolar.Infrastructure.Persistence.InstallationWorkflow;

public sealed class ContractUnitOfWork(AppDbContext db) : TransactionalUnitOfWork(db), IContractUnitOfWork
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AcceptedQuotationSource?> GetQuotationAsync(Guid quotationId, CancellationToken cancellationToken)
    {
        var q = await Db.Quotations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == quotationId, cancellationToken);
        if (q is null) return null;
        var sale = await Db.SurveyRequests.AsNoTracking().Where(s => s.Id == q.SurveyRequestId).Select(s => s.AssignedSaleId).FirstOrDefaultAsync(cancellationToken);
        var lines = await Db.QuotationLines.AsNoTracking().Where(l => l.QuotationId == quotationId).OrderBy(l => l.LineNumber).ToListAsync(cancellationToken);
        var pricing = JsonSerializer.Serialize(new
        {
            quoteNumber = q.QuoteNumber, quotationVersion = q.Version, currency = q.Currency,
            lines = lines.Select(l => new { l.LineNumber, l.Sku, l.Name, l.Brand, l.Model, l.Unit, l.Quantity, l.UnitPrice, l.LineTotal, l.Description }),
            q.EquipmentSubtotal, q.LaborCost, q.TransportCost, q.OtherServiceCost, q.Subtotal, q.DiscountAmount, q.TaxRatePercent, q.TaxAmount, q.TotalAmount,
        }, Json);
        return new AcceptedQuotationSource(q.Id, q.Version, q.QuoteNumber, q.Status, q.SurveyRequestId, q.CustomerUserId, sale, q.Currency, q.TotalAmount,
            pricing, q.CustomerSnapshot, q.Terms);
    }

    public Task<Guid?> GetAssignedSaleAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => Db.SurveyRequests.AsNoTracking().Where(s => s.Id == surveyRequestId).Select(s => s.AssignedSaleId).FirstOrDefaultAsync(cancellationToken);

    public Task<CustomerContract?> FindAsync(Guid contractId, bool track, CancellationToken cancellationToken)
        => (track ? Db.CustomerContracts : Db.CustomerContracts.AsNoTracking()).FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken);

    public async Task<IReadOnlyList<CustomerContract>> ListByQuotationAsync(Guid quotationId, CancellationToken cancellationToken)
        => await Db.CustomerContracts.AsNoTracking().Where(c => c.QuotationId == quotationId).OrderBy(c => c.Version).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CustomerContract>> ListBySurveyRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => await Db.CustomerContracts.AsNoTracking().Where(c => c.SurveyRequestId == surveyRequestId).OrderBy(c => c.Version).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CustomerContract>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken)
        => (await Db.CustomerContracts.AsNoTracking().Where(c => c.CustomerUserId == customerUserId).ToListAsync(cancellationToken))
            .OrderByDescending(c => c.CreatedAt).ToList();

    public async Task<IReadOnlyList<ContractEvent>> GetEventsAsync(Guid contractId, CancellationToken cancellationToken)
        => (await Db.ContractEvents.AsNoTracking().Where(e => e.ContractId == contractId).ToListAsync(cancellationToken)).OrderBy(e => e.CreatedAt).ToList();

    public async Task<IReadOnlyList<ContractSignatureSubmission>> GetSubmissionsAsync(Guid contractId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.ContractSignatureSubmissions : Db.ContractSignatureSubmissions.AsNoTracking()).Where(s => s.ContractId == contractId).ToListAsync(cancellationToken);

    public async Task<bool> TryTransitionAsync(Guid contractId, string from, string to, DateTimeOffset now, CancellationToken cancellationToken)
        => await Db.CustomerContracts.Where(c => c.Id == contractId && c.Status == from)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, to).SetProperty(c => c.UpdatedAt, now).SetProperty(c => c.Revision, c => c.Revision + 1), cancellationToken) == 1;

    public void AddContract(CustomerContract contract) => Db.CustomerContracts.Add(contract);
    public void AddSubmission(ContractSignatureSubmission submission) => Db.ContractSignatureSubmissions.Add(submission);
    public void AddEvent(ContractEvent contractEvent) => Db.ContractEvents.Add(contractEvent);
}
