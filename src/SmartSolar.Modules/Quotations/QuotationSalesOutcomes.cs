using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.Quotations;

/// <summary>A survey request that ended without an installation.</summary>
public sealed record SalesOutcomeView(Guid SurveyRequestId, string Status, string ReasonCode, string? Note, DateTimeOffset ClosedAt,
    IReadOnlyList<Guid> CancelledQuotationIds);

public static class SalesOutcomeErrorCodes
{
    public const string RequestNotFound = "SURVEY_REQUEST_NOT_FOUND";
    /// <summary>The request is already withdrawn or closed; terminal requests are never reopened (409).</summary>
    public const string RequestClosed = "SURVEY_REQUEST_CLOSED";
    /// <summary>A contract (not superseded) exists: closing would bypass a binding commitment, payment or refund process (409).</summary>
    public const string ContractExists = "SURVEY_REQUEST_CONTRACT_EXISTS";
    /// <summary>A survey appointment is still open: cancel it first through the scheduling flow (409).</summary>
    public const string AppointmentOpen = "SURVEY_REQUEST_APPOINTMENT_OPEN";
    /// <summary>NOT_FEASIBLE is only a closure reason when the on-site survey concluded NOT_FEASIBLE (409).</summary>
    public const string NotFeasibleUnconfirmed = "SURVEY_REQUEST_NOT_FEASIBLE_UNCONFIRMED";
    /// <summary>A quotation of the request changed while it was being closed (e.g. accepted meanwhile): nothing was closed (409).</summary>
    public const string Conflict = "SURVEY_REQUEST_CLOSURE_CONFLICT";
}

