using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    IReadOnlyList<SignatureSubmissionView>? Submissions, IReadOnlyList<ContractEventView>? Events);

public enum ReviewDecision { Approve = 1, Reject = 2 }

/// <summary>
/// Contract lifecycle: DRAFT → ISSUED (content frozen, canonical hash fixed) → AWAITING_SIGNATURE
/// (official PDF verified and confirmed by Sales against the hash) → UNDER_REVIEW (customer submitted
/// a hand-signed scan of exactly that PDF) → SIGNED (Sales/Manager approved) → PAID (payment module).
/// Rejection: UNDER_REVIEW → SIGNATURE_REJECTED, after which the customer submits again. Amendments
/// create a new version that supersedes the previous one when issued; PAID contracts are final.
/// Every transition is a conditional update with an audit event.
/// </summary>
public sealed class ContractHandlers(IContractUnitOfWork unitOfWork, FileService files, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ Sales

    public async Task<OperationResult<ContractView>> CreateAsync(Guid salesUserId, Guid quotationId, string? terms, CancellationToken cancellationToken)
    {
        var quotation = await unitOfWork.GetQuotationAsync(quotationId, cancellationToken);
        if (quotation is null || quotation.AssignedSaleId != salesUserId)
            return OperationResult<ContractView>.Fail(ContractErrorCodes.NotFound, "Quotation was not found.");
        if (quotation.Status != "ACCEPTED")
            return OperationResult<ContractView>.Fail(ContractErrorCodes.QuotationNotAccepted, "Contracts are created only from a quotation the customer accepted.");
        if ((await unitOfWork.ListByQuotationAsync(quotationId, cancellationToken)).Any(c => c.Status != ContractStatuses.Superseded))
            return OperationResult<ContractView>.Fail(ContractErrorCodes.AlreadyExists, "A contract already exists for this quotation; amend it instead.");

        var now = time.GetUtcNow();
        var contract = new CustomerContract
        {
            Id = Guid.NewGuid(), ContractNumber = NewNumber(now), Version = 1, QuotationId = quotation.QuotationId, QuotationVersion = quotation.QuotationVersion,
            SurveyRequestId = quotation.SurveyRequestId, CustomerUserId = quotation.CustomerUserId, SalesUserId = salesUserId, Status = ContractStatuses.Draft,
            Currency = quotation.Currency, TotalAmount = quotation.TotalAmount, PricingSnapshot = quotation.PricingSnapshotJson,
            CustomerSnapshot = quotation.CustomerSnapshotJson ?? "{}", Terms = Trim(terms, 8000) ?? quotation.Terms, CreatedAt = now, UpdatedAt = now,
        };
        return await AddNewAsync(contract, salesUserId, "CREATED", $"from quotation {quotation.QuoteNumber} v{quotation.QuotationVersion}", cancellationToken);
    }

    public Task<OperationResult<ContractView>> UpdateTermsAsync(Guid salesUserId, Guid contractId, int revision, string? terms, CancellationToken cancellationToken)
        => RunAsSalesAsync(salesUserId, contractId, revision, allowManager: false, (c, now, _) =>
        {
            if (c.Status != ContractStatuses.Draft) return Task.FromResult(Fail(ContractErrorCodes.InvalidState, "Issued contracts are immutable; create an amendment."));
            c.Terms = Trim(terms, 8000);
            Event(c, "UPDATED", null, null, salesUserId, null, now);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);

    /// <summary>DRAFT → ISSUED: content is frozen and its canonical hash computed. An amendment supersedes its predecessor here.</summary>
    public Task<OperationResult<ContractView>> IssueAsync(Guid salesUserId, Guid contractId, int revision, CancellationToken cancellationToken)
        => RunAsSalesAsync(salesUserId, contractId, revision, allowManager: false, async (c, now, token) =>
        {
            if (c.Status != ContractStatuses.Draft) return Fail(ContractErrorCodes.InvalidState, "Only drafts can be issued.");
            if (c.PreviousContractId is { } previousId)
            {
                var previous = await unitOfWork.FindAsync(previousId, false, token);
                if (previous is null || previous.Status is ContractStatuses.Paid or ContractStatuses.Superseded
                    || !await unitOfWork.TryTransitionAsync(previousId, previous.Status, ContractStatuses.Superseded, now, token))
                    return Fail(ContractErrorCodes.InvalidState, "The previous contract version was paid or changed meanwhile; this amendment cannot be issued.");
                unitOfWork.AddEvent(new ContractEvent { Id = Guid.NewGuid(), ContractId = previousId, EventType = "SUPERSEDED", FromStatus = previous.Status,
                    ToStatus = ContractStatuses.Superseded, ActorUserId = salesUserId, Detail = $"by version {c.Version}", CreatedAt = now });
            }
            c.IssuedAt = now;
            c.CanonicalHash = CanonicalHash(c);
            Move(c, ContractStatuses.Issued, salesUserId, now, null);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    /// <summary>
    /// Links the FE-generated official PDF. The file must be a VERIFIED CONTRACT_DOCUMENT registered
    /// by this salesperson (backend-computed SHA-256), and the caller must echo the contract's current
    /// canonical hash — confirming the PDF was rendered from exactly this issued version.
    /// </summary>
    public async Task<OperationResult<ContractView>> AttachOfficialDocumentAsync(Guid salesUserId, Guid contractId, int revision, Guid fileId, string canonicalHash, CancellationToken cancellationToken)
    {
        var verified = await files.RequireVerifiedAsync([fileId], FilePurposes.ContractDocument, salesUserId, cancellationToken);
        if (!verified.Succeeded) return verified.As<ContractView>();
        var file = verified.Value![0];

        return await RunAsSalesAsync(salesUserId, contractId, revision, allowManager: false, (c, now, _) =>
        {
            if (c.Status != ContractStatuses.Issued) return Task.FromResult(Fail(ContractErrorCodes.InvalidState, "The official document is attached after issuing and before publishing."));
            if (!string.Equals(c.CanonicalHash, canonicalHash?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Fail(ContractErrorCodes.CanonicalHashMismatch, "The document was not generated from the current contract version."));
            if (file.ChecksumSha256 is null) return Task.FromResult(Fail(FileErrorCodes.NotVerified, "The document checksum was not verified."));
            c.OfficialDocumentFileId = file.FileId;
            c.OfficialDocumentSha256 = file.ChecksumSha256;
            c.OfficialDocumentConfirmedByUserId = salesUserId;
            c.OfficialDocumentConfirmedAt = now;
            Event(c, "OFFICIAL_DOCUMENT_ATTACHED", null, null, salesUserId, $"sha256={file.ChecksumSha256}", now);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);
    }

    /// <summary>ISSUED → AWAITING_SIGNATURE: the customer can now download the official PDF.</summary>
    public Task<OperationResult<ContractView>> PublishAsync(Guid salesUserId, Guid contractId, int revision, CancellationToken cancellationToken)
        => RunAsSalesAsync(salesUserId, contractId, revision, allowManager: false, (c, now, _) =>
        {
            if (c.Status != ContractStatuses.Issued) return Task.FromResult(Fail(ContractErrorCodes.InvalidState, "Only issued contracts can be published."));
            if (c.OfficialDocumentFileId is null) return Task.FromResult(Fail(ContractErrorCodes.DocumentMissing, "Attach and confirm the official PDF first."));
            c.PublishedAt = now;
            Move(c, ContractStatuses.AwaitingSignature, salesUserId, now, null);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);

    /// <summary>UNDER_REVIEW → SIGNED or SIGNATURE_REJECTED, by the owning salesperson or a manager. Internal review, not signature authentication.</summary>
    public async Task<OperationResult<ContractView>> ReviewAsync(Guid reviewerUserId, bool isManager, Guid contractId, int revision, ReviewDecision decision, string? reason, CancellationToken cancellationToken)
    {
        if (decision == ReviewDecision.Reject && string.IsNullOrWhiteSpace(reason))
            return OperationResult<ContractView>.Fail(ContractErrorCodes.ReasonRequired, "A rejection reason is required.");
        if (decision == ReviewDecision.Approve)
        {
            // The signed scan and the official PDF must still be exactly what was verified (S3 call, no transaction open).
            var current = await unitOfWork.FindAsync(contractId, false, cancellationToken);
            if (current is not null && (isManager || current.SalesUserId == reviewerUserId))
            {
                var pending = (await unitOfWork.GetSubmissionsAsync(contractId, false, cancellationToken)).FirstOrDefault(s => s.Outcome == SignatureOutcomes.Pending);
                foreach (var fileId in new[] { pending?.FileId, current.OfficialDocumentFileId }.OfType<Guid>().Distinct())
                {
                    var intact = await files.VerifyIntegrityAsync(fileId, cancellationToken);
                    if (!intact.Succeeded) return intact.As<ContractView>();
                }
            }
        }
        return await RunAsSalesAsync(reviewerUserId, contractId, revision, allowManager: isManager, async (c, now, token) =>
        {
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
            }
            else
            {
                submission.Outcome = SignatureOutcomes.Rejected;
                submission.RejectionReason = reason!.Trim();
                c.RejectionReason = reason.Trim();
                Move(c, ContractStatuses.SignatureRejected, reviewerUserId, now, Trim(reason, 500));
            }
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>New DRAFT version with changed terms; pricing stays the accepted quotation's. Not possible once PAID.</summary>
    public async Task<OperationResult<ContractView>> CreateAmendmentAsync(Guid salesUserId, Guid contractId, string? terms, CancellationToken cancellationToken)
    {
        var current = await unitOfWork.FindAsync(contractId, false, cancellationToken);
        if (current is null || await unitOfWork.GetAssignedSaleAsync(current.SurveyRequestId, cancellationToken) != salesUserId) return NotFound();
        if (current.Status is ContractStatuses.Draft or ContractStatuses.Paid or ContractStatuses.Superseded)
            return OperationResult<ContractView>.Fail(ContractErrorCodes.InvalidState, "Only issued, unpaid contracts can be amended (edit drafts directly).");
        var versions = await unitOfWork.ListByQuotationAsync(current.QuotationId, cancellationToken);
        if (versions.Any(c => c.Status == ContractStatuses.Draft))
            return OperationResult<ContractView>.Fail(ContractErrorCodes.AlreadyExists, "An amendment draft already exists.");

        var now = time.GetUtcNow();
        var amendment = new CustomerContract
        {
            Id = Guid.NewGuid(), ContractNumber = current.ContractNumber, Version = versions.Max(c => c.Version) + 1, PreviousContractId = current.Id,
            QuotationId = current.QuotationId, QuotationVersion = current.QuotationVersion, SurveyRequestId = current.SurveyRequestId,
            CustomerUserId = current.CustomerUserId, SalesUserId = salesUserId, Status = ContractStatuses.Draft, Currency = current.Currency,
            TotalAmount = current.TotalAmount, PricingSnapshot = current.PricingSnapshot, CustomerSnapshot = current.CustomerSnapshot,
            Terms = Trim(terms, 8000) ?? current.Terms, CreatedAt = now, UpdatedAt = now,
        };
        return await AddNewAsync(amendment, salesUserId, "AMENDMENT_CREATED", $"amends version {current.Version}", cancellationToken);
    }

    public async Task<OperationResult<ContractView>> GetForStaffAsync(Guid userId, bool isManager, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.FindAsync(contractId, false, cancellationToken);
        if (contract is null || (!isManager && await unitOfWork.GetAssignedSaleAsync(contract.SurveyRequestId, cancellationToken) != userId)) return NotFound();
        return OperationResult<ContractView>.Ok(await ViewAsync(contract, true, cancellationToken));
    }

    public async Task<OperationResult<IReadOnlyList<ContractView>>> ListForSurveyRequestAsync(Guid userId, bool isManager, Guid surveyRequestId, CancellationToken cancellationToken)
    {
        if (!isManager && await unitOfWork.GetAssignedSaleAsync(surveyRequestId, cancellationToken) != userId)
            return OperationResult<IReadOnlyList<ContractView>>.Fail(ContractErrorCodes.NotFound, "Survey request was not found.");
        var list = new List<ContractView>();
        foreach (var c in await unitOfWork.ListBySurveyRequestAsync(surveyRequestId, cancellationToken)) list.Add(await ViewAsync(c, false, cancellationToken));
        return OperationResult<IReadOnlyList<ContractView>>.Ok(list);
    }

    // ------------------------------------------------------------------ Customer

    public async Task<IReadOnlyList<ContractView>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken)
    {
        var list = new List<ContractView>();
        foreach (var c in (await unitOfWork.ListForCustomerAsync(customerUserId, cancellationToken)).Where(c => ContractStatuses.CustomerVisible.Contains(c.Status)))
            list.Add(await ViewAsync(c, false, cancellationToken));
        return list;
    }

    public async Task<OperationResult<ContractView>> GetForCustomerAsync(Guid customerUserId, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.FindAsync(contractId, false, cancellationToken);
        return contract is null || contract.CustomerUserId != customerUserId || !ContractStatuses.CustomerVisible.Contains(contract.Status)
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
        if (contract is null || contract.CustomerUserId != customerUserId || !ContractStatuses.CustomerVisible.Contains(contract.Status)) return NotFound();
        var verified = await files.RequireVerifiedAsync([fileId], FilePurposes.SignedContract, customerUserId, cancellationToken);
        if (!verified.Succeeded) return verified.As<ContractView>();
        var file = verified.Value![0];

        return await RunAsync(contractId, null, async (c, now, token) =>
        {
            if (c.Status is not (ContractStatuses.AwaitingSignature or ContractStatuses.SignatureRejected))
                return Fail(ContractErrorCodes.InvalidState, "The contract is not waiting for a signed document.");
            if (!string.Equals(c.OfficialDocumentSha256, officialDocumentSha256?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Fail(ContractErrorCodes.WrongDocumentVersion, "The signed scan must be of the current official contract document.");
            if ((await unitOfWork.GetSubmissionsAsync(c.Id, false, token)).Any(s => s.FileId == fileId))
                return Fail(FileErrorCodes.AlreadyLinked, "This file was already submitted.");
            unitOfWork.AddSubmission(new ContractSignatureSubmission
            {
                Id = Guid.NewGuid(), ContractId = c.Id, FileId = fileId, FileSha256 = file.ChecksumSha256!, OfficialDocumentSha256 = c.OfficialDocumentSha256!,
                SubmittedByUserId = customerUserId, SubmittedAt = now, Outcome = SignatureOutcomes.Pending,
            });
            c.SignedDocumentFileId = fileId;
            c.SignedDocumentSha256 = file.ChecksumSha256;
            c.SignedSubmittedAt = now;
            Move(c, ContractStatuses.UnderReview, customerUserId, now, null);
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
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private async Task<OperationResult<ContractView>> RunAsSalesAsync(Guid userId, Guid contractId, int revision, bool allowManager,
        Func<CustomerContract, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, CancellationToken cancellationToken)
    {
        var contract = await unitOfWork.FindAsync(contractId, false, cancellationToken);
        if (contract is null || (!allowManager && await unitOfWork.GetAssignedSaleAsync(contract.SurveyRequestId, cancellationToken) != userId)) return NotFound();
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

    private async Task<OperationResult<ContractView>> AddNewAsync(CustomerContract contract, Guid actor, string eventType, string detail, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
            {
                unitOfWork.AddContract(contract);
                Event(contract, eventType, null, ContractStatuses.Draft, actor, detail, contract.CreatedAt);
                await unitOfWork.SaveChangesAsync(token);
                return (true, true);
            }, cancellationToken);
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
        var events = staff ? (await unitOfWork.GetEventsAsync(c.Id, cancellationToken))
            .Select(e => new ContractEventView(e.EventType, e.FromStatus, e.ToStatus, e.ActorUserId, e.Detail, e.CreatedAt)).ToList() : null;
        return new ContractView(c.Id, c.ContractNumber, c.Version, c.PreviousContractId, c.QuotationId, c.QuotationVersion, c.SurveyRequestId, c.Status,
            c.Currency, c.TotalAmount, JsonDocument.Parse(c.PricingSnapshot).RootElement.Clone(), JsonDocument.Parse(c.CustomerSnapshot).RootElement.Clone(),
            c.Terms, c.CanonicalHash,
            c.OfficialDocumentFileId is { } official ? new ContractDocumentView(official, c.OfficialDocumentSha256, c.OfficialDocumentConfirmedAt) : null,
            c.SignedDocumentFileId is { } signed ? new ContractDocumentView(signed, c.SignedDocumentSha256, c.SignedSubmittedAt) : null,
            c.RejectionReason, c.IssuedAt, c.PublishedAt, c.SignedAt, c.PaidAt, c.Revision, submissions, events);
    }

    private static string NewNumber(DateTimeOffset now)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return $"HD-{now:yyyyMMdd}-{string.Concat(RandomNumberGenerator.GetBytes(6).Select(b => alphabet[b % alphabet.Length]))}";
    }

    private static OperationResult<bool> Fail(string code, string message) => OperationResult<bool>.Fail(code, message);
    private static OperationResult<ContractView> NotFound() => OperationResult<ContractView>.Fail(ContractErrorCodes.NotFound, "Contract was not found.");
    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();
}
