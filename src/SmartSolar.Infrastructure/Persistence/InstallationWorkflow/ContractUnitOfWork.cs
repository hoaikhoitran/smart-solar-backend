using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.CustomerContracts;

namespace SmartSolar.Infrastructure.Persistence.InstallationWorkflow;

public sealed partial class ContractUnitOfWork(AppDbContext db) : TransactionalUnitOfWork(db), IContractUnitOfWork
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AcceptedQuotationSource?> GetQuotationAsync(Guid quotationId, CancellationToken cancellationToken)
    {
        var q = await Db.Quotations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == quotationId, cancellationToken);
        if (q is null) return null;
        var sale = await Db.SurveyRequests.AsNoTracking().Where(s => s.Id == q.SurveyRequestId).Select(s => s.AssignedSaleId).FirstOrDefaultAsync(cancellationToken);
        var lines = await Db.QuotationLines.AsNoTracking().Where(l => l.QuotationId == quotationId).OrderBy(l => l.LineNumber).ToListAsync(cancellationToken);
        var packages = (await Db.QuotationServicePackages.AsNoTracking().Where(p => p.QuotationId == quotationId).ToListAsync(cancellationToken))
            .OrderBy(p => p.Kind, StringComparer.Ordinal).ToList();
        // Quotations without packages keep exactly the snapshot shape (and therefore the canonical hash input) they always had.
        var pricing = packages.Count == 0
            ? JsonSerializer.Serialize(new
            {
                quoteNumber = q.QuoteNumber, quotationVersion = q.Version, currency = q.Currency,
                lines = lines.Select(l => new { l.LineNumber, l.Sku, l.Name, l.Brand, l.Model, l.Unit, l.Quantity, l.UnitPrice, l.LineTotal, l.Description }),
                q.EquipmentSubtotal, q.LaborCost, q.TransportCost, q.OtherServiceCost, q.Subtotal, q.DiscountAmount, q.TaxRatePercent, q.TaxAmount, q.TotalAmount,
            }, Json)
            : JsonSerializer.Serialize(new
            {
                quoteNumber = q.QuoteNumber, quotationVersion = q.Version, currency = q.Currency,
                lines = lines.Select(l => new { l.LineNumber, l.Sku, l.Name, l.Brand, l.Model, l.Unit, l.Quantity, l.UnitPrice, l.LineTotal, l.Description }),
                q.EquipmentSubtotal, q.LaborCost, q.TransportCost, q.OtherServiceCost, q.Subtotal, q.DiscountAmount, q.TaxRatePercent, q.TaxAmount, q.TotalAmount,
                installationTotal = q.TotalAmount, panelQuantity = q.PanelQuantity,
                servicePackages = packages.Select(p => new
                {
                    p.Kind, p.PackageId, p.PackageName, p.PricingUnit, p.UnitPrice, p.Quantity, p.TotalAmount, p.DurationMonths, p.VisitMonths, p.CoveredDisasterTypes,
                    p.CoveragePercent, p.PerIncidentLimit, p.TermLimit, p.CatalogRevision, p.SnapshotAt,
                }),
                servicePackagesTotal = q.ServicePackagesTotal, grandTotal = q.GrandTotal,
                packageTaxTreatment = "CATALOG_FINAL_PRICE_NO_QUOTATION_TAX",
            }, Json);
        var surveyVersion = await Db.OnSiteSurveyReports.AsNoTracking().Where(r => r.SurveyRequestId == q.SurveyRequestId).Select(r => r.Version).FirstOrDefaultAsync(cancellationToken);
        return new AcceptedQuotationSource(q.Id, q.Version, q.QuoteNumber, q.Status, q.SurveyRequestId, q.CustomerUserId, sale, q.Currency,
            packages.Count == 0 ? q.TotalAmount : q.GrandTotal, pricing, q.CustomerSnapshot, q.Terms, q.OnSiteSurveyVersion, surveyVersion, q.ValidUntil,
            packages.Count == 0 ? null : q.TotalAmount, packages.Count == 0 ? null : packages);
    }

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

    public Task<bool> IsSurveyRequestClosedAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => Db.SurveyRequests.AsNoTracking().AnyAsync(s => s.Id == surveyRequestId
            && (s.Status == SmartSolar.Modules.PreSurvey.Enums.SurveyRequestStatus.Cancelled || s.Status == SmartSolar.Modules.PreSurvey.Enums.SurveyRequestStatus.Closed),
            cancellationToken);

    public Task<Guid?> GetAssignedSaleAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => Db.SurveyRequests.AsNoTracking().Where(s => s.Id == surveyRequestId).Select(s => s.AssignedSaleId).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ContractServicePackage>> GetServicePackagesAsync(Guid contractId, bool track, CancellationToken cancellationToken)
        => (await (track ? Db.ContractServicePackages : Db.ContractServicePackages.AsNoTracking()).Where(p => p.ContractId == contractId).ToListAsync(cancellationToken))
            .OrderBy(p => p.Kind, StringComparer.Ordinal).ToList();

    public void AddServicePackage(ContractServicePackage servicePackage) => Db.ContractServicePackages.Add(servicePackage);

    public Task<CustomerContract?> FindAsync(Guid contractId, bool track, CancellationToken cancellationToken)
        => (track ? Db.CustomerContracts : Db.CustomerContracts.AsNoTracking()).FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken);

    public async Task<ContractPrepayment?> GetPrepaymentAsync(Guid contractId, CancellationToken cancellationToken)
        => await Db.PaymentAttempts.AsNoTracking()
            .Where(a => a.ContractId == contractId && a.Status == SmartSolar.Modules.Payments.PaymentStatuses.Paid
                        && Db.PaymentAttemptItems.Any(i => i.PaymentAttemptId == a.Id && i.ItemType == SmartSolar.Modules.Payments.PaymentItemTypes.InstallationQuotation))
            .Select(a => new ContractPrepayment(a.Id, a.PaidAt!.Value, a.Amount, a.Provider == SmartSolar.Modules.Payments.PaymentProviders.Cash ? "CASH" : "ONLINE"))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<bool> HasSettledQuotationPaymentAsync(Guid quotationId, CancellationToken cancellationToken)
        => Db.PaymentAttemptItems.AsNoTracking().AnyAsync(i => i.InstallationQuotationId == quotationId
            && i.Status == SmartSolar.Modules.Payments.PaymentItemStatuses.Settled, cancellationToken);

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

    public async Task<(IReadOnlyList<int> Active, int? Default)> GetInstallationWarrantyOptionsAsync(CancellationToken cancellationToken)
    {
        var options = await Db.WarrantyOptions.AsNoTracking()
            .Where(o => o.Kind == SmartSolar.Modules.AfterSales.WarrantyOptionKinds.Installation && o.IsActive).ToListAsync(cancellationToken);
        return (options.Select(o => o.Months).Order().ToList(), options.FirstOrDefault(o => o.IsDefault)?.Months);
    }

    public void AddContract(CustomerContract contract) => Db.CustomerContracts.Add(contract);
    public void AddSubmission(ContractSignatureSubmission submission) => Db.ContractSignatureSubmissions.Add(submission);
    public void AddEvent(ContractEvent contractEvent) => Db.ContractEvents.Add(contractEvent);
}
