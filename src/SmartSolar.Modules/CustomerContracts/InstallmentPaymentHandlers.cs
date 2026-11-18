using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Modules.CustomerContracts;

/// <summary>Derived payment state of one installment for the FE (the installment status and its attempts are the source).</summary>
public static class InstallmentPaymentStates
{
    /// <summary>Not payable now (balance before acceptance, cancelled, contract not in the right state).</summary>
    public const string NotPayable = "NOT_PAYABLE";
    public const string AwaitingPayment = "AWAITING_PAYMENT";
    public const string OnlineInProgress = "ONLINE_IN_PROGRESS";
    /// <summary>Cash chosen; a Manager/Admin must record the receipt (<c>POST /api/manager/payments/{attemptId}/cash/confirm</c>). Not paid yet.</summary>
    public const string AwaitingCashConfirmation = "AWAITING_CASH_CONFIRMATION";
    public const string Paid = "PAID";
}

/// <summary>Who opens an installment payment.</summary>
public enum InstallmentPayer
{
    /// <summary>The technician assigned to the on-site sale (the request's surveying technician): QR shown on the technician's device, or cash.</summary>
    Technician = 1,
    /// <summary>The contract's customer in the app.</summary>
    Customer = 2,
    /// <summary>Sales / Technician / Manager / Admin reading the state (read-all; never opens or cancels a payment).</summary>
    Staff = 3,
}

/// <param name="Overdue">DUE and past its deadline (derived).</param>
public sealed record InstallmentPaymentView(Guid ContractId, string ContractNumber, Guid InstallmentId, int Sequence, string Kind, decimal Amount, string Currency,
    string InstallmentStatus, DateTimeOffset? DueAt, bool Overdue, DateTimeOffset? PaidAt, bool PaidLate, string PaymentState, PaymentAttemptView? Current,
    IReadOnlyList<PaymentAttemptView> Attempts);

/// <summary>
/// Opening, reading and cancelling the payment of one installment of a 30/70 contract (2026-11-19):
/// <list type="bullet">
/// <item>Deposit (sequence 1): payable while the contract is DRAFT (agreed, not discarded). Balance (sequence 2): payable once the customer accepted
/// the installation (DUE) and the contract is SIGNED; late payment is accepted and flagged.</item>
/// <item>ONLINE = PayOS QR / link; only the verified webhook or a server-to-server status query marks it paid. CASH = a pending choice; only a
/// Manager/Admin records the receipt. Neither the technician nor Sales ever marks money as received.</item>
/// <item>One live payment per installment (contract row lock + check in the creating transaction); the amount is exactly the installment amount
/// (the balance can never be paid during the deposit stage and vice versa).</item>
/// </list>
/// </summary>
public sealed class InstallmentPaymentHandlers(IContractUnitOfWork contracts, PaymentService payments, PaymentOptions paymentOptions, TimeProvider time)
{
    public async Task<OperationResult<InstallmentPaymentView>> StartAsync(InstallmentPayer payer, Guid actorUserId, Guid contractId, int sequence, string? method,
        CancellationToken cancellationToken)
    {
        if (payer == InstallmentPayer.Staff) throw new ArgumentException("Staff never open a payment.", nameof(payer));
        var chosen = method?.Trim().ToUpperInvariant();
        if (chosen is not (PaymentMethods.Online or PaymentMethods.Cash))
            return OperationResult<InstallmentPaymentView>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { ["method"] = ["Must be ONLINE or CASH."] });
        var (contract, installment, error) = await ResolveAsync(payer, actorUserId, contractId, sequence, cancellationToken);
        if (error is not null) return error;
        var c = contract!;
        var i = installment!;
        if (i.Status == InstallmentStatuses.Paid) return Fail(PaymentPlanErrorCodes.InstallmentPaid, $"Installment {i.Sequence} is already paid.");
        if (PayableProblem(c, i) is { } problem) return Fail(PaymentPlanErrorCodes.InstallmentNotPayable, problem);

