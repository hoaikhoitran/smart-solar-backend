using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.OnSiteSurveys;

namespace SmartSolar.Modules.Quotations;

/// <summary>
/// One pricing exception of a quotation that needs a Manager: the header discount, a line priced below its catalog reference price, or a
/// zero-priced line (an intentional free item is still an exception; a missing catalog price is refused when the line is added).
/// </summary>
public sealed record PriceAdjustment(string Kind, int? LineNumber, Guid? ProductId, decimal? CatalogUnitPrice, decimal? UnitPrice, decimal? Amount);

public static class PriceAdjustmentKinds
{
    public const string Discount = "DISCOUNT";
    public const string ReducedUnitPrice = "REDUCED_UNIT_PRICE";
    public const string ZeroPrice = "ZERO_PRICE";
}

/// <summary>A request or decision of the pricing approval (who, when, why).</summary>
public sealed record PricingApprovalStep(string EventType, Guid ActorUserId, string? Reason, DateTimeOffset At);

/// <summary>
/// Staff-only approval state of the quotation's current pricing. <see cref="Fingerprint"/> identifies the exact version and pricing values
/// (lines, unit and catalog prices, quantities, costs, discount, tax rate); a request or decision only counts for the same fingerprint, so any
/// pricing edit after an approval makes the approval obsolete (<see cref="PricingApprovalStatuses.Required"/> again).
/// </summary>
public sealed record PricingApprovalView(string Status, IReadOnlyList<PriceAdjustment> Adjustments, string Fingerprint,
    PricingApprovalStep? Request, PricingApprovalStep? Decision);

public static class PricingApprovalStatuses
{
    /// <summary>Catalog prices, no discount: Sales issues without approval.</summary>
    public const string NotRequired = "NOT_REQUIRED";
    /// <summary>Adjustments without a request or decision for the current pricing.</summary>
    public const string Required = "REQUIRED";
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
}

/// <summary>One difference between the quotation's equipment lines and the current survey's bill of materials.</summary>
public sealed record BomMismatch(Guid ProductId, string? Sku, decimal SurveyQuantity, decimal QuotedQuantity);

public sealed partial class QuotationHandlers
{
    private static readonly string[] ApprovalEventTypes =
        [QuotationEventTypes.PricingApprovalRequested, QuotationEventTypes.PricingApproved, QuotationEventTypes.PricingRejected];

