using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.Payments;

public sealed class PaymentOptions
{
    public const string SectionName = "Payments";
    /// <summary>Payment link lifetime.</summary>
    public int LinkExpiryMinutes { get; set; } = 15;
    /// <summary>Largest amount sent as a single PayOS transaction (VND). Larger contracts are refused, never split.</summary>
    public long MaxAmountVnd { get; set; } = 10_000_000_000;
    /// <summary>Cluster-wide cap on new attempts per contract in a 10-minute window (database counted).</summary>
    public int MaxAttemptsPerTenMinutes { get; set; } = 5;
    /// <summary>Status queries reconcile with the provider at most this often per attempt.</summary>
    public int ReconcileThrottleSeconds { get; set; } = 20;
    /// <summary>Background reconciliation tick (seconds). With <see cref="ReconcileAfterSeconds"/> it bounds how late a payment whose webhook was lost is
    /// confirmed: worst case ≈ tick + re-check age + provider call (default 30 + 60 s, below the 2-minute NFR).</summary>
    public int ReconciliationIntervalSeconds { get; set; } = 30;
    /// <summary>A payable online attempt is asked again once its last reconciliation is older than this (seconds).</summary>
    public int ReconcileAfterSeconds { get; set; } = 60;
    /// <summary>Attempts reconciled per batch; a tick drains every due attempt batch by batch (bounded by <see cref="MaxBatchesPerTick"/>).</summary>
    public int ReconcileBatchSize { get; set; } = 25;
    public int MaxBatchesPerTick { get; set; } = 20;
    public string ReturnUrl { get; set; } = "";
    public string CancelUrl { get; set; } = "";
}

/// <param name="Method">ONLINE (payment gateway) or CASH.</param>
/// <param name="ExpiresAt">Link expiry of an ONLINE attempt; null for CASH (a cash choice does not expire, the customer or staff cancel it).</param>
/// <param name="ProviderReference">Gateway transaction reference, or the receipt reference of a confirmed CASH payment.</param>
/// <param name="AmountPaid">Amount the gateway reported or the cash amount confirmed.</param>
/// <param name="ConfirmedByUserId">CASH, staff view only: the Manager/Admin who recorded the receipt.</param>
/// <param name="EvidenceFileId">CASH, staff view only: the receipt evidence file.</param>
/// <param name="ConfirmationNote">CASH, staff view only.</param>
public sealed record PaymentAttemptView(Guid PaymentAttemptId, Guid? ContractId, long OrderCode, decimal Amount, string Currency, string Status,
    string? CheckoutUrl, string? QrCode, DateTimeOffset? ExpiresAt, DateTimeOffset? PaidAt, string? ProviderReference, string? FailureReason, DateTimeOffset CreatedAt,
    string Method = "ONLINE", decimal? AmountPaid = null, Guid? ConfirmedByUserId = null, Guid? EvidenceFileId = null, string? ConfirmationNote = null);

/// <summary>What a Manager/Admin records when the customer paid the contract in cash.</summary>
public sealed record CashConfirmation(decimal Amount, string? ReceiptReference, Guid EvidenceFileId, string? Note);

public sealed record PaymentEventView(Guid PaymentAttemptId, string EventType, string? FromStatus, string? ToStatus, string Source, string? Detail, DateTimeOffset CreatedAt);

/// <param name="PaymentState">Derived state for the FE (<see cref="ContractPaymentStates"/>); the contract stays SIGNED until it is PAID.</param>
public sealed record ContractPaymentView(Guid ContractId, string ContractStatus, decimal Amount, string Currency, PaymentAttemptView? Current,
    IReadOnlyList<PaymentAttemptView> Attempts, IReadOnlyList<PaymentEventView>? History, string PaymentState = ContractPaymentStates.NotPayable);

public sealed record WebhookResult(bool Accepted, string Outcome);

/// <summary>One line of a checkout (the item it pays and whether it was applied).</summary>
public sealed record PaymentLineView(string ItemType, decimal Amount, string Status, Guid? ContractId, Guid? SubscriptionId, Guid? QuotationId,
    Guid? InstallationQuotationId = null, Guid? ContractInstallmentId = null);

/// <summary>A payment attempt as staff see it, with its customer and lines.</summary>
public sealed record StaffPaymentAttemptView(PaymentAttemptView Attempt, Guid CustomerUserId, IReadOnlyList<PaymentLineView> Lines);

