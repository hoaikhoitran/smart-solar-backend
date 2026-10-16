using System.Security.Cryptography;
using System.Text.Json;
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
    DateTimeOffset? IssuedAt, DateTimeOffset? AcceptedAt, string? RevisionReason, int Revision, IReadOnlyList<QuotationEventView>? Events);

public sealed class QuotationOptions
{
    public const string SectionName = "Quotation";
    public decimal DefaultTaxRatePercent { get; set; } = 8m;
    public int MaxLines { get; set; } = 100;
    public int MaxValidityDays { get; set; } = 90;
}

/// <summary>
/// Quotation lifecycle: DRAFT → ISSUED → ACCEPTED, or ISSUED → REVISION_REQUESTED → (new version
/// DRAFT → ISSUED, previous SUPERSEDED). Sales issues without manager approval. Every transition is
/// a conditional single-row update, and the database allows at most one DRAFT and one
/// ISSUED/ACCEPTED quotation per survey request, so concurrent edits, issues and acceptances can
/// never produce two live offers.
/// </summary>
public sealed class QuotationHandlers(IQuotationUnitOfWork unitOfWork, FileService files, QuotationOptions options, TimeProvider time)
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
        if (source.OnSiteSurveyStatus != "COMPLETED" || source.OnSiteSurveyReportId is null)
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.SurveyNotCompleted, "The on-site survey must be completed before quoting.");

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
    public async Task<OperationResult<QuotationView>> IssueAsync(Guid salesUserId, Guid quotationId, int revision, CancellationToken cancellationToken)
    {
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (quotation is null) return NotFound();
        var source = await unitOfWork.GetSourceAsync(quotation.SurveyRequestId, cancellationToken);
        if (source?.AssignedSaleId != salesUserId) return NotFound();
        var snapshot = JsonSerializer.Serialize(new
        {
            source.CustomerName, source.CustomerPhone, source.CustomerEmail, source.PropertyName, source.Province, source.District, source.StreetLine,
        }, Json);

        return await RunAsync(quotationId, async (q, lines, now, token) =>
        {
            if (q.Status != QuotationStatuses.Draft) return OperationResult<bool>.Fail(QuotationErrorCodes.NotDraft, "Only drafts can be issued.");
            if (q.Revision != revision) return Stale(q);
            if (lines.Count == 0) return OperationResult<bool>.Fail(QuotationErrorCodes.Empty, "Add at least one line before issuing.");
            Recalculate(q, lines);
            if (q.TotalAmount <= 0 || q.TotalAmount > QuotationPricing.MaxAmount)
                return OperationResult<bool>.Fail(QuotationErrorCodes.InvalidLine, "The total amount must be positive.");
            if (q.ValidUntil is not { } validUntil || validUntil <= now)
                return OperationResult<bool>.Fail(CommonErrorCodes.ValidationFailed, "validUntil must be in the future.");

            if (q.PreviousQuotationId is { } previousId)
            {
                var previous = await unitOfWork.FindAsync(previousId, false, token);
                var superseded = previous is not null
                    && (await unitOfWork.TryTransitionAsync(previousId, QuotationStatuses.RevisionRequested, QuotationStatuses.Superseded, now, token)
                        || await unitOfWork.TryTransitionAsync(previousId, QuotationStatuses.Issued, QuotationStatuses.Superseded, now, token));
                if (!superseded)
                    return OperationResult<bool>.Fail(QuotationErrorCodes.CannotRevise, "The previous version was accepted or changed meanwhile; this revision cannot be issued.");
                AddEvent(previousId, QuotationEventTypes.Superseded, salesUserId, $"by version {q.Version}", now);
            }

            q.Status = QuotationStatuses.Issued;
            q.IssuedAt = now;
            q.CustomerSnapshot = snapshot;
            AddEvent(q.Id, QuotationEventTypes.Issued, salesUserId, $"total={q.TotalAmount} {q.Currency}", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>New DRAFT version copied from an ISSUED or REVISION_REQUESTED quotation (the original stays unchanged).</summary>
    public async Task<OperationResult<QuotationView>> CreateRevisionAsync(Guid salesUserId, Guid quotationId, CancellationToken cancellationToken)
    {
        var original = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (original is null) return NotFound();
        var source = await unitOfWork.GetSourceAsync(original.SurveyRequestId, cancellationToken);
        if (source?.AssignedSaleId != salesUserId) return NotFound();
        if (original.Status is not (QuotationStatuses.Issued or QuotationStatuses.RevisionRequested))
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.CannotRevise, "Only issued quotations or those with a revision request can be revised.");

        var all = await unitOfWork.ListBySurveyRequestAsync(original.SurveyRequestId, cancellationToken);
        if (all.Any(q => q.Status == QuotationStatuses.Draft))
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.DraftExists, "A draft quotation already exists for this request.");

        var now = time.GetUtcNow();
        var copy = new Quotation
        {
            Id = Guid.NewGuid(), QuoteNumber = original.QuoteNumber, SurveyRequestId = original.SurveyRequestId,
            OnSiteSurveyReportId = original.OnSiteSurveyReportId, OnSiteSurveyVersion = source.OnSiteSurveyVersion,
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
        return await PersistNewAsync(copy, lines, salesUserId, QuotationEventTypes.RevisionCreated, $"from version {original.Version}", cancellationToken);
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
            list.Add(await ViewAsync(q, includeEvents: false, cancellationToken));
        return list;
    }

    public async Task<OperationResult<QuotationView>> GetForCustomerAsync(Guid customerUserId, Guid quotationId, CancellationToken cancellationToken)
    {
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        return quotation is null || quotation.CustomerUserId != customerUserId || quotation.Status == QuotationStatuses.Draft
            ? NotFound()
            : OperationResult<QuotationView>.Ok(await ViewAsync(quotation, includeEvents: false, cancellationToken));
    }

    public async Task<OperationResult<QuotationView>> AcceptAsync(Guid customerUserId, Guid quotationId, CancellationToken cancellationToken)
    {
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (quotation is null || quotation.CustomerUserId != customerUserId || quotation.Status == QuotationStatuses.Draft) return NotFound();
        if (quotation.Status == QuotationStatuses.Accepted) return OperationResult<QuotationView>.Ok(await ViewAsync(quotation, false, cancellationToken));

        return await RunAsync(quotationId, async (q, _, now, token) =>
        {
            if (q.ValidUntil is { } validUntil && validUntil <= now)
                return OperationResult<bool>.Fail(QuotationErrorCodes.Expired, "This quotation has expired; ask Sales for a new version.");
            if (!await unitOfWork.TryTransitionAsync(q.Id, QuotationStatuses.Issued, QuotationStatuses.Accepted, now, token))
                return OperationResult<bool>.Fail(QuotationErrorCodes.NotIssued, "This quotation can no longer be accepted (it was revised or changed).");
            AddEvent(q.Id, QuotationEventTypes.Accepted, customerUserId, null, now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken, trackedChanges: false);
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
        }, cancellationToken);
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
        CancellationToken cancellationToken, bool trackedChanges = true)
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

        return OperationResult<QuotationView>.Ok(await ViewAsync((await unitOfWork.FindAsync(quotationId, false, cancellationToken))!, true, cancellationToken));
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
            await unitOfWork.ExecuteInTransactionAsync<bool>(async token =>
            {
                unitOfWork.AddQuotation(quotation);
                foreach (var line in lines) unitOfWork.AddLine(line);
                AddEvent(quotation.Id, eventType, actor, detail, quotation.CreatedAt);
                await unitOfWork.SaveChangesAsync(token);
                return (true, true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<QuotationView>.Fail(QuotationErrorCodes.DraftExists, "A draft quotation already exists for this request.");
        }
        return OperationResult<QuotationView>.Ok(await ViewAsync(quotation, true, cancellationToken));
    }

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

    private async Task<QuotationView> ViewAsync(Quotation q, bool includeEvents, CancellationToken cancellationToken)
    {
        var lines = await unitOfWork.GetLinesAsync(q.Id, false, cancellationToken);
        var now = time.GetUtcNow();
        var expired = q.ValidUntil is { } until && until <= now;
        var events = includeEvents ? (await unitOfWork.GetEventsAsync(q.Id, cancellationToken)).Select(e => new QuotationEventView(e.EventType, e.ActorUserId, e.Detail, e.CreatedAt)).ToList() : null;
        return new QuotationView(q.Id, q.QuoteNumber, q.SurveyRequestId, q.Version, q.PreviousQuotationId, q.Status, q.Currency,
            lines.OrderBy(l => l.LineNumber).Select(l => new QuotationLineView(l.Id, l.LineNumber, l.ProductId, l.Sku, l.Name, l.Brand, l.Model, l.Unit,
                l.Quantity, l.UnitPrice, l.CatalogUnitPrice, l.LineTotal, l.Description)).ToList(),
            q.EquipmentSubtotal, q.LaborCost, q.TransportCost, q.OtherServiceCost, q.Subtotal, q.DiscountAmount, q.TaxRatePercent, q.TaxAmount, q.TotalAmount,
            q.ValidUntil, expired, q.Status == QuotationStatuses.Issued && !expired, q.Terms, q.Notes, q.DocumentFileId, q.OnSiteSurveyReportId,
            q.OnSiteSurveyVersion, q.IssuedAt, q.AcceptedAt, q.RevisionReason, q.Revision, events);
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

    private static string? Trim(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();
}
