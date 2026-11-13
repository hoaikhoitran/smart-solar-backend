using System.Security.Cryptography;
using System.Text.Json;
using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;

namespace SmartSolar.Modules.Quotations;

public sealed record QuotationHeaderInput(decimal LaborCost, decimal TransportCost, decimal OtherServiceCost, decimal DiscountAmount,
    decimal TaxRatePercent, DateTimeOffset? ValidUntil, string? Terms, string? Notes);

public sealed record QuotationLineInput(Guid? ProductId, string? Name, string? Unit, decimal Quantity, decimal? UnitPrice, string? Description);

public sealed record QuotationLineView(Guid LineId, int LineNumber, Guid? ProductId, string? Sku, string Name, string? Brand, string? Model,
    string Unit, decimal Quantity, decimal UnitPrice, decimal? CatalogUnitPrice, decimal LineTotal, string? Description);

public sealed record QuotationEventView(string EventType, Guid ActorUserId, string? Detail, DateTimeOffset CreatedAt);

public sealed record QuotationView(
    Guid QuotationId, string QuoteNumber, Guid SurveyRequestId, int Version, Guid? PreviousQuotationId, string Status, string Currency,
    IReadOnlyList<QuotationLineView> Lines, decimal EquipmentSubtotal, decimal LaborCost, decimal TransportCost, decimal OtherServiceCost,
    decimal Subtotal, decimal DiscountAmount, decimal TaxRatePercent, decimal TaxAmount, decimal TotalAmount, DateTimeOffset? ValidUntil,
    bool IsExpired, bool CanAccept, string? Terms, string? Notes, Guid? DocumentFileId, Guid OnSiteSurveyReportId, int OnSiteSurveyVersion,
    DateTimeOffset? IssuedAt, DateTimeOffset? AcceptedAt, string? RevisionReason, int Revision, IReadOnlyList<QuotationEventView>? Events,
    string Origin = QuotationOrigins.Sales, PricingBasis? PricingBasis = null, int? CurrentSurveyVersion = null, bool SurveyChanged = false,
    PricingReview? PricingReview = null, PricingApprovalView? PricingApproval = null, DateTimeOffset? ClosedAt = null, string? CloseReason = null);

/// <summary>
/// Staff-only state of an AUTOMATIC draft's pricing issues, recomputed from the current lines and costs: <see cref="Outstanding"/> are the
/// generation-time issues not resolved yet. A missing price, a missing panel / inverter / labor or an invalid total blocks the issue
/// until it is fixed; the others (unavailable or wrong-kind product, conditions) can be issued with a review note
/// (<see cref="NoteRequired"/>).
/// </summary>
public sealed record PricingReview(IReadOnlyList<PricingIssue> Outstanding, bool NoteRequired);

public sealed class QuotationOptions
{
    public const string SectionName = "Quotation";
    public decimal DefaultTaxRatePercent { get; set; } = 8m;
    public int MaxLines { get; set; } = 100;
    public int MaxValidityDays { get; set; } = 90;

    /// <summary>Validity of an automatically issued quotation (days from issue; at most <see cref="MaxValidityDays"/>).</summary>
    public int AutomaticValidityDays { get; set; } = 30;

    /// <summary>Terms printed on automatically issued quotations (null = none).</summary>
    public string? AutomaticTerms { get; set; }

    /// <summary>
    /// The configuration problems of these options (empty when valid). The bounds are the ones the quotation rules and the database
    /// already enforce (tax 0–20 %, ck_quotation_tax_rate), so an invalid setting fails at startup instead of failing every quotation.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (DefaultTaxRatePercent is < 0 or > 20) problems.Add($"{SectionName}:DefaultTaxRatePercent must be between 0 and 20 (is {DefaultTaxRatePercent}).");
        if (MaxLines is < 1 or > 1000) problems.Add($"{SectionName}:MaxLines must be between 1 and 1000 (is {MaxLines}).");
        if (MaxValidityDays is < 1 or > 365) problems.Add($"{SectionName}:MaxValidityDays must be between 1 and 365 (is {MaxValidityDays}).");
        if (AutomaticValidityDays < 1 || AutomaticValidityDays > MaxValidityDays)
            problems.Add($"{SectionName}:AutomaticValidityDays must be between 1 and MaxValidityDays ({MaxValidityDays}) (is {AutomaticValidityDays}).");
        if (AutomaticTerms is { Length: > 4000 }) problems.Add($"{SectionName}:AutomaticTerms must be at most 4000 characters.");
        return problems;
    }
}