    /// <summary>
    /// Sales owner asks a Manager to approve the draft's pricing exceptions (discount, reduced or zero prices). The request names the exact
    /// pricing (fingerprint); a repeat while the same pricing is pending is refused. A reason is required.
    /// </summary>
    public async Task<OperationResult<QuotationView>> RequestPricingApprovalAsync(Guid salesUserId, Guid quotationId, int revision, string? reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            return Invalid(new() { ["reason"] = ["A reason of at most 1000 characters is required."] });
        var quotation = await unitOfWork.FindAsync(quotationId, false, cancellationToken);
        if (quotation is null || (await unitOfWork.GetSourceAsync(quotation.SurveyRequestId, cancellationToken))?.AssignedSaleId != salesUserId) return NotFound();

        return await RunAsync(quotationId, async (q, lines, now, token) =>
        {
            if (q.Status != QuotationStatuses.Draft) return OperationResult<bool>.Fail(QuotationErrorCodes.NotDraft, "Only drafts need a pricing approval.");
            if (q.Revision != revision) return Stale(q);
            Recalculate(q, lines);
            var events = await unitOfWork.GetEventsAsync(q.Id, token);
            var approval = ApprovalOf(q, lines, events);
            if (approval.Status == PricingApprovalStatuses.NotRequired)
                return OperationResult<bool>.Fail(QuotationErrorCodes.PricingApprovalNotRequired, "The quotation uses catalog prices without a discount; no approval is needed.");
            if (approval.Status == PricingApprovalStatuses.Pending)
                return OperationResult<bool>.Fail(QuotationErrorCodes.PricingApprovalPending, "This pricing is already waiting for a Manager.");
            if (approval.Status == PricingApprovalStatuses.Approved)
                return OperationResult<bool>.Fail(CommonErrorCodes.InvalidState, "This pricing is already approved.");
            AddApprovalEvent(q, QuotationEventTypes.PricingApprovalRequested, salesUserId, approval.Fingerprint, reason, now, events);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>
    /// Manager approves or rejects the pending request for exactly the draft's current pricing (reason required either way). The Sales owner
    /// can never decide their own request. Concurrent edits are serialised by the row lock and the revision.
    /// </summary>
    public async Task<OperationResult<QuotationView>> DecidePricingApprovalAsync(Guid managerUserId, Guid quotationId, int revision, bool approve, string? reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            return Invalid(new() { ["reason"] = ["A reason of at most 1000 characters is required."] });
        if (await unitOfWork.FindAsync(quotationId, false, cancellationToken) is null) return NotFound();

        return await RunAsync(quotationId, async (q, lines, now, token) =>
        {
            if (q.Status != QuotationStatuses.Draft) return OperationResult<bool>.Fail(QuotationErrorCodes.NotDraft, "Only a draft's pricing is approved.");
            if (q.Revision != revision) return Stale(q);
            if (q.SalesUserId == managerUserId)
                return OperationResult<bool>.Fail(QuotationErrorCodes.SelfApprovalNotAllowed, "The Sales owner of a quotation cannot approve its pricing.");
            Recalculate(q, lines);
            var events = await unitOfWork.GetEventsAsync(q.Id, token);
            var approval = ApprovalOf(q, lines, events);
            if (approval.Status != PricingApprovalStatuses.Pending)
                return OperationResult<bool>.Fail(QuotationErrorCodes.PricingApprovalNotPending,
                    "No approval request is waiting for the quotation's current pricing.", new { approvalStatus = approval.Status });
            AddApprovalEvent(q, approve ? QuotationEventTypes.PricingApproved : QuotationEventTypes.PricingRejected, managerUserId, approval.Fingerprint, reason, now, events);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>Manager queue: drafts whose current pricing waits for a decision.</summary>
    public async Task<IReadOnlyList<QuotationView>> ListPendingPricingApprovalsAsync(CancellationToken cancellationToken)
    {
        var list = new List<QuotationView>();
        foreach (var q in await unitOfWork.ListDraftsWithPricingApprovalRequestsAsync(cancellationToken))
        {
            var view = await ViewAsync(q, includeEvents: true, cancellationToken);
            if (view.PricingApproval?.Status == PricingApprovalStatuses.Pending) list.Add(view);
        }
        return list;
    }

    /// <summary>Pricing exceptions of the lines and header: discount, unit price below the catalog reference, zero price.</summary>
    internal static IReadOnlyList<PriceAdjustment> AdjustmentsOf(Quotation q, IReadOnlyList<QuotationLine> lines)
    {
        var adjustments = new List<PriceAdjustment>();
        if (q.DiscountAmount > 0) adjustments.Add(new(PriceAdjustmentKinds.Discount, null, null, null, null, q.DiscountAmount));
        foreach (var l in lines.OrderBy(l => l.LineNumber))
        {
            if (l.UnitPrice == 0)
                adjustments.Add(new(PriceAdjustmentKinds.ZeroPrice, l.LineNumber, l.ProductId, l.CatalogUnitPrice, l.UnitPrice, null));
            else if (l.CatalogUnitPrice is { } catalog && l.UnitPrice < catalog)
                adjustments.Add(new(PriceAdjustmentKinds.ReducedUnitPrice, l.LineNumber, l.ProductId, catalog, l.UnitPrice,
                    QuotationPricing.LineTotal(l.Quantity, catalog) - l.LineTotal));
        }
        return adjustments;
    }

    /// <summary>SHA-256 (first 128 bits, hex) of the quotation id and version and every pricing value, in a fixed invariant format.</summary>
    internal static string FingerprintOf(Quotation q, IReadOnlyList<QuotationLine> lines)
    {
        var s = new StringBuilder();
        s.Append(q.Id).Append('|').Append(q.Version).Append('|').Append(Money(q.LaborCost)).Append('|').Append(Money(q.TransportCost)).Append('|')
            .Append(Money(q.OtherServiceCost)).Append('|').Append(Money(q.DiscountAmount)).Append('|').Append(Money(q.TaxRatePercent));
        foreach (var l in lines.OrderBy(l => l.LineNumber))
            s.Append('\n').Append(l.LineNumber).Append('|').Append(l.ProductId).Append('|').Append(Money(l.Quantity)).Append('|')
                .Append(Money(l.UnitPrice)).Append('|').Append(l.CatalogUnitPrice is { } c ? Money(c) : "-");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.ToString())), 0, 16).ToLowerInvariant();

        static string Money(decimal value) => value.ToString("0.############", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The approval state for the current pricing: the latest request/decision (by its sequence number) counts only when it names the same
    /// fingerprint; otherwise the pricing changed after it and a new request is needed.
    /// </summary>
    internal static PricingApprovalView ApprovalOf(Quotation q, IReadOnlyList<QuotationLine> lines, IReadOnlyList<QuotationEvent> events)
    {
        var adjustments = AdjustmentsOf(q, lines);
        var fingerprint = FingerprintOf(q, lines);
        if (adjustments.Count == 0) return new(PricingApprovalStatuses.NotRequired, adjustments, fingerprint, null, null);

        var steps = events.Where(e => ApprovalEventTypes.Contains(e.EventType)).Select(e => (Event: e, Parsed: ParseApprovalDetail(e.Detail)))
            .Where(x => x.Parsed is not null).OrderBy(x => x.Parsed!.Value.Sequence).ToList();
        var latest = steps.LastOrDefault();
        if (latest.Event is null || latest.Parsed!.Value.Fingerprint != fingerprint)
            return new(PricingApprovalStatuses.Required, adjustments, fingerprint, null, null);
        var request = steps.LastOrDefault(x => x.Event.EventType == QuotationEventTypes.PricingApprovalRequested && x.Parsed!.Value.Fingerprint == fingerprint);
        var requestStep = request.Event is null ? null : Step(request.Event, request.Parsed!.Value.Reason);
        return latest.Event.EventType switch
        {
            QuotationEventTypes.PricingApproved => new(PricingApprovalStatuses.Approved, adjustments, fingerprint, requestStep, Step(latest.Event, latest.Parsed.Value.Reason)),
            QuotationEventTypes.PricingRejected => new(PricingApprovalStatuses.Rejected, adjustments, fingerprint, requestStep, Step(latest.Event, latest.Parsed.Value.Reason)),
            _ => new(PricingApprovalStatuses.Pending, adjustments, fingerprint, requestStep, null),
        };

        static PricingApprovalStep Step(QuotationEvent e, string? reason) => new(e.EventType, e.ActorUserId, reason, e.CreatedAt);
    }

    /// <summary>"fp:{fingerprint}:{sequence}; {reason}" → its parts (null for anything else).</summary>
    private static (string Fingerprint, int Sequence, string? Reason)? ParseApprovalDetail(string? detail)
    {
        if (detail is null || !detail.StartsWith("fp:", StringComparison.Ordinal)) return null;
        var end = detail.IndexOf(';');
        var head = (end < 0 ? detail[3..] : detail[3..end]).Split(':');
        if (head.Length != 2 || !int.TryParse(head[1], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)) return null;
        return (head[0], sequence, end < 0 ? null : detail[(end + 1)..].Trim());
    }

    /// <summary>Appends a request/decision; its sequence number (next after <paramref name="events"/>, read under the row lock) orders steps written at the same instant.</summary>
    private void AddApprovalEvent(Quotation q, string type, Guid actor, string fingerprint, string reason, DateTimeOffset now, IReadOnlyList<QuotationEvent> events)
    {
        var sequence = events.Where(e => ApprovalEventTypes.Contains(e.EventType)).Select(e => ParseApprovalDetail(e.Detail)?.Sequence ?? 0).DefaultIfEmpty(0).Max() + 1;
        AddEvent(q.Id, type, actor, $"fp:{fingerprint}:{sequence}; {Trim(reason, 400)}", now);
    }

    /// <summary>
    /// Differences between the equipment lines and the current survey's bill of materials: every BOM material must be quoted with the same
    /// quantity, and no SOLAR_PANEL / INVERTER outside the BOM may be quoted. Empty when the survey has no bill of materials (legacy/manual
    /// surveys: nothing to compare against).
    /// </summary>
    private async Task<IReadOnlyList<BomMismatch>> BomMismatchesAsync(Guid surveyRequestId, IReadOnlyList<QuotationLine> lines, CancellationToken cancellationToken)
    {
        var basis = await unitOfWork.GetSurveyBasisAsync(surveyRequestId, cancellationToken);
        var bom = OnSiteSurveyHandlers.ParseBillOfMaterials(basis?.BillOfMaterialsJson);
        if (bom?.Materials is not { Count: > 0 } materials) return [];
        var surveyed = materials.GroupBy(m => m.ProductId).ToDictionary(g => g.Key, g => g.Sum(m => m.Quantity));
        var quoted = lines.Where(l => l.ProductId is not null).GroupBy(l => l.ProductId!.Value)
            .ToDictionary(g => g.Key, g => (Quantity: g.Sum(l => l.Quantity), g.First().Sku));
        var mismatches = new List<BomMismatch>();
        foreach (var (productId, quantity) in surveyed)
        {
            var q = quoted.TryGetValue(productId, out var line) ? line.Quantity : 0;
            if (q != quantity) mismatches.Add(new(productId, line.Sku, quantity, q));
        }
        foreach (var (productId, line) in quoted.Where(x => !surveyed.ContainsKey(x.Key)))
            if (await unitOfWork.GetProductAsync(productId, cancellationToken) is { } product
                && Catalog.Constants.ProductTypes.Normalize(product.ProductType) is Catalog.Constants.ProductTypes.SolarPanel or Catalog.Constants.ProductTypes.Inverter)
                mismatches.Add(new(productId, line.Sku, 0, line.Quantity));
        return mismatches;
    }

    private static OperationResult<bool> BomMismatchError(IReadOnlyList<BomMismatch> mismatches)
        => OperationResult<bool>.Fail(QuotationErrorCodes.BomMismatch,
            "The equipment lines do not match the current on-site survey's bill of materials. Correct the lines, or have the survey amended.",
            new { mismatches });
}