/// <summary>
/// Unsuccessful-sales outcomes (D-03): the customer rejects an issued quotation or withdraws the request; the Sales owner closes the request
/// with a reason. Every outcome records actor, time and reason, keeps the full history, and is terminal: a closed request is never
/// reopened (whether a new request should be started instead is a pending Product Owner decision). A request with a contract is never
/// closed here, so no paid contract, refund obligation or installation project is bypassed.
/// </summary>
public sealed partial class QuotationHandlers
{
    /// <summary>ISSUED → REJECTED by the customer (reason optional). Atomic against a concurrent acceptance: exactly one of them wins.</summary>
    public async Task<OperationResult<QuotationView>> RejectAsync(Guid customerUserId, Guid quotationId, string? reason, CancellationToken cancellationToken)
    {
        if (reason is { Length: > 2000 }) return Invalid(new() { ["reason"] = ["At most 2000 characters."] });
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (quotation is null || quotation.CustomerUserId != customerUserId || quotation.Status == QuotationStatuses.Draft) return NotFound();
        if (quotation.Status == QuotationStatuses.Rejected) return OperationResult<QuotationView>.Ok(await ViewAsync(quotation, false, cancellationToken, forCustomer: true));

        var why = Trim(reason, 2000);
        try
        {
            var outcome = await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                var now = time.GetUtcNow();
                if (!await unitOfWork.TryCloseQuotationAsync(quotationId, QuotationStatuses.Issued, QuotationStatuses.Rejected, customerUserId, why, now, token))
                    return (OperationResult<bool>.Fail(QuotationErrorCodes.NotIssued, "Only an issued quotation can be rejected (it was accepted, revised or closed)."), false);
                AddEvent(quotationId, QuotationEventTypes.Rejected, customerUserId, Trim(why, 500), now);
                await unitOfWork.SaveChangesAsync(token);
                return (OperationResult<bool>.Ok(true), true);
            }, cancellationToken);
            if (!outcome.Succeeded) return outcome.As<QuotationView>();
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.StaleRevision, "The quotation was changed meanwhile. Reload and try again.");
        }
        return OperationResult<QuotationView>.Ok(await ViewAsync((await unitOfWork.FindAsync(quotationId, false, cancellationToken))!, false, cancellationToken, forCustomer: true));
    }

    /// <summary>The customer withdraws their own open request (no contract, no open appointment). Repeating it returns the same outcome.</summary>
    public async Task<OperationResult<SalesOutcomeView>> WithdrawRequestAsync(Guid customerUserId, Guid surveyRequestId, string? reason, CancellationToken cancellationToken)
    {
        if (reason is { Length: > 1000 }) return InvalidOutcome("reason", "At most 1000 characters.");
        var state = await unitOfWork.GetRequestStateAsync(surveyRequestId, cancellationToken);
        if (state is null || state.CustomerUserId != customerUserId) return RequestNotFound();
        return await CloseAsync(state, SurveyRequestStatus.Cancelled, SurveyRequestEventTypes.RequestWithdrawn, customerUserId,
            SurveyRequestClosureReasons.CustomerWithdrew, Trim(reason, 1000), cancellationToken);
    }

    /// <summary>
    /// The Sales owner closes an unsuccessful request with a reason (<see cref="SurveyRequestClosureReasons.SalesClosure"/>; OTHER needs a
    /// note; NOT_FEASIBLE needs a NOT_FEASIBLE survey). Same safety conditions as a withdrawal.
    /// </summary>
    public async Task<OperationResult<SalesOutcomeView>> CloseRequestAsync(Guid salesUserId, Guid surveyRequestId, string? reasonCode, string? note,
        CancellationToken cancellationToken)
    {
        var code = reasonCode?.Trim().ToUpperInvariant();
        if (code is null || !SurveyRequestClosureReasons.SalesClosure.Contains(code))
            return InvalidOutcome("reasonCode", $"Must be one of {string.Join(", ", SurveyRequestClosureReasons.SalesClosure)}.");
        if (note is { Length: > 1000 }) return InvalidOutcome("note", "At most 1000 characters.");
        if (code == SurveyRequestClosureReasons.Other && string.IsNullOrWhiteSpace(note)) return InvalidOutcome("note", "A note is required for OTHER.");
        var state = await unitOfWork.GetRequestStateAsync(surveyRequestId, cancellationToken);
        if (state is null || state.AssignedSaleId != salesUserId) return RequestNotFound();
        if (code == SurveyRequestClosureReasons.NotFeasible && state.InstallationFeasibility != "NOT_FEASIBLE"
            && state.Status is not (nameof(SurveyRequestStatus.Cancelled) or nameof(SurveyRequestStatus.Closed)))
            return OperationResult<SalesOutcomeView>.Fail(SalesOutcomeErrorCodes.NotFeasibleUnconfirmed,
                "The on-site survey did not conclude NOT_FEASIBLE; choose another reason or have the survey amended.");
        return await CloseAsync(state, SurveyRequestStatus.Closed, SurveyRequestEventTypes.RequestClosed, salesUserId, code, Trim(note, 1000), cancellationToken);
    }

    private async Task<OperationResult<SalesOutcomeView>> CloseAsync(SalesRequestState state, SurveyRequestStatus status, string eventType, Guid actor, string code,
        string? note, CancellationToken cancellationToken)
    {
        // Fast path on the pre-read; the same rules are decided again under the request lock below.
        if (Refusal(state) is { } early)
            return early.ErrorCode == AlreadyTerminal ? await ExistingOutcomeAsync(state, status, code, cancellationToken) : early.As<SalesOutcomeView>();

        var cancelled = new List<Guid>();
        var reason = $"{code}{(note is null ? "" : $": {note}")}";
        try
        {
            var outcome = await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                // Same lock as contract and quotation creation, taken first: a contract created meanwhile is seen here (READ COMMITTED reads
                // after the lock), and a contract creation waiting on this lock sees the closed request.
                await unitOfWork.LockSurveyRequestAsync(state.SurveyRequestId, token);
                var locked = await unitOfWork.GetRequestStateAsync(state.SurveyRequestId, token);
                if (locked is null) return (OperationResult<bool>.Fail(SalesOutcomeErrorCodes.RequestNotFound, "Survey request was not found."), false);
                if (Refusal(locked) is { } refused) return (refused, false);
                var now = time.GetUtcNow();
                if (!await unitOfWork.TryCloseRequestAsync(state.SurveyRequestId, status, actor, code, note, now, token))
                    return (OperationResult<bool>.Fail(SalesOutcomeErrorCodes.RequestClosed, "The request was closed meanwhile."), false);
                // Every open quotation ends with the request; rejected and superseded ones stay as they are (history).
                foreach (var q in await unitOfWork.ListBySurveyRequestAsync(state.SurveyRequestId, token))
                {
                    if (QuotationStatuses.IsTerminal(q.Status)) continue;
                    if (!await unitOfWork.TryCloseQuotationAsync(q.Id, q.Status, QuotationStatuses.Cancelled, actor, Trim(reason, 2000), now, token))
                        return (OperationResult<bool>.Fail(SalesOutcomeErrorCodes.Conflict, "A quotation of this request changed meanwhile. Reload and try again."), false);
                    AddEvent(q.Id, QuotationEventTypes.Cancelled, actor, Trim(reason, 500), now);
                    cancelled.Add(q.Id);
                }
                unitOfWork.AddRequestEvent(new SurveyRequestEvent
                {
                    Id = Guid.NewGuid(), SurveyRequestId = state.SurveyRequestId, EventType = eventType, ActorUserId = actor, Reason = Trim(reason, 500), CreatedAt = now,
                });
                await unitOfWork.SaveChangesAsync(token);
                return (OperationResult<bool>.Ok(true), true);
            }, cancellationToken);
            if (outcome.ErrorCode == AlreadyTerminal)
                return await ExistingOutcomeAsync((await unitOfWork.GetRequestStateAsync(state.SurveyRequestId, cancellationToken))!, status, code, cancellationToken);
            if (!outcome.Succeeded) return outcome.As<SalesOutcomeView>();
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<SalesOutcomeView>.Fail(SalesOutcomeErrorCodes.Conflict, "The request changed meanwhile. Reload and try again.");
        }
        var fresh = (await unitOfWork.GetRequestStateAsync(state.SurveyRequestId, cancellationToken))!;
        return await ExistingOutcomeAsync(fresh, status, code, cancellationToken, cancelled);
    }

    private async Task<OperationResult<SalesOutcomeView>> ExistingOutcomeAsync(SalesRequestState state, SurveyRequestStatus expected, string code,
        CancellationToken cancellationToken, IReadOnlyList<Guid>? cancelled = null)
    {
        var closure = await unitOfWork.GetClosureAsync(state.SurveyRequestId, cancellationToken);
        if (state.Status != expected.ToString() || closure?.ReasonCode != code)
            return OperationResult<SalesOutcomeView>.Fail(SalesOutcomeErrorCodes.RequestClosed, "The request is already closed; a closed request is not reopened.",
                new { status = state.Status, reasonCode = closure?.ReasonCode });
        var ids = cancelled ?? (await unitOfWork.ListBySurveyRequestAsync(state.SurveyRequestId, cancellationToken))
            .Where(q => q.Status == QuotationStatuses.Cancelled).Select(q => q.Id).ToList();
        return OperationResult<SalesOutcomeView>.Ok(new SalesOutcomeView(state.SurveyRequestId, state.Status, closure.ReasonCode!, closure.Note, closure.ClosedAt!.Value, ids));
    }

    /// <summary>Internal outcome: the request is already terminal (answered with the stored outcome, or refused if it differs).</summary>
    private const string AlreadyTerminal = "ALREADY_TERMINAL";

    /// <summary>Why the request cannot be withdrawn or closed now (null = it can).</summary>
    private static OperationResult<bool>? Refusal(SalesRequestState state)
    {
        if (state.Status is nameof(SurveyRequestStatus.Cancelled) or nameof(SurveyRequestStatus.Closed))
            return OperationResult<bool>.Fail(AlreadyTerminal, "already terminal");
        if (state.HasLiveContract)
            return OperationResult<bool>.Fail(SalesOutcomeErrorCodes.ContractExists,
                state.LiveContractStatus == CustomerContracts.ContractStatuses.Draft
                    ? "A DRAFT contract exists for this request; it must be discarded with a reason before the request can be closed."
                    : "A contract exists for this request; it cannot be withdrawn or closed here (a signed or paid contract is never cancelled automatically).",
                new { contractStatus = state.LiveContractStatus });
        if (state.HasOpenAppointment)
            return OperationResult<bool>.Fail(SalesOutcomeErrorCodes.AppointmentOpen, "Cancel the open survey appointment first.");
        return null;
    }

    private static OperationResult<SalesOutcomeView> RequestNotFound()
        => OperationResult<SalesOutcomeView>.Fail(SalesOutcomeErrorCodes.RequestNotFound, "Survey request was not found.");

    private static OperationResult<SalesOutcomeView> InvalidOutcome(string field, string message)
        => OperationResult<SalesOutcomeView>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.", new Dictionary<string, string[]> { [field] = [message] });
}
