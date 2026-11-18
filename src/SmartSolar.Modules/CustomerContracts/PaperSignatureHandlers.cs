using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;

namespace SmartSolar.Modules.CustomerContracts;

/// <param name="ExpectedPageCount">Number of pages of the printed contract (1–50).</param>
/// <param name="SignedOn">Local date written on the paper (optional, not in the future).</param>
public sealed record StartSignatureSetInput(int ExpectedPageCount, DateOnly? SignedOn, string? Note);

public sealed record SignedPageView(int PageNumber, Guid FileId, string Sha256, string ContentType, Guid UploadedByUserId, DateTimeOffset UploadedAt,
    DateTimeOffset? ReplacedAt);

/// <param name="MissingPages">Page numbers 1..N without a current file (empty when complete).</param>
public sealed record SignatureSetView(Guid SignatureSetId, Guid ContractId, int Version, string Status, int ExpectedPageCount, int UploadedPageCount,
    IReadOnlyList<int> MissingPages, DateOnly? SignedOn, string? Note, Guid CreatedByUserId, DateTimeOffset CreatedAt, Guid? FinalizedByUserId,
    DateTimeOffset? FinalizedAt, int Revision, IReadOnlyList<SignedPageView> Pages);

/// <summary>Who handles the photographed pages.</summary>
public enum SignatureActor
{
    /// <summary>The technician assigned to the on-site sale (the request's surveying technician).</summary>
    Technician = 1,
    /// <summary>A Manager / Admin supporting the upload (e.g. the technician's phone failed).</summary>
    Support = 2,
}

