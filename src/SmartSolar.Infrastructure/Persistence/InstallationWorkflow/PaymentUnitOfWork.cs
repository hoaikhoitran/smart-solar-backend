using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Infrastructure.Persistence.InstallationWorkflow;

public sealed class PaymentUnitOfWork(AppDbContext db) : TransactionalUnitOfWork(db), IPaymentUnitOfWork
{
    public async Task<PayableContract?> GetContractAsync(Guid contractId, CancellationToken cancellationToken)
        => await Db.CustomerContracts.AsNoTracking().Where(c => c.Id == contractId)
            .Join(Db.UserAccounts, c => c.CustomerUserId, u => u.Id, (c, u) => new PayableContract(c.Id, c.ContractNumber, c.CustomerUserId, c.Status, c.TotalAmount,
                c.Currency, u.FullName, u.Email, u.Phone, c.PaymentPlan))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<PaymentAttempt?> FindAttemptAsync(Guid attemptId, bool track, CancellationToken cancellationToken)
        => (track ? Db.PaymentAttempts : Db.PaymentAttempts.AsNoTracking()).FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);

    public Task<PaymentAttempt?> FindByOrderCodeAsync(long orderCode, bool track, CancellationToken cancellationToken)
        => (track ? Db.PaymentAttempts : Db.PaymentAttempts.AsNoTracking()).FirstOrDefaultAsync(a => a.OrderCode == orderCode, cancellationToken);

    public async Task<IReadOnlyList<PaymentAttempt>> ListAttemptsAsync(Guid contractId, CancellationToken cancellationToken)
        => await Db.PaymentAttempts.AsNoTracking().Where(a => a.ContractId == contractId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PaymentAttempt>> ListAttemptsForInstallationQuotationAsync(Guid quotationId, CancellationToken cancellationToken)
        => await Db.PaymentAttempts.AsNoTracking()
            .Where(a => Db.PaymentAttemptItems.Any(i => i.PaymentAttemptId == a.Id && i.InstallationQuotationId == quotationId))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PaymentAttempt>> ListAttemptsForInstallmentAsync(Guid installmentId, CancellationToken cancellationToken)
        => await Db.PaymentAttempts.AsNoTracking()
            .Where(a => Db.PaymentAttemptItems.Any(i => i.PaymentAttemptId == a.Id && i.ContractInstallmentId == installmentId))
            .ToListAsync(cancellationToken);

    public Task<int> CountAttemptsSinceAsync(Guid contractId, DateTimeOffset since, CancellationToken cancellationToken)
        => Db.PaymentAttempts.AsNoTracking().CountAsync(a => a.ContractId == contractId && a.CreatedAt >= since, cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListAttemptsToReconcileAsync(DateTimeOffset checkedBefore, int limit, CancellationToken cancellationToken)
        => await Db.PaymentAttempts.AsNoTracking()
            .Where(a => (a.Status == PaymentStatuses.Pending || a.Status == PaymentStatuses.Processing) && a.Provider != PaymentProviders.Cash
                        && (a.LastReconciledAt == null || a.LastReconciledAt < checkedBefore))
            .OrderBy(a => a.CreatedAt).Take(limit).Select(a => a.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PaymentEvent>> GetEventsAsync(Guid contractId, CancellationToken cancellationToken)
        => (await Db.PaymentEvents.AsNoTracking().Where(e => e.ContractId == contractId).ToListAsync(cancellationToken)).OrderBy(e => e.CreatedAt).ToList();

    public Task<bool> WebhookSeenAsync(string payloadSha256, CancellationToken cancellationToken)
        => Db.PaymentWebhookEvents.AsNoTracking().AnyAsync(e => e.PayloadSha256 == payloadSha256, cancellationToken);

    public async Task<bool> TryMarkContractPaidAsync(Guid contractId, DateTimeOffset now, CancellationToken cancellationToken, Guid? actorUserId = null, string? detail = null)
    {
        // A 30/70 contract becomes PAID only through its installments (never through a 100% contract payment).
        var updated = await Db.CustomerContracts.Where(c => c.Id == contractId && c.Status == ContractStatuses.Signed && c.PaymentPlan == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ContractStatuses.Paid).SetProperty(c => c.PaidAt, now)
                .SetProperty(c => c.UpdatedAt, now).SetProperty(c => c.Revision, c => c.Revision + 1), cancellationToken) == 1;
        if (updated)
        {
            Db.ContractEvents.Add(new ContractEvent { Id = Guid.NewGuid(), ContractId = contractId, EventType = "STATUS_CHANGED", FromStatus = ContractStatuses.Signed,
                ToStatus = ContractStatuses.Paid,
                ActorUserId = actorUserId ?? await Db.CustomerContracts.Where(c => c.Id == contractId).Select(c => c.CustomerUserId).FirstAsync(cancellationToken),
                Detail = detail ?? "full payment verified", CreatedAt = now });
        }
        return updated;
    }

    public Task<bool> IsContractPaidAsync(Guid contractId, CancellationToken cancellationToken)
        => Db.CustomerContracts.AsNoTracking().AnyAsync(c => c.Id == contractId && c.Status == ContractStatuses.Paid, cancellationToken);

    public async Task<IReadOnlyList<PaymentAttemptItem>> GetItemsAsync(Guid attemptId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.PaymentAttemptItems : Db.PaymentAttemptItems.AsNoTracking()).Where(i => i.PaymentAttemptId == attemptId).ToListAsync(cancellationToken);

    public void AddItem(PaymentAttemptItem item) => Db.PaymentAttemptItems.Add(item);

    public async Task<(IReadOnlyList<PaymentAttempt> Rows, int Total)> PageAttemptsAsync(PaymentAttemptFilter filter, int skip, int take, CancellationToken cancellationToken)
    {
        var query = Db.PaymentAttempts.AsNoTracking().AsQueryable();
        if (filter.Method == PaymentMethods.Cash) query = query.Where(a => a.Provider == PaymentProviders.Cash);
        else if (filter.Method == PaymentMethods.Online) query = query.Where(a => a.Provider != PaymentProviders.Cash);
        if (filter.Status is { } status) query = query.Where(a => a.Status == status);
        if (filter.CreatedFrom is { } from) query = query.Where(a => a.CreatedAt >= from);
        if (filter.CreatedTo is { } to) query = query.Where(a => a.CreatedAt < to);
        if (filter.CustomerUserId is { } customer) query = query.Where(a => a.CustomerUserId == customer);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (rows, total);
    }

    public async Task<IReadOnlyList<PaymentAttemptItem>> GetItemsForAttemptsAsync(IReadOnlyCollection<Guid> attemptIds, CancellationToken cancellationToken)
        => await Db.PaymentAttemptItems.AsNoTracking().Where(i => attemptIds.Contains(i.PaymentAttemptId)).ToListAsync(cancellationToken);

    public void AddAttempt(PaymentAttempt attempt) => Db.PaymentAttempts.Add(attempt);
    public void AddEvent(PaymentEvent paymentEvent) => Db.PaymentEvents.Add(paymentEvent);
    public void AddWebhookEvent(PaymentWebhookEvent webhookEvent) => Db.PaymentWebhookEvents.Add(webhookEvent);

    public void AddCashReceipt(CashReceiptSubmission receipt) => Db.CashReceiptSubmissions.Add(receipt);

    public Task<CashReceiptSubmission?> FindCashReceiptAsync(Guid receiptId, bool track, CancellationToken cancellationToken)
        => (track ? Db.CashReceiptSubmissions : Db.CashReceiptSubmissions.AsNoTracking()).FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken);

    public async Task<IReadOnlyList<CashReceiptSubmission>> ListCashReceiptsAsync(IReadOnlyCollection<Guid> attemptIds, CancellationToken cancellationToken)
    {
        if (attemptIds.Count == 0) return [];
        var ids = attemptIds.Distinct().ToList();
        return (await Db.CashReceiptSubmissions.AsNoTracking().Where(r => ids.Contains(r.PaymentAttemptId)).ToListAsync(cancellationToken))
            .OrderBy(r => r.SubmittedAt).ThenBy(r => LiveRank(r.Status)).ThenBy(r => r.ReviewedAt ?? DateTimeOffset.MaxValue).ThenBy(r => r.Id).ToList();
    }

    public Task<bool> HasOpenCashReceiptAsync(Guid attemptId, CancellationToken cancellationToken)
        => Db.CashReceiptSubmissions.AsNoTracking().AnyAsync(r => r.PaymentAttemptId == attemptId && r.Status == CashReceiptStatuses.Submitted, cancellationToken);

    public async Task<bool> IsCashReceiptNumberUsedAsync(string receiptNumber, Guid exceptAttemptId, CancellationToken cancellationToken)
        => await Db.PaymentAttempts.AsNoTracking().AnyAsync(a => a.Id != exceptAttemptId && a.Provider == PaymentProviders.Cash && a.ProviderReference == receiptNumber,
               cancellationToken)
           || await Db.CashReceiptSubmissions.AsNoTracking().AnyAsync(r => r.PaymentAttemptId != exceptAttemptId && r.ReceiptNumber == receiptNumber
               && (r.Status == CashReceiptStatuses.Submitted || r.Status == CashReceiptStatuses.Accepted), cancellationToken);

    public async Task<bool> IsCashEvidenceUsedAsync(Guid fileId, Guid exceptAttemptId, CancellationToken cancellationToken)
        => await Db.PaymentAttempts.AsNoTracking().AnyAsync(a => a.Id != exceptAttemptId && a.EvidenceFileId == fileId, cancellationToken)
           || await Db.CashReceiptSubmissions.AsNoTracking().AnyAsync(r => r.PaymentAttemptId != exceptAttemptId && r.EvidenceFileId == fileId
               && r.Status != CashReceiptStatuses.Rejected, cancellationToken);

    public async Task<(IReadOnlyList<CashReceiptRow> Rows, int Total)> PageCashReceiptsAsync(string? status, int skip, int take, CancellationToken cancellationToken,
        string? kind = null)
    {
        var query = Db.CashReceiptSubmissions.AsNoTracking().AsQueryable();
        if (status is not null) query = query.Where(r => r.Status == status);
        if (kind is not null) query = query.Where(r => r.Kind == kind);
        var total = await query.CountAsync(cancellationToken);
        var page = await query.OrderByDescending(r => r.SubmittedAt).ThenByDescending(r => r.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (await RowsAsync(page, cancellationToken), total);
    }

    public async Task<CashReceiptRow?> GetCashReceiptRowAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var receipt = await Db.CashReceiptSubmissions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken);
        return receipt is null ? null : (await RowsAsync([receipt], cancellationToken)).Single();
    }

    public async Task<IReadOnlyList<CashReceiptSubmission>> ListCashReceiptsForQuotationAsync(Guid quotationId, CancellationToken cancellationToken)
        => (await Db.CashReceiptSubmissions.AsNoTracking().Where(r => r.ServiceQuotationId == quotationId).ToListAsync(cancellationToken))
            .OrderBy(r => r.SubmittedAt).ThenBy(r => LiveRank(r.Status)).ThenBy(r => r.ReviewedAt ?? DateTimeOffset.MaxValue).ThenBy(r => r.Id).ToList();

    public async Task<bool> IsCashReportedCollectedAsync(Guid attemptId, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var receipts = await Db.CashReceiptSubmissions.AsNoTracking().Where(r => r.PaymentAttemptId == attemptId)
            .Select(r => new { r.Status, r.CollectedAt, r.SubmittedAt, r.ReviewedAt }).ToListAsync(cancellationToken);
        if (receipts.Any(r => r.Status == CashReceiptStatuses.Submitted)) return true;
        var latest = receipts.OrderBy(r => r.SubmittedAt).ThenBy(r => LiveRank(r.Status)).ThenBy(r => r.ReviewedAt ?? DateTimeOffset.MaxValue).LastOrDefault();
        return latest is not null && latest.Status == CashReceiptStatuses.Rejected && latest.CollectedAt <= deadline;
    }

    /// <summary>
    /// History order tie-break: a payment has at most one live receipt (SUBMITTED / ACCEPTED) and it is always the newest one, so closed receipts
    /// (REJECTED / WITHDRAWN) sort before it when timestamps are equal.
    /// </summary>
    private static int LiveRank(string status) => status is CashReceiptStatuses.Submitted or CashReceiptStatuses.Accepted ? 1 : 0;

    /// <summary>Context of a page of receipts in batch queries (contracts, installments, quotations, requests, users, attempts): no per-row query.</summary>
    private async Task<IReadOnlyList<CashReceiptRow>> RowsAsync(IReadOnlyList<CashReceiptSubmission> receipts, CancellationToken cancellationToken)
    {
        if (receipts.Count == 0) return [];
        var contractIds = receipts.Where(r => r.ContractId is not null).Select(r => r.ContractId!.Value).Distinct().ToList();
        var installmentIds = receipts.Where(r => r.ContractInstallmentId is not null).Select(r => r.ContractInstallmentId!.Value).Distinct().ToList();
        var quotationIds = receipts.Where(r => r.ServiceQuotationId is not null).Select(r => r.ServiceQuotationId!.Value).Distinct().ToList();
        var requestIds = receipts.Where(r => r.ServiceRequestId is not null).Select(r => r.ServiceRequestId!.Value).Distinct().ToList();
        var attemptIds = receipts.Select(r => r.PaymentAttemptId).Distinct().ToList();
        var contracts = await Db.CustomerContracts.AsNoTracking().Where(c => contractIds.Contains(c.Id))
            .Select(c => new { c.Id, c.ContractNumber, c.CustomerUserId }).ToDictionaryAsync(c => c.Id, cancellationToken);
        var installments = await Db.ContractInstallments.AsNoTracking().Where(i => installmentIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Sequence, i.Kind }).ToDictionaryAsync(i => i.Id, cancellationToken);
        var quotations = await Db.ServiceQuotations.AsNoTracking().Where(q => quotationIds.Contains(q.Id))
            .Select(q => new { q.Id, q.Version }).ToDictionaryAsync(q => q.Id, q => q.Version, cancellationToken);
        var requests = await Db.ServiceRequests.AsNoTracking().Where(r => requestIds.Contains(r.Id))
            .Select(r => new { r.Id, r.RequestNumber, r.Kind, r.CustomerUserId }).ToDictionaryAsync(r => r.Id, cancellationToken);
        var attempts = await Db.PaymentAttempts.AsNoTracking().Where(a => attemptIds.Contains(a.Id)).Select(a => new { a.Id, a.Status })
            .ToDictionaryAsync(a => a.Id, a => a.Status, cancellationToken);
        var userIds = receipts.Select(r => r.CollectedByUserId).Concat(contracts.Values.Select(c => c.CustomerUserId))
            .Concat(requests.Values.Select(r => r.CustomerUserId)).Distinct().ToList();
        var names = await Db.UserAccounts.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
        return receipts.Select(r =>
        {
            var contract = r.ContractId is { } c ? contracts.GetValueOrDefault(c) : null;
            var installment = r.ContractInstallmentId is { } i ? installments.GetValueOrDefault(i) : null;
            var request = r.ServiceRequestId is { } q ? requests.GetValueOrDefault(q) : null;
            var customer = contract?.CustomerUserId ?? request?.CustomerUserId ?? Guid.Empty;
            return new CashReceiptRow(r, contract?.ContractNumber ?? "", installment?.Sequence, installment?.Kind, customer,
                customer == Guid.Empty ? "" : names.GetValueOrDefault(customer) ?? "", names.GetValueOrDefault(r.CollectedByUserId) ?? "",
                attempts.GetValueOrDefault(r.PaymentAttemptId) ?? "", request?.RequestNumber, request?.Kind,
                r.ServiceQuotationId is { } sq && quotations.TryGetValue(sq, out var version) ? version : null);
        }).ToList();
    }
}
