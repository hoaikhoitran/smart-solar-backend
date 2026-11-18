using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Modules.CustomerContracts;

/// <summary>
/// The 30/70 payment plan's state changes (2026-11-19). Every method that takes no transaction of its own runs INSIDE the caller's transaction (quotation
/// decision, payment confirmation, customer acceptance) on the same database context, so the business step and its financial effect commit together.
/// <list type="bullet">
/// <item>At the on-site agreement: exactly one contract (DRAFT, frozen content, canonical hash, payment plan, generated payment / ownership clauses) and
/// its two installments (deposit DUE, balance PENDING). Retried or concurrent agreements return the same contract (one-draft unique index).</item>
/// <item>A verified payment line settles exactly its installment (exact amount; unique settled index): deposit → contract AWAITING_SIGNATURE (paper may
/// be signed); balance → contract PAID (fully paid) and any open collection case resolved. Money that cannot be applied is never ignored: the caller
/// flags the line REVIEW_REQUIRED and opens an unapplied-money (refund) record.</item>
/// <item>The customer's acceptance opens the balance: DUE with deadline acceptance + configured hours (≤ 72 h).</item>
/// <item>The overdue sweep opens one collection case per overdue installment (people follow up; nothing automatic happens to money or assets).</item>
/// </list>
/// </summary>
public sealed class PaymentPlanLedger(IContractUnitOfWork unitOfWork, PaymentPlanOptions options, TimeProvider time, ILogger<PaymentPlanLedger>? logger = null)
    : Quotations.IAgreedQuotationContracts, Installations.IInstallationAcceptanceHook
{
    // ================================================================== Agreement (inside the quotation decision transaction)

    public async Task<Quotations.AgreementContractResult> CreateOnAgreementAsync(Guid quotationId, Guid technicianUserId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await unitOfWork.ListByQuotationAsync(quotationId, cancellationToken);
        if (existing.FirstOrDefault(c => c.PaymentPlan is not null && c.Status != ContractStatuses.Discarded) is { } same)
            return new(same.Id, null, null);   // retried agreement: the same contract
        if (existing.Any(c => c.Status != ContractStatuses.Discarded))
            return new(null, ContractErrorCodes.AlreadyExists, "A contract of the previous flow already exists for this quotation.");
        var source = await unitOfWork.GetQuotationAsync(quotationId, cancellationToken);
        if (source is null) return new(null, ContractErrorCodes.NotFound, "Quotation was not found.");
        if (source.Currency != "VND" || PaymentPlanCalculator.Split(source.TotalAmount) is not { } split)
            return new(null, PaymentPlanErrorCodes.AmountNotSupported, "The quotation total must be a whole VND amount of at least 2 VND to be split 30/70.");
        var (_, defaultMonths) = await unitOfWork.GetInstallationWarrantyOptionsAsync(cancellationToken);
        if (defaultMonths is null)
            return new(null, ContractErrorCodes.WarrantyRequired,
                "No default installation warranty is configured; an Admin must set one before a contract can be created at the agreement.");

        var contract = ContractHandlers.BuildFromQuotation(source, terms: null, defaultMonths, now);
        contract.PaymentPlan = PaymentPlans.Deposit30Balance70;
        contract.TermsVersion = Truncate(options.TermsVersion, 60);
        var clauses = PaymentPlanCalculator.Clauses(split, options);
        contract.Terms = Truncate(string.IsNullOrWhiteSpace(source.Terms) ? clauses : $"{source.Terms!.Trim()}\n\n{clauses}", 8000);
        // Frozen content: the hash the customer previews is the content of the paper they sign (issuedAt is not part of it yet).
        contract.CanonicalHash = ContractHandlers.CanonicalHash(contract);
        unitOfWork.AddContract(contract);
        foreach (var installment in PaymentPlanCalculator.CreateInstallments(contract.Id, split, now)) unitOfWork.AddInstallment(installment);
        AddEvent(contract.Id, "CREATED", null, ContractStatuses.Draft, technicianUserId,
            $"at the on-site agreement of quotation {source.QuoteNumber} v{source.QuotationVersion}; plan {contract.PaymentPlan}: deposit {split.Deposit:0} + balance {split.Balance:0} VND",
            now);
        unitOfWork.AddNotification(source.CustomerUserId, CustomerNotificationTypes.ContractReadyForPreview,
            $"Hợp đồng {contract.ContractNumber} đã được lập theo báo giá {source.QuoteNumber}. Bạn có thể xem toàn bộ nội dung trước khi thanh toán đợt 1 ({split.Deposit:N0} VND).",
            now);
        return new(contract.Id, null, null);
    }

    public async Task<Guid?> FindAgreementContractAsync(Guid quotationId, CancellationToken cancellationToken)
        => (await unitOfWork.ListByQuotationAsync(quotationId, cancellationToken))
            .FirstOrDefault(c => c.PaymentPlan is not null && c.Status != ContractStatuses.Discarded)?.Id;

    /// <summary>
    /// The customer withdrew the agreement before paying the deposit: the DRAFT contract is DISCARDED (kept as history) and its installments CANCELLED.
    /// Refused (message) while a deposit payment is open or after it was verified.
    /// </summary>
    public async Task<string?> ReleaseOnWithdrawalAsync(Guid quotationId, Guid technicianUserId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var contractId = await FindAgreementContractAsync(quotationId, cancellationToken);
        if (contractId is null) return null;
        await unitOfWork.LockContractAsync(contractId.Value, cancellationToken);
        var contract = (await unitOfWork.FindAsync(contractId.Value, true, cancellationToken))!;
        if (contract.Status != ContractStatuses.Draft) return "The deposit was already verified; the agreement can no longer be withdrawn on site.";
        var installments = await unitOfWork.GetInstallmentsAsync(contract.Id, true, cancellationToken);
        foreach (var i in installments)
        {
            if (i.Status == InstallmentStatuses.Paid) return "The deposit was already verified; the agreement can no longer be withdrawn on site.";
            if (await unitOfWork.HasPayableInstallmentAttemptAsync(i.Id, cancellationToken))
                return "A deposit payment is open; cancel it first (a payment the provider already completed cannot be cancelled).";
        }
        foreach (var i in installments)
        {
            i.Status = InstallmentStatuses.Cancelled;
            i.UpdatedAt = now;
            i.Revision++;
        }
        contract.DiscardedAt = now;
        contract.DiscardedByUserId = technicianUserId;
        contract.DiscardReason = Truncate($"Customer withdrew the on-site agreement before the deposit: {reason}", 1000);
        AddEvent(contract.Id, "STATUS_CHANGED", contract.Status, ContractStatuses.Discarded, technicianUserId, Truncate(contract.DiscardReason, 450), now);
        contract.Status = ContractStatuses.Discarded;
        contract.UpdatedAt = now;
        contract.Revision++;
        return null;
    }

    // ================================================================== Settlement (inside the payment confirmation transaction)

    /// <summary>Applies one verified CONTRACT_INSTALLMENT line. Null = settled; otherwise why it cannot be applied (nothing written).</summary>
    public async Task<string?> SettleAsync(PaymentAttempt attempt, PaymentAttemptItem item, DateTimeOffset providerPaidAt, DateTimeOffset now,
        CancellationToken cancellationToken, bool checkOnly = false)
    {
        if (item.ContractInstallmentId is not { } installmentId) return "The payment line names no installment.";
        var probe = await unitOfWork.FindInstallmentAsync(installmentId, false, cancellationToken);
        if (probe is null) return "The installment was not found.";
        await unitOfWork.LockContractAsync(probe.ContractId, cancellationToken);
        var contract = await unitOfWork.FindAsync(probe.ContractId, !checkOnly, cancellationToken);
        var installment = await unitOfWork.FindInstallmentAsync(installmentId, !checkOnly, cancellationToken);
        if (contract is null || installment is null) return "The contract of the installment was not found.";
        if (contract.PaymentPlan is null) return "The contract has no payment plan.";
        if (contract.CustomerUserId != attempt.CustomerUserId) return "The payment was made for another customer.";
        if (installment.Status == InstallmentStatuses.Paid)
            return $"Installment {installment.Sequence} of contract {contract.ContractNumber} was already paid (duplicate payment).";
        if (installment.Status != InstallmentStatuses.Due)
            return $"Installment {installment.Sequence} of contract {contract.ContractNumber} is {installment.Status}, not payable.";
        if (item.Amount != installment.Amount)
            return $"The paid amount {item.Amount:0} does not equal installment {installment.Sequence} ({installment.Amount:0} VND).";
        var expectedStatus = installment.Kind == InstallmentKinds.Deposit ? ContractStatuses.Draft : ContractStatuses.Signed;
        if (contract.Status != expectedStatus)
            return $"Contract {contract.ContractNumber} is {contract.Status}; the {installment.Kind.ToLowerInvariant()} cannot be applied to it.";
        if (checkOnly) return null;

        var paidAt = providerPaidAt <= now ? providerPaidAt : now;
        var actor = attempt.ConfirmedByUserId ?? contract.CustomerUserId;
        var method = attempt.Provider == PaymentProviders.Cash ? $"cash confirmed by staff (receipt {attempt.ProviderReference})" : $"online payment {attempt.OrderCode} verified";
        installment.Status = InstallmentStatuses.Paid;
        installment.PaidAt = paidAt;
        installment.PaidAmount = item.Amount;
        installment.PaidLate = PaymentPlanCalculator.IsLate(installment.DueAt, paidAt);
        installment.SettledPaymentAttemptId = attempt.Id;
        installment.CurrentPaymentAttemptId = null;
        installment.UpdatedAt = now;
        installment.Revision++;

        if (installment.Kind == InstallmentKinds.Deposit)
        {
            contract.IssuedAt = now;
            contract.PublishedAt = now;
            AddEvent(contract.Id, "DEPOSIT_PAID", null, null, actor, $"installment 1: {item.Amount:0} VND, {method}", now);
            AddEvent(contract.Id, "STATUS_CHANGED", contract.Status, ContractStatuses.AwaitingSignature, actor, "deposit verified; the paper contract may be signed", now);
            contract.Status = ContractStatuses.AwaitingSignature;
            unitOfWork.AddNotification(contract.CustomerUserId, CustomerNotificationTypes.ContractDepositReceived,
                $"SmartSolar đã nhận khoản thanh toán đợt 1 ({item.Amount:N0} VND) của hợp đồng {contract.ContractNumber}. Hợp đồng giấy có thể được ký.", now);
        }
        else
        {
            AddEvent(contract.Id, "BALANCE_PAID", null, null, actor,
                $"installment {installment.Sequence}: {item.Amount:0} VND, {method}" + (installment.PaidLate ? $"; LATE (due {installment.DueAt:O})" : ""), now);
            var all = await unitOfWork.GetInstallmentsAsync(contract.Id, true, cancellationToken);
            if (all.All(i => i.Status == InstallmentStatuses.Paid))
            {
                contract.PaidAt = now;
                AddEvent(contract.Id, "STATUS_CHANGED", contract.Status, ContractStatuses.Paid, actor, "both installments paid (fully paid; ownership transfer eligible)", now);
                contract.Status = ContractStatuses.Paid;
                unitOfWork.AddNotification(contract.CustomerUserId, CustomerNotificationTypes.ContractFullyPaid,
                    $"Hợp đồng {contract.ContractNumber} đã được thanh toán đủ. Cảm ơn bạn đã sử dụng dịch vụ SmartSolar.", now);
            }
            if (await unitOfWork.FindOpenCollectionCaseAsync(installment.Id, true, cancellationToken) is { } open)
            {
                unitOfWork.AddCollectionActivity(new ContractCollectionActivity
                {
                    Id = Guid.NewGuid(), CaseId = open.Id, Kind = CollectionActivityKinds.PaidResolved, FromStatus = open.Status, ToStatus = CollectionCaseStatuses.Resolved,
                    Summary = $"Installment paid ({method}).", OccurredAt = now,
                });
                open.Status = CollectionCaseStatuses.Resolved;
                open.ResolvedAt = now;
                open.Resolution = "PAID";
                open.UpdatedAt = now;
                open.Revision++;
            }
        }
        contract.UpdatedAt = now;
        contract.Revision++;
        return null;
    }

    // ================================================================== Acceptance (inside the customer's acceptance transaction)

    /// <summary>Opens the balance of a payment-plan contract: DUE, deadline = acceptance + configured hours (≤ 72 h). Legacy contracts: nothing.</summary>
    public async Task<string?> CustomerAcceptedAsync(Guid contractId, Guid customerUserId, DateTimeOffset acceptedAt, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.FindAsync(contractId, false, cancellationToken);
        if (contract?.PaymentPlan is null) return null;
        await unitOfWork.LockContractAsync(contractId, cancellationToken);
        var balance = (await unitOfWork.GetInstallmentsAsync(contractId, true, cancellationToken)).FirstOrDefault(i => i.Kind == InstallmentKinds.Balance);
        if (balance is null) return "The contract has no balance installment.";
        if (balance.Status != InstallmentStatuses.Pending) return null;   // already opened (never re-opened with a new deadline)
        balance.Status = InstallmentStatuses.Due;
        balance.OpenedAt = acceptedAt;
        balance.DueAt = PaymentPlanCalculator.BalanceDueAt(acceptedAt, options);
        balance.UpdatedAt = acceptedAt;
        balance.Revision++;
        AddEvent(contractId, "BALANCE_DUE", null, null, customerUserId,
            $"customer accepted the installation; installment 2: {balance.Amount:0} VND due {balance.DueAt:O}", acceptedAt);
        unitOfWork.AddNotification(contract.CustomerUserId, CustomerNotificationTypes.ContractBalanceDue,
            $"Bạn đã nghiệm thu công trình. Vui lòng thanh toán đợt 2 ({balance.Amount:N0} VND) của hợp đồng {contract.ContractNumber} trước {Local(balance.DueAt.Value):HH:mm dd/MM/yyyy}.",
            acceptedAt);
        return null;
    }

    // ================================================================== Overdue sweep (own transactions)

    /// <summary>Opens a collection case (SALES_CONTACT_REQUIRED) for each overdue installment without one. Returns the number opened.</summary>
    public async Task<int> SweepOverdueAsync(int limit, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var opened = 0;
        foreach (var installmentId in await unitOfWork.ListOverdueInstallmentsWithoutCaseAsync(now, Math.Clamp(limit, 1, 500), cancellationToken))
        {
            try
            {
                var created = await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
                {
                    var probe = await unitOfWork.FindInstallmentAsync(installmentId, false, token);
                    if (probe is null) return (false, false);
                    await unitOfWork.LockContractAsync(probe.ContractId, token);
                    var installment = await unitOfWork.FindInstallmentAsync(installmentId, false, token);
                    if (installment is null || !PaymentPlanCalculator.IsOverdue(installment, now)) return (false, false);
                    if (await unitOfWork.FindOpenCollectionCaseAsync(installmentId, false, token) is not null) return (false, false);
                    var contract = (await unitOfWork.FindAsync(installment.ContractId, false, token))!;
                    var collectionCase = new ContractCollectionCase
                    {
                        Id = Guid.NewGuid(), ContractId = contract.Id, InstallmentId = installment.Id, CustomerUserId = contract.CustomerUserId,
                        Status = CollectionCaseStatuses.SalesContactRequired, OpenedAt = now, DueAt = installment.DueAt!.Value, AmountDue = installment.Amount, UpdatedAt = now,
                    };
                    unitOfWork.AddCollectionCase(collectionCase);
                    unitOfWork.AddCollectionActivity(new ContractCollectionActivity
                    {
                        Id = Guid.NewGuid(), CaseId = collectionCase.Id, Kind = CollectionActivityKinds.Opened, ToStatus = collectionCase.Status,
                        Summary = $"Installment {installment.Sequence} ({installment.Amount:0} VND) overdue since {installment.DueAt:O}.", OccurredAt = now,
                    });
                    AddEvent(contract.Id, "INSTALLMENT_OVERDUE", null, null, contract.CustomerUserId,
                        $"installment {installment.Sequence}: {installment.Amount:0} VND due {installment.DueAt:O}; collection case {collectionCase.Id} opened", now);
                    unitOfWork.AddNotification(contract.CustomerUserId, CustomerNotificationTypes.ContractBalanceOverdue,
                        $"Khoản thanh toán đợt {installment.Sequence} ({installment.Amount:N0} VND) của hợp đồng {contract.ContractNumber} đã quá hạn. Nhân viên SmartSolar sẽ liên hệ với bạn.",
                        now);
                    await unitOfWork.SaveChangesAsync(token);
                    return (true, true);
                }, cancellationToken);
                if (created) opened++;
            }
            catch (PersistenceConflictException)
            {
                // A concurrent sweep opened the case first (one-open-case index): nothing to do.
            }
        }
        if (opened > 0) logger?.LogInformation("Overdue sweep opened {Count} collection case(s).", opened);
        return opened;
    }

    private void AddEvent(Guid contractId, string type, string? from, string? to, Guid actor, string? detail, DateTimeOffset now)
        => unitOfWork.AddEvent(new ContractEvent { Id = Guid.NewGuid(), ContractId = contractId, EventType = type, FromStatus = from, ToStatus = to, ActorUserId = actor,
            Detail = Truncate(detail, 500), CreatedAt = now });

    private DateTimeOffset Local(DateTimeOffset at)
    {
        try
        {
            return TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId));
        }
        catch (TimeZoneNotFoundException)
        {
            return at.ToOffset(TimeSpan.FromHours(7));
        }
    }

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
