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
    public string ReturnUrl { get; set; } = "";
    public string CancelUrl { get; set; } = "";
}

public sealed record PaymentAttemptView(Guid PaymentAttemptId, Guid? ContractId, long OrderCode, decimal Amount, string Currency, string Status,
    string? CheckoutUrl, string? QrCode, DateTimeOffset ExpiresAt, DateTimeOffset? PaidAt, string? ProviderReference, string? FailureReason, DateTimeOffset CreatedAt);

public sealed record PaymentEventView(Guid PaymentAttemptId, string EventType, string? FromStatus, string? ToStatus, string Source, string? Detail, DateTimeOffset CreatedAt);

public sealed record ContractPaymentView(Guid ContractId, string ContractStatus, decimal Amount, string Currency, PaymentAttemptView? Current,
    IReadOnlyList<PaymentAttemptView> Attempts, IReadOnlyList<PaymentEventView>? History);

public sealed record WebhookResult(bool Accepted, string Outcome);

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
public sealed class PaymentService(IPaymentUnitOfWork unitOfWork, IPaymentGateway gateway, PaymentOptions options, TimeProvider time, ILogger<PaymentService> logger,
    IPaymentItemSettlement? settlement = null)
{
    public async Task<OperationResult<PaymentAttemptView>> CreateOrGetAttemptAsync(Guid customerUserId, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetContractAsync(contractId, cancellationToken);
        if (contract is null || contract.CustomerUserId != customerUserId)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ContractNotFound, "Contract was not found.");
        if (contract.Status == "PAID") return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This contract is already paid.");
        if (contract.Status != "SIGNED")
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ContractNotSigned, "Payment opens after the signed contract is approved.");
        if (contract.Currency != "VND" || contract.TotalAmount <= 0 || decimal.Truncate(contract.TotalAmount) != contract.TotalAmount || contract.TotalAmount > options.MaxAmountVnd)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AmountNotSupported,
                $"The contract total must be a whole VND amount up to {options.MaxAmountVnd:N0}; it is never split into several payments.");

        // Reuse a live attempt; settle stale ones with the provider first.
        foreach (var attempt in (await unitOfWork.ListAttemptsAsync(contractId, cancellationToken)).Where(a => PaymentStatuses.Payable.Contains(a.Status)))
        {
            if (attempt.ExpiresAt > time.GetUtcNow()) return OperationResult<PaymentAttemptView>.Ok(View(attempt));
            var settled = await ReconcileAsync(attempt.Id, force: true, cancellationToken);
            if (settled?.Status == PaymentStatuses.Paid) return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This contract is already paid.");
            if (settled is not null && PaymentStatuses.Payable.Contains(settled.Status)) return OperationResult<PaymentAttemptView>.Ok(View(settled));
        }

        var now = time.GetUtcNow();
        if (await unitOfWork.CountAttemptsSinceAsync(contractId, now.AddMinutes(-10), cancellationToken) >= options.MaxAttemptsPerTenMinutes)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.TooManyAttempts, "Too many payment attempts for this contract. Try again in a few minutes.");

        var orderCode = NewOrderCode(now);
        var created = new PaymentAttempt
        {
            Id = Guid.NewGuid(), ContractId = contractId, CustomerUserId = customerUserId, Provider = gateway.ProviderName, OrderCode = orderCode,
            Amount = contract.TotalAmount, Currency = "VND", Description = $"SS{orderCode % 10_000_000:D7}", Status = PaymentStatuses.Pending,
            ExpiresAt = now.AddMinutes(Math.Clamp(options.LinkExpiryMinutes, 5, 1440)), CreatedAt = now, UpdatedAt = now,
        };
        try
        {
            await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
            {
                unitOfWork.AddAttempt(created);
                AddEvent(created, "CREATED", null, PaymentStatuses.Pending, PaymentSources.Customer, null, now);
                await unitOfWork.SaveChangesAsync(token);
                return (true, true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            // A concurrent request created the payable attempt first: return it (never two links).
            var live = (await unitOfWork.ListAttemptsAsync(contractId, cancellationToken)).FirstOrDefault(a => PaymentStatuses.Payable.Contains(a.Status));
            return live is not null
                ? OperationResult<PaymentAttemptView>.Ok(View(live))
                : OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This contract is already paid or being paid.");
        }

        var link = await gateway.CreatePaymentLinkAsync(new PaymentLinkRequest(orderCode, (long)contract.TotalAmount, created.Description, options.ReturnUrl,
            options.CancelUrl, created.ExpiresAt, contract.CustomerName, contract.CustomerEmail, contract.CustomerPhone), cancellationToken);
        return await RecordLinkAsync(created.Id, link, cancellationToken);
    }

    /// <summary>
    /// Creates one checkout for after-sales items, optionally together with the customer's SIGNED contract (contract amount kept as
    /// its own CONTRACT line; the contract total itself is never changed). <paramref name="reserveItems"/> runs inside the same
    /// transaction as the new attempt and links the items to it (revision-checked), so a double request cannot create two links for
    /// the same item; it returns an error message to refuse. The link expires at <paramref name="expiresAt"/>.
    /// </summary>
    public async Task<OperationResult<PaymentAttemptView>> CreateCheckoutAsync(Guid customerUserId, Guid? contractId, IReadOnlyList<CheckoutItem> items,
        DateTimeOffset expiresAt, Func<Guid, CancellationToken, Task<string?>> reserveItems, CancellationToken cancellationToken)
    {
        var lines = items.Select(i => new PaymentAttemptItem
        {
            Id = Guid.NewGuid(), ItemType = i.ItemType, SubscriptionId = i.SubscriptionId, QuotationId = i.QuotationId, Amount = i.Amount,
            Status = PaymentItemStatuses.Pending,
        }).ToList();
        PayableContract? contract = null;
        if (contractId is { } id)
        {
            contract = await unitOfWork.GetContractAsync(id, cancellationToken);
            if (contract is null || contract.CustomerUserId != customerUserId)
                return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ContractNotFound, "Contract was not found.");
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
        var orderCode = NewOrderCode(now);
        var created = new PaymentAttempt
        {
            Id = Guid.NewGuid(), ContractId = contractId, CustomerUserId = customerUserId, Provider = gateway.ProviderName, OrderCode = orderCode,
            Amount = total, Currency = "VND", Description = $"SS{orderCode % 10_000_000:D7}", Status = PaymentStatuses.Pending,
            ExpiresAt = expiresAt, CreatedAt = now, UpdatedAt = now,
        };
        string? refused = null;
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
                AddEvent(created, "CREATED", null, PaymentStatuses.Pending, PaymentSources.Customer, string.Join(", ", lines.Select(l => $"{l.ItemType} {l.Amount:0}")), now);
                await unitOfWork.SaveChangesAsync(token);
                return (true, true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            // A concurrent checkout of the same contract or item was created first.
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CheckoutInProgress, "A payment for these items is already in progress.");
        }
        if (refused is not null) return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CheckoutInProgress, refused);

        var link = await gateway.CreatePaymentLinkAsync(new PaymentLinkRequest(orderCode, (long)total, created.Description, options.ReturnUrl,
            options.CancelUrl, created.ExpiresAt, contract?.CustomerName, contract?.CustomerEmail, contract?.CustomerPhone), cancellationToken);
        return await RecordLinkAsync(created.Id, link, cancellationToken);
    }

    /// <summary>
    /// Cancels an after-sales checkout link of the customer (the quotation was rejected or superseded, or the package cancelled). When the
    /// provider reports it already paid, it is confirmed instead (money is never ignored). Returns the attempt as it ends up.
    /// </summary>
    public async Task<PaymentAttempt?> CancelAttemptAsync(Guid attemptId, string reason, CancellationToken cancellationToken)
    {
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

    private async Task<OperationResult<PaymentAttemptView>> RecordLinkAsync(Guid attemptId, PaymentLinkResult link, CancellationToken cancellationToken)
    {
        var result = await unitOfWork.ExecuteInTransactionAsync<PaymentAttempt?>(async token =>
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

        return link.Status switch
        {
            GatewayCallStatus.Succeeded => OperationResult<PaymentAttemptView>.Ok(View(result!)),
            GatewayCallStatus.NotConfigured => OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ProviderNotConfigured, "Online payment is not configured in this environment."),
            _ => OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ProviderError, "The payment provider could not create a payment link. Please try again."),
        };
    }

    /// <summary>Customer cancels the current link (if the provider reports it already paid, it is confirmed instead).</summary>
    public async Task<OperationResult<PaymentAttemptView>> CancelCurrentAsync(Guid customerUserId, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.GetContractAsync(contractId, cancellationToken);
        if (contract is null || contract.CustomerUserId != customerUserId)
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.ContractNotFound, "Contract was not found.");
        var live = (await unitOfWork.ListAttemptsAsync(contractId, cancellationToken)).FirstOrDefault(a => PaymentStatuses.Payable.Contains(a.Status));
        if (live is null) return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AttemptNotFound, "There is no payment in progress.");

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
    public async Task<PaymentAttempt?> ReconcileAsync(Guid attemptId, bool force, CancellationToken cancellationToken)
    {
        var attempt = await unitOfWork.FindAttemptAsync(attemptId, false, cancellationToken);
        if (attempt is null || !PaymentStatuses.Payable.Contains(attempt.Status)) return attempt;
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
            try
            {
                return await unitOfWork.ExecuteInTransactionAsync<PaymentAttempt?>(async token =>
                {
                    var attempt = await unitOfWork.FindAttemptAsync(attemptId, true, token);
                    if (attempt is null) return (null, false);
                    if (attempt.Status is PaymentStatuses.Paid or PaymentStatuses.RefundRequired or PaymentStatuses.ReviewRequired) return (attempt, false);
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
                    await unitOfWork.SaveChangesAsync(token);
                    if (attempt.Status != PaymentStatuses.Paid)
                        logger.LogWarning("Payment attempt {AttemptId} needs manual handling: {Status}.", attempt.Id, attempt.Status);
                    return (attempt, true);
                }, cancellationToken);
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
        var attempts = (await unitOfWork.ListAttemptsAsync(contractId, cancellationToken)).OrderByDescending(a => a.CreatedAt).Select(View).ToList();
        var current = attempts.FirstOrDefault(a => a.Status == PaymentStatuses.Paid) ?? attempts.FirstOrDefault(a => PaymentStatuses.Payable.Contains(a.Status));
        var history = includeHistory
            ? (await unitOfWork.GetEventsAsync(contractId, cancellationToken)).Select(e => new PaymentEventView(e.PaymentAttemptId, e.EventType, e.FromStatus, e.ToStatus, e.Source, e.Detail, e.CreatedAt)).ToList()
            : null;
        return new ContractPaymentView(contractId, contract.Status, contract.TotalAmount, contract.Currency, current, attempts, history);
    }

    private void AddEvent(PaymentAttempt a, string type, string? from, string? to, string source, string? detail, DateTimeOffset now)
        => unitOfWork.AddEvent(new PaymentEvent { Id = Guid.NewGuid(), PaymentAttemptId = a.Id, ContractId = a.ContractId, EventType = type, FromStatus = from,
            ToStatus = to, Source = source, Detail = Truncate(detail, 300), CreatedAt = now });

    public static PaymentAttemptView View(PaymentAttempt a) => new(a.Id, a.ContractId, a.OrderCode, a.Amount, a.Currency, a.Status,
        PaymentStatuses.Payable.Contains(a.Status) ? a.CheckoutUrl : null, PaymentStatuses.Payable.Contains(a.Status) ? a.QrCode : null,
        a.ExpiresAt, a.PaidAt, a.ProviderReference, a.FailureReason, a.CreatedAt);

    /// <summary>Positive, below 2^53 (PayOS limit), unique by index; time-prefixed with random low digits.</summary>
    private static long NewOrderCode(DateTimeOffset now) => (now.ToUnixTimeSeconds() % 1_000_000_000L) * 10_000L + RandomNumberGenerator.GetInt32(0, 10_000);

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