        // A payment already open: reconcile an online one first; the same method again returns it (idempotent).
        foreach (var attempt in await payments.ListInstallmentAttemptsAsync(i.Id, cancellationToken))
        {
            var current = PaymentStatuses.Payable.Contains(attempt.Status) && attempt.Provider != PaymentProviders.Cash
                ? await payments.ReconcileAsync(attempt.Id, force: true, cancellationToken) ?? attempt
                : attempt;
            if (!PaymentStatuses.Payable.Contains(current.Status)) continue;
            var view = PaymentService.View(current);
            if (view.Method == chosen) return OperationResult<InstallmentPaymentView>.Ok(await ViewAsync(c.Id, i.Id, cancellationToken));
            return view.Method == PaymentMethods.Cash
                ? Fail(PaymentErrorCodes.CashSelected, "Cash was chosen for this installment. Cancel the cash choice first to show a QR code.")
                : Fail(PaymentErrorCodes.CheckoutInProgress, "An online payment is open for this installment. Cancel it first to take cash.");
        }
        if ((await contracts.FindInstallmentAsync(i.Id, false, cancellationToken))?.Status == InstallmentStatuses.Paid)
            return Fail(PaymentPlanErrorCodes.InstallmentPaid, $"Installment {i.Sequence} is already paid.");

        var item = new CheckoutItem(PaymentItemTypes.ContractInstallment, null, null, i.Amount, null, i.Id);
        async Task<string?> Reserve(Guid attemptId, CancellationToken token)
        {
            // Same transaction as the new attempt: the contract lock serialises concurrent starts, settlements and acceptance of this contract.
            await contracts.LockContractAsync(c.Id, token);
            var freshContract = await contracts.FindAsync(c.Id, false, token);
            var fresh = await contracts.FindInstallmentAsync(i.Id, true, token);
            if (freshContract is null || fresh is null || fresh.Revision != i.Revision) return "The installment changed meanwhile. Reload and try again.";
            if (PayableProblem(freshContract, fresh) is { } changed) return changed;
            if (await contracts.HasPayableInstallmentAttemptAsync(fresh.Id, token)) return "A payment of this installment is already open.";
            fresh.CurrentPaymentAttemptId = attemptId;
            fresh.UpdatedAt = time.GetUtcNow();
            fresh.Revision++;
            return null;
        }