/// <summary>
/// Full payment of a signed contract through the provider. Rules:
/// <list type="bullet">
/// <item>Only SIGNED contracts are payable, for exactly their total, in one transaction (no deposits, no partial payments).</item>
/// <item>At most one payable attempt per contract: a repeated request returns it instead of creating another link.</item>
/// <item>Provider calls never run inside a database transaction.</item>
/// <item>PAID is set only from a verified webhook or a server-to-server status query whose amount equals the contract
/// total; the confirmation marks the attempt PAID and the contract PAID in one short transaction.</item>
/// <item>A second successful payment can never pay the contract twice: it becomes REFUND_REQUIRED.</item>
/// </list>
/// </summary>
/// <para>
/// After-sales checkouts (Flow 3) use the same attempt with <see cref="PaymentAttemptItem"/> allocations: optionally the signed
/// contract plus service packages, or a repair / maintenance quotation alone. Their items are settled by
/// <see cref="IPaymentItemSettlement"/> in the confirmation transaction, using the provider-confirmed transaction time.
/// </para>
/// <para>
/// CASH (2026-11-17): the customer may instead choose to pay a SIGNED contract in cash. The choice is a CASH attempt that holds the same
/// "one payable attempt per contract" slot as an online link (so the two can never run together); it is not a payment. Only a Manager/Admin
/// marks it PAID by recording who, when, the exact contract amount, a unique receipt reference and a verified evidence file. Online money that
/// arrives late for a contract already paid in cash is REFUND_REQUIRED and opens a manual refund request (no automatic refund).
/// </para>
/// <param name="files">Verifies cash evidence files (production wiring; without it cash confirmation is refused).</param>
/// <param name="paidFollowUp">Runs after a transaction that made a contract PAID has committed (the technician-agreed installation schedule is
/// confirmed there). Its failure never undoes or fails the payment; the installation sweep retries.</param>
public sealed class PaymentService(IPaymentUnitOfWork unitOfWork, IPaymentGateway gateway, PaymentOptions options, TimeProvider time, ILogger<PaymentService> logger,
    IPaymentItemSettlement? settlement = null, Func<DateTimeOffset, long>? orderCodes = null, Files.FileService? files = null,
    Installations.IContractPaidFollowUp? paidFollowUp = null)
{
    private async Task FollowUpPaidAsync(Guid contractId, CancellationToken cancellationToken)
    {
        if (paidFollowUp is null) return;
        try
        {
            await paidFollowUp.ContractPaidAsync(contractId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Post-payment follow-up for contract {ContractId} failed; the installation sweep retries it.", contractId);
        }
    }

    /// <summary>Tries for a new attempt whose random order-code digits repeat one already issued in the same second.</summary>
    private const int OrderCodeTries = 3;

    public async Task<OperationResult<PaymentAttemptView>> CreateOrGetAttemptAsync(Guid customerUserId, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetContractAsync(contractId, cancellationToken);
        if (contract is null || contract.CustomerUserId != customerUserId)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ContractNotFound, "Contract was not found.");
        if (contract.PaymentPlan is not null) return InstallmentsOnly<PaymentAttemptView>();
        if (contract.Status == "PAID") return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This contract is already paid.");
        if (contract.Status != "SIGNED")
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ContractNotSigned, "Payment opens after the signed contract is approved.");
        if (contract.Currency != "VND" || contract.TotalAmount <= 0 || decimal.Truncate(contract.TotalAmount) != contract.TotalAmount || contract.TotalAmount > options.MaxAmountVnd)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AmountNotSupported,
                $"The contract total must be a whole VND amount up to {options.MaxAmountVnd:N0}; it is never split into several payments.");

        // A contract paid through its quotation before it existed (technician-led flow) is never charged again.
        if ((await unitOfWork.ListAttemptsAsync(contractId, cancellationToken)).Any(a => a.Status == PaymentStatuses.Paid))
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This contract is already paid.");
        // Reuse a live attempt; settle stale ones with the provider first. A live CASH choice blocks online payment until it is cancelled.
        foreach (var attempt in (await unitOfWork.ListAttemptsAsync(contractId, cancellationToken)).Where(a => PaymentStatuses.Payable.Contains(a.Status)))
        {
            if (attempt.Provider == PaymentProviders.Cash)
                return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CashSelected,
                    "Cash payment was chosen and waits for confirmation by the company. Cancel the cash choice first to pay online.");
            if (attempt.ExpiresAt > time.GetUtcNow()) return OperationResult<PaymentAttemptView>.Ok(View(attempt));
            var settled = await ReconcileAsync(attempt.Id, force: true, cancellationToken);
            if (settled?.Status == PaymentStatuses.Paid) return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This contract is already paid.");
            if (settled is not null && PaymentStatuses.Payable.Contains(settled.Status)) return OperationResult<PaymentAttemptView>.Ok(View(settled));
        }

        var now = time.GetUtcNow();
        if (await unitOfWork.CountAttemptsSinceAsync(contractId, now.AddMinutes(-10), cancellationToken) >= options.MaxAttemptsPerTenMinutes)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.TooManyAttempts, "Too many payment attempts for this contract. Try again in a few minutes.");

        var created = new PaymentAttempt
        {
            Id = Guid.NewGuid(), ContractId = contractId, CustomerUserId = customerUserId, Provider = gateway.ProviderName,
            Amount = contract.TotalAmount, Currency = "VND", Status = PaymentStatuses.Pending,
            ExpiresAt = now.AddMinutes(Math.Clamp(options.LinkExpiryMinutes, 5, 1440)), CreatedAt = now, UpdatedAt = now,
        };
        for (var tries = 1; ; tries++)
        {
            AssignOrderCode(created, now);
            try
            {
                await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
                {
                    unitOfWork.AddAttempt(created);
                    AddEvent(created, "CREATED", null, PaymentStatuses.Pending, PaymentSources.Customer, null, now);
                    await unitOfWork.SaveChangesAsync(token);
                    return (true, true);
                }, cancellationToken);
                break;
            }
            catch (PersistenceConflictException ex) when (IsOrderCodeCollision(ex) && tries < OrderCodeTries)
            {
                // Same second, same random digits as an existing order: not a payment conflict, draw again.
            }
            catch (PersistenceConflictException)
            {
                // A concurrent request created the payable attempt first: return it (never two links). A concurrent CASH choice won the slot.
                var live = (await unitOfWork.ListAttemptsAsync(contractId, cancellationToken)).FirstOrDefault(a => PaymentStatuses.Payable.Contains(a.Status));
                return live is null
                    ? OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This contract is already paid or being paid.")
                    : live.Provider == PaymentProviders.Cash
                        ? OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CashSelected, "Cash payment was chosen meanwhile; cancel it first to pay online.")
                        : OperationResult<PaymentAttemptView>.Ok(View(live));
            }
        }

        var link = await gateway.CreatePaymentLinkAsync(new PaymentLinkRequest(created.OrderCode, (long)contract.TotalAmount, created.Description, options.ReturnUrl,
            options.CancelUrl, created.ExpiresAt, contract.CustomerName, contract.CustomerEmail, contract.CustomerPhone), cancellationToken);
        return await RecordLinkAsync(created.Id, link, cancellationToken);
    }

    /// <summary>
    /// Creates one checkout for after-sales items, optionally together with the customer's SIGNED contract (contract amount kept as
    /// its own CONTRACT line; the contract total itself is never changed). <paramref name="reserveItems"/> runs inside the same
    /// transaction as the new attempt and links the items to it (revision-checked), so a double request cannot create two links for
    /// the same item; it returns an error message to refuse. The link expires at <paramref name="expiresAt"/>.
    /// </summary>
    /// <param name="source">Who opened the checkout: the customer in the app, or the technician on site (<see cref="PaymentSources.Technician"/>).</param>
    public async Task<OperationResult<PaymentAttemptView>> CreateCheckoutAsync(Guid customerUserId, Guid? contractId, IReadOnlyList<CheckoutItem> items,
        DateTimeOffset expiresAt, Func<Guid, CancellationToken, Task<string?>> reserveItems, CancellationToken cancellationToken,
        string source = PaymentSources.Customer)
    {
        var lines = items.Select(i => new PaymentAttemptItem
        {
            Id = Guid.NewGuid(), ItemType = i.ItemType, SubscriptionId = i.SubscriptionId, QuotationId = i.QuotationId, InstallationQuotationId = i.InstallationQuotationId,
            ContractInstallmentId = i.ContractInstallmentId, Amount = i.Amount, Status = PaymentItemStatuses.Pending,
        }).ToList();
        PayableContract? contract = null;
        if (contractId is { } id)
        {
            contract = await unitOfWork.GetContractAsync(id, cancellationToken);
            if (contract is null || contract.CustomerUserId != customerUserId)
                return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ContractNotFound, "Contract was not found.");
            if (contract.PaymentPlan is not null) return InstallmentsOnly<PaymentAttemptView>();
            if (contract.Status == "PAID") return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This contract is already paid.");
            if (contract.Status != "SIGNED")
                return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ContractNotSigned, "Payment opens after the signed contract is approved.");
            lines.Insert(0, new PaymentAttemptItem { Id = Guid.NewGuid(), ItemType = PaymentItemTypes.Contract, ContractId = id, Amount = contract.TotalAmount,
                Status = PaymentItemStatuses.Pending });
        }
        var total = lines.Sum(l => l.Amount);
        if (lines.Count == 0 || lines.Any(l => l.Amount <= 0 || decimal.Truncate(l.Amount) != l.Amount) || total > options.MaxAmountVnd)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AmountNotSupported,
                $"A checkout must contain whole VND amounts with a total up to {options.MaxAmountVnd:N0}.");

        var now = time.GetUtcNow();
        if (expiresAt <= now.AddMinutes(1))
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AmountNotSupported, "The checkout deadline has already passed.");
        var created = new PaymentAttempt
        {
            Id = Guid.NewGuid(), ContractId = contractId, CustomerUserId = customerUserId, Provider = gateway.ProviderName,
            Amount = total, Currency = "VND", Status = PaymentStatuses.Pending,
            ExpiresAt = expiresAt, CreatedAt = now, UpdatedAt = now,
        };
        string? refused = null;
        for (var tries = 1; ; tries++)
        {
            AssignOrderCode(created, now);
            try
            {
                await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
                {
                    refused = await reserveItems(created.Id, token);
                    if (refused is not null) return (false, false);
                    unitOfWork.AddAttempt(created);
                    foreach (var line in lines)
                    {
                        line.PaymentAttemptId = created.Id;
                        unitOfWork.AddItem(line);
                    }
                    AddEvent(created, "CREATED", null, PaymentStatuses.Pending, source, string.Join(", ", lines.Select(l => $"{l.ItemType} {l.Amount:0}")), now);
                    await unitOfWork.SaveChangesAsync(token);
                    return (true, true);
                }, cancellationToken);
                break;
            }
            catch (PersistenceConflictException ex) when (IsOrderCodeCollision(ex) && tries < OrderCodeTries)
            {
                // Same second, same random digits as an existing order: not a payment conflict, draw again.
            }
            catch (PersistenceConflictException)
            {
                // A concurrent checkout of the same contract or item was created first.
                return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CheckoutInProgress, "A payment for these items is already in progress.");
            }
        }
        if (refused is not null) return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CheckoutInProgress, refused);

        var link = await gateway.CreatePaymentLinkAsync(new PaymentLinkRequest(created.OrderCode, (long)total, created.Description, options.ReturnUrl,
            options.CancelUrl, created.ExpiresAt, contract?.CustomerName, contract?.CustomerEmail, contract?.CustomerPhone), cancellationToken);
        return await RecordLinkAsync(created.Id, link, cancellationToken);
    }

    /// <summary>
    /// CASH for a combined checkout (2026-11-18): the customer pays the SIGNED contract and the selected packages together in cash. One CASH
    /// attempt carries the CONTRACT line (the contract total, never changed) and one line per package, so the total is the sum of its lines and
    /// every line stays traceable. It takes the contract's single payable slot (no online link and no other cash choice can run at the same time),
    /// holds no deadline (a Manager/Admin confirms the receipt of the exact total with <see cref="ConfirmCashAsync"/>, which settles every line in
    /// the same transaction) and is withdrawn with <see cref="CancelCurrentAsync"/>. <paramref name="reserveItems"/> runs in the creating
    /// transaction (the package orders are stored with it).
    /// </summary>
    public async Task<OperationResult<PaymentAttemptView>> CreateCashCheckoutAsync(Guid customerUserId, Guid? contractId, IReadOnlyList<CheckoutItem> items,
        Func<Guid, CancellationToken, Task<string?>> reserveItems, CancellationToken cancellationToken, string source = PaymentSources.Customer)
    {
        if (contractId is null) return await CreateItemsCashCheckoutAsync(customerUserId, items, reserveItems, cancellationToken, source);
        var contract = await unitOfWork.GetContractAsync(contractId.Value, cancellationToken);
        if (contract is null || contract.CustomerUserId != customerUserId)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ContractNotFound, "Contract was not found.");
        if (contract.PaymentPlan is not null) return InstallmentsOnly<PaymentAttemptView>();
        if (contract.Status == "PAID") return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This contract is already paid.");
        if (contract.Status != "SIGNED")
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ContractNotSigned, "Payment opens after the signed contract is approved.");
        if (settlement is null)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ProviderNotConfigured, "Package lines cannot be settled in this environment.");
        var lines = new List<PaymentAttemptItem>
        {
            new() { Id = Guid.NewGuid(), ItemType = PaymentItemTypes.Contract, ContractId = contractId.Value, Amount = contract.TotalAmount, Status = PaymentItemStatuses.Pending },
        };
        lines.AddRange(items.Select(i => new PaymentAttemptItem
        {
            Id = Guid.NewGuid(), ItemType = i.ItemType, SubscriptionId = i.SubscriptionId, QuotationId = i.QuotationId, InstallationQuotationId = i.InstallationQuotationId, ContractInstallmentId = i.ContractInstallmentId, Amount = i.Amount, Status = PaymentItemStatuses.Pending,
        }));
        var total = lines.Sum(l => l.Amount);
        if (contract.Currency != "VND" || items.Count == 0 || lines.Any(l => l.Amount <= 0 || decimal.Truncate(l.Amount) != l.Amount) || total > options.MaxAmountVnd)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AmountNotSupported, $"A checkout must contain whole VND amounts with a total up to {options.MaxAmountVnd:N0}.");

        // Same rule as choosing cash for the contract alone: an open online link is asked first; an existing cash choice must be withdrawn first.
        foreach (var live in (await unitOfWork.ListAttemptsAsync(contractId.Value, cancellationToken)).Where(a => PaymentStatuses.Payable.Contains(a.Status)))
        {
            if (live.Provider == PaymentProviders.Cash)
                return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CashSelected,
                    "Cash payment was already chosen for this contract. Cancel that choice first to add packages to the cash payment.");
            var settled = await ReconcileAsync(live.Id, force: true, cancellationToken);
            if (settled?.Status == PaymentStatuses.Paid) return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This contract is already paid.");
            if (settled is not null && PaymentStatuses.Payable.Contains(settled.Status))
                return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CheckoutInProgress,
                    "An online payment is in progress. Cancel it first (its status is checked with the payment provider) to pay in cash.");
        }

        return await InsertCashAttemptAsync(customerUserId, contractId, lines, reserveItems, cancellationToken, source);
    }

    /// <summary>
    /// CASH for after-sales items without a contract line (2026-11-18): a repair / maintenance quotation, or packages of an already PAID contract.
    /// The item reservation (same transaction) is what keeps one live checkout per item; the attempt holds no deadline of its own, the items' own
    /// rules (quotation expiry) are checked again at confirmation.
    /// </summary>
    private async Task<OperationResult<PaymentAttemptView>> CreateItemsCashCheckoutAsync(Guid customerUserId, IReadOnlyList<CheckoutItem> items,
        Func<Guid, CancellationToken, Task<string?>> reserveItems, CancellationToken cancellationToken, string source)
    {
        if (settlement is null)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ProviderNotConfigured, "After-sales lines cannot be settled in this environment.");
        var lines = items.Select(i => new PaymentAttemptItem
        {
            Id = Guid.NewGuid(), ItemType = i.ItemType, SubscriptionId = i.SubscriptionId, QuotationId = i.QuotationId, InstallationQuotationId = i.InstallationQuotationId, ContractInstallmentId = i.ContractInstallmentId, Amount = i.Amount, Status = PaymentItemStatuses.Pending,
        }).ToList();
        if (lines.Count == 0 || lines.Any(l => l.Amount <= 0 || decimal.Truncate(l.Amount) != l.Amount) || lines.Sum(l => l.Amount) > options.MaxAmountVnd)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AmountNotSupported, $"A checkout must contain whole VND amounts with a total up to {options.MaxAmountVnd:N0}.");
        return await InsertCashAttemptAsync(customerUserId, null, lines, reserveItems, cancellationToken, source);
    }

    private async Task<OperationResult<PaymentAttemptView>> InsertCashAttemptAsync(Guid customerUserId, Guid? contractId, List<PaymentAttemptItem> lines,
        Func<Guid, CancellationToken, Task<string?>> reserveItems, CancellationToken cancellationToken, string source)
    {
        var total = lines.Sum(l => l.Amount);
        var now = time.GetUtcNow();
        var created = new PaymentAttempt
        {
            Id = Guid.NewGuid(), ContractId = contractId, CustomerUserId = customerUserId, Provider = PaymentProviders.Cash, Amount = total, Currency = "VND",
            Status = PaymentStatuses.Pending, ExpiresAt = now.AddYears(1), CreatedAt = now, UpdatedAt = now,
        };
        string? refused = null;
        for (var tries = 1; ; tries++)
        {
            AssignOrderCode(created, now);
            try
            {
                await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
                {
                    refused = await reserveItems(created.Id, token);
                    if (refused is not null) return (false, false);
                    unitOfWork.AddAttempt(created);
                    foreach (var line in lines)
                    {
                        line.PaymentAttemptId = created.Id;
                        unitOfWork.AddItem(line);
                    }
                    AddEvent(created, "CASH_SELECTED", null, PaymentStatuses.Pending, source,
                        "awaiting cash confirmation; " + string.Join(", ", lines.Select(l => $"{l.ItemType} {l.Amount:0}")), now);
                    await unitOfWork.SaveChangesAsync(token);
                    return (true, true);
                }, cancellationToken);
                break;
            }
            catch (PersistenceConflictException ex) when (IsOrderCodeCollision(ex) && tries < OrderCodeTries)
            {
            }
            catch (PersistenceConflictException)
            {
                return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CheckoutInProgress, "Another payment for these items started meanwhile.");
            }
        }
        return refused is not null
            ? OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CheckoutInProgress, refused)
            : OperationResult<PaymentAttemptView>.Ok(View(created));
    }

    /// <summary>
    /// Cancels an after-sales checkout link of the customer (the quotation was rejected or superseded, or the package cancelled). When the
    /// provider reports it already paid, it is confirmed instead (money is never ignored). Returns the attempt as it ends up.
    /// </summary>
    public async Task<PaymentAttempt?> CancelAttemptAsync(Guid attemptId, string reason, CancellationToken cancellationToken)
    {
        // A cash choice has no provider link: withdrawing it is a local, conditional transition (a confirmation that won stays PAID).
        if (await unitOfWork.FindAttemptAsync(attemptId, false, cancellationToken) is { Provider: PaymentProviders.Cash } cashChoice)
            return PaymentStatuses.Payable.Contains(cashChoice.Status)
                ? await TransitionAsync(attemptId, PaymentStatuses.Cancelled, PaymentSources.System, Truncate(reason, 300), cancellationToken) ?? cashChoice
                : cashChoice;
        var settled = await ReconcileAsync(attemptId, force: true, cancellationToken);
        if (settled is null || !PaymentStatuses.Payable.Contains(settled.Status)) return settled;
        if (await gateway.CancelPaymentLinkAsync(settled.OrderCode, reason, cancellationToken) is not (GatewayCallStatus.Succeeded or GatewayCallStatus.NotConfigured))
            return settled;
        return await TransitionAsync(attemptId, PaymentStatuses.Cancelled, PaymentSources.System, Truncate(reason, 300), cancellationToken);
    }

    /// <summary>A checkout of the customer (for the after-sales payment views); null when it is not theirs.</summary>
    public async Task<PaymentAttemptView?> GetAttemptForCustomerAsync(Guid customerUserId, Guid attemptId, bool reconcile, CancellationToken cancellationToken)
    {
        var attempt = await unitOfWork.FindAttemptAsync(attemptId, false, cancellationToken);
        if (attempt is null || attempt.CustomerUserId != customerUserId) return null;
        if (reconcile && PaymentStatuses.Payable.Contains(attempt.Status)) attempt = await ReconcileAsync(attemptId, force: false, cancellationToken) ?? attempt;
        return View(attempt);
    }

    /// <summary>Every payment attempt of an installation quotation (any status), newest first.</summary>
    public async Task<IReadOnlyList<PaymentAttempt>> ListInstallationQuotationAttemptsAsync(Guid quotationId, CancellationToken cancellationToken)
        => (await unitOfWork.ListAttemptsForInstallationQuotationAsync(quotationId, cancellationToken)).OrderByDescending(a => a.CreatedAt).ToList();

    /// <summary>Every payment attempt of a contract installment (30/70 plan; any status), newest first.</summary>
    public async Task<IReadOnlyList<PaymentAttempt>> ListInstallmentAttemptsAsync(Guid installmentId, CancellationToken cancellationToken)
        => (await unitOfWork.ListAttemptsForInstallmentAsync(installmentId, cancellationToken)).OrderByDescending(a => a.CreatedAt).ToList();

    private static OperationResult<T> InstallmentsOnly<T>() => OperationResult<T>.Fail(PaymentErrorCodes.ContractHasInstallments,
        "This contract is paid in two installments (30% before signing, 70% after acceptance); use the installment payment of the contract.");

    private async Task<OperationResult<PaymentAttemptView>> RecordLinkAsync(Guid attemptId, PaymentLinkResult link, CancellationToken cancellationToken)
    {
        // A concurrent reconciliation or cash-choice check may touch the same attempt (revision bump) between the read and the save:
        // re-read and apply the gateway's answer again (bounded), so the created link is never lost and no conflict escapes.
        PaymentAttempt? result = null;
        for (var tries = 0; ; tries++)
        {
            try
            {
                result = await RecordLinkOnceAsync(attemptId, link, cancellationToken);
                break;
            }
            catch (PersistenceConflictException) when (tries < 4)
            {
            }
        }
        return LinkOutcome(result, link);
    }

    private Task<PaymentAttempt?> RecordLinkOnceAsync(Guid attemptId, PaymentLinkResult link, CancellationToken cancellationToken)
        => unitOfWork.ExecuteInTransactionAsync<PaymentAttempt?>(async token =>
        {
            var attempt = await unitOfWork.FindAttemptAsync(attemptId, true, token);
            if (attempt is null || attempt.Status != PaymentStatuses.Pending) return (attempt, false);
            var now = time.GetUtcNow();
            if (link.Status == GatewayCallStatus.Succeeded)
            {
                attempt.ProviderPaymentLinkId = link.PaymentLinkId;
                attempt.CheckoutUrl = link.CheckoutUrl;
                attempt.QrCode = link.QrCode;
                AddEvent(attempt, "LINK_CREATED", null, null, PaymentSources.System, null, now);
            }
            else
            {
                // Free the payable slot so the customer can retry.
                AddEvent(attempt, "LINK_FAILED", attempt.Status, PaymentStatuses.Failed, PaymentSources.System, link.Status.ToString(), now);
                attempt.Status = PaymentStatuses.Failed;
                attempt.FailureReason = Truncate(link.Error ?? link.Status.ToString(), 300);
            }
            attempt.UpdatedAt = now;
            attempt.Revision++;
            await unitOfWork.SaveChangesAsync(token);
            return (attempt, true);
        }, cancellationToken);

    private OperationResult<PaymentAttemptView> LinkOutcome(PaymentAttempt? result, PaymentLinkResult link)
        => link.Status switch
        {
            GatewayCallStatus.Succeeded => OperationResult<PaymentAttemptView>.Ok(View(result!)),
            GatewayCallStatus.NotConfigured => OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ProviderNotConfigured, "Online payment is not configured in this environment."),
            _ => OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ProviderError, "The payment provider could not create a payment link. Please try again."),
        };

    /// <summary>Customer cancels the current link (if the provider reports it already paid, it is confirmed instead).</summary>
    public async Task<OperationResult<PaymentAttemptView>> CancelCurrentAsync(Guid customerUserId, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetContractAsync(contractId, cancellationToken);
        if (contract is null || contract.CustomerUserId != customerUserId)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ContractNotFound, "Contract was not found.");
        var live = (await unitOfWork.ListAttemptsAsync(contractId, cancellationToken)).FirstOrDefault(a => PaymentStatuses.Payable.Contains(a.Status));
        if (live is null) return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AttemptNotFound, "There is no payment in progress.");
        if (live.Provider == PaymentProviders.Cash)
        {
            // Withdrawing the cash choice (e.g. to pay online). Conditional on the attempt still being PENDING: a confirmation that won stays PAID.
            var withdrawn = await TransitionAsync(live.Id, PaymentStatuses.Cancelled, PaymentSources.Customer, "cash choice cancelled by customer", cancellationToken);
            return OperationResult<PaymentAttemptView>.Ok(View(withdrawn ?? live));
        }

        var settled = await ReconcileAsync(live.Id, force: true, cancellationToken);
        if (settled is not null && !PaymentStatuses.Payable.Contains(settled.Status)) return OperationResult<PaymentAttemptView>.Ok(View(settled));
        if (await gateway.CancelPaymentLinkAsync(live.OrderCode, "Cancelled by customer", cancellationToken) is not (GatewayCallStatus.Succeeded or GatewayCallStatus.NotConfigured))
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ProviderError, "The payment link could not be cancelled right now.");
        var cancelled = await TransitionAsync(live.Id, PaymentStatuses.Cancelled, PaymentSources.Customer, "cancelled by customer", cancellationToken);
        return OperationResult<PaymentAttemptView>.Ok(View(cancelled ?? live));
    }

    /// <summary>Current state for the customer; a stale payable attempt is reconciled server-to-server (throttled). Browser return URLs never mark PAID.</summary>
    public async Task<OperationResult<ContractPaymentView>> GetForCustomerAsync(Guid customerUserId, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetContractAsync(contractId, cancellationToken);
        if (contract is null || contract.CustomerUserId != customerUserId)
            return OperationResult<ContractPaymentView>.Fail(PaymentErrorCodes.ContractNotFound, "Contract was not found.");
        foreach (var live in (await unitOfWork.ListAttemptsAsync(contractId, cancellationToken)).Where(a => PaymentStatuses.Payable.Contains(a.Status)))
            await ReconcileAsync(live.Id, force: false, cancellationToken);
        return OperationResult<ContractPaymentView>.Ok(await ViewForContractAsync(contractId, includeHistory: false, cancellationToken));
    }

    public async Task<OperationResult<ContractPaymentView>> GetForStaffAsync(Guid contractId, CancellationToken cancellationToken)
        => await unitOfWork.GetContractAsync(contractId, cancellationToken) is null
            ? OperationResult<ContractPaymentView>.Fail(PaymentErrorCodes.ContractNotFound, "Contract was not found.")
            : OperationResult<ContractPaymentView>.Ok(await ViewForContractAsync(contractId, includeHistory: true, cancellationToken));

    /// <summary>
    /// The customer chooses to pay the SIGNED contract in cash. Not a payment: the contract stays SIGNED and its payment state becomes
    /// AWAITING_CASH_CONFIRMATION until a Manager/Admin records the receipt. An online link in progress is first checked with the gateway
    /// (paid → already paid; still open → refused until the customer cancels it), so the two methods never overlap. Idempotent.
    /// </summary>
    public async Task<OperationResult<PaymentAttemptView>> SelectCashAsync(Guid customerUserId, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetContractAsync(contractId, cancellationToken);
        if (contract is null || contract.CustomerUserId != customerUserId)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ContractNotFound, "Contract was not found.");
        if (contract.PaymentPlan is not null) return InstallmentsOnly<PaymentAttemptView>();
        if (contract.Status == "PAID") return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This contract is already paid.");
        if (contract.Status != "SIGNED")
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ContractNotSigned, "Payment opens after the signed contract is approved.");
        if (contract.Currency != "VND" || contract.TotalAmount <= 0 || decimal.Truncate(contract.TotalAmount) != contract.TotalAmount)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AmountNotSupported, "The contract total must be a whole VND amount.");

        if ((await unitOfWork.ListAttemptsAsync(contractId, cancellationToken)).Any(a => a.Status == PaymentStatuses.Paid))
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This contract is already paid.");
        foreach (var live in (await unitOfWork.ListAttemptsAsync(contractId, cancellationToken)).Where(a => PaymentStatuses.Payable.Contains(a.Status)))
        {
            if (live.Provider == PaymentProviders.Cash) return OperationResult<PaymentAttemptView>.Ok(View(live));
            // An online link is open: ask the gateway first (money may already have arrived).
            var settled = await ReconcileAsync(live.Id, force: true, cancellationToken);
            if (settled?.Status == PaymentStatuses.Paid) return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This contract is already paid.");
            if (settled is not null && PaymentStatuses.Payable.Contains(settled.Status))
                return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CheckoutInProgress,
                    "An online payment is in progress. Cancel it first (its status is checked with the payment provider) to pay in cash.");
        }

        var now = time.GetUtcNow();
        var created = new PaymentAttempt
        {
            Id = Guid.NewGuid(), ContractId = contractId, CustomerUserId = customerUserId, Provider = PaymentProviders.Cash, Amount = contract.TotalAmount,
            Currency = "VND", Status = PaymentStatuses.Pending,
            // Not a deadline: a cash choice stays open until it is confirmed or cancelled (the view shows no expiry for CASH).
            ExpiresAt = now.AddYears(1), CreatedAt = now, UpdatedAt = now,
        };
        for (var tries = 1; ; tries++)
        {
            AssignOrderCode(created, now);
            try
            {
                await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
                {
                    unitOfWork.AddAttempt(created);
                    AddEvent(created, "CASH_SELECTED", null, PaymentStatuses.Pending, PaymentSources.Customer, "awaiting cash confirmation", now);
                    await unitOfWork.SaveChangesAsync(token);
                    return (true, true);
                }, cancellationToken);
                return OperationResult<PaymentAttemptView>.Ok(View(created));
            }
            catch (PersistenceConflictException ex) when (IsOrderCodeCollision(ex) && tries < OrderCodeTries)
            {
            }
            catch (PersistenceConflictException)
            {
                // The payable slot was taken concurrently (an online link, or the same cash choice twice).
                var live = (await unitOfWork.ListAttemptsAsync(contractId, cancellationToken)).FirstOrDefault(a => PaymentStatuses.Payable.Contains(a.Status));
                return live?.Provider == PaymentProviders.Cash
                    ? OperationResult<PaymentAttemptView>.Ok(View(live))
                    : OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CheckoutInProgress, "Another payment for this contract started meanwhile.");
            }
        }
    }

    /// <summary>
    /// A Manager/Admin records the cash the customer paid: the CASH attempt becomes PAID and the contract SIGNED → PAID in one transaction.
    /// The amount must be exactly the contract total (no partial or over-payment), the receipt reference unique, and the evidence a VERIFIED
    /// PAYMENT_EVIDENCE file uploaded by the confirming staff member. A second confirmation is refused (409), never applied twice.
    /// </summary>
    public async Task<OperationResult<PaymentAttemptView>> ConfirmCashAsync(Guid staffUserId, Guid contractId, CashConfirmation input, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (input.Amount <= 0 || decimal.Truncate(input.Amount) != input.Amount) errors["amount"] = ["A positive whole VND amount is required."];
        var receipt = input.ReceiptReference?.Trim();
        if (string.IsNullOrEmpty(receipt) || receipt.Length > 100) errors["receiptReference"] = ["A receipt reference of at most 100 characters is required."];
        if (input.EvidenceFileId == Guid.Empty) errors["evidenceFileId"] = ["The receipt evidence file is required."];
        if (input.Note is { Length: > 500 }) errors["note"] = ["At most 500 characters."];
        if (errors.Count > 0) return OperationResult<PaymentAttemptView>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.", errors);

        var contract = await unitOfWork.GetContractAsync(contractId, cancellationToken);
        if (contract is null) return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ContractNotFound, "Contract was not found.");
        if (contract.PaymentPlan is not null) return InstallmentsOnly<PaymentAttemptView>();
        if (contract.Status == "PAID") return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This contract is already paid.");
        var cash = (await unitOfWork.ListAttemptsAsync(contractId, cancellationToken))
            .FirstOrDefault(a => a.Provider == PaymentProviders.Cash && a.Status == PaymentStatuses.Pending);
        if (cash is null)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CashNotSelected, "The customer has not chosen cash payment for this contract (or it was cancelled).");
        // The amount due is the cash attempt's total: the contract alone, or the contract line plus its package lines (combined cash checkout).
        var cashLines = await unitOfWork.GetItemsAsync(cash.Id, false, cancellationToken);
        var contractLine = cashLines.Where(i => i.ItemType == PaymentItemTypes.Contract).Sum(i => i.Amount);
        var consistent = cashLines.Count == 0
            ? cash.Amount == contract.TotalAmount
            : contractLine == contract.TotalAmount && cashLines.Sum(i => i.Amount) == cash.Amount;
        if (!consistent || input.Amount != cash.Amount)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CashAmountMismatch,
                $"The confirmed amount must be exactly the amount due {cash.Amount:0} VND; partial or over-payments are not recorded.",
                new { expected = cash.Amount, received = input.Amount, contractAmount = contract.TotalAmount, packagesAmount = cash.Amount - contract.TotalAmount });
        if (cashLines.Any(i => i.ItemType != PaymentItemTypes.Contract) && settlement is null)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ProviderNotConfigured, "Package lines cannot be settled in this environment.");
        if (files is null)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ProviderNotConfigured, "Cash confirmation needs file verification, which is not configured.");
        var evidence = await files.RequireVerifiedAsync([input.EvidenceFileId], Files.FilePurposes.PaymentEvidence, staffUserId, cancellationToken);
        if (!evidence.Succeeded) return evidence.As<PaymentAttemptView>();

        try
        {
            var outcome = await unitOfWork.ExecuteInTransactionAsync<OperationResult<PaymentAttempt>>(async token =>
            {
                var attempt = await unitOfWork.FindAttemptAsync(cash.Id, true, token);
                if (attempt is null || attempt.Status != PaymentStatuses.Pending)
                    return (attempt?.Status == PaymentStatuses.Paid
                        ? OperationResult<PaymentAttempt>.Fail(PaymentErrorCodes.AlreadyPaid, "This cash payment was already confirmed.")
                        : OperationResult<PaymentAttempt>.Fail(PaymentErrorCodes.CashNotSelected, "The cash choice was cancelled meanwhile."), false);
                var now = time.GetUtcNow();
                // SIGNED → PAID is the single decisive, conditional step: if anything else paid the contract first, nothing is written.
                if (!await unitOfWork.TryMarkContractPaidAsync(contractId, now, token, staffUserId, $"cash received; receipt {receipt}"))
                    return (await unitOfWork.IsContractPaidAsync(contractId, token)
                        ? OperationResult<PaymentAttempt>.Fail(PaymentErrorCodes.AlreadyPaid, "This contract is already paid.")
                        : OperationResult<PaymentAttempt>.Fail(PaymentErrorCodes.ContractNotSigned, "The contract is no longer SIGNED."), false);
                var from = attempt.Status;
                attempt.Status = PaymentStatuses.Paid;
                attempt.AmountPaid = input.Amount;
                attempt.PaidAt = now;
                attempt.ProviderReference = receipt;
                attempt.ProviderTransactionAt = now;
                attempt.ConfirmedByUserId = staffUserId;
                attempt.EvidenceFileId = input.EvidenceFileId;
                attempt.ConfirmationNote = Truncate(input.Note?.Trim(), 500);
                attempt.UpdatedAt = now;
                attempt.Revision++;
                // The contract line is settled by the contract's SIGNED → PAID step above; package lines by the after-sales settlement (each once).
                var items = await unitOfWork.GetItemsAsync(attempt.Id, true, token);
                foreach (var item in items.Where(i => i.ItemType == PaymentItemTypes.Contract)) item.Status = PaymentItemStatuses.Settled;
                var others = items.Where(i => i.ItemType != PaymentItemTypes.Contract).ToList();
                if (others.Count > 0)
                {
                    if (await settlement!.CashSettlementProblemAsync(attempt, others, now, token) is { } problem)
                        return (OperationResult<PaymentAttempt>.Fail(PaymentErrorCodes.CashItemNotPayable, problem), false);
                    await settlement.SettleAsync(attempt, others, now, now, token);
                }
                AddEvent(attempt, "CASH_CONFIRMED", from, PaymentStatuses.Paid, PaymentSources.Staff, $"by {staffUserId}; receipt {receipt}; amount {input.Amount:0}", now);
                await unitOfWork.SaveChangesAsync(token);
                return (OperationResult<PaymentAttempt>.Ok(attempt), true);
            }, cancellationToken);
            if (outcome.Succeeded) await FollowUpPaidAsync(contractId, cancellationToken);
            return outcome.Succeeded ? OperationResult<PaymentAttemptView>.Ok(View(outcome.Value!, staff: true)) : outcome.As<PaymentAttemptView>();
        }
        catch (PersistenceConflictException ex) when (ex.Kind == PersistenceConflictException.UniqueViolation
                                                     && (ex.Constraint?.Contains("provider_reference", StringComparison.Ordinal) ?? false))
        {
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CashReceiptDuplicate, "This receipt reference was already used for another cash payment.");
        }
        catch (PersistenceConflictException)
        {
            // A concurrent confirmation or cancellation of the same attempt won.
            var now = await unitOfWork.FindAttemptAsync(cash.Id, false, cancellationToken);
            return now?.Status == PaymentStatuses.Paid
                ? OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This cash payment was already confirmed.")
                : OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CashNotSelected, "The cash choice changed meanwhile. Reload and try again.");
        }
    }

    /// <summary>
    /// Staff list of payment attempts (Manager/Admin; also the Admin dashboard detail): filters in SQL, newest first, paged, each with its item lines
    /// (one extra query for the page). <paramref name="method"/> ONLINE/CASH, <paramref name="status"/> a payment status.
    /// </summary>
    public async Task<Common.Paging.PagedResult<StaffPaymentAttemptView>> PageForStaffAsync(PaymentAttemptFilter filter, Common.Paging.PageRequest page,
        CancellationToken cancellationToken)
    {
        var (rows, total) = await unitOfWork.PageAttemptsAsync(filter, page.Skip, page.PageSize, cancellationToken);
        var lines = (await unitOfWork.GetItemsForAttemptsAsync(rows.Select(r => r.Id).ToList(), cancellationToken)).ToLookup(l => l.PaymentAttemptId);
        return page.Result(rows.Select(a => new StaffPaymentAttemptView(View(a, staff: true), a.CustomerUserId,
            lines[a.Id].Select(l => new PaymentLineView(l.ItemType, l.Amount, l.Status, l.ContractId, l.SubscriptionId, l.QuotationId, l.InstallationQuotationId,
                l.ContractInstallmentId)).ToList())).ToList(), total);
    }

    /// <summary>
    /// A Manager/Admin records cash for one CASH attempt identified by its id (2026-11-18): the generic route for after-sales cash (repair /
    /// maintenance quotation, packages of a PAID contract). A contract attempt is delegated to <see cref="ConfirmCashAsync"/> (same rules). Without a
    /// contract: exact attempt amount, unique receipt, VERIFIED evidence of the confirming staff member, every line still payable now (quotation not
    /// expired, item waiting for this attempt) — checked again inside the transaction, so nothing is recorded for an item that can no longer be
    /// applied — then the attempt is PAID and every line settled once (the request moves on as after a verified PayOS payment).
    /// </summary>
    public async Task<OperationResult<PaymentAttemptView>> ConfirmCashAttemptAsync(Guid staffUserId, Guid attemptId, CashConfirmation input, CancellationToken cancellationToken)
    {
        var attempt = await unitOfWork.FindAttemptAsync(attemptId, false, cancellationToken);
        if (attempt is null || attempt.Provider != PaymentProviders.Cash)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AttemptNotFound, "Cash payment was not found.");
        if (attempt.ContractId is { } contractId)
        {
            // The contract route confirms the contract's pending cash choice: it must be this attempt (not a newer or an older one).
            if (attempt.Status == PaymentStatuses.Paid) return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This cash payment was already confirmed.");
            if (attempt.Status != PaymentStatuses.Pending)
                return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CashNotSelected, "The cash choice was cancelled or withdrawn.");
            return await ConfirmCashAsync(staffUserId, contractId, input, cancellationToken);
        }

        var errors = new Dictionary<string, string[]>();
        if (input.Amount <= 0 || decimal.Truncate(input.Amount) != input.Amount) errors["amount"] = ["A positive whole VND amount is required."];
        var receipt = input.ReceiptReference?.Trim();
        if (string.IsNullOrEmpty(receipt) || receipt.Length > 100) errors["receiptReference"] = ["A receipt reference of at most 100 characters is required."];
        if (input.EvidenceFileId == Guid.Empty) errors["evidenceFileId"] = ["The receipt evidence file is required."];
        if (input.Note is { Length: > 500 }) errors["note"] = ["At most 500 characters."];
        if (errors.Count > 0) return OperationResult<PaymentAttemptView>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.", errors);
        if (attempt.Status == PaymentStatuses.Paid) return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This cash payment was already confirmed.");
        if (attempt.Status != PaymentStatuses.Pending)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CashNotSelected, "The cash choice was cancelled or withdrawn.");
        if (input.Amount != attempt.Amount)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CashAmountMismatch,
                $"The confirmed amount must be exactly the amount due {attempt.Amount:0} VND; partial or over-payments are not recorded.",
                new { expected = attempt.Amount, received = input.Amount });
        if (files is null || settlement is null)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ProviderNotConfigured, "Cash confirmation is not configured in this environment.");
        var evidence = await files.RequireVerifiedAsync([input.EvidenceFileId], Files.FilePurposes.PaymentEvidence, staffUserId, cancellationToken);
        if (!evidence.Succeeded) return evidence.As<PaymentAttemptView>();

        try
        {
            var outcome = await unitOfWork.ExecuteInTransactionAsync<OperationResult<PaymentAttempt>>(async token =>
            {
                var tracked = await unitOfWork.FindAttemptAsync(attemptId, true, token);
                if (tracked is null || tracked.Status != PaymentStatuses.Pending)
                    return (tracked?.Status == PaymentStatuses.Paid
                        ? OperationResult<PaymentAttempt>.Fail(PaymentErrorCodes.AlreadyPaid, "This cash payment was already confirmed.")
                        : OperationResult<PaymentAttempt>.Fail(PaymentErrorCodes.CashNotSelected, "The cash choice was cancelled meanwhile."), false);
                var now = time.GetUtcNow();
                var items = await unitOfWork.GetItemsAsync(tracked.Id, true, token);
                if (await settlement.CashSettlementProblemAsync(tracked, items, now, token) is { } problem)
                {
                    // A concurrent confirmation of this same attempt may have committed between our read and this check (its item is now PAID):
                    // report that, not a misleading "not payable". Nothing is written either way.
                    var committed = await unitOfWork.FindAttemptAsync(attemptId, false, token);
                    return (committed?.Status == PaymentStatuses.Paid
                        ? OperationResult<PaymentAttempt>.Fail(PaymentErrorCodes.AlreadyPaid, "This cash payment was already confirmed.")
                        : OperationResult<PaymentAttempt>.Fail(PaymentErrorCodes.CashItemNotPayable, problem), false);
                }
                var from = tracked.Status;
                tracked.Status = PaymentStatuses.Paid;
                tracked.AmountPaid = input.Amount;
                tracked.PaidAt = now;
                tracked.ProviderReference = receipt;
                tracked.ProviderTransactionAt = now;
                tracked.ConfirmedByUserId = staffUserId;
                tracked.EvidenceFileId = input.EvidenceFileId;
                tracked.ConfirmationNote = Truncate(input.Note?.Trim(), 500);
                tracked.UpdatedAt = now;
                tracked.Revision++;
                await settlement.SettleAsync(tracked, items, now, now, token);
                AddEvent(tracked, "CASH_CONFIRMED", from, PaymentStatuses.Paid, PaymentSources.Staff, $"by {staffUserId}; receipt {receipt}; amount {input.Amount:0}", now);
                await unitOfWork.SaveChangesAsync(token);
                return (OperationResult<PaymentAttempt>.Ok(tracked), true);
            }, cancellationToken);
            return outcome.Succeeded ? OperationResult<PaymentAttemptView>.Ok(View(outcome.Value!, staff: true)) : outcome.As<PaymentAttemptView>();
        }
        catch (PersistenceConflictException ex) when (ex.Kind == PersistenceConflictException.UniqueViolation
                                                     && (ex.Constraint?.Contains("provider_reference", StringComparison.Ordinal) ?? false))
        {
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CashReceiptDuplicate, "This receipt reference was already used for another cash payment.");
        }
        catch (PersistenceConflictException)
        {
            var current = await unitOfWork.FindAttemptAsync(attemptId, false, cancellationToken);
            return current?.Status == PaymentStatuses.Paid
                ? OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This cash payment was already confirmed.")
                : OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CashNotSelected, "The cash choice changed meanwhile. Reload and try again.");
        }
    }

    /// <summary>
    /// Provider webhook. Invalid signatures are rejected; deliveries are deduplicated by payload hash;
    /// unknown order codes are acknowledged (so the provider stops retrying) and recorded. Rejection
    /// is reported to the caller only for bad signatures.
    /// </summary>
    public async Task<WebhookResult> HandleWebhookAsync(string rawBody, CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        var notification = gateway.VerifyWebhook(rawBody);
        var now = time.GetUtcNow();

        if (!notification.SignatureValid)
        {
            logger.LogWarning("Rejected payment webhook with an invalid signature.");
            return new WebhookResult(false, WebhookOutcomes.Rejected);
        }
        if (await unitOfWork.WebhookSeenAsync(hash, cancellationToken)) return new WebhookResult(true, WebhookOutcomes.Duplicate);

        var outcome = WebhookOutcomes.Ignored;
        string? detail = null;
        var attempt = notification.OrderCode is { } code ? await unitOfWork.FindByOrderCodeAsync(code, false, cancellationToken) : null;
        if (attempt is null)
        {
            detail = "unknown order code";
        }
        else if (notification.Success && notification.Code == "00")
        {
            var confirmed = await ConfirmAsync(attempt.Id, notification.Amount, notification.Reference, notification.TransactionAt, PaymentSources.Webhook, cancellationToken);
            outcome = WebhookOutcomes.Applied;
            detail = confirmed?.Status;
        }
        else
        {
            detail = $"provider code {notification.Code}";
        }

        try
        {
            await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
            {
                unitOfWork.AddWebhookEvent(new PaymentWebhookEvent
                {
                    Id = Guid.NewGuid(), Provider = gateway.ProviderName, PayloadSha256 = hash, OrderCode = notification.OrderCode, ProviderReference = notification.Reference,
                    Amount = notification.Amount, Code = notification.Code, SignatureValid = true, Outcome = outcome, Detail = Truncate(detail, 300), ReceivedAt = now,
                });
                await unitOfWork.SaveChangesAsync(token);
                return (true, true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            return new WebhookResult(true, WebhookOutcomes.Duplicate);   // the same delivery raced in parallel
        }
        return new WebhookResult(true, outcome);
    }

    /// <summary>Asks the provider for the attempt's real state and applies it. Throttled unless <paramref name="force"/>.</summary>
    /// <summary>
    /// One background reconciliation pass: every payable ONLINE attempt whose last check is older than <see cref="PaymentOptions.ReconcileAfterSeconds"/>,
    /// batch by batch (each batch re-reads the due list, so attempts settled meanwhile are skipped). Returns the number of attempts asked.
    /// </summary>
    public async Task<int> ReconcileDueAsync(CancellationToken cancellationToken)
    {
        var asked = 0;
        var batch = Math.Clamp(options.ReconcileBatchSize, 1, 500);
        for (var round = 0; round < Math.Clamp(options.MaxBatchesPerTick, 1, 1000); round++)
        {
            var due = await unitOfWork.ListAttemptsToReconcileAsync(time.GetUtcNow().AddSeconds(-Math.Clamp(options.ReconcileAfterSeconds, 10, 3600)), batch, cancellationToken);
            foreach (var id in due) await ReconcileAsync(id, force: true, cancellationToken);
            asked += due.Count;
            if (due.Count < batch) break;
        }
        return asked;
    }

    public async Task<PaymentAttempt?> ReconcileAsync(Guid attemptId, bool force, CancellationToken cancellationToken)
    {
        var attempt = await unitOfWork.FindAttemptAsync(attemptId, false, cancellationToken);
        // A CASH choice has nothing to ask a gateway about: only a Manager/Admin confirmation settles it.
        if (attempt is null || !PaymentStatuses.Payable.Contains(attempt.Status) || attempt.Provider == PaymentProviders.Cash) return attempt;
        var now = time.GetUtcNow();
        if (!force && attempt.LastReconciledAt is { } last && last > now.AddSeconds(-options.ReconcileThrottleSeconds) && attempt.ExpiresAt > now) return attempt;

        var status = await gateway.GetPaymentStatusAsync(attempt.OrderCode, cancellationToken);
        if (status.CallStatus != GatewayCallStatus.Succeeded)
        {
            // Unknown at the provider and past expiry (for example the link was never created): expire locally.
            if (status.CallStatus == GatewayCallStatus.Rejected && attempt.ExpiresAt <= now)
                return await TransitionAsync(attemptId, PaymentStatuses.Expired, PaymentSources.Reconciliation, "unknown at provider", cancellationToken);
            return attempt;
        }

        return status.Status switch
        {
            "PAID" => await ConfirmAsync(attemptId, status.AmountPaid, status.Reference, status.TransactionAt, PaymentSources.Reconciliation, cancellationToken),
            "CANCELLED" => await TransitionAsync(attemptId, PaymentStatuses.Cancelled, PaymentSources.Reconciliation, "cancelled at provider", cancellationToken),
            "EXPIRED" => await TransitionAsync(attemptId, PaymentStatuses.Expired, PaymentSources.Reconciliation, "expired at provider", cancellationToken),
            "PROCESSING" => await TransitionAsync(attemptId, PaymentStatuses.Processing, PaymentSources.Reconciliation, null, cancellationToken),
            _ when attempt.ExpiresAt <= now => await TransitionAsync(attemptId, PaymentStatuses.Expired, PaymentSources.Reconciliation, "expired", cancellationToken),
            _ => await TouchAsync(attemptId, cancellationToken),
        };
    }

    /// <summary>
    /// Atomic confirmation: amount must equal the attempt amount; the attempt becomes PAID and the
    /// contract SIGNED → PAID in one transaction. If the contract was already paid by another attempt,
    /// this one becomes REFUND_REQUIRED; a wrong amount becomes REVIEW_REQUIRED. Idempotent.
    /// </summary>
    public async Task<PaymentAttempt?> ConfirmAsync(Guid attemptId, long? amountPaid, string? reference, DateTimeOffset? transactionAt, string source, CancellationToken cancellationToken)
    {
        for (var tries = 0; tries < 3; tries++)
        {
            Guid? paidContract = null;
            try
            {
                var confirmed = await unitOfWork.ExecuteInTransactionAsync<PaymentAttempt?>(async token =>
                {
                    paidContract = null;
                    var attempt = await unitOfWork.FindAttemptAsync(attemptId, true, token);
                    if (attempt is null) return (null, false);
                    if (attempt.Status is PaymentStatuses.Paid or PaymentStatuses.RefundRequired or PaymentStatuses.ReviewRequired) return (attempt, false);
                    // Gateway confirmations (webhook / reconciliation) never settle a CASH attempt (order codes are internal; defence in depth).
                    if (attempt.Provider == PaymentProviders.Cash) return (attempt, false);
                    var now = time.GetUtcNow();
                    var from = attempt.Status;
                    attempt.ProviderReference = Truncate(reference, 100);
                    attempt.AmountPaid = amountPaid;
                    attempt.ProviderTransactionAt = transactionAt;
                    attempt.LastReconciledAt = now;
                    attempt.UpdatedAt = now;
                    attempt.Revision++;

                    var items = await unitOfWork.GetItemsAsync(attempt.Id, true, token);
                    if (amountPaid != (long)attempt.Amount)
                    {
                        attempt.Status = PaymentStatuses.ReviewRequired;
                        attempt.FailureReason = $"Amount paid {amountPaid} does not equal {attempt.Amount:0}.";
                        AddEvent(attempt, "AMOUNT_MISMATCH", from, attempt.Status, source, attempt.FailureReason, now);
                    }
                    else if (attempt.ContractId is null)
                    {
                        // After-sales-only checkout: the money is captured; each item is applied (or flagged for a manual refund).
                        attempt.Status = PaymentStatuses.Paid;
                        attempt.PaidAt = now;
                        AddEvent(attempt, "PAID", from, attempt.Status, source, reference, now);
                        await SettleItemsAsync(attempt, items, transactionAt ?? now, now, token);
                    }
                    else if (await unitOfWork.TryMarkContractPaidAsync(attempt.ContractId.Value, now, token))
                    {
                        paidContract = attempt.ContractId;
                        attempt.Status = PaymentStatuses.Paid;
                        attempt.PaidAt = now;
                        AddEvent(attempt, "PAID", from, attempt.Status, source, reference, now);
                        await SettleItemsAsync(attempt, items, transactionAt ?? now, now, token);
                    }
                    else if (await unitOfWork.IsContractPaidAsync(attempt.ContractId.Value, token))
                    {
                        attempt.Status = PaymentStatuses.RefundRequired;
                        attempt.FailureReason = "The contract was already paid by another attempt.";
                        AddEvent(attempt, "DUPLICATE_PAYMENT", from, attempt.Status, source, reference, now);
                    }
                    else
                    {
                        attempt.Status = PaymentStatuses.ReviewRequired;
                        attempt.FailureReason = "Payment received while the contract was not in SIGNED state.";
                        AddEvent(attempt, "CONTRACT_STATE_MISMATCH", from, attempt.Status, source, reference, now);
                    }
                    // Money that was received but applied to nothing is never only a log line: it becomes a refund request for a Manager.
                    if (attempt.Status is PaymentStatuses.RefundRequired or PaymentStatuses.ReviewRequired && amountPaid is > 0 && settlement is not null)
                        await settlement.RecordUnappliedPaymentAsync(attempt, amountPaid.Value, attempt.FailureReason ?? attempt.Status, now, token);
                    await unitOfWork.SaveChangesAsync(token);
                    if (attempt.Status != PaymentStatuses.Paid)
                        logger.LogWarning("Payment attempt {AttemptId} needs manual handling: {Status}.", attempt.Id, attempt.Status);
                    return (attempt, true);
                }, cancellationToken);
                if (paidContract is { } contractId) await FollowUpPaidAsync(contractId, cancellationToken);
                return confirmed;
            }
            catch (PersistenceConflictException)
            {
                // A concurrent confirmation (webhook vs reconciliation) won; re-read and answer idempotently.
            }
        }
        return await unitOfWork.FindAttemptAsync(attemptId, false, cancellationToken);
    }

    /// <summary>The contract line is settled by the contract transition above; every other line goes to the after-sales settlement.</summary>
    private async Task SettleItemsAsync(PaymentAttempt attempt, IReadOnlyList<PaymentAttemptItem> items, DateTimeOffset providerPaidAt, DateTimeOffset now,
        CancellationToken token)
    {
        foreach (var contractLine in items.Where(i => i.ItemType == PaymentItemTypes.Contract)) contractLine.Status = PaymentItemStatuses.Settled;
        var serviceLines = items.Where(i => i.ItemType != PaymentItemTypes.Contract).ToList();
        if (serviceLines.Count == 0) return;
        if (settlement is null)
        {
            foreach (var line in serviceLines)
            {
                line.Status = PaymentItemStatuses.ReviewRequired;
                line.Note = "After-sales settlement is not available.";
            }
            return;
        }
        await settlement.SettleAsync(attempt, serviceLines, providerPaidAt, now, token);
    }

    private async Task<PaymentAttempt?> TransitionAsync(Guid attemptId, string to, string source, string? detail, CancellationToken cancellationToken)
    {
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync<PaymentAttempt?>(async token =>
            {
                var attempt = await unitOfWork.FindAttemptAsync(attemptId, true, token);
                if (attempt is null || !PaymentStatuses.Payable.Contains(attempt.Status) || attempt.Status == to) return (attempt, false);
                var now = time.GetUtcNow();
                AddEvent(attempt, "STATUS_CHANGED", attempt.Status, to, source, detail, now);
                attempt.Status = to;
                attempt.LastReconciledAt = now;
                attempt.UpdatedAt = now;
                attempt.Revision++;
                await unitOfWork.SaveChangesAsync(token);
                return (attempt, true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            return await unitOfWork.FindAttemptAsync(attemptId, false, cancellationToken);
        }
    }

    private async Task<PaymentAttempt?> TouchAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync<PaymentAttempt?>(async token =>
            {
                var attempt = await unitOfWork.FindAttemptAsync(attemptId, true, token);
                if (attempt is null) return (null, false);
                attempt.LastReconciledAt = time.GetUtcNow();
                attempt.Revision++;
                await unitOfWork.SaveChangesAsync(token);
                return (attempt, true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            return await unitOfWork.FindAttemptAsync(attemptId, false, cancellationToken);
        }
    }

    private async Task<ContractPaymentView> ViewForContractAsync(Guid contractId, bool includeHistory, CancellationToken cancellationToken)
    {
        var contract = (await unitOfWork.GetContractAsync(contractId, cancellationToken))!;
        var attempts = (await unitOfWork.ListAttemptsAsync(contractId, cancellationToken)).OrderByDescending(a => a.CreatedAt)
            .Select(a => View(a, staff: includeHistory)).ToList();
        var current = attempts.FirstOrDefault(a => a.Status == PaymentStatuses.Paid) ?? attempts.FirstOrDefault(a => PaymentStatuses.Payable.Contains(a.Status));
        var history = includeHistory
            ? (await unitOfWork.GetEventsAsync(contractId, cancellationToken)).Select(e => new PaymentEventView(e.PaymentAttemptId, e.EventType, e.FromStatus, e.ToStatus, e.Source, e.Detail, e.CreatedAt)).ToList()
            : null;
        var state = contract.Status == "PAID" ? ContractPaymentStates.Paid
            : contract.Status != "SIGNED" || contract.PaymentPlan is not null ? ContractPaymentStates.NotPayable
            : current is { Status: not PaymentStatuses.Paid } live ? (live.Method == PaymentProviders.Cash ? ContractPaymentStates.AwaitingCashConfirmation : ContractPaymentStates.OnlineInProgress)
            : ContractPaymentStates.AwaitingPayment;
        return new ContractPaymentView(contractId, contract.Status, contract.TotalAmount, contract.Currency, current, attempts, history, state);
    }

    private void AddEvent(PaymentAttempt a, string type, string? from, string? to, string source, string? detail, DateTimeOffset now)
        => unitOfWork.AddEvent(new PaymentEvent { Id = Guid.NewGuid(), PaymentAttemptId = a.Id, ContractId = a.ContractId, EventType = type, FromStatus = from,
            ToStatus = to, Source = source, Detail = Truncate(detail, 300), CreatedAt = now });

    public static PaymentAttemptView View(PaymentAttempt a) => View(a, staff: false);

    /// <param name="staff">Adds who recorded a cash payment, its evidence file and note.</param>
    public static PaymentAttemptView View(PaymentAttempt a, bool staff)
    {
        var cash = a.Provider == PaymentProviders.Cash;
        return new(a.Id, a.ContractId, a.OrderCode, a.Amount, a.Currency, a.Status,
            PaymentStatuses.Payable.Contains(a.Status) ? a.CheckoutUrl : null, PaymentStatuses.Payable.Contains(a.Status) ? a.QrCode : null,
            cash ? null : a.ExpiresAt, a.PaidAt, a.ProviderReference, a.FailureReason, a.CreatedAt, cash ? PaymentProviders.Cash : "ONLINE", a.AmountPaid,
            staff ? a.ConfirmedByUserId : null, staff ? a.EvidenceFileId : null, staff ? a.ConfirmationNote : null);
    }

    /// <summary>Positive, below 2^53 (PayOS limit), unique by index; time-prefixed with random low digits.</summary>
    private static long NewOrderCode(DateTimeOffset now) => (now.ToUnixTimeSeconds() % 1_000_000_000L) * 10_000L + RandomNumberGenerator.GetInt32(0, 10_000);

    private void AssignOrderCode(PaymentAttempt attempt, DateTimeOffset now)
    {
        attempt.OrderCode = (orderCodes ?? NewOrderCode)(now);
        attempt.Description = $"SS{attempt.OrderCode % 10_000_000:D7}";
    }

    /// <summary>The new order code repeated an existing one (PostgreSQL index name, or SQLite's "table.column" text).</summary>
    private static bool IsOrderCodeCollision(PersistenceConflictException ex)
        => ex.Kind == PersistenceConflictException.UniqueViolation && ex.Constraint is "ux_payment_attempt_order_code" or "payment_attempt.order_code";

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
