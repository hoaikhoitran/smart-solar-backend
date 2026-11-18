using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;

namespace SmartSolar.Modules.CustomerContracts;

public sealed record ContractDocumentView(Guid FileId, string? Sha256, DateTimeOffset? ConfirmedAt);

public sealed record SignatureSubmissionView(Guid SubmissionId, Guid FileId, string FileSha256, string OfficialDocumentSha256, DateTimeOffset SubmittedAt,
    string Outcome, DateTimeOffset? ReviewedAt, string? RejectionReason);

public sealed record ContractEventView(string EventType, string? FromStatus, string? ToStatus, Guid ActorUserId, string? Detail, DateTimeOffset CreatedAt);

public sealed record ContractView(
    Guid ContractId, string ContractNumber, int Version, Guid? PreviousContractId, Guid QuotationId, int QuotationVersion, Guid SurveyRequestId,
    string Status, string Currency, decimal TotalAmount, JsonElement PricingSnapshot, JsonElement CustomerSnapshot, string? Terms, string? CanonicalHash,
    ContractDocumentView? OfficialDocument, ContractDocumentView? SignedDocument, string? RejectionReason, DateTimeOffset? IssuedAt,
    DateTimeOffset? PublishedAt, DateTimeOffset? SignedAt, DateTimeOffset? PaidAt, int Revision,
    IReadOnlyList<SignatureSubmissionView>? Submissions, IReadOnlyList<ContractEventView>? Events, int? WarrantyMonths = null,
    ContractDiscardView? Discard = null, bool PaymentCompleted = false, Guid? PaymentAttemptId = null, DateTimeOffset? PaymentConfirmedAt = null,
    ContractPaymentPlanView? PaymentPlan = null);

/// <summary>Staff-only record of a discarded draft (customers never see drafts, discarded or not).</summary>
public sealed record ContractDiscardView(DateTimeOffset DiscardedAt, Guid DiscardedByUserId, string Reason);

public enum ReviewDecision { Approve = 1, Reject = 2 }