        var source = payer == InstallmentPayer.Technician ? PaymentSources.Technician : PaymentSources.Customer;
        var created = chosen == PaymentMethods.Cash
            ? await payments.CreateCashCheckoutAsync(c.CustomerUserId, null, [item], Reserve, cancellationToken, source)
            : await payments.CreateCheckoutAsync(c.CustomerUserId, null, [item],
                time.GetUtcNow().AddMinutes(Math.Clamp(paymentOptions.LinkExpiryMinutes, 5, 1440)), Reserve, cancellationToken, source);
        if (!created.Succeeded) return created.As<InstallmentPaymentView>();
        return OperationResult<InstallmentPaymentView>.Ok(await ViewAsync(c.Id, i.Id, cancellationToken));
    }

    /// <summary>The installment's payment (an open online link is checked with the provider, throttled).</summary>
    public async Task<OperationResult<InstallmentPaymentView>> GetAsync(InstallmentPayer payer, Guid actorUserId, Guid contractId, int sequence,
        CancellationToken cancellationToken)
    {
        var (contract, installment, error) = await ResolveAsync(payer, actorUserId, contractId, sequence, cancellationToken);
        if (error is not null) return error;
        foreach (var live in (await payments.ListInstallmentAttemptsAsync(installment!.Id, cancellationToken)).Where(a => PaymentStatuses.Payable.Contains(a.Status)))
            await payments.ReconcileAsync(live.Id, force: false, cancellationToken);
        return OperationResult<InstallmentPaymentView>.Ok(await ViewAsync(contract!.Id, installment.Id, cancellationToken));
    }

    /// <summary>Cancels the open payment (switch method / customer stopped). A payment the provider reports paid is confirmed instead.</summary>
    public async Task<OperationResult<InstallmentPaymentView>> CancelAsync(InstallmentPayer payer, Guid actorUserId, Guid contractId, int sequence,
        CancellationToken cancellationToken)
    {
        if (payer == InstallmentPayer.Staff) throw new ArgumentException("Staff never cancel a payment here.", nameof(payer));
        var (contract, installment, error) = await ResolveAsync(payer, actorUserId, contractId, sequence, cancellationToken);
        if (error is not null) return error;
        var live = (await payments.ListInstallmentAttemptsAsync(installment!.Id, cancellationToken)).FirstOrDefault(a => PaymentStatuses.Payable.Contains(a.Status));
        if (live is null) return Fail(PaymentErrorCodes.AttemptNotFound, "There is no open payment for this installment.");
        var ended = await payments.CancelAttemptAsync(live.Id, payer == InstallmentPayer.Technician ? "cancelled on site by the technician" : "cancelled by the customer",
            cancellationToken);
        if (ended?.Status == PaymentStatuses.Paid) return Fail(PaymentErrorCodes.AlreadyPaid, "The payment was completed meanwhile; it cannot be cancelled.");
        return OperationResult<InstallmentPaymentView>.Ok(await ViewAsync(contract!.Id, installment.Id, cancellationToken));
    }

    /// <summary>Why the installment cannot be paid now (null = payable).</summary>
    public static string? PayableProblem(CustomerContract c, ContractInstallment i)
    {
        if (i.Status == InstallmentStatuses.Pending)
            return "The balance becomes payable when the customer accepts the installation.";
        if (i.Status != InstallmentStatuses.Due) return $"Installment {i.Sequence} is {i.Status}; it is not payable.";
        if (i.Kind == InstallmentKinds.Deposit && c.Status != ContractStatuses.Draft)
            return $"The deposit is payable only before the paper signature; the contract is {c.Status}.";
        if (i.Kind == InstallmentKinds.Balance && c.Status != ContractStatuses.Signed)
            return $"The balance is payable once the paper contract is signed and the installation accepted; the contract is {c.Status}.";
        return null;
    }

    private async Task<(CustomerContract? Contract, ContractInstallment? Installment, OperationResult<InstallmentPaymentView>? Error)> ResolveAsync(
        InstallmentPayer payer, Guid actorUserId, Guid contractId, int sequence, CancellationToken cancellationToken)
    {
        var contract = await contracts.FindAsync(contractId, false, cancellationToken);
        var visible = contract is not null && payer switch
        {
            InstallmentPayer.Customer => contract.CustomerUserId == actorUserId && ContractStatuses.IsCustomerVisible(contract),
            InstallmentPayer.Staff => true,
            _ => await contracts.GetSurveyTechnicianAsync(contract.SurveyRequestId, cancellationToken) == actorUserId,
        };
        if (!visible) return (null, null, Fail(ContractErrorCodes.NotFound, "Contract was not found."));
        if (contract!.PaymentPlan is null)
            return (null, null, Fail(PaymentPlanErrorCodes.NotApplicable,
                "This contract is paid in full with the contract payment (previous flow); it has no installments."));
        var installment = (await contracts.GetInstallmentsAsync(contractId, false, cancellationToken)).FirstOrDefault(i => i.Sequence == sequence);
        return installment is null
            ? (null, null, Fail(PaymentPlanErrorCodes.InstallmentNotFound, "Installment was not found."))
            : (contract, installment, null);
    }

    private async Task<InstallmentPaymentView> ViewAsync(Guid contractId, Guid installmentId, CancellationToken cancellationToken)
    {
        var c = (await contracts.FindAsync(contractId, false, cancellationToken))!;
        var i = (await contracts.FindInstallmentAsync(installmentId, false, cancellationToken))!;
        var attempts = await payments.ListInstallmentAttemptsAsync(i.Id, cancellationToken);
        var paid = i.SettledPaymentAttemptId is { } settled ? attempts.FirstOrDefault(a => a.Id == settled) : null;
        var live = attempts.FirstOrDefault(a => PaymentStatuses.Payable.Contains(a.Status));
        var state = i.Status == InstallmentStatuses.Paid ? InstallmentPaymentStates.Paid
            : live is not null ? (live.Provider == PaymentProviders.Cash ? InstallmentPaymentStates.AwaitingCashConfirmation : InstallmentPaymentStates.OnlineInProgress)
            : PayableProblem(c, i) is null ? InstallmentPaymentStates.AwaitingPayment
            : InstallmentPaymentStates.NotPayable;
        var current = paid ?? live;
        return new InstallmentPaymentView(c.Id, c.ContractNumber, i.Id, i.Sequence, i.Kind, i.Amount, c.Currency, i.Status, i.DueAt,
            PaymentPlanCalculator.IsOverdue(i, time.GetUtcNow()), i.PaidAt, i.PaidLate, state, current is null ? null : PaymentService.View(current),
            attempts.Select(a => PaymentService.View(a)).ToList());
    }

    private static OperationResult<InstallmentPaymentView> Fail(string code, string message) => OperationResult<InstallmentPaymentView>.Fail(code, message);
}