/// <summary>
/// Paper contract signed on site (30/70 plan, 2026-11-19). After the deposit is verified (contract AWAITING_SIGNATURE) the customer signs the printed
/// contract; the technician photographs every page and uploads them in order (page 1..N, one current file per page, verified SIGNED_CONTRACT_PAGE
/// files the technician registered), then finalizes. Finalizing locks the set (immutable, audited) and the contract becomes SIGNED = ready for
/// installation. No Manager review and no electronic signature are involved. Only the assigned technician (or Manager/Admin support) writes; anyone
/// else gets 404.
/// </summary>
/// <param name="readyFollowUp">Runs after finalizing (outside the transaction): confirms the technician-agreed installation schedule.</param>
public sealed class PaperSignatureHandlers(IContractUnitOfWork contracts, FileService files, PaymentPlanOptions options, TimeProvider time,
    Installations.IContractPaidFollowUp? readyFollowUp = null)
{
    public const int MaxPages = 50;

    /// <summary>Starts (or returns) the upload of the signed pages. A different page count restarts the unfinished upload (old set SUPERSEDED).</summary>
    public async Task<OperationResult<SignatureSetView>> StartAsync(SignatureActor actor, Guid actorUserId, Guid contractId, StartSignatureSetInput input,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (input.ExpectedPageCount is < 1 or > MaxPages) errors["expectedPageCount"] = [$"Between 1 and {MaxPages}."];
        if (input.Note is { Length: > 500 }) errors["note"] = ["At most 500 characters."];
        if (input.SignedOn is { } day && day > DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime.AddDays(1))) errors["signedOn"] = ["The signing date cannot be in the future."];
        if (errors.Count > 0) return OperationResult<SignatureSetView>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.", errors);
        if (await AuthorizedAsync(actor, actorUserId, contractId, cancellationToken) is { } denied) return denied;

        return await RunAsync(contractId, async (c, now, token) =>
        {
            if (StateProblem(c) is { } problem) return problem;
            var sets = await contracts.GetSignatureSetsAsync(contractId, true, token);
            var current = sets.FirstOrDefault(s => s.Status != SignatureSetStatuses.Superseded);
            if (current is { Status: SignatureSetStatuses.Finalized })
                return Fail<bool>(PaymentPlanErrorCodes.SignatureFinalized, "The signed pages were already finalized.");
            if (current is not null && current.ExpectedPageCount == input.ExpectedPageCount) return OperationResult<bool>.Ok(false);   // idempotent
            if (current is not null)
            {
                current.Status = SignatureSetStatuses.Superseded;
                current.UpdatedAt = now;
                current.Revision++;
                await contracts.SaveChangesAsync(token);   // leaves the one-current index before the new set is inserted
            }
            contracts.AddSignatureSet(new ContractSignatureSet
            {
                Id = Guid.NewGuid(), ContractId = contractId, Version = sets.Count == 0 ? 1 : sets.Max(s => s.Version) + 1, ExpectedPageCount = input.ExpectedPageCount,
                Status = SignatureSetStatuses.Uploading, SignedOn = input.SignedOn, Note = Trim(input.Note, 500), CreatedByUserId = actorUserId, CreatedAt = now,
                UpdatedAt = now,
            });
            Event(contractId, "SIGNED_PAGES_STARTED", actorUserId, $"{input.ExpectedPageCount} page(s)" + (current is null ? "" : $"; restarted (was {current.ExpectedPageCount})"), now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>Uploads (or replaces, before finalization) one page. The file is a VERIFIED SIGNED_CONTRACT_PAGE registered by the caller.</summary>
    public async Task<OperationResult<SignatureSetView>> PutPageAsync(SignatureActor actor, Guid actorUserId, Guid contractId, int pageNumber, Guid fileId,
        CancellationToken cancellationToken)
    {
        if (fileId == Guid.Empty)
            return OperationResult<SignatureSetView>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { ["fileId"] = ["Required."] });
        if (await AuthorizedAsync(actor, actorUserId, contractId, cancellationToken) is { } denied) return denied;
        var verified = await files.RequireVerifiedAsync([fileId], FilePurposes.SignedContractPage, actorUserId, cancellationToken);
        if (!verified.Succeeded) return verified.As<SignatureSetView>();
        var file = verified.Value![0];
        if (file.ContentType == FilePolicies.Pdf && !options.AllowPdfSignedPages)
            return OperationResult<SignatureSetView>.Fail(FileErrorCodes.TypeNotAllowed, "Signed pages must be photos (JPEG, PNG or WebP).");

        return await RunAsync(contractId, async (c, now, token) =>
        {
            if (StateProblem(c) is { } problem) return problem;
            var set = (await contracts.GetSignatureSetsAsync(contractId, true, token)).FirstOrDefault(s => s.Status != SignatureSetStatuses.Superseded);
            if (set is null) return Fail<bool>(PaymentPlanErrorCodes.SignatureNotStarted, "Start the signed-page upload first (page count).");
            if (set.Status == SignatureSetStatuses.Finalized) return Fail<bool>(PaymentPlanErrorCodes.SignatureFinalized, "The signed pages were already finalized.");
            if (pageNumber < 1 || pageNumber > set.ExpectedPageCount)
                return Fail<bool>(PaymentPlanErrorCodes.PageOutOfRange, $"Page numbers run from 1 to {set.ExpectedPageCount}.");
            var pages = (await contracts.GetSignedPagesAsync(contractId, true, token)).Where(p => p.SignatureSetId == set.Id).ToList();
            if (pages.Any(p => p.FileId == fileId && p.ReplacedAt is null && p.PageNumber == pageNumber)) return OperationResult<bool>.Ok(false);   // same file again
            if ((await contracts.GetSignedPagesAsync(contractId, false, token)).Any(p => p.FileId == fileId) || pages.Any(p => p.FileId == fileId))
                return Fail<bool>(FileErrorCodes.AlreadyLinked, "This file is already used for another page.");
            if (pages.FirstOrDefault(p => p.PageNumber == pageNumber && p.ReplacedAt is null) is { } previous)
            {
                previous.ReplacedAt = now;
                await contracts.SaveChangesAsync(token);   // leaves the one-current-page index before the new row is inserted
            }
            contracts.AddSignedPage(new ContractSignedPage
            {
                Id = Guid.NewGuid(), SignatureSetId = set.Id, ContractId = contractId, PageNumber = pageNumber, FileId = fileId, Sha256 = file.ChecksumSha256!,
                ContentType = file.ContentType, UploadedByUserId = actorUserId, UploadedAt = now,
            });
            set.UpdatedAt = now;
            set.Revision++;
            Event(contractId, "SIGNED_PAGE_UPLOADED", actorUserId, $"set v{set.Version} page {pageNumber}/{set.ExpectedPageCount} sha256={file.ChecksumSha256}", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>
    /// Locks the set once pages 1..N are all present (no gap, no duplicate) and every file is still exactly what was verified (storage re-check, no
    /// transaction open). The contract AWAITING_SIGNATURE → SIGNED (ready for installation; balance still waits for the acceptance).
    /// </summary>
    public async Task<OperationResult<SignatureSetView>> FinalizeAsync(SignatureActor actor, Guid actorUserId, Guid contractId, int? expectedRevision,
        CancellationToken cancellationToken)
    {
        if (await AuthorizedAsync(actor, actorUserId, contractId, cancellationToken) is { } denied) return denied;
        var current = (await contracts.GetSignatureSetsAsync(contractId, false, cancellationToken)).FirstOrDefault(s => s.Status != SignatureSetStatuses.Superseded);
        if (current is { Status: SignatureSetStatuses.Uploading })
        {
            foreach (var page in (await contracts.GetSignedPagesAsync(contractId, false, cancellationToken)).Where(p => p.SignatureSetId == current.Id && p.ReplacedAt is null))
            {
                var intact = await files.VerifyIntegrityAsync(page.FileId, cancellationToken);
                if (!intact.Succeeded) return intact.As<SignatureSetView>();
            }
        }

        var finalized = false;
        var result = await RunAsync(contractId, async (c, now, token) =>
        {
            if (StateProblem(c) is { } problem) return problem;
            var set = (await contracts.GetSignatureSetsAsync(contractId, true, token)).FirstOrDefault(s => s.Status != SignatureSetStatuses.Superseded);
            if (set is null) return Fail<bool>(PaymentPlanErrorCodes.SignatureNotStarted, "Start the signed-page upload first (page count).");
            if (set.Status == SignatureSetStatuses.Finalized) return Fail<bool>(PaymentPlanErrorCodes.SignatureFinalized, "The signed pages were already finalized.");
            if (expectedRevision is { } expected && set.Revision != expected)
                return OperationResult<bool>.Fail(PaymentPlanErrorCodes.StaleRevision, "The signed pages changed meanwhile. Reload and try again.",
                    new { currentRevision = set.Revision });
            var pages = (await contracts.GetSignedPagesAsync(contractId, false, token)).Where(p => p.SignatureSetId == set.Id && p.ReplacedAt is null).ToList();
            var missing = Enumerable.Range(1, set.ExpectedPageCount).Where(n => pages.All(p => p.PageNumber != n)).ToList();
            if (missing.Count > 0 || pages.Count != set.ExpectedPageCount)
                return OperationResult<bool>.Fail(PaymentPlanErrorCodes.PagesIncomplete,
                    $"Upload every page before finalizing: {pages.Count} of {set.ExpectedPageCount} page(s) present.",
                    new { expected = set.ExpectedPageCount, actual = pages.Count, missingPages = missing });
            set.Status = SignatureSetStatuses.Finalized;
            set.FinalizedAt = now;
            set.FinalizedByUserId = actorUserId;
            set.UpdatedAt = now;
            set.Revision++;
            c.SignedAt = now;
            c.SignedSubmittedAt = now;
            Event(contractId, "SIGNED_PAGES_FINALIZED", actorUserId,
                $"paper contract signed on site; set v{set.Version}: {pages.Count} page(s) locked" + (set.SignedOn is { } d ? $"; signed on {d:yyyy-MM-dd}" : ""), now);
            contracts.AddEvent(new ContractEvent { Id = Guid.NewGuid(), ContractId = contractId, EventType = "STATUS_CHANGED", FromStatus = c.Status,
                ToStatus = ContractStatuses.Signed, ActorUserId = actorUserId, Detail = "paper contract signed (photographed pages finalized; not an e-signature)", CreatedAt = now });
            c.Status = ContractStatuses.Signed;
            contracts.AddNotification(c.CustomerUserId, CustomerNotificationTypes.ContractPaperSigned,
                $"Hợp đồng {c.ContractNumber} đã được ký và lưu trữ ({pages.Count} trang). Công trình sẽ được thi công theo lịch đã thống nhất.", now);
            finalized = true;
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
        if (finalized && result.Succeeded && readyFollowUp is not null)
        {
            try
            {
                await readyFollowUp.ContractPaidAsync(contractId, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The installation sweep confirms the schedule of ready contracts every minute; the signature stands.
            }
        }
        return result;
    }

    /// <summary>Every set of the contract (history included), newest first. Read access is decided by the caller (read-all staff / owning customer).</summary>
    public async Task<IReadOnlyList<SignatureSetView>> ListAsync(Guid contractId, CancellationToken cancellationToken)
    {
        var pages = await contracts.GetSignedPagesAsync(contractId, false, cancellationToken);
        return (await contracts.GetSignatureSetsAsync(contractId, false, cancellationToken)).OrderByDescending(s => s.Version).Select(s => View(s, pages)).ToList();
    }

    public static SignatureSetView View(ContractSignatureSet s, IReadOnlyList<ContractSignedPage> allPages)
    {
        var pages = allPages.Where(p => p.SignatureSetId == s.Id).OrderBy(p => p.PageNumber).ThenBy(p => p.UploadedAt).ToList();
        var current = pages.Where(p => p.ReplacedAt is null).ToList();
        return new SignatureSetView(s.Id, s.ContractId, s.Version, s.Status, s.ExpectedPageCount, current.Count,
            Enumerable.Range(1, s.ExpectedPageCount).Where(n => current.All(p => p.PageNumber != n)).ToList(), s.SignedOn, s.Note, s.CreatedByUserId, s.CreatedAt,
            s.FinalizedByUserId, s.FinalizedAt, s.Revision,
            pages.Select(p => new SignedPageView(p.PageNumber, p.FileId, p.Sha256, p.ContentType, p.UploadedByUserId, p.UploadedAt, p.ReplacedAt)).ToList());
    }

    private async Task<OperationResult<SignatureSetView>?> AuthorizedAsync(SignatureActor actor, Guid actorUserId, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await contracts.FindAsync(contractId, false, cancellationToken);
        if (contract is null) return NotFound();
        if (actor == SignatureActor.Technician && await contracts.GetSurveyTechnicianAsync(contract.SurveyRequestId, cancellationToken) != actorUserId) return NotFound();
        return contract.PaymentPlan is null
            ? OperationResult<SignatureSetView>.Fail(PaymentPlanErrorCodes.NotApplicable,
                "This contract follows the previous flow (official PDF + signed scan); photographed pages apply to 30/70 contracts only.")
            : null;
    }

    private static OperationResult<bool>? StateProblem(CustomerContract c) => c.Status switch
    {
        ContractStatuses.AwaitingSignature => null,
        ContractStatuses.Draft => Fail<bool>(PaymentPlanErrorCodes.DepositRequired, "The paper contract is signed after the deposit (30%) is verified."),
        ContractStatuses.Signed or ContractStatuses.Paid => Fail<bool>(PaymentPlanErrorCodes.SignatureFinalized, "The paper contract was already signed and finalized."),
        _ => Fail<bool>(ContractErrorCodes.InvalidState, $"The contract is {c.Status}; it cannot be signed."),
    };

    private async Task<OperationResult<SignatureSetView>> RunAsync(Guid contractId, Func<CustomerContract, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await contracts.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                await contracts.LockContractAsync(contractId, token);
                var c = await contracts.FindAsync(contractId, true, token);
                if (c is null) return (Fail<bool>(ContractErrorCodes.NotFound, "Contract was not found."), false);
                var now = time.GetUtcNow();
                var result = await change(c, now, token);
                if (!result.Succeeded) return (result, false);
                if (result.Value)
                {
                    c.UpdatedAt = now;
                    c.Revision++;
                }
                await contracts.SaveChangesAsync(token);
                return (result, true);
            }, cancellationToken);
            if (!outcome.Succeeded) return outcome.As<SignatureSetView>();
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<SignatureSetView>.Fail(PaymentPlanErrorCodes.StaleRevision, "The signed pages changed meanwhile. Reload and try again.");
        }
        var sets = await ListAsync(contractId, cancellationToken);
        return sets.FirstOrDefault(s => s.Status != SignatureSetStatuses.Superseded) is { } view
            ? OperationResult<SignatureSetView>.Ok(view)
            : Fail<SignatureSetView>(PaymentPlanErrorCodes.SignatureNotStarted, "No signed-page upload exists.");
    }

    private void Event(Guid contractId, string type, Guid actor, string detail, DateTimeOffset now)
        => contracts.AddEvent(new ContractEvent { Id = Guid.NewGuid(), ContractId = contractId, EventType = type, ActorUserId = actor,
            Detail = detail.Length <= 500 ? detail : detail[..500], CreatedAt = now });

    private static OperationResult<SignatureSetView> NotFound() => Fail<SignatureSetView>(ContractErrorCodes.NotFound, "Contract was not found.");
    private static OperationResult<T> Fail<T>(string code, string message) => OperationResult<T>.Fail(code, message);
    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();
}
