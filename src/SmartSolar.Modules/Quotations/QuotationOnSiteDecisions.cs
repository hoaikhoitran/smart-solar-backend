using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.Quotations;

/// <summary>What the customer told the technician on site about the quotation shown on the technician's device.</summary>
public static class OnSiteDecisions
{
    /// <summary>The customer agreed: the quotation is ACCEPTED (prices frozen) and the technician may open the payment.</summary>
    public const string Agreed = "AGREED";
    /// <summary>The customer did not agree: the quotation is REJECTED; nothing is charged.</summary>
    public const string Declined = "DECLINED";
    /// <summary>The customer wants changes: the quotation is REVISION_REQUESTED and the technician builds a new version from it.</summary>
    public const string RevisionNeeded = "REVISION_NEEDED";

    public static readonly IReadOnlyList<string> All = [Agreed, Declined, RevisionNeeded];
}

/// <param name="Decision"><see cref="OnSiteDecisions"/>.</param>
/// <param name="ExpectedRevision">The quotation revision the technician showed (stale → 409).</param>
/// <param name="Note">What was discussed (required for DECLINED and REVISION_NEEDED, ≤ 1000).</param>
public sealed record OnSiteDecisionInput(string? Decision, int ExpectedRevision, string? Note);

/// <summary>
/// Technician-led on-site sales (2026-11-18). The quotation is not sent to the customer's app: the surveying technician finalizes it (issue =
/// prices frozen for presentation), shows it on their own device, discusses it with the customer in person and records the outcome with their OWN
/// account. The recorded actor is always the technician (the customer's identity is never used), with time, version, outcome and note. Rules:
/// <list type="bullet">
/// <item>Only the technician who completed the request's on-site survey records it; anyone else gets 404.</item>
/// <item>AGREED needs an ISSUED quotation of the current version: not expired, survey not amended since, request open. The agreed quotation is
/// immutable; any later change is a new version.</item>
/// <item>DECLINED / REVISION_NEEDED close an ISSUED quotation, or withdraw an AGREED one whose payment has not started (no live or verified
/// payment, no contract): the customer changed their mind before paying. Nothing is charged.</item>
/// <item>30/70 plan (2026-11-19): AGREED also creates the contract (DRAFT, frozen content, deposit 30% + balance 70%) in the same transaction, so the
/// customer can read it before paying the deposit; a retried AGREED returns the same contract. Withdrawing before the deposit discards that contract
/// (kept as history); after the deposit was verified (or while its payment is open) the agreement can no longer be withdrawn on site.</item>
/// </list>
/// </summary>
public sealed partial class QuotationHandlers
{
    public async Task<OperationResult<QuotationView>> RecordOnSiteDecisionAsync(Guid technicianUserId, Guid quotationId, OnSiteDecisionInput input,
        CancellationToken cancellationToken)
    {
        var decision = input.Decision?.Trim().ToUpperInvariant();
        var note = Trim(input.Note, 1000);
        var errors = new Dictionary<string, string[]>();
        if (decision is null || !OnSiteDecisions.All.Contains(decision)) errors["decision"] = [$"Must be one of {string.Join(", ", OnSiteDecisions.All)}."];
        if (decision is OnSiteDecisions.Declined or OnSiteDecisions.RevisionNeeded && note is null) errors["note"] = ["Record what the customer said (at most 1000 characters)."];
        if (input.Note is { Length: > 1000 }) errors["note"] = ["At most 1000 characters."];
        if (errors.Count > 0) return Invalid(errors);

        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        // Any origin: an ISSUED legacy (Sales / automatic) quotation that waited for the customer's app is now decided on site as well.
        if (quotation is null) return NotFound();
        var (_, _, error) = await AssignedSurveyAsync(technicianUserId, quotation.SurveyRequestId, cancellationToken);
        if (error is not null) return error.ErrorCode == QuotationErrorCodes.NotAssignedTechnician ? NotFound() : error;

        Guid? contractId = null;
        var recorded = await RunAsync(quotationId, async (q, _, now, token) =>
        {
            // A retried AGREED on the agreed quotation (lost response, double tap): the same contract, nothing else changes.
            if (decision == OnSiteDecisions.Agreed && q.Status == QuotationStatuses.Accepted && agreedContracts is not null
                && await agreedContracts.FindAgreementContractAsync(q.Id, token) is { } same)
            {
                contractId = same;
                return OperationResult<bool>.Ok(false);
            }
            if (q.Revision != input.ExpectedRevision) return Stale(q);
            if (IsClosedRequest((await unitOfWork.GetSourceAsync(q.SurveyRequestId, token))?.SurveyRequestStatus)) return RequestClosedError<bool>();
            var detail = $"on site by technician; v{q.Version}; total={q.TotalAmount:0} {q.Currency}" + (note is null ? "" : $"; note: {Trim(note, 400)}");
            switch (decision)
            {
                case OnSiteDecisions.Agreed:
                    if (q.Status == QuotationStatuses.Accepted)
                    {
                        // Cutover (2026-11-19): agreed before the 30/70 release and never paid (no contract, no payment open or verified) — the 100%
                        // quotation payment is retired, so confirming the agreement again creates the 30/70 contract. Same validity rules as AGREED.
                        if (agreedContracts is null || await unitOfWork.HasPaymentInProgressOrPaidAsync(q.Id, token) || await unitOfWork.HasLiveContractAsync(q.Id, token))
                            return OperationResult<bool>.Fail(QuotationErrorCodes.AlreadyAccepted, "This quotation was already agreed.");
                        if (q.ValidUntil is not { } stillValid || stillValid <= now)
                            return OperationResult<bool>.Fail(QuotationErrorCodes.Expired, "This quotation has expired; create a new version with current catalog prices.");
                        if (SurveyChangedError(q, await unitOfWork.GetSurveyVersionAsync(q.SurveyRequestId, token)) is { } amended) return amended;
                        var cutover = await agreedContracts.CreateOnAgreementAsync(q.Id, technicianUserId, now, token);
                        if (cutover.ErrorCode is not null) return OperationResult<bool>.Fail(cutover.ErrorCode, cutover.Message ?? "The contract could not be created.");
                        contractId = cutover.ContractId;
                        AddEvent(q.Id, QuotationEventTypes.AgreedOnSite, technicianUserId, $"{detail}; agreement re-confirmed for the 30/70 plan (agreed before the release)", now);
                        return OperationResult<bool>.Ok(true);
                    }
                    if (q.Status != QuotationStatuses.Issued)
                        return OperationResult<bool>.Fail(QuotationErrorCodes.NotIssued, $"Only a finalized (ISSUED) quotation can be agreed; this one is {q.Status}.");
                    if (q.ValidUntil is not { } validUntil || validUntil <= now)
                        return OperationResult<bool>.Fail(QuotationErrorCodes.Expired, "This quotation has expired; create a new version with current catalog prices.");
                    if (SurveyChangedError(q, await unitOfWork.GetSurveyVersionAsync(q.SurveyRequestId, token)) is { } changed) return changed;
                    q.Status = QuotationStatuses.Accepted;
                    q.AcceptedAt = now;
                    AddEvent(q.Id, QuotationEventTypes.AgreedOnSite, technicianUserId, detail, now);
                    if (agreedContracts is not null)
                    {
                        var contract = await agreedContracts.CreateOnAgreementAsync(q.Id, technicianUserId, now, token);
                        if (contract.ErrorCode is not null) return OperationResult<bool>.Fail(contract.ErrorCode, contract.Message ?? "The contract could not be created.");
                        contractId = contract.ContractId;
                    }
                    return OperationResult<bool>.Ok(true);

                default:
                    if (q.Status == QuotationStatuses.Accepted)
                    {
                        // The customer changed their mind before paying. Never after money moved or a contract exists.
                        if (agreedContracts is not null && await agreedContracts.FindAgreementContractAsync(q.Id, token) is not null)
                        {
                            // 30/70 contract still before its deposit: discarded (history kept). Refused once the deposit is open or verified.
                            if (await agreedContracts.ReleaseOnWithdrawalAsync(q.Id, technicianUserId, note ?? decision!, now, token) is { } refused)
                                return OperationResult<bool>.Fail(QuotationErrorCodes.PaymentStarted, refused);
                        }
                        else if (await unitOfWork.HasPaymentInProgressOrPaidAsync(q.Id, token) || await unitOfWork.HasLiveContractAsync(q.Id, token))
                            return OperationResult<bool>.Fail(QuotationErrorCodes.PaymentStarted,
                                "A payment of this quotation is in progress or verified; cancel the open payment first (a verified one is final).");
                    }
                    else if (q.Status != QuotationStatuses.Issued)
                    {
                        return OperationResult<bool>.Fail(QuotationErrorCodes.NotIssued, $"This quotation is {q.Status}; nothing can be recorded on it.");
                    }
                    if (decision == OnSiteDecisions.Declined)
                    {
                        q.Status = QuotationStatuses.Rejected;
                        q.ClosedAt = now;
                        q.ClosedByUserId = technicianUserId;
                        q.CloseReason = Trim($"DECLINED_ON_SITE: {note}", 1000);
                        AddEvent(q.Id, QuotationEventTypes.DeclinedOnSite, technicianUserId, detail, now);
                    }
                    else
                    {
                        q.Status = QuotationStatuses.RevisionRequested;
                        q.RevisionRequestedAt = now;
                        q.RevisionReason = note;
                        AddEvent(q.Id, QuotationEventTypes.RevisionRequestedOnSite, technicianUserId, detail, now);
                    }
                    return OperationResult<bool>.Ok(true);
            }
        }, cancellationToken, lockSurveyRequestId: quotation.SurveyRequestId);
        if (!recorded.Succeeded || agreedContracts is null) return recorded;
        contractId ??= await agreedContracts.FindAgreementContractAsync(quotationId, cancellationToken);
        return OperationResult<QuotationView>.Ok(recorded.Value! with { ContractId = contractId });
    }
}

/// <param name="ContractId">The payment-plan contract of the agreement (null on error).</param>
public sealed record AgreementContractResult(Guid? ContractId, string? ErrorCode, string? Message);

/// <summary>
/// Contract of an on-site agreement (30/70 plan, 2026-11-19), implemented by the contracts module. Called INSIDE the decision transaction, after the
/// survey-request lock, so the quotation's ACCEPTED status and its contract commit together (or not at all).
/// </summary>
public interface IAgreedQuotationContracts
{
    /// <summary>Creates the contract and its installments once; a retry returns the same contract.</summary>
    Task<AgreementContractResult> CreateOnAgreementAsync(Guid quotationId, Guid technicianUserId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<Guid?> FindAgreementContractAsync(Guid quotationId, CancellationToken cancellationToken);

    /// <summary>Withdrawal before the deposit: discards the contract. Null = done (or nothing to do); otherwise why it is refused.</summary>
    Task<string?> ReleaseOnWithdrawalAsync(Guid quotationId, Guid technicianUserId, string reason, DateTimeOffset now, CancellationToken cancellationToken);
}
