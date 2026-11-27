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
            .OrderBy(r => r.SubmittedAt).ThenBy(r => r.ReviewedAt ?? DateTimeOffset.MaxValue).ThenBy(r => r.Id).ToList();   // a reviewed receipt precedes a newer open one
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

    public async Task<(IReadOnlyList<CashReceiptRow> Rows, int Total)> PageCashReceiptsAsync(string? status, int skip, int take, CancellationToken cancellationToken)
    {
        var query = Db.CashReceiptSubmissions.AsNoTracking().AsQueryable();
        if (status is not null) query = query.Where(r => r.Status == status);
        var total = await query.CountAsync(cancellationToken);
        var page = await query.OrderByDescending(r => r.SubmittedAt).ThenByDescending(r => r.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (await RowsAsync(page, cancellationToken), total);
    }

    public async Task<CashReceiptRow?> GetCashReceiptRowAsync(Guid receiptId, CancellationToken cancellationToken)
    {
        var receipt = await Db.CashReceiptSubmissions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == receiptId, cancellationToken);
        return receipt is null ? null : (await RowsAsync([receipt], cancellationToken)).Single();
    }

    /// <summary>Context of a page of receipts in four batch queries (contracts, installments, users, attempts): no per-row query.</summary>
    private async Task<IReadOnlyList<CashReceiptRow>> RowsAsync(IReadOnlyList<CashReceiptSubmission> receipts, CancellationToken cancellationToken)
    {
        if (receipts.Count == 0) return [];
        var contractIds = receipts.Select(r => r.ContractId).Distinct().ToList();
        var installmentIds = receipts.Select(r => r.ContractInstallmentId).Distinct().ToList();
        var attemptIds = receipts.Select(r => r.PaymentAttemptId).Distinct().ToList();
        var contracts = await Db.CustomerContracts.AsNoTracking().Where(c => contractIds.Contains(c.Id))
            .Select(c => new { c.Id, c.ContractNumber, c.CustomerUserId }).ToDictionaryAsync(c => c.Id, cancellationToken);
        var installments = await Db.ContractInstallments.AsNoTracking().Where(i => installmentIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Sequence, i.Kind }).ToDictionaryAsync(i => i.Id, cancellationToken);
        var attempts = await Db.PaymentAttempts.AsNoTracking().Where(a => attemptIds.Contains(a.Id)).Select(a => new { a.Id, a.Status })
            .ToDictionaryAsync(a => a.Id, a => a.Status, cancellationToken);
        var userIds = receipts.Select(r => r.CollectedByUserId).Concat(contracts.Values.Select(c => c.CustomerUserId)).Distinct().ToList();
        var names = await Db.UserAccounts.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
        return receipts.Select(r =>
        {
            var contract = contracts.GetValueOrDefault(r.ContractId);
            var installment = installments.GetValueOrDefault(r.ContractInstallmentId);
            return new CashReceiptRow(r, contract?.ContractNumber ?? "", installment?.Sequence ?? 0, installment?.Kind ?? "", contract?.CustomerUserId ?? Guid.Empty,
                contract is null ? "" : names.GetValueOrDefault(contract.CustomerUserId) ?? "", names.GetValueOrDefault(r.CollectedByUserId) ?? "",
                attempts.GetValueOrDefault(r.PaymentAttemptId) ?? "");
        }).ToList();
    }
}