/// <summary>
/// Contract lifecycle: DRAFT (created automatically when the customer accepts the quotation) → ISSUED (content frozen, canonical hash
/// fixed) → AWAITING_SIGNATURE (official PDF verified and confirmed against the hash) → UNDER_REVIEW (customer submitted a hand-signed
/// scan of exactly that PDF) → SIGNED (signature confirmed) → PAID (payment module: online or confirmed cash).
/// Rejection: UNDER_REVIEW → SIGNATURE_REJECTED, after which the customer submits again. Amendments
/// create a new version that supersedes the previous one when issued; PAID contracts are final.
/// Since 2026-11-17 every staff step is a Manager/Admin step (the API restricts the roles); Sales is consult-only and has read access to the
/// contracts of the requests it owns. Every transition is a conditional update with an audit event.
/// </summary>
/// <param name="paidFollowUp">Runs after a review made a prepaid contract PAID (the technician-agreed installation schedule is confirmed there); the
/// installation sweep retries it when it fails.</param>
public sealed class ContractHandlers(IContractUnitOfWork unitOfWork, FileService files, TimeProvider time, Installations.IContractPaidFollowUp? paidFollowUp = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ Contract from a PAID quotation (technician-led flow, 2026-11-18)

    /// <summary>
    /// Settles a verified payment of an installation quotation (PayOS webhook / reconciliation or a Manager's cash confirmation), inside the payment
    /// confirmation transaction: the contract DRAFT is created ONCE from the paid quotation's snapshot (lines, totals, customer data, default
    /// installation warranty) and the payment attempt is linked to it, so the contract is born paid and is never charged again. Takes the
    /// survey-request lock first (same order as closure and manual creation). Returns null when settled, otherwise why the money cannot be applied
    /// (nothing is written; the caller flags the line for a manual refund). With <paramref name="checkOnly"/> it only answers whether it could settle.
    /// </summary>
    public async Task<string?> CreateForPaidQuotationAsync(Guid quotationId, Payments.PaymentAttempt attempt, decimal amount, DateTimeOffset providerPaidAt,
        DateTimeOffset now, CancellationToken cancellationToken, bool checkOnly = false)
    {
        var quotation = await unitOfWork.GetQuotationAsync(quotationId, cancellationToken);
        if (quotation is null) return "The installation quotation was not found.";
        await unitOfWork.LockSurveyRequestAsync(quotation.SurveyRequestId, cancellationToken);
        if (await unitOfWork.IsSurveyRequestClosedAsync(quotation.SurveyRequestId, cancellationToken)) return "The survey request was withdrawn or closed.";
        quotation = (await unitOfWork.GetQuotationAsync(quotationId, cancellationToken))!;
        if (quotation.Status != "ACCEPTED") return $"The quotation is {quotation.Status}; only a quotation agreed on site can be paid.";
        if (quotation.QuotationSurveyVersion != quotation.CurrentSurveyVersion) return "The on-site survey was amended after this quotation was agreed.";
        if (quotation.CustomerUserId != attempt.CustomerUserId) return "The payment was made for another customer.";
        if (quotation.TotalAmount != amount) return $"The paid amount {amount:0} does not equal the quotation total {quotation.TotalAmount:0}.";
        if (quotation.ValidUntil is { } validUntil && validUntil < providerPaidAt) return "The quotation expired before it was paid.";
        if ((await unitOfWork.ListByQuotationAsync(quotationId, cancellationToken)).Count > 0)
            return "A contract already exists for this quotation (it was paid already, or is a contract of the previous flow).";
        if (checkOnly) return null;

        var (_, defaultMonths) = await unitOfWork.GetInstallationWarrantyOptionsAsync(cancellationToken);
        var contract = BuildFromQuotation(quotation, terms: null, defaultMonths, now);
        unitOfWork.AddContract(contract);
        var method = attempt.Provider == Payments.PaymentProviders.Cash ? "cash confirmed by staff" : "online payment verified by the provider";
        Event(contract, "CREATED", null, ContractStatuses.Draft, attempt.ConfirmedByUserId ?? quotation.CustomerUserId,
            $"from paid quotation {quotation.QuoteNumber} v{quotation.QuotationVersion} (payment {attempt.OrderCode}, {method})", now);
        unitOfWork.AddEvent(new ContractEvent { Id = Guid.NewGuid(), ContractId = contract.Id, EventType = "PAID_BEFORE_CONTRACT",
            ActorUserId = attempt.ConfirmedByUserId ?? quotation.CustomerUserId, Detail = $"payment {attempt.OrderCode}: {amount:0} VND, {method}; Manager/Admin prepare the contract document",
            CreatedAt = now });
        attempt.ContractId = contract.Id;
        unitOfWork.AddNotification(quotation.CustomerUserId, CustomerNotificationTypes.ContractCreatedAfterPayment,
            $"SmartSolar đã nhận đủ {amount:N0} VND cho báo giá {quotation.QuoteNumber}. Hợp đồng {contract.ContractNumber} đã được lập từ báo giá đã thanh toán; bạn có thể theo dõi tiến độ trong ứng dụng.",
            now);
        return null;
    }

    /// <summary>The contract DRAFT built from a quotation snapshot (pricing, customer, terms, warranty months). Pure factory, nothing stored.</summary>
    public static CustomerContract BuildFromQuotation(AcceptedQuotationSource quotation, string? terms, int? warrantyMonths, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), ContractNumber = NewNumber(now), Version = 1, QuotationId = quotation.QuotationId, QuotationVersion = quotation.QuotationVersion,
        SurveyRequestId = quotation.SurveyRequestId, CustomerUserId = quotation.CustomerUserId, SalesUserId = null, Status = ContractStatuses.Draft,
        Currency = quotation.Currency, TotalAmount = quotation.TotalAmount, PricingSnapshot = quotation.PricingSnapshotJson,
        CustomerSnapshot = quotation.CustomerSnapshotJson ?? "{}", Terms = Trim(terms, 8000) ?? quotation.Terms, WarrantyMonths = warrantyMonths,
        CreatedAt = now, UpdatedAt = now,
    };

    // ------------------------------------------------------------------ Manager / Admin

    /// <summary>
    /// Manual creation (recovery) for an ACCEPTED quotation that has a settled payment but no contract. Since 2026-11-18 a contract is created only
    /// after the quotation was paid (normally by the payment settlement itself): without a settled payment this answers 409 CONTRACT_PAYMENT_REQUIRED.
    /// </summary>
    /// <param name="warrantyMonths">Installation warranty from the Admin's active options; omitted = the default option. A draft without months cannot be issued.</param>
    public async Task<OperationResult<ContractView>> CreateAsync(Guid staffUserId, Guid quotationId, string? terms, CancellationToken cancellationToken,
        int? warrantyMonths = null)
    {
        var quotation = await unitOfWork.GetQuotationAsync(quotationId, cancellationToken);
        if (quotation is null)
            return OperationResult<ContractView>.Fail(ContractErrorCodes.NotFound, "Quotation was not found.");
        if (quotation.Status != "ACCEPTED")
            return OperationResult<ContractView>.Fail(ContractErrorCodes.QuotationNotAccepted, "Contracts are created only from a quotation the customer accepted.");
        if (quotation.QuotationSurveyVersion != quotation.CurrentSurveyVersion)
            return OperationResult<ContractView>.Fail(ContractErrorCodes.SurveyChanged,
                $"The on-site survey was amended (version {quotation.QuotationSurveyVersion} → {quotation.CurrentSurveyVersion}) after this quotation was made; issue a revised quotation first.",
                new { quotationSurveyVersion = quotation.QuotationSurveyVersion, currentSurveyVersion = quotation.CurrentSurveyVersion });
        if (ExistingContractError(await unitOfWork.ListByQuotationAsync(quotationId, cancellationToken)) is { } existing) return existing.As<ContractView>();
        if (!await unitOfWork.HasSettledQuotationPaymentAsync(quotationId, cancellationToken))
            return OperationResult<ContractView>.Fail(ContractErrorCodes.PaymentRequired,
                "A contract is created only after the quotation agreed on site was paid (the payment creates it automatically).");
        var warranty = await WarrantyMonthsAsync(warrantyMonths, null, cancellationToken);
        if (!warranty.Succeeded) return warranty.As<ContractView>();

        var now = time.GetUtcNow();
        var contract = BuildFromQuotation(quotation, terms, warranty.Value, now);
        // The checks above are a fast path; the decisive ones run again under the survey-request lock that withdrawal and closure also take,
        // so a contract is never created for a request closed meanwhile, and a closure never misses a contract created meanwhile.
        return await AddNewAsync(contract, staffUserId, "CREATED", $"from quotation {quotation.QuoteNumber} v{quotation.QuotationVersion}", cancellationToken,
            async token =>
            {
                await unitOfWork.LockSurveyRequestAsync(quotation.SurveyRequestId, token);
                if (await unitOfWork.IsSurveyRequestClosedAsync(quotation.SurveyRequestId, token))
                    return Fail(ContractErrorCodes.RequestClosed, "The survey request was withdrawn or closed; no contract can be made for it.");
                var fresh = await unitOfWork.GetQuotationAsync(quotationId, token);
                if (fresh?.Status != "ACCEPTED")
                    return Fail(ContractErrorCodes.QuotationNotAccepted, "The quotation is no longer accepted (it was cancelled or changed meanwhile).");
                if (fresh.QuotationSurveyVersion != fresh.CurrentSurveyVersion)
                    return Fail(ContractErrorCodes.SurveyChanged, "The on-site survey was amended meanwhile; issue a revised quotation first.");
                if (ExistingContractError(await unitOfWork.ListByQuotationAsync(quotationId, token)) is { } existing) return existing;
                return OperationResult<bool>.Ok(true);
            });
    }

    /// <summary>
    /// A Manager/Admin explicitly discards an initial DRAFT (never issued) with a reason, so the request can then be closed. The row is kept as
    /// DISCARDED with actor, time and reason; nothing paid, signed or issued is touched. Takes the survey-request lock first (same order as
    /// closure and contract creation), then re-reads the contract; a concurrent issue or edit makes this stale, never half-applied. Repeating
    /// it on a discarded contract returns it unchanged (no second event).
    /// </summary>
    public async Task<OperationResult<ContractView>> DiscardAsync(Guid staffUserId, Guid contractId, int revision, string? reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 1000)
            return OperationResult<ContractView>.Fail(ContractErrorCodes.DiscardReasonRequired, "A reason of at most 1000 characters is required.");
        var contract = await unitOfWork.FindAsync(contractId, false, cancellationToken);
        if (contract is null) return NotFound();
        try
        {
            var outcome = await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                await unitOfWork.LockSurveyRequestAsync(contract.SurveyRequestId, token);
                var c = await unitOfWork.FindAsync(contractId, true, token);
                if (c is null) return (Fail(ContractErrorCodes.NotFound, "Contract was not found."), false);
                if (c.Status == ContractStatuses.Discarded) return (OperationResult<bool>.Ok(false), false);   // idempotent repeat: nothing written
                // A 30/70 contract is withdrawn with the technician's on-site decision (its installments are cancelled with it).
                if (c.PaymentPlan is not null) return (LegacyOnly(), false);
                if (c.Revision != revision)
                    return (OperationResult<bool>.Fail(ContractErrorCodes.StaleRevision, "The contract was changed meanwhile. Reload and try again.", new { currentRevision = c.Revision }), false);
                if (c.Status != ContractStatuses.Draft || c.PreviousContractId is not null)
                    return (OperationResult<bool>.Fail(ContractErrorCodes.NotDiscardable,
                        c.Status != ContractStatuses.Draft ? $"A {c.Status} contract cannot be discarded; only a draft that was never issued can."
                            : "An amendment draft cannot be discarded here; only the initial draft of a quotation can.", new { status = c.Status }), false);
                var now = time.GetUtcNow();
                c.DiscardedAt = now;
                c.DiscardedByUserId = staffUserId;
                c.DiscardReason = reason.Trim();
                Move(c, ContractStatuses.Discarded, staffUserId, now, reason.Trim().Length > 450 ? reason.Trim()[..450] : reason.Trim());
                c.UpdatedAt = now;
                c.Revision++;
                await unitOfWork.SaveChangesAsync(token);
                return (OperationResult<bool>.Ok(true), true);
            }, cancellationToken);
            if (!outcome.Succeeded) return outcome.As<ContractView>();
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<ContractView>.Fail(ContractErrorCodes.StaleRevision, "The contract was changed meanwhile. Reload and try again.");
        }
        return OperationResult<ContractView>.Ok(await ViewAsync((await unitOfWork.FindAsync(contractId, false, cancellationToken))!, true, cancellationToken));
    }

    /// <summary>
    /// Why a quotation cannot get a new contract: a live one exists, or its draft was discarded (re-contracting after a discard is not a
    /// defined process, and the one-live index would refuse it too).
    /// </summary>
    private static OperationResult<bool>? ExistingContractError(IReadOnlyList<CustomerContract> contracts)
        => contracts.Any(c => c.Status == ContractStatuses.Discarded)
            ? Fail(ContractErrorCodes.DraftDiscarded, "This quotation's contract draft was discarded; a new contract for it is not possible.")
            : contracts.Any(c => c.Status != ContractStatuses.Superseded)
                ? Fail(ContractErrorCodes.AlreadyExists, "A contract already exists for this quotation; amend it instead.")
                : null;

    /// <param name="warrantyMonths">When given, changes the draft's installation warranty (one of the Admin's active options).</param>
    public Task<OperationResult<ContractView>> UpdateTermsAsync(Guid staffUserId, Guid contractId, int revision, string? terms, CancellationToken cancellationToken,
        int? warrantyMonths = null)
        => RunAsStaffAsync(contractId, revision, async (c, now, token) =>
        {
            if (c.PaymentPlan is not null) return LegacyOnly();
            if (c.Status != ContractStatuses.Draft) return Fail(ContractErrorCodes.InvalidState, "Issued contracts are immutable; create an amendment.");
            if (warrantyMonths is not null)
            {
                var warranty = await WarrantyMonthsAsync(warrantyMonths, c.WarrantyMonths, token);
                if (!warranty.Succeeded) return warranty.As<bool>();
                c.WarrantyMonths = warranty.Value;
            }
            c.Terms = Trim(terms, 8000);
            Event(c, "UPDATED", null, null, staffUserId, null, now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    /// <summary>DRAFT → ISSUED: content is frozen and its canonical hash computed. An amendment supersedes its predecessor here.</summary>
    public Task<OperationResult<ContractView>> IssueAsync(Guid staffUserId, Guid contractId, int revision, CancellationToken cancellationToken)
        => RunAsStaffAsync(contractId, revision, async (c, now, token) =>
        {
            if (c.PaymentPlan is not null) return LegacyOnly();
            if (c.Status != ContractStatuses.Draft) return Fail(ContractErrorCodes.InvalidState, "Only drafts can be issued.");
            // The terms are part of the canonical (signed) content: an issued contract always has them.
            if (string.IsNullOrWhiteSpace(c.Terms))
                return Fail(ContractErrorCodes.TermsRequired, "Enter the contract terms before issuing.");
            // Every new contract states its installation warranty (contracts issued before warranty options existed keep theirs as signed).
            if (c.WarrantyMonths is null)
                return Fail(ContractErrorCodes.WarrantyRequired,
                    "Choose the installation warranty duration before issuing. If none can be chosen, an Admin must first configure the warranty options.");
            if (c.PreviousContractId is { } previousId)
            {
                var previous = await unitOfWork.FindAsync(previousId, false, token);
                if (previous is null || previous.Status is ContractStatuses.Paid or ContractStatuses.Superseded
                    || !await unitOfWork.TryTransitionAsync(previousId, previous.Status, ContractStatuses.Superseded, now, token))
                    return Fail(ContractErrorCodes.InvalidState, "The previous contract version was paid or changed meanwhile; this amendment cannot be issued.");
                unitOfWork.AddEvent(new ContractEvent { Id = Guid.NewGuid(), ContractId = previousId, EventType = "SUPERSEDED", FromStatus = previous.Status,
                    ToStatus = ContractStatuses.Superseded, ActorUserId = staffUserId, Detail = $"by version {c.Version}", CreatedAt = now });
            }
            c.IssuedAt = now;
            c.CanonicalHash = CanonicalHash(c);
            Move(c, ContractStatuses.Issued, staffUserId, now, null);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    /// <summary>
    /// Links the FE-generated official PDF. The file must be a VERIFIED CONTRACT_DOCUMENT registered
    /// by this salesperson (backend-computed SHA-256), and the caller must echo the contract's current
    /// canonical hash — confirming the PDF was rendered from exactly this issued version.
    /// </summary>
    public async Task<OperationResult<ContractView>> AttachOfficialDocumentAsync(Guid staffUserId, Guid contractId, int revision, Guid fileId, string canonicalHash, CancellationToken cancellationToken)
    {
        var verified = await files.RequireVerifiedAsync([fileId], FilePurposes.ContractDocument, staffUserId, cancellationToken);
        if (!verified.Succeeded) return verified.As<ContractView>();
        var file = verified.Value![0];

        return await RunAsStaffAsync(contractId, revision, (c, now, _) =>
        {
            if (c.PaymentPlan is not null) return Task.FromResult(LegacyOnly());
            if (c.Status != ContractStatuses.Issued) return Task.FromResult(Fail(ContractErrorCodes.InvalidState, "The official document is attached after issuing and before publishing."));
            if (!string.Equals(c.CanonicalHash, canonicalHash?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Fail(ContractErrorCodes.CanonicalHashMismatch, "The document was not generated from the current contract version."));
            if (file.ChecksumSha256 is null) return Task.FromResult(Fail(FileErrorCodes.NotVerified, "The document checksum was not verified."));
            c.OfficialDocumentFileId = file.FileId;
            c.OfficialDocumentSha256 = file.ChecksumSha256;
            c.OfficialDocumentConfirmedByUserId = staffUserId;
            c.OfficialDocumentConfirmedAt = now;
            Event(c, "OFFICIAL_DOCUMENT_ATTACHED", null, null, staffUserId, $"sha256={file.ChecksumSha256}", now);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);
    }

    /// <summary>ISSUED → AWAITING_SIGNATURE: the customer can now download the official PDF.</summary>
    public Task<OperationResult<ContractView>> PublishAsync(Guid staffUserId, Guid contractId, int revision, CancellationToken cancellationToken)
        => RunAsStaffAsync(contractId, revision, (c, now, _) =>
        {
            if (c.PaymentPlan is not null) return Task.FromResult(LegacyOnly());
            if (c.Status != ContractStatuses.Issued) return Task.FromResult(Fail(ContractErrorCodes.InvalidState, "Only issued contracts can be published."));
            if (c.OfficialDocumentFileId is null) return Task.FromResult(Fail(ContractErrorCodes.DocumentMissing, "Attach and confirm the official PDF first."));
            c.PublishedAt = now;
            Move(c, ContractStatuses.AwaitingSignature, staffUserId, now, null);
            unitOfWork.AddNotification(c.CustomerUserId, CustomerNotificationTypes.ContractAwaitingSignature,
                $"Contract {c.ContractNumber} is ready for your signature: download the official document, sign it and submit the signed copy.", now);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);

    /// <summary>UNDER_REVIEW → SIGNED or SIGNATURE_REJECTED, by a Manager/Admin (signature confirmation). Internal review, not signature authentication.</summary>
    /// <param name="isManager">Kept for source compatibility; the API admits only Manager/Admin, so the reviewer is always staff.</param>
    public async Task<OperationResult<ContractView>> ReviewAsync(Guid reviewerUserId, bool isManager, Guid contractId, int revision, ReviewDecision decision, string? reason, CancellationToken cancellationToken)
    {
        if (decision == ReviewDecision.Reject && string.IsNullOrWhiteSpace(reason))
            return OperationResult<ContractView>.Fail(ContractErrorCodes.ReasonRequired, "A rejection reason is required.");
        if (decision == ReviewDecision.Approve)
        {
            // The signed scan and the official PDF must still be exactly what was verified (S3 call, no transaction open).
            var current = await unitOfWork.FindAsync(contractId, false, cancellationToken);
            if (current is not null)
            {
                var pending = (await unitOfWork.GetSubmissionsAsync(contractId, false, cancellationToken)).FirstOrDefault(s => s.Outcome == SignatureOutcomes.Pending);
                foreach (var fileId in new[] { pending?.FileId, current.OfficialDocumentFileId }.OfType<Guid>().Distinct())
                {
                    var intact = await files.VerifyIntegrityAsync(fileId, cancellationToken);
                    if (!intact.Succeeded) return intact.As<ContractView>();
                }
            }
        }
        var becamePaid = false;
        var reviewed = await RunAsStaffAsync(contractId, revision, async (c, now, token) =>
        {
            if (c.PaymentPlan is not null) return LegacyOnly();
            if (c.Status != ContractStatuses.UnderReview) return Fail(ContractErrorCodes.InvalidState, "Only submitted signatures can be reviewed.");
            var submission = (await unitOfWork.GetSubmissionsAsync(c.Id, true, token)).FirstOrDefault(s => s.Outcome == SignatureOutcomes.Pending);
            if (submission is null) return Fail(ContractErrorCodes.InvalidState, "No pending submission exists.");
            submission.ReviewedByUserId = reviewerUserId;
            submission.ReviewedAt = now;
            c.ReviewedByUserId = reviewerUserId;
            c.ReviewedAt = now;
            if (decision == ReviewDecision.Approve)
            {
                submission.Outcome = SignatureOutcomes.Approved;
                c.SignedAt = now;
                c.RejectionReason = null;
                Move(c, ContractStatuses.Signed, reviewerUserId, now, "signed scan approved after internal review");
                unitOfWork.AddNotification(c.CustomerUserId, CustomerNotificationTypes.ContractSigned,
                    $"Your signed copy of contract {c.ContractNumber} was accepted after review; the contract is now signed.", now);
                // Paid before it existed (technician-led flow): the verified payment completes the signed contract at once — it is never charged again.
                if (await unitOfWork.GetPrepaymentAsync(c.Id, token) is { } prepaid)
                {
                    c.PaidAt = now;
                    Move(c, ContractStatuses.Paid, reviewerUserId, now,
                        $"paid before the contract (payment attempt {prepaid.PaymentAttemptId}, {prepaid.Method}, {prepaid.Amount:0} VND verified {prepaid.PaidAt:O})");
                    becamePaid = true;
                }
            }
            else
            {
                submission.Outcome = SignatureOutcomes.Rejected;
                submission.RejectionReason = reason!.Trim();
                c.RejectionReason = reason.Trim();
                Move(c, ContractStatuses.SignatureRejected, reviewerUserId, now, Trim(reason, 500));
                unitOfWork.AddNotification(c.CustomerUserId, CustomerNotificationTypes.ContractSignatureRejected,
                    $"The signed copy of contract {c.ContractNumber} was not accepted: {reason.Trim()} Please submit a new signed copy.", now);
            }
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
        if (becamePaid && reviewed.Succeeded && paidFollowUp is not null)
        {
            try
            {
                await paidFollowUp.ContractPaidAsync(contractId, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The installation sweep confirms the schedule of PAID contracts every minute; the review itself stands.
            }
            return OperationResult<ContractView>.Ok(await ViewAsync((await unitOfWork.FindAsync(contractId, false, cancellationToken))!, true, cancellationToken));
        }
        return reviewed;
    }

    /// <summary>New DRAFT version with changed terms; pricing stays the accepted quotation's. Not possible once PAID.</summary>
    public async Task<OperationResult<ContractView>> CreateAmendmentAsync(Guid staffUserId, Guid contractId, string? terms, CancellationToken cancellationToken,
        int? warrantyMonths = null)
    {
        var current = await unitOfWork.FindAsync(contractId, false, cancellationToken);
        if (current is null) return NotFound();
        if (current.PaymentPlan is not null) return LegacyOnly().As<ContractView>();
        if (current.Status is ContractStatuses.Draft or ContractStatuses.Paid or ContractStatuses.Superseded or ContractStatuses.Discarded)
            return OperationResult<ContractView>.Fail(ContractErrorCodes.InvalidState, "Only issued, unpaid contracts can be amended (edit drafts directly).");
        var versions = await unitOfWork.ListByQuotationAsync(current.QuotationId, cancellationToken);
        if (versions.Any(c => c.Status == ContractStatuses.Draft))
            return OperationResult<ContractView>.Fail(ContractErrorCodes.AlreadyExists, "An amendment draft already exists.");
        var warranty = await WarrantyMonthsAsync(warrantyMonths, current.WarrantyMonths, cancellationToken);
        if (!warranty.Succeeded) return warranty.As<ContractView>();

        var now = time.GetUtcNow();
        var amendment = new CustomerContract
        {
            Id = Guid.NewGuid(), ContractNumber = current.ContractNumber, Version = versions.Max(c => c.Version) + 1, PreviousContractId = current.Id,
            QuotationId = current.QuotationId, QuotationVersion = current.QuotationVersion, SurveyRequestId = current.SurveyRequestId,
            CustomerUserId = current.CustomerUserId, SalesUserId = null, Status = ContractStatuses.Draft, Currency = current.Currency,
            TotalAmount = current.TotalAmount, PricingSnapshot = current.PricingSnapshot, CustomerSnapshot = current.CustomerSnapshot,
            Terms = Trim(terms, 8000) ?? current.Terms, WarrantyMonths = warranty.Value, CreatedAt = now, UpdatedAt = now,
        };
        return await AddNewAsync(amendment, staffUserId, "AMENDMENT_CREATED", $"amends version {current.Version}", cancellationToken);
    }

    /// <summary>
    /// Staff read (2026-11-19): Sales, Technician, Manager and Admin read every contract (the API restricts the roles; reads are audited by the
    /// read endpoints). <paramref name="isManager"/> is kept for source compatibility.
    /// </summary>
    public async Task<OperationResult<ContractView>> GetForStaffAsync(Guid userId, bool isManager, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.FindAsync(contractId, false, cancellationToken);
        if (contract is null) return NotFound();
        return OperationResult<ContractView>.Ok(await ViewAsync(contract, true, cancellationToken));
    }

    public async Task<OperationResult<IReadOnlyList<ContractView>>> ListForSurveyRequestAsync(Guid userId, bool isManager, Guid surveyRequestId, CancellationToken cancellationToken)
    {
        var list = new List<ContractView>();
        foreach (var c in await unitOfWork.ListBySurveyRequestAsync(surveyRequestId, cancellationToken)) list.Add(await ViewAsync(c, false, cancellationToken));
        return OperationResult<IReadOnlyList<ContractView>>.Ok(list);
    }

    // ------------------------------------------------------------------ Customer

    public async Task<IReadOnlyList<ContractView>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken)
    {
        var list = new List<ContractView>();
        foreach (var c in (await unitOfWork.ListForCustomerAsync(customerUserId, cancellationToken)).Where(ContractStatuses.IsCustomerVisible))
            list.Add(await ViewAsync(c, false, cancellationToken));
        return list;
    }

    public async Task<OperationResult<ContractView>> GetForCustomerAsync(Guid customerUserId, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.FindAsync(contractId, false, cancellationToken);
        return contract is null || contract.CustomerUserId != customerUserId || !ContractStatuses.IsCustomerVisible(contract)
            ? NotFound()
            : OperationResult<ContractView>.Ok(await ViewAsync(contract, false, cancellationToken));
    }

    /// <summary>
    /// The customer submits the hand-signed scan (a VERIFIED SIGNED_CONTRACT file they registered) and
    /// states which official PDF they signed by its SHA-256; it must be the contract's current official document.
    /// </summary>
    public async Task<OperationResult<ContractView>> SubmitSignedDocumentAsync(Guid customerUserId, Guid contractId, Guid fileId, string officialDocumentSha256, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.FindAsync(contractId, false, cancellationToken);
        if (contract is null || contract.CustomerUserId != customerUserId || !ContractStatuses.IsCustomerVisible(contract)) return NotFound();
        return await SubmitSignedAsync(customerUserId, contractId, fileId, officialDocumentSha256, null, cancellationToken);
    }

    /// <summary>
    /// Paper contract (2026-11-18): the customer signs the printed official PDF in person and the Sales owner of the request (or a
    /// Manager/Admin) uploads the scan of that physically signed paper (a VERIFIED SIGNED_CONTRACT PDF the staff member registered). Same rules
    /// as the customer's own submission: only AWAITING_SIGNATURE / SIGNATURE_REJECTED versions, the scan must be of the current official PDF
    /// (its SHA-256), every submission is kept as history, and the contract only moves to UNDER_REVIEW — a Manager/Admin still confirms the
    /// signature (SIGNED). Uploading never makes the contract signed by itself. Sales of another request (or none) gets 404.
    /// </summary>
    /// <param name="signedOn">The local date written on the paper contract (optional, recorded in the contract history; not in the future).</param>
    /// <param name="note">Optional note recorded in the contract history (e.g. where it was signed), at most 300 characters.</param>
    public async Task<OperationResult<ContractView>> SubmitPaperSignedDocumentAsync(Guid staffUserId, bool isManager, Guid contractId, Guid fileId,
        string officialDocumentSha256, DateOnly? signedOn, string? note, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.FindAsync(contractId, false, cancellationToken);
        if (contract is null || (!isManager && await unitOfWork.GetAssignedSaleAsync(contract.SurveyRequestId, cancellationToken) != staffUserId)) return NotFound();
        if (contract.PaymentPlan is not null) return LegacyOnly().As<ContractView>();
        if (note is { Length: > 300 })
            return OperationResult<ContractView>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { ["note"] = ["At most 300 characters."] });
        // One day of tolerance: the date is the local date on the paper, the clock is UTC.
        if (signedOn is { } day && day > DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime.AddDays(1)))
            return OperationResult<ContractView>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { ["signedOn"] = ["The signing date cannot be in the future."] });
        var detail = $"paper contract scan uploaded by staff {staffUserId}"
                     + (signedOn is { } d ? $"; signed on {d:yyyy-MM-dd}" : "")
                     + (string.IsNullOrWhiteSpace(note) ? "" : $"; {note.Trim()}");
        return await SubmitSignedAsync(staffUserId, contractId, fileId, officialDocumentSha256, detail, cancellationToken);
    }

    private async Task<OperationResult<ContractView>> SubmitSignedAsync(Guid submitterUserId, Guid contractId, Guid fileId, string officialDocumentSha256,
        string? detail, CancellationToken cancellationToken)
    {
        var verified = await files.RequireVerifiedAsync([fileId], FilePurposes.SignedContract, submitterUserId, cancellationToken);
        if (!verified.Succeeded) return verified.As<ContractView>();
        var file = verified.Value![0];

        return await RunAsync(contractId, null, async (c, now, token) =>
        {
            if (c.PaymentPlan is not null) return LegacyOnly();
            if (c.Status is not (ContractStatuses.AwaitingSignature or ContractStatuses.SignatureRejected))
                return Fail(ContractErrorCodes.InvalidState, "The contract is not waiting for a signed document.");
            if (!string.Equals(c.OfficialDocumentSha256, officialDocumentSha256?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Fail(ContractErrorCodes.WrongDocumentVersion, "The signed scan must be of the current official contract document.");
            if ((await unitOfWork.GetSubmissionsAsync(c.Id, false, token)).Any(s => s.FileId == fileId))
                return Fail(FileErrorCodes.AlreadyLinked, "This file was already submitted.");
            unitOfWork.AddSubmission(new ContractSignatureSubmission
            {
                Id = Guid.NewGuid(), ContractId = c.Id, FileId = fileId, FileSha256 = file.ChecksumSha256!, OfficialDocumentSha256 = c.OfficialDocumentSha256!,
                SubmittedByUserId = submitterUserId, SubmittedAt = now, Outcome = SignatureOutcomes.Pending,
            });
            c.SignedDocumentFileId = fileId;
            c.SignedDocumentSha256 = file.ChecksumSha256;
            c.SignedSubmittedAt = now;
            Move(c, ContractStatuses.UnderReview, submitterUserId, now, detail);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    // ------------------------------------------------------------------ internals

    /// <summary>SHA-256 over a stable JSON of everything that defines the contract version.</summary>
    public static string CanonicalHash(CustomerContract c)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            c.ContractNumber, c.Version, c.QuotationId, c.QuotationVersion, c.Currency, totalAmount = c.TotalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            pricing = JsonDocument.Parse(c.PricingSnapshot).RootElement, customer = JsonDocument.Parse(c.CustomerSnapshot).RootElement, terms = c.Terms ?? "",
            issuedAt = c.IssuedAt?.UtcDateTime.ToString("O"),
            // Only present for contracts with a warranty snapshot, so hashes of contracts issued before Flow 3 stay unchanged.
            warrantyMonths = c.WarrantyMonths,
        }, CanonicalJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>A Manager/Admin step (the API admits no other role): no per-request ownership.</summary>
    private async Task<OperationResult<ContractView>> RunAsStaffAsync(Guid contractId, int revision,
        Func<CustomerContract, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, CancellationToken cancellationToken)
    {
        if (await unitOfWork.FindAsync(contractId, false, cancellationToken) is null) return NotFound();
        return await RunAsync(contractId, revision, change, cancellationToken);
    }

    private async Task<OperationResult<ContractView>> RunAsync(Guid contractId, int? revision,
        Func<CustomerContract, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                var c = await unitOfWork.FindAsync(contractId, true, token);
                if (c is null) return (Fail(ContractErrorCodes.NotFound, "Contract was not found."), false);
                if (revision is { } expected && c.Revision != expected)
                    return (OperationResult<bool>.Fail(ContractErrorCodes.StaleRevision, "The contract was changed meanwhile. Reload and try again.", new { currentRevision = c.Revision }), false);
                var now = time.GetUtcNow();
                var result = await change(c, now, token);
                if (!result.Succeeded) return (result, false);
                c.UpdatedAt = now;
                c.Revision++;
                await unitOfWork.SaveChangesAsync(token);
                return (result, true);
            }, cancellationToken);
            if (!outcome.Succeeded) return outcome.As<ContractView>();
        }
        catch (PersistenceConflictException ex)
        {
            return ex.Kind == PersistenceConflictException.UniqueViolation
                ? OperationResult<ContractView>.Fail(ContractErrorCodes.AlreadyExists, "Another live contract exists for this quotation.")
                : OperationResult<ContractView>.Fail(ContractErrorCodes.StaleRevision, "The contract was changed meanwhile. Reload and try again.");
        }
        return OperationResult<ContractView>.Ok(await ViewAsync((await unitOfWork.FindAsync(contractId, false, cancellationToken))!, true, cancellationToken));
    }

    /// <param name="precondition">Runs first inside the transaction (after its locks); a failure aborts without writing anything.</param>
    private async Task<OperationResult<ContractView>> AddNewAsync(CustomerContract contract, Guid actor, string eventType, string detail, CancellationToken cancellationToken,
        Func<CancellationToken, Task<OperationResult<bool>>>? precondition = null)
    {
        try
        {
            var outcome = await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                if (precondition is not null && await precondition(token) is { Succeeded: false } refused) return (refused, false);
                unitOfWork.AddContract(contract);
                Event(contract, eventType, null, ContractStatuses.Draft, actor, detail, contract.CreatedAt);
                await unitOfWork.SaveChangesAsync(token);
                return (OperationResult<bool>.Ok(true), true);
            }, cancellationToken);
            if (!outcome.Succeeded) return outcome.As<ContractView>();
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<ContractView>.Fail(ContractErrorCodes.AlreadyExists, "A contract (or amendment draft) already exists for this quotation.");
        }
        return OperationResult<ContractView>.Ok(await ViewAsync(contract, true, cancellationToken));
    }

    private void Move(CustomerContract c, string to, Guid actor, DateTimeOffset now, string? detail)
    {
        Event(c, "STATUS_CHANGED", c.Status, to, actor, detail, now);
        c.Status = to;
    }

    private void Event(CustomerContract c, string type, string? from, string? to, Guid actor, string? detail, DateTimeOffset now)
        => unitOfWork.AddEvent(new ContractEvent { Id = Guid.NewGuid(), ContractId = c.Id, EventType = type, FromStatus = from, ToStatus = to, ActorUserId = actor, Detail = detail, CreatedAt = now });

    private async Task<ContractView> ViewAsync(CustomerContract c, bool staff, CancellationToken cancellationToken)
    {
        var submissions = (await unitOfWork.GetSubmissionsAsync(c.Id, false, cancellationToken))
            .OrderBy(s => s.SubmittedAt)
            .Select(s => new SignatureSubmissionView(s.Id, s.FileId, s.FileSha256, s.OfficialDocumentSha256, s.SubmittedAt, s.Outcome, s.ReviewedAt, s.RejectionReason)).ToList();
        var prepayment = await unitOfWork.GetPrepaymentAsync(c.Id, cancellationToken);
        var events = staff ? (await unitOfWork.GetEventsAsync(c.Id, cancellationToken))
            .Select(e => new ContractEventView(e.EventType, e.FromStatus, e.ToStatus, e.ActorUserId, e.Detail, e.CreatedAt)).ToList() : null;
        var plan = c.PaymentPlan is null ? null
            : PaymentPlanViews.Build(c, await unitOfWork.GetInstallmentsAsync(c.Id, false, cancellationToken),
                await unitOfWork.GetSignatureSetsAsync(c.Id, false, cancellationToken), await unitOfWork.GetSignedPagesAsync(c.Id, false, cancellationToken),
                staff ? await unitOfWork.GetCollectionCasesAsync(c.Id, cancellationToken) : null, time.GetUtcNow());
        return new ContractView(c.Id, c.ContractNumber, c.Version, c.PreviousContractId, c.QuotationId, c.QuotationVersion, c.SurveyRequestId, c.Status,
            c.Currency, c.TotalAmount, JsonDocument.Parse(c.PricingSnapshot).RootElement.Clone(), JsonDocument.Parse(c.CustomerSnapshot).RootElement.Clone(),
            c.Terms, c.CanonicalHash,
            c.OfficialDocumentFileId is { } official ? new ContractDocumentView(official, c.OfficialDocumentSha256, c.OfficialDocumentConfirmedAt) : null,
            c.SignedDocumentFileId is { } signed ? new ContractDocumentView(signed, c.SignedDocumentSha256, c.SignedSubmittedAt) : null,
            c.RejectionReason, c.IssuedAt, c.PublishedAt, c.SignedAt, c.PaidAt, c.Revision, submissions, events, c.WarrantyMonths,
            staff && c.DiscardedAt is { } discardedAt ? new ContractDiscardView(discardedAt, c.DiscardedByUserId!.Value, c.DiscardReason!) : null,
            // Payment completion is separate from the contract status: a contract created from a paid quotation is DRAFT … SIGNED and already paid.
            // A 30/70 contract is completed by its installments: PaymentCompleted = fully paid (both installments), never the deposit alone.
            prepayment is not null || c.Status == ContractStatuses.Paid, prepayment?.PaymentAttemptId, prepayment?.PaidAt ?? c.PaidAt, plan);
    }

    private static readonly JsonSerializerOptions CanonicalJson = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>Requested months must be an active Admin option; omitted = <paramref name="keep"/> (amendment) or the default option.</summary>
    private async Task<OperationResult<int?>> WarrantyMonthsAsync(int? requested, int? keep, CancellationToken cancellationToken)
    {
        var (active, defaultMonths) = await unitOfWork.GetInstallationWarrantyOptionsAsync(cancellationToken);
        if (requested is null) return OperationResult<int?>.Ok(keep ?? defaultMonths);
        if (active.Count == 0)
            return OperationResult<int?>.Fail(ContractErrorCodes.InvalidWarranty, "No installation warranty durations are configured yet; an Admin must add them first.");
        return active.Contains(requested.Value)
            ? OperationResult<int?>.Ok(requested)
            : OperationResult<int?>.Fail(ContractErrorCodes.InvalidWarranty, $"Choose one of the active warranty durations: {string.Join(", ", active)} months.");
    }

    private static string NewNumber(DateTimeOffset now)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return $"HD-{now:yyyyMMdd}-{string.Concat(RandomNumberGenerator.GetBytes(6).Select(b => alphabet[b % alphabet.Length]))}";
    }

    private static OperationResult<bool> Fail(string code, string message) => OperationResult<bool>.Fail(code, message);

    /// <summary>A step of the previous contract flow (official PDF, scan review, amendments) asked on a 30/70 contract (409).</summary>
    private static OperationResult<bool> LegacyOnly() => Fail(PaymentPlanErrorCodes.NotApplicable,
        "This contract follows the 30/70 flow (created at the on-site agreement, signed on paper after the deposit); this step of the previous flow does not apply.");
    private static OperationResult<ContractView> NotFound() => OperationResult<ContractView>.Fail(ContractErrorCodes.NotFound, "Contract was not found.");
    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();
}
