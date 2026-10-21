using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Infrastructure.Persistence.InstallationWorkflow;

public sealed class PaymentUnitOfWork(AppDbContext db) : TransactionalUnitOfWork(db), IPaymentUnitOfWork
{
    public async Task<PayableContract?> GetContractAsync(Guid contractId, CancellationToken cancellationToken)
        => await Db.CustomerContracts.AsNoTracking().Where(c => c.Id == contractId)
            .Join(Db.UserAccounts, c => c.CustomerUserId, u => u.Id, (c, u) => new PayableContract(c.Id, c.ContractNumber, c.CustomerUserId, c.Status, c.TotalAmount,
                c.Currency, u.FullName, u.Email, u.Phone))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<PaymentAttempt?> FindAttemptAsync(Guid attemptId, bool track, CancellationToken cancellationToken)
        => (track ? Db.PaymentAttempts : Db.PaymentAttempts.AsNoTracking()).FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);

    public Task<PaymentAttempt?> FindByOrderCodeAsync(long orderCode, bool track, CancellationToken cancellationToken)
        => (track ? Db.PaymentAttempts : Db.PaymentAttempts.AsNoTracking()).FirstOrDefaultAsync(a => a.OrderCode == orderCode, cancellationToken);

    public async Task<IReadOnlyList<PaymentAttempt>> ListAttemptsAsync(Guid contractId, CancellationToken cancellationToken)
        => await Db.PaymentAttempts.AsNoTracking().Where(a => a.ContractId == contractId).ToListAsync(cancellationToken);

    public Task<int> CountAttemptsSinceAsync(Guid contractId, DateTimeOffset since, CancellationToken cancellationToken)
        => Db.PaymentAttempts.AsNoTracking().CountAsync(a => a.ContractId == contractId && a.CreatedAt >= since, cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListAttemptsToReconcileAsync(DateTimeOffset checkedBefore, int limit, CancellationToken cancellationToken)
        => await Db.PaymentAttempts.AsNoTracking()
            .Where(a => (a.Status == PaymentStatuses.Pending || a.Status == PaymentStatuses.Processing) && (a.LastReconciledAt == null || a.LastReconciledAt < checkedBefore))
            .OrderBy(a => a.CreatedAt).Take(limit).Select(a => a.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PaymentEvent>> GetEventsAsync(Guid contractId, CancellationToken cancellationToken)
        => (await Db.PaymentEvents.AsNoTracking().Where(e => e.ContractId == contractId).ToListAsync(cancellationToken)).OrderBy(e => e.CreatedAt).ToList();

    public Task<bool> WebhookSeenAsync(string payloadSha256, CancellationToken cancellationToken)
        => Db.PaymentWebhookEvents.AsNoTracking().AnyAsync(e => e.PayloadSha256 == payloadSha256, cancellationToken);

    public async Task<bool> TryMarkContractPaidAsync(Guid contractId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var updated = await Db.CustomerContracts.Where(c => c.Id == contractId && c.Status == ContractStatuses.Signed)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ContractStatuses.Paid).SetProperty(c => c.PaidAt, now)
                .SetProperty(c => c.UpdatedAt, now).SetProperty(c => c.Revision, c => c.Revision + 1), cancellationToken) == 1;
        if (updated)
        {
            Db.ContractEvents.Add(new ContractEvent { Id = Guid.NewGuid(), ContractId = contractId, EventType = "STATUS_CHANGED", FromStatus = ContractStatuses.Signed,
                ToStatus = ContractStatuses.Paid, ActorUserId = await Db.CustomerContracts.Where(c => c.Id == contractId).Select(c => c.CustomerUserId).FirstAsync(cancellationToken),
                Detail = "full payment verified", CreatedAt = now });
        }
        return updated;
    }

    public Task<bool> IsContractPaidAsync(Guid contractId, CancellationToken cancellationToken)
        => Db.CustomerContracts.AsNoTracking().AnyAsync(c => c.Id == contractId && c.Status == ContractStatuses.Paid, cancellationToken);

    public async Task<IReadOnlyList<PaymentAttemptItem>> GetItemsAsync(Guid attemptId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.PaymentAttemptItems : Db.PaymentAttemptItems.AsNoTracking()).Where(i => i.PaymentAttemptId == attemptId).ToListAsync(cancellationToken);

    public void AddItem(PaymentAttemptItem item) => Db.PaymentAttemptItems.Add(item);

    public void AddAttempt(PaymentAttempt attempt) => Db.PaymentAttempts.Add(attempt);
    public void AddEvent(PaymentEvent paymentEvent) => Db.PaymentEvents.Add(paymentEvent);
    public void AddWebhookEvent(PaymentWebhookEvent webhookEvent) => Db.PaymentWebhookEvents.Add(webhookEvent);
}