/// <summary>
/// Quotation lifecycle: DRAFT → ISSUED → ACCEPTED, or ISSUED → REVISION_REQUESTED → (new version
/// DRAFT → ISSUED, previous SUPERSEDED). Sales issues without manager approval. Every transition is
/// a conditional single-row update, and the database allows at most one DRAFT and one
/// ISSUED/ACCEPTED quotation per survey request, so concurrent edits, issues and acceptances can
/// never produce two live offers.
/// </summary>
public sealed partial class QuotationHandlers(IQuotationUnitOfWork unitOfWork, FileService files, QuotationOptions options, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ Sales

    public async Task<OperationResult<QuotationView>> CreateAsync(Guid salesUserId, Guid surveyRequestId, QuotationHeaderInput header, CancellationToken cancellationToken)
    {
        var headerError = ValidateHeader(header);
        if (headerError is not null) return Invalid(headerError);
        var source = await unitOfWork.GetSourceAsync(surveyRequestId, cancellationToken);
        if (source is null || source.AssignedSaleId != salesUserId)
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.NotFound, "Survey request was not found.");
        if (IsClosedRequest(source.SurveyRequestStatus)) return RequestClosedError<QuotationView>();
        if (source.OnSiteSurveyStatus != "COMPLETED" || source.OnSiteSurveyReportId is null)
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.SurveyNotCompleted, "The on-site survey must be completed before quoting.");
        if ((await unitOfWork.GetSurveyBasisAsync(surveyRequestId, cancellationToken))?.InstallationFeasibility == "NOT_FEASIBLE")
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.SurveyNotFeasible,
                "The on-site survey concluded the installation is NOT_FEASIBLE; no installation quotation can be made. Amend the survey if that was wrong.");

        var existing = await unitOfWork.ListBySurveyRequestAsync(surveyRequestId, cancellationToken);
        if (existing.Any(q => q.Status == QuotationStatuses.Draft))
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.DraftExists, "A draft quotation already exists for this request.");
        if (existing.Any(q => q.Status is QuotationStatuses.Issued or QuotationStatuses.Accepted))
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.ActiveExists, "An issued or accepted quotation exists; create a revision instead.");

        var now = time.GetUtcNow();
        var quotation = new Quotation
        {
            Id = Guid.NewGuid(), QuoteNumber = NewQuoteNumber(now), SurveyRequestId = surveyRequestId, OnSiteSurveyReportId = source.OnSiteSurveyReportId.Value,
            OnSiteSurveyVersion = source.OnSiteSurveyVersion, Version = 1, Status = QuotationStatuses.Draft, CustomerUserId = source.CustomerUserId,
            SalesUserId = salesUserId, Currency = QuotationPricing.Currency, CreatedAt = now, UpdatedAt = now,
        };
        ApplyHeader(quotation, header);
        return await PersistNewAsync(quotation, [], salesUserId, QuotationEventTypes.Created, null, cancellationToken);
    }

    public async Task<OperationResult<QuotationView>> UpdateHeaderAsync(Guid salesUserId, Guid quotationId, int revision, QuotationHeaderInput header, CancellationToken cancellationToken)
    {
        var headerError = ValidateHeader(header);
        if (headerError is not null) return Invalid(headerError);
        return await MutateDraftAsync(salesUserId, quotationId, revision, (q, lines, _) =>
        {
            ApplyHeader(q, header);
            return Task.FromResult<string?>(null);
        }, QuotationEventTypes.Updated, cancellationToken);
    }

    public async Task<OperationResult<QuotationView>> AddLineAsync(Guid salesUserId, Guid quotationId, int revision, QuotationLineInput input, CancellationToken cancellationToken)
    {
        if (input.Quantity <= 0 || input.Quantity > 100_000 || input.UnitPrice is < 0 or > QuotationPricing.MaxAmount)
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.InvalidLine, "Quantity must be positive and the unit price non-negative.");

        CatalogProductSnapshot? product = null;
        if (input.ProductId is { } productId)
        {
            product = await unitOfWork.GetProductAsync(productId, cancellationToken);
            if (product is null || !product.IsActive || product.Currency != QuotationPricing.Currency)
                return OperationResult<QuotationView>.Fail(QuotationErrorCodes.ProductUnavailable, "The product is not available for quoting in VND.");
            // A missing catalog price is never taken as free: Sales enters the price (a zero price then needs a Manager approval).
            if (product.UnitPrice <= 0 && input.UnitPrice is null)
                return OperationResult<QuotationView>.Fail(QuotationErrorCodes.PriceMissing, $"Product {product.Sku} has no catalog price; enter the unit price.");
        }
        else if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Unit) || input.UnitPrice is null)
        {
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.InvalidLine, "A custom line needs a name, a unit and a unit price.");
        }

        return await MutateDraftAsync(salesUserId, quotationId, revision, (q, lines, _) =>
        {
            if (lines.Count >= options.MaxLines) return Task.FromResult<string?>($"At most {options.MaxLines} lines are allowed.");
            var unitPrice = QuotationPricing.Round(input.UnitPrice ?? product!.UnitPrice);
            var line = new QuotationLine
            {
                Id = Guid.NewGuid(), QuotationId = q.Id, LineNumber = lines.Count == 0 ? 1 : lines.Max(l => l.LineNumber) + 1,
                ProductId = product?.ProductId, Sku = product?.Sku, Name = Trim(product?.Name ?? input.Name, 255)!, Brand = product?.Brand, Model = product?.Model,
                Unit = Trim(product?.Unit ?? input.Unit, 20)!, Quantity = input.Quantity, UnitPrice = unitPrice, CatalogUnitPrice = product?.UnitPrice,
                LineTotal = QuotationPricing.LineTotal(input.Quantity, unitPrice), Description = Trim(input.Description, 500),
            };
            unitOfWork.AddLine(line);
            lines.Add(line);
            return Task.FromResult<string?>(null);
        }, QuotationEventTypes.Updated, cancellationToken);
    }

    public Task<OperationResult<QuotationView>> UpdateLineAsync(Guid salesUserId, Guid quotationId, Guid lineId, int revision, decimal quantity, decimal? unitPrice, string? description, CancellationToken cancellationToken)
    {
        if (quantity <= 0 || quantity > 100_000 || unitPrice is < 0 or > QuotationPricing.MaxAmount)
            return Task.FromResult(OperationResult<QuotationView>.Fail(QuotationErrorCodes.InvalidLine, "Quantity must be positive and the unit price non-negative."));
        return MutateDraftAsync(salesUserId, quotationId, revision, (q, lines, _) =>
        {
            var line = lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null) return Task.FromResult<string?>("Line was not found.");
            line.Quantity = quantity;
            if (unitPrice is { } price) line.UnitPrice = QuotationPricing.Round(price);
            line.Description = Trim(description, 500) ?? line.Description;
            line.LineTotal = QuotationPricing.LineTotal(line.Quantity, line.UnitPrice);
            return Task.FromResult<string?>(null);
        }, QuotationEventTypes.Updated, cancellationToken);
    }

    public Task<OperationResult<QuotationView>> RemoveLineAsync(Guid salesUserId, Guid quotationId, Guid lineId, int revision, CancellationToken cancellationToken)
        => MutateDraftAsync(salesUserId, quotationId, revision, (q, lines, _) =>
        {
            var line = lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null) return Task.FromResult<string?>("Line was not found.");
            unitOfWork.RemoveLine(line);
            lines.Remove(line);
            return Task.FromResult<string?>(null);
        }, QuotationEventTypes.Updated, cancellationToken);

    /// <summary>DRAFT → ISSUED. A revision supersedes its predecessor in the same transaction (refused if the predecessor was accepted meanwhile).</summary>
    /// <param name="reviewNote">
    /// For an AUTOMATIC draft whose remaining pricing issues may be accepted by Sales (unavailable or wrong-kind product, conditions):
    /// why it is correct to issue it anyway. Recorded in the ISSUED event; ignored for manual quotations.
    /// </param>
    public async Task<OperationResult<QuotationView>> IssueAsync(Guid salesUserId, Guid quotationId, int revision, CancellationToken cancellationToken,
        string? reviewNote = null)
    {
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (quotation is null) return NotFound();
        var source = await unitOfWork.GetSourceAsync(quotation.SurveyRequestId, cancellationToken);
        if (source?.AssignedSaleId != salesUserId) return NotFound();
        if (IsClosedRequest(source.SurveyRequestStatus)) return RequestClosedError<QuotationView>();
        var snapshot = CustomerSnapshotOf(source);
        if (reviewNote is { Length: > 1000 }) return Invalid(new() { ["reviewNote"] = ["At most 1000 characters."] });

        return await RunAsync(quotationId, async (q, lines, now, token) =>
        {
            if (q.Status != QuotationStatuses.Draft) return OperationResult<bool>.Fail(QuotationErrorCodes.NotDraft, "Only drafts can be issued.");
            if (q.Revision != revision) return Stale(q);
            if (lines.Count == 0) return OperationResult<bool>.Fail(QuotationErrorCodes.Empty, "Add at least one line before issuing.");
            if (SurveyChangedError(q, source.OnSiteSurveyVersion) is { } changed) return changed;
            Recalculate(q, lines);
            if (await ReviewOfAsync(q, lines, BasisOf(q), token) is { } review && review.Outstanding.Count > 0)
            {
                var blocking = review.Outstanding.Where(i => !Acknowledgeable.Contains(i.Code)).ToList();
                if (blocking.Count > 0)
                    return OperationResult<bool>.Fail(QuotationErrorCodes.PricingReviewRequired,
                        "Resolve the pricing issues first: a missing price is never issued as free.", new { outstanding = blocking });
                if (string.IsNullOrWhiteSpace(reviewNote))
                    return OperationResult<bool>.Fail(QuotationErrorCodes.PricingReviewRequired,
                        "Explain in reviewNote why the remaining pricing issues are acceptable.", new { outstanding = review.Outstanding });
            }
            if (q.TotalAmount <= 0 || q.TotalAmount > QuotationPricing.MaxAmount)
                return OperationResult<bool>.Fail(QuotationErrorCodes.InvalidLine, "The total amount must be positive.");
            if (await BomMismatchesAsync(q.SurveyRequestId, lines, token) is { Count: > 0 } mismatches) return BomMismatchError(mismatches);
            // Every discount, reduced or zero price needs a Manager approval of exactly this pricing.
            var approval = ApprovalOf(q, lines, await unitOfWork.GetEventsAsync(q.Id, token));
            if (approval.Status is not (PricingApprovalStatuses.NotRequired or PricingApprovalStatuses.Approved))
                return OperationResult<bool>.Fail(QuotationErrorCodes.PricingApprovalRequired,
                    "A Manager must approve the discount and reduced prices of exactly this pricing before it is issued.",
                    new { approvalStatus = approval.Status, adjustments = approval.Adjustments });
            if (q.ValidUntil is not { } validUntil || validUntil <= now)
                return OperationResult<bool>.Fail(CommonErrorCodes.ValidationFailed, "validUntil must be in the future.");

            if (q.PreviousQuotationId is { } previousId)
            {
                var previous = await unitOfWork.FindAsync(previousId, false, token);
                // A REJECTED predecessor keeps its outcome (the new version is a new offer, not a reuse of the rejected one).
                // An ACCEPTED predecessor is replaced only when its survey was amended and no contract was made from it.
                var superseded = previous is not null
                    && (previous.Status == QuotationStatuses.Rejected
                        || await unitOfWork.TryTransitionAsync(previousId, QuotationStatuses.RevisionRequested, QuotationStatuses.Superseded, now, token)
                        || await unitOfWork.TryTransitionAsync(previousId, QuotationStatuses.Issued, QuotationStatuses.Superseded, now, token)
                        || (previous.OnSiteSurveyVersion < source.OnSiteSurveyVersion && !await unitOfWork.HasLiveContractAsync(previousId, token)
                            && await unitOfWork.TryTransitionAsync(previousId, QuotationStatuses.Accepted, QuotationStatuses.Superseded, now, token)));
                if (!superseded)
                    return OperationResult<bool>.Fail(QuotationErrorCodes.CannotRevise, "The previous version was accepted or changed meanwhile; this revision cannot be issued.");
                if (previous!.Status != QuotationStatuses.Rejected)
                    AddEvent(previousId, QuotationEventTypes.Superseded, salesUserId, $"by version {q.Version}", now);
            }

            q.Status = QuotationStatuses.Issued;
            q.IssuedAt = now;
            q.CustomerSnapshot = snapshot;
            unitOfWork.AddNotification(q.CustomerUserId, CustomerNotificationTypes.QuotationIssued, QuotationIssuedMessage(q), now);
            AddEvent(q.Id, QuotationEventTypes.Issued, salesUserId,
                string.IsNullOrWhiteSpace(reviewNote) ? $"total={q.TotalAmount} {q.Currency}" : $"total={q.TotalAmount} {q.Currency}; review: {Trim(reviewNote, 500)}", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>Customer notification of an issued quotation (manual or automatic issue): the customer accepts it or requests changes.</summary>
    private static string QuotationIssuedMessage(Quotation q)
        => $"Quotation {q.QuoteNumber} (version {q.Version}, {q.TotalAmount:N0} {q.Currency}) is ready for your review. Please accept it or request changes.";

    /// <summary>New DRAFT version copied from an ISSUED or REVISION_REQUESTED quotation (the original stays unchanged).</summary>
    public async Task<OperationResult<QuotationView>> CreateRevisionAsync(Guid salesUserId, Guid quotationId, CancellationToken cancellationToken)
    {
        var original = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (original is null) return NotFound();
        var source = await unitOfWork.GetSourceAsync(original.SurveyRequestId, cancellationToken);
        if (source?.AssignedSaleId != salesUserId) return NotFound();
        if (IsClosedRequest(source.SurveyRequestStatus)) return RequestClosedError<QuotationView>();
        // A REJECTED quotation stays rejected; Sales may answer with a new version while the request is open.
        // An accepted quotation is revised only when the survey changed after it and no contract was made from it (it can no longer
        // become a contract, so the customer needs a new version).
        var staleAccepted = original.Status == QuotationStatuses.Accepted && original.OnSiteSurveyVersion < source.OnSiteSurveyVersion
                            && !await unitOfWork.HasLiveContractAsync(original.Id, cancellationToken);
        if (original.Status is not (QuotationStatuses.Issued or QuotationStatuses.RevisionRequested or QuotationStatuses.Rejected) && !staleAccepted)
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.CannotRevise, "Only issued, sent-back or rejected quotations can be revised.");
        if (original.Status == QuotationStatuses.Rejected
            && (await unitOfWork.ListBySurveyRequestAsync(original.SurveyRequestId, cancellationToken)).Any(q => q.PreviousQuotationId == original.Id))
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.CannotRevise, "A newer version was already made from this rejected quotation.");

        var all = await unitOfWork.ListBySurveyRequestAsync(original.SurveyRequestId, cancellationToken);
        if (all.Any(q => q.Status == QuotationStatuses.Draft))
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.DraftExists, "A draft quotation already exists for this request.");

        var now = time.GetUtcNow();
        var copy = new Quotation
        {
            Id = Guid.NewGuid(), QuoteNumber = original.QuoteNumber, SurveyRequestId = original.SurveyRequestId,
            // The lines are copied, not recalculated: the copy keeps the survey version they were made for. When the survey changed
            // meanwhile, Sales reviews the lines and confirms the draft against the current version before issuing it.
            OnSiteSurveyReportId = original.OnSiteSurveyReportId, OnSiteSurveyVersion = original.OnSiteSurveyVersion,
            Version = all.Max(q => q.Version) + 1, PreviousQuotationId = original.Id, Status = QuotationStatuses.Draft,
            CustomerUserId = original.CustomerUserId, SalesUserId = salesUserId, Currency = original.Currency, LaborCost = original.LaborCost,
            TransportCost = original.TransportCost, OtherServiceCost = original.OtherServiceCost, DiscountAmount = original.DiscountAmount,
            TaxRatePercent = original.TaxRatePercent, ValidUntil = original.ValidUntil, Terms = original.Terms, Notes = original.Notes,
            CreatedAt = now, UpdatedAt = now,
        };
        var lines = (await unitOfWork.GetLinesAsync(original.Id, false, cancellationToken))
            .Select(l => new QuotationLine
            {
                Id = Guid.NewGuid(), QuotationId = copy.Id, LineNumber = l.LineNumber, ProductId = l.ProductId, Sku = l.Sku, Name = l.Name,
                Brand = l.Brand, Model = l.Model, Unit = l.Unit, Quantity = l.Quantity, UnitPrice = l.UnitPrice, CatalogUnitPrice = l.CatalogUnitPrice,
                LineTotal = l.LineTotal, Description = l.Description,
            }).ToList();
        return await PersistNewAsync(copy, lines, salesUserId, QuotationEventTypes.RevisionCreated,
            copy.OnSiteSurveyVersion < source.OnSiteSurveyVersion
                ? $"from version {original.Version}; lines of survey v{copy.OnSiteSurveyVersion}, survey is now v{source.OnSiteSurveyVersion}"
                : $"from version {original.Version}", cancellationToken);
    }

    /// <summary>
    /// Sales states that a DRAFT made for an older survey version was reviewed against the current one (after adjusting its lines):
    /// the draft is re-based on the current version. A reason is required and recorded; issued quotations are never re-based.
    /// </summary>
    public async Task<OperationResult<QuotationView>> ConfirmSurveyVersionAsync(Guid salesUserId, Guid quotationId, int revision, string? reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            return Invalid(new() { ["reason"] = ["A reason of at most 1000 characters is required."] });
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (quotation is null) return NotFound();
        var source = await unitOfWork.GetSourceAsync(quotation.SurveyRequestId, cancellationToken);
        if (source?.AssignedSaleId != salesUserId) return NotFound();

        return await RunAsync(quotationId, async (q, lines, now, token) =>
        {
            if (q.Status != QuotationStatuses.Draft) return OperationResult<bool>.Fail(QuotationErrorCodes.NotDraft, "Only drafts can be re-based.");
            if (q.Revision != revision) return Stale(q);
            if (q.OnSiteSurveyVersion == source.OnSiteSurveyVersion)
                return OperationResult<bool>.Fail(CommonErrorCodes.InvalidState, "The draft is already based on the current survey version.");
            // The confirmation is a statement that the lines match the new survey: it cannot re-label old quantities as reviewed.
            if (await BomMismatchesAsync(q.SurveyRequestId, lines, token) is { Count: > 0 } mismatches) return BomMismatchError(mismatches);
            AddEvent(q.Id, QuotationEventTypes.SurveyVersionConfirmed, salesUserId,
                $"survey v{q.OnSiteSurveyVersion} → v{source.OnSiteSurveyVersion}: {Trim(reason, 400)}", now);
            q.OnSiteSurveyVersion = source.OnSiteSurveyVersion;
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>
    /// Called after a completed survey was amended: every quotation still based on an older version gets one SURVEY_AMENDED event
    /// for the new version (idempotent: a repeat adds nothing). The quotations themselves are not changed; they cannot be issued,
    /// accepted or turned into a contract until Sales handles them, and they appear in the review queue.
    /// </summary>
    public async Task<int> RecordSurveyAmendedAsync(Guid surveyRequestId, int newVersion, Guid actorUserId, CancellationToken cancellationToken)
    {
        var detail = $"survey v{newVersion}";
        var affected = (await unitOfWork.ListBySurveyRequestAsync(surveyRequestId, cancellationToken))
            .Where(q => !QuotationStatuses.IsTerminal(q.Status) && q.OnSiteSurveyVersion < newVersion).ToList();
        var added = 0;
        await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
        {
            var now = time.GetUtcNow();
            foreach (var q in affected)
            {
                if ((await unitOfWork.GetEventsAsync(q.Id, token)).Any(e => e.EventType == QuotationEventTypes.SurveyAmended && e.Detail == detail)) continue;
                AddEvent(q.Id, QuotationEventTypes.SurveyAmended, actorUserId, detail, now);
                added++;
            }
            await unitOfWork.SaveChangesAsync(token);
            return (true, true);
        }, cancellationToken);
        return added;
    }

    public async Task<OperationResult<QuotationView>> AttachDocumentAsync(Guid salesUserId, Guid quotationId, Guid fileId, CancellationToken cancellationToken)
    {
        var verified = await files.RequireVerifiedAsync([fileId], FilePurposes.QuotationDocument, salesUserId, cancellationToken);
        if (!verified.Succeeded) return verified.As<QuotationView>();
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (quotation is null || (await unitOfWork.GetSourceAsync(quotation.SurveyRequestId, cancellationToken))?.AssignedSaleId != salesUserId) return NotFound();

        return await RunAsync(quotationId, (q, _, now, _) =>
        {
            if (q.Status is not (QuotationStatuses.Draft or QuotationStatuses.Issued))
                return Task.FromResult(OperationResult<bool>.Fail(CommonErrorCodes.InvalidState, "Documents can be attached to drafts or issued quotations only."));
            if (q.DocumentFileId is not null && q.Status == QuotationStatuses.Issued)
                return Task.FromResult(OperationResult<bool>.Fail(CommonErrorCodes.InvalidState, "The issued quotation already has its document."));
            q.DocumentFileId = fileId;
            AddEvent(q.Id, QuotationEventTypes.DocumentAttached, salesUserId, fileId.ToString(), now);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);
    }

    public async Task<OperationResult<QuotationView>> GetForSalesAsync(Guid salesUserId, bool isManager, Guid quotationId, CancellationToken cancellationToken)
    {
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (quotation is null) return NotFound();
        if (!isManager && (await unitOfWork.GetSourceAsync(quotation.SurveyRequestId, cancellationToken))?.AssignedSaleId != salesUserId) return NotFound();
        return OperationResult<QuotationView>.Ok(await ViewAsync(quotation, includeEvents: true, cancellationToken));
    }

    public async Task<OperationResult<IReadOnlyList<QuotationView>>> ListForSurveyRequestAsync(Guid salesUserId, bool isManager, Guid surveyRequestId, CancellationToken cancellationToken)
    {
        var source = await unitOfWork.GetSourceAsync(surveyRequestId, cancellationToken);
        if (source is null || (!isManager && source.AssignedSaleId != salesUserId))
            return OperationResult<IReadOnlyList<QuotationView>>.Fail(QuotationErrorCodes.NotFound, "Survey request was not found.");
        var list = new List<QuotationView>();
        foreach (var q in await unitOfWork.ListBySurveyRequestAsync(surveyRequestId, cancellationToken))
            list.Add(await ViewAsync(q, includeEvents: false, cancellationToken));
        return OperationResult<IReadOnlyList<QuotationView>>.Ok(list);
    }

    // ------------------------------------------------------------------ Customer

    public async Task<IReadOnlyList<QuotationView>> ListForCustomerAsync(Guid customerUserId, CancellationToken cancellationToken)
    {
        var list = new List<QuotationView>();
        foreach (var q in await unitOfWork.ListForCustomerAsync(customerUserId, cancellationToken))
            list.Add(await ViewAsync(q, includeEvents: false, cancellationToken, forCustomer: true));
        return list;
    }

    public async Task<OperationResult<QuotationView>> GetForCustomerAsync(Guid customerUserId, Guid quotationId, CancellationToken cancellationToken)
    {
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        return quotation is null || quotation.CustomerUserId != customerUserId || quotation.Status == QuotationStatuses.Draft
            ? NotFound()
            : OperationResult<QuotationView>.Ok(await ViewAsync(quotation, includeEvents: false, cancellationToken, forCustomer: true));
    }

    public async Task<OperationResult<QuotationView>> AcceptAsync(Guid customerUserId, Guid quotationId, CancellationToken cancellationToken)
    {
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (quotation is null || quotation.CustomerUserId != customerUserId || quotation.Status == QuotationStatuses.Draft) return NotFound();
        if (quotation.Status == QuotationStatuses.Accepted) return OperationResult<QuotationView>.Ok(await ViewAsync(quotation, false, cancellationToken, forCustomer: true));

        return await RunAsync(quotationId, async (q, _, now, token) =>
        {
            if (q.ValidUntil is { } validUntil && validUntil <= now)
                return OperationResult<bool>.Fail(QuotationErrorCodes.Expired, "This quotation has expired; ask Sales for a new version.");
            if (q.Status == QuotationStatuses.Issued && SurveyChangedError(q, await unitOfWork.GetSurveyVersionAsync(q.SurveyRequestId, token)) is { } changed)
                return changed;
            if (!await unitOfWork.TryAcceptAsync(q.Id, now, token))
                return q.Status == QuotationStatuses.Issued
                    ? OperationResult<bool>.Fail(QuotationErrorCodes.SurveyChanged, "The on-site survey changed meanwhile; this quotation can no longer be accepted.")
                    : OperationResult<bool>.Fail(QuotationErrorCodes.NotIssued, "This quotation can no longer be accepted (it was revised or changed).");
            AddEvent(q.Id, QuotationEventTypes.Accepted, customerUserId, null, now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken, trackedChanges: false, forCustomer: true);
    }

    public async Task<OperationResult<QuotationView>> RequestRevisionAsync(Guid customerUserId, Guid quotationId, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            return Invalid(new() { ["reason"] = ["A reason of at most 2000 characters is required."] });
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (quotation is null || quotation.CustomerUserId != customerUserId || quotation.Status == QuotationStatuses.Draft) return NotFound();

        return await RunAsync(quotationId, (q, _, now, _) =>
        {
            if (q.Status != QuotationStatuses.Issued)
                return Task.FromResult(OperationResult<bool>.Fail(QuotationErrorCodes.NotIssued, "Only issued quotations can be sent back for revision."));
            q.Status = QuotationStatuses.RevisionRequested;
            q.RevisionRequestedAt = now;
            q.RevisionReason = reason.Trim();
            AddEvent(q.Id, QuotationEventTypes.RevisionRequested, customerUserId, Trim(reason, 500), now);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken, forCustomer: true);
    }

    // ------------------------------------------------------------------ internals

    private async Task<OperationResult<QuotationView>> MutateDraftAsync(Guid salesUserId, Guid quotationId, int revision,
        Func<Quotation, List<QuotationLine>, CancellationToken, Task<string?>> change, string eventType, CancellationToken cancellationToken)
    {
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (quotation is null || (await unitOfWork.GetSourceAsync(quotation.SurveyRequestId, cancellationToken))?.AssignedSaleId != salesUserId) return NotFound();

        return await RunAsync(quotationId, async (q, lines, now, token) =>
        {
            if (q.Status != QuotationStatuses.Draft) return OperationResult<bool>.Fail(QuotationErrorCodes.NotDraft, "Issued quotations are immutable; create a revision.");
            if (q.Revision != revision) return Stale(q);
            var error = await change(q, lines, token);
            if (error is not null) return OperationResult<bool>.Fail(QuotationErrorCodes.InvalidLine, error);
            // The change keeps `lines` in sync with what will be stored; totals come from that list.
            Recalculate(q, lines);
            if (q.DiscountAmount > q.Subtotal)
                return OperationResult<bool>.Fail(QuotationErrorCodes.InvalidLine, "The discount cannot exceed the subtotal.");
            AddEvent(q.Id, eventType, salesUserId, null, now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>Loads the quotation and lines tracked, applies the change, bumps the revision and commits.</summary>
    private async Task<OperationResult<QuotationView>> RunAsync(Guid quotationId,
        Func<Quotation, List<QuotationLine>, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change,
        CancellationToken cancellationToken, bool trackedChanges = true, bool forCustomer = false)
    {
        try
        {
            var outcome = await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                // Lock first, then read: a concurrent editor waits and then sees the new revision (STALE),
                // instead of racing on derived values such as the next line number.
                await unitOfWork.LockAsync(quotationId, token);
                var q = await unitOfWork.FindAsync(quotationId, true, token);
                if (q is null) return (OperationResult<bool>.Fail(QuotationErrorCodes.NotFound, "Quotation was not found."), false);
                var lines = (await unitOfWork.GetLinesAsync(quotationId, true, token)).ToList();
                var now = time.GetUtcNow();
                var result = await change(q, lines, now, token);
                if (!result.Succeeded) return (result, false);
                if (trackedChanges)
                {
                    q.UpdatedAt = now;
                    q.Revision++;
                }
                await unitOfWork.SaveChangesAsync(token);
                return (result, true);
            }, cancellationToken);
            if (!outcome.Succeeded) return outcome.As<QuotationView>();
        }
        catch (PersistenceConflictException ex)
        {
            return ex.Kind == PersistenceConflictException.UniqueViolation && IsOneLivePerRequestConstraint(ex.Constraint)
                ? OperationResult<QuotationView>.Fail(QuotationErrorCodes.ActiveExists, "Another quotation for this request is already live.")
                : OperationResult<QuotationView>.Fail(QuotationErrorCodes.StaleRevision, "The quotation was changed meanwhile. Reload and try again.");
        }

        return OperationResult<QuotationView>.Ok(await ViewAsync((await unitOfWork.FindAsync(quotationId, false, cancellationToken))!, !forCustomer, cancellationToken, forCustomer));
    }

    /// <summary>
    /// The "one draft / one live quotation per request" indexes (PostgreSQL names; SQLite reports the
    /// columns instead). Any other unique violation, e.g. on the line number, is a concurrent edit.
    /// </summary>
    public static bool IsOneLivePerRequestConstraint(string? constraint)
        => constraint is "ux_quotation_one_live" or "ux_quotation_one_draft"
           || (constraint?.StartsWith("quotation.survey_request_id", StringComparison.Ordinal) ?? false);

    private async Task<OperationResult<QuotationView>> PersistNewAsync(Quotation quotation, List<QuotationLine> lines, Guid actor, string eventType, string? detail, CancellationToken cancellationToken)
    {
        Recalculate(quotation, lines);
        if (quotation.DiscountAmount > quotation.Subtotal)
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.InvalidLine, "The discount cannot exceed the subtotal.");
        try
        {
            var outcome = await unitOfWork.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                // Same request lock as withdrawal/closure: a closure either sees this new draft (and cancels it) or this sees the closure.
                if (await RequestClosedUnderLockAsync(quotation.SurveyRequestId, token)) return (RequestClosedError<bool>(), false);
                unitOfWork.AddQuotation(quotation);
                foreach (var line in lines) unitOfWork.AddLine(line);
                AddEvent(quotation.Id, eventType, actor, detail, quotation.CreatedAt);
                await unitOfWork.SaveChangesAsync(token);
                return (OperationResult<bool>.Ok(true), true);
            }, cancellationToken);
            if (!outcome.Succeeded) return outcome.As<QuotationView>();
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.DraftExists, "A draft quotation already exists for this request.");
        }
        return OperationResult<QuotationView>.Ok(await ViewAsync(quotation, true, cancellationToken));
    }

    /// <summary>Customer name, contact and site captured when a quotation is issued (JSON).</summary>
    private static string CustomerSnapshotOf(QuotationSource source) => JsonSerializer.Serialize(new
    {
        source.CustomerName, source.CustomerPhone, source.CustomerEmail, source.PropertyName, source.Province, source.District, source.StreetLine,
    }, Json);

    private static void Recalculate(Quotation q, IReadOnlyList<QuotationLine> lines)
    {
        var totals = QuotationPricing.Compute(lines.Select(l => l.LineTotal), q.LaborCost, q.TransportCost, q.OtherServiceCost, q.DiscountAmount, q.TaxRatePercent);
        q.EquipmentSubtotal = totals.EquipmentSubtotal;
        q.Subtotal = totals.Subtotal;
        q.TaxAmount = totals.TaxAmount;
        q.TotalAmount = totals.TotalAmount;
    }

    private void ApplyHeader(Quotation q, QuotationHeaderInput h)
    {
        q.LaborCost = QuotationPricing.Round(h.LaborCost);
        q.TransportCost = QuotationPricing.Round(h.TransportCost);
        q.OtherServiceCost = QuotationPricing.Round(h.OtherServiceCost);
        q.DiscountAmount = QuotationPricing.Round(h.DiscountAmount);
        q.TaxRatePercent = h.TaxRatePercent;
        q.ValidUntil = h.ValidUntil?.ToUniversalTime();
        q.Terms = Trim(h.Terms, 4000);
        q.Notes = Trim(h.Notes, 2000);
    }

    private Dictionary<string, string[]>? ValidateHeader(QuotationHeaderInput h)
    {
        var errors = new Dictionary<string, string[]>();
        foreach (var (name, value) in new[] { ("laborCost", h.LaborCost), ("transportCost", h.TransportCost), ("otherServiceCost", h.OtherServiceCost), ("discountAmount", h.DiscountAmount) })
            if (value is < 0 or > QuotationPricing.MaxAmount) errors[name] = ["Must be between 0 and 999,999,999,999 VND."];
        if (h.TaxRatePercent is < 0 or > 20) errors["taxRatePercent"] = ["Must be between 0 and 20."];
        if (h.ValidUntil is { } until && until > time.GetUtcNow().AddDays(options.MaxValidityDays))
            errors["validUntil"] = [$"Must be within {options.MaxValidityDays} days."];
        return errors.Count == 0 ? null : errors;
    }

    /// <param name="forCustomer">The customer's view: no internal pricing diagnostics (pricing basis, review state).</param>
    private async Task<QuotationView> ViewAsync(Quotation q, bool includeEvents, CancellationToken cancellationToken, bool forCustomer = false)
    {
        var lines = await unitOfWork.GetLinesAsync(q.Id, false, cancellationToken);
        var now = time.GetUtcNow();
        var expired = q.ValidUntil is { } until && until <= now;
        var currentSurveyVersion = await unitOfWork.GetSurveyVersionAsync(q.SurveyRequestId, cancellationToken);
        var surveyChanged = !QuotationStatuses.IsTerminal(q.Status) && currentSurveyVersion > q.OnSiteSurveyVersion;
        var basis = BasisOf(q);
        var allEvents = forCustomer ? [] : await unitOfWork.GetEventsAsync(q.Id, cancellationToken);
        var events = includeEvents && !forCustomer ? allEvents.Select(e => new QuotationEventView(e.EventType, e.ActorUserId, e.Detail, e.CreatedAt)).ToList() : null;
        // The customer sees the quoted selling price, quantity, totals, tax and discount, never the internal catalog reference price.
        return new QuotationView(q.Id, q.QuoteNumber, q.SurveyRequestId, q.Version, q.PreviousQuotationId, q.Status, q.Currency,
            lines.OrderBy(l => l.LineNumber).Select(l => new QuotationLineView(l.Id, l.LineNumber, l.ProductId, l.Sku, l.Name, l.Brand, l.Model, l.Unit,
                l.Quantity, l.UnitPrice, forCustomer ? null : l.CatalogUnitPrice, l.LineTotal, l.Description)).ToList(),
            q.EquipmentSubtotal, q.LaborCost, q.TransportCost, q.OtherServiceCost, q.Subtotal, q.DiscountAmount, q.TaxRatePercent, q.TaxAmount, q.TotalAmount,
            q.ValidUntil, expired, q.Status == QuotationStatuses.Issued && !expired && !surveyChanged, q.Terms, q.Notes, q.DocumentFileId, q.OnSiteSurveyReportId,
            q.OnSiteSurveyVersion, q.IssuedAt, q.AcceptedAt, q.RevisionReason, q.Revision, events, q.Origin,
            forCustomer ? null : basis, currentSurveyVersion, surveyChanged, forCustomer ? null : await ReviewOfAsync(q, lines, basis, cancellationToken),
            forCustomer ? null : ApprovalOf(q, lines, allEvents), q.ClosedAt, q.CloseReason);
    }

    private static PricingBasis? BasisOf(Quotation q) => q.PricingBasisJson is null ? null : JsonSerializer.Deserialize<PricingBasis>(q.PricingBasisJson, Json);

    private static OperationResult<bool>? SurveyChangedError(Quotation q, int currentSurveyVersion)
        => q.OnSiteSurveyVersion == currentSurveyVersion ? null
            : OperationResult<bool>.Fail(QuotationErrorCodes.SurveyChanged,
                $"The on-site survey was amended (version {q.OnSiteSurveyVersion} → {currentSurveyVersion}) after this quotation was made.",
                new { quotationSurveyVersion = q.OnSiteSurveyVersion, currentSurveyVersion });

    /// <summary>Pricing issues Sales may accept with a review note; every other issue must be fixed before an automatic draft is issued.</summary>
    private static readonly HashSet<string> Acknowledgeable =
        [PricingIssueCodes.ProductUnavailable, PricingIssueCodes.WrongItemKind, PricingIssueCodes.CurrencyNotSupported, PricingIssueCodes.ConditionsRequireReview];

    /// <summary>Recomputes which generation-time issues of an AUTOMATIC draft are still unresolved (null for other quotations).</summary>
    private async Task<PricingReview?> ReviewOfAsync(Quotation q, IReadOnlyList<QuotationLine> lines, PricingBasis? basis, CancellationToken cancellationToken)
    {
        if (q.Origin != QuotationOrigins.Automatic || q.Status != QuotationStatuses.Draft || basis is null) return null;
        var lineTypes = new HashSet<string>();
        foreach (var productId in lines.Where(l => l.ProductId is not null).Select(l => l.ProductId!.Value).Distinct())
            if (await unitOfWork.GetProductAsync(productId, cancellationToken) is { } product) lineTypes.Add(Catalog.Constants.ProductTypes.Normalize(product.ProductType));
        decimal Bucket(string? type) => type is null ? 0 : Catalog.Constants.ProductTypes.Normalize(type) switch
        {
            Catalog.Constants.ProductTypes.InstallationLabor => q.LaborCost,
            Catalog.Constants.ProductTypes.Transport => q.TransportCost,
            Catalog.Constants.ProductTypes.OtherService => q.OtherServiceCost,
            _ => 0,
        };
        bool Resolved(PricingIssue issue) => issue.Code switch
        {
            PricingIssueCodes.MissingSolarPanel => lineTypes.Contains(Catalog.Constants.ProductTypes.SolarPanel),
            PricingIssueCodes.MissingInverter => lineTypes.Contains(Catalog.Constants.ProductTypes.Inverter),
            PricingIssueCodes.LaborNotSpecified => q.LaborCost > 0,
            PricingIssueCodes.InvalidTotal => q.TotalAmount > 0 && q.TotalAmount <= QuotationPricing.MaxAmount,
            PricingIssueCodes.ConditionsRequireReview => false,
            // A service item is priced through its cost bucket (labor / transport / other); a material through a priced line of that product.
            _ when issue.ProductId is { } id && basis.Services.FirstOrDefault(s => s.ProductId == id) is { } service => Bucket(service.ProductType) > 0,
            _ when issue.ProductId is { } id => lines.Any(l => l.ProductId == id && l.UnitPrice > 0),
            _ => false,
        };
        var outstanding = basis.Issues.Where(i => !Resolved(i)).ToList();
        return new PricingReview(outstanding, outstanding.Any(i => Acknowledgeable.Contains(i.Code)));
    }

    private void AddEvent(Guid quotationId, string type, Guid actor, string? detail, DateTimeOffset now)
        => unitOfWork.AddEvent(new QuotationEvent { Id = Guid.NewGuid(), QuotationId = quotationId, EventType = type, ActorUserId = actor, Detail = detail, CreatedAt = now });

    private static string NewQuoteNumber(DateTimeOffset now)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var suffix = string.Concat(RandomNumberGenerator.GetBytes(6).Select(b => alphabet[b % alphabet.Length]));
        return $"QT-{now:yyyyMMdd}-{suffix}";
    }

    private static OperationResult<bool> Stale(Quotation q)
        => OperationResult<bool>.Fail(QuotationErrorCodes.StaleRevision, "The quotation was changed meanwhile. Reload and try again.", new { currentRevision = q.Revision });

    private static OperationResult<QuotationView> NotFound() => OperationResult<QuotationView>.Fail(QuotationErrorCodes.NotFound, "Quotation was not found.");

    private static OperationResult<QuotationView> Invalid(Dictionary<string, string[]> errors)
        => OperationResult<QuotationView>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.", errors);

    /// <summary>Takes the survey-request lock (first lock of the transaction) and reads whether the request is withdrawn or closed.</summary>
    private async Task<bool> RequestClosedUnderLockAsync(Guid surveyRequestId, CancellationToken cancellationToken)
    {
        await unitOfWork.LockSurveyRequestAsync(surveyRequestId, cancellationToken);
        return IsClosedRequest((await unitOfWork.GetSourceAsync(surveyRequestId, cancellationToken))?.SurveyRequestStatus);
    }

    /// <summary>Withdrawn (Cancelled) or Closed survey request.</summary>
    internal static bool IsClosedRequest(string? status) => status is nameof(PreSurvey.Enums.SurveyRequestStatus.Cancelled) or nameof(PreSurvey.Enums.SurveyRequestStatus.Closed);

    private static OperationResult<T> RequestClosedError<T>()
        => OperationResult<T>.Fail(QuotationErrorCodes.RequestClosed, "The survey request was withdrawn or closed; it is history and is not reopened.");

    private static string? Trim(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();
}
